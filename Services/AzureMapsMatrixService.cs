using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Slh.Tms.Api.Services;

public interface IAzureMapsMatrixService
{
    Task<RouteMatrix> ComputeMatrixAsync(
        IReadOnlyList<MatrixPoint> origins,
        IReadOnlyList<MatrixPoint> destinations,
        HgvVehicleProfile profile,
        CancellationToken ct);

    Task<RouteMatrixCell?> GetSingleRouteAsync(
        MatrixPoint origin,
        MatrixPoint destination,
        HgvVehicleProfile profile,
        CancellationToken ct);
}

public sealed record MatrixPoint(decimal Latitude, decimal Longitude)
{
    public MatrixPoint Validate()
    {
        if (Latitude is < -90 or > 90) throw new ArgumentOutOfRangeException(nameof(Latitude));
        if (Longitude is < -180 or > 180) throw new ArgumentOutOfRangeException(nameof(Longitude));
        return this;
    }

    internal string Key => string.Create(
        CultureInfo.InvariantCulture,
        $"{Latitude:0.######},{Longitude:0.######}");
}

public sealed record RouteMatrixCell(int TravelTimeMinutes, long DistanceMetres);

public sealed class RouteMatrix
{
    public RouteMatrix(IReadOnlyList<IReadOnlyList<RouteMatrixCell?>> cells)
    {
        Cells = cells;
    }

    public IReadOnlyList<IReadOnlyList<RouteMatrixCell?>> Cells { get; }

    public RouteMatrixCell? this[int originIndex, int destinationIndex] => Cells[originIndex][destinationIndex];
}

public sealed class HgvVehicleProfile
{
    public int VehicleMaxSpeed { get; set; } = 90;
    public int VehicleWeight { get; set; } = 44_000;
    public int VehicleAxleWeight { get; set; } = 11_500;
    public decimal VehicleLength { get; set; } = 16.5m;
    public decimal VehicleHeight { get; set; } = 4.0m;
    public decimal VehicleWidth { get; set; } = 2.55m;

    // Azure Maps only defines vehicleLoadType values for hazardous cargo. Ordinary produce
    // must therefore leave this null; configuring a value applies the corresponding hazmat restriction.
    public string? VehicleLoadType { get; set; }

    internal string CacheKey => string.Join(':',
        VehicleMaxSpeed,
        VehicleWeight,
        VehicleAxleWeight,
        VehicleLength.ToString(CultureInfo.InvariantCulture),
        VehicleHeight.ToString(CultureInfo.InvariantCulture),
        VehicleWidth.ToString(CultureInfo.InvariantCulture),
        VehicleLoadType ?? "none");
}

public sealed class AzureMapsMatrixOptions
{
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(20);
    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan InitialPollDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxPollDelay { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class MatrixTimeoutException(string requestId)
    : TimeoutException($"Azure Maps route matrix request {requestId} did not complete within the configured timeout.")
{
    public string RequestId { get; } = requestId;
}

public sealed class MatrixResponseException(string message) : Exception(message);

public sealed class AzureMapsMatrixService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    TokenCredential credential,
    IConfiguration configuration,
    IOptions<AzureMapsMatrixOptions> options,
    TelemetryClient telemetry,
    ILogger<AzureMapsMatrixService> logger) : IAzureMapsMatrixService
{
    private const string ClientName = "AzureMapsMatrix";
    private static readonly TokenRequestContext TokenContext = new(["https://atlas.microsoft.com/.default"]);
    private readonly AzureMapsMatrixOptions _options = options.Value;

    public async Task<RouteMatrix> ComputeMatrixAsync(
        IReadOnlyList<MatrixPoint> origins,
        IReadOnlyList<MatrixPoint> destinations,
        HgvVehicleProfile profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(destinations);
        ArgumentNullException.ThrowIfNull(profile);
        if (origins.Count == 0) throw new ArgumentException("At least one origin is required.", nameof(origins));
        if (destinations.Count == 0) throw new ArgumentException("At least one destination is required.", nameof(destinations));

        foreach (var point in origins) point.Validate();
        foreach (var point in destinations) point.Validate();
        ValidateProfile(profile);

        var cacheKey = BuildCacheKey(origins, destinations, profile);
        if (cache.TryGetValue<CachedMatrix>(cacheKey, out var cached) && cached is not null)
            return cached.Rehydrate(origins, destinations);

        var started = Stopwatch.GetTimestamp();
        var matrix = await SubmitAndPollAsync(origins, destinations, profile, ct);
        var canonical = CachedMatrix.From(matrix, origins, destinations);
        cache.Set(cacheKey, canonical, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = _options.CacheTtl });

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        telemetry.TrackEvent("RouteMatrixComputed", new Dictionary<string, string>
        {
            ["origins"] = origins.Count.ToString(CultureInfo.InvariantCulture),
            ["destinations"] = destinations.Count.ToString(CultureInfo.InvariantCulture)
        });
        telemetry.TrackMetric("MatrixComputeTimeMs", elapsedMs);
        logger.LogDebug("Azure Maps matrix {Origins}x{Destinations} completed in {ElapsedMs:F0}ms.", origins.Count, destinations.Count, elapsedMs);

        return matrix;
    }

    public async Task<RouteMatrixCell?> GetSingleRouteAsync(
        MatrixPoint origin,
        MatrixPoint destination,
        HgvVehicleProfile profile,
        CancellationToken ct)
    {
        var matrix = await ComputeMatrixAsync([origin], [destination], profile, ct);
        return matrix.Cells[0][0];
    }

    private async Task<RouteMatrix> SubmitAndPollAsync(
        IReadOnlyList<MatrixPoint> origins,
        IReadOnlyList<MatrixPoint> destinations,
        HgvVehicleProfile profile,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildSubmitUri(profile));
        await AuthenticateAsync(request, ct);
        request.Content = JsonContent.Create(new
        {
            origins = new { type = "MultiPoint", coordinates = origins.Select(p => new[] { p.Longitude, p.Latitude }).ToArray() },
            destinations = new { type = "MultiPoint", coordinates = destinations.Select(p => new[] { p.Longitude, p.Latitude }).ToArray() }
        });

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.OK)
            return await ParseMatrixAsync(response, origins.Count, destinations.Count, ct);

        if (response.StatusCode != HttpStatusCode.Accepted)
            throw await CreateHttpExceptionAsync(response, "submit", ct);

        var location = response.Headers.Location
            ?? throw new MatrixResponseException("Azure Maps accepted the matrix request but did not provide a Location header.");
        var requestId = ExtractRequestId(location);
        return await PollAsync(client, location, requestId, origins.Count, destinations.Count, ct);
    }

    private async Task<RouteMatrix> PollAsync(
        HttpClient client,
        Uri location,
        string requestId,
        int originCount,
        int destinationCount,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _options.PollTimeout;
        var delay = _options.InitialPollDelay;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(delay <= remaining ? delay : remaining, ct);

            using var poll = new HttpRequestMessage(HttpMethod.Get, location);
            await AuthenticateAsync(poll, ct);
            using var response = await client.SendAsync(poll, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.OK)
                return await ParseMatrixAsync(response, originCount, destinationCount, ct);
            if (response.StatusCode != HttpStatusCode.Accepted)
                throw await CreateHttpExceptionAsync(response, $"poll request {requestId}", ct);

            var doubledTicks = delay.Ticks > long.MaxValue / 2 ? long.MaxValue : delay.Ticks * 2;
            delay = TimeSpan.FromTicks(Math.Min(doubledTicks, _options.MaxPollDelay.Ticks));
        }

        throw new MatrixTimeoutException(requestId);
    }

    private async Task AuthenticateAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(TokenContext, ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var clientId = configuration["Maps:ClientId"];
        if (!string.IsNullOrWhiteSpace(clientId)) request.Headers.TryAddWithoutValidation("x-ms-client-id", clientId);
    }

    private static Uri BuildSubmitUri(HgvVehicleProfile profile)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("api-version", "1.0"),
            new("travelMode", "truck"),
            new("routeType", "fastest"),
            new("traffic", "true"),
            new("vehicleCommercial", "true"),
            new("vehicleMaxSpeed", profile.VehicleMaxSpeed.ToString(CultureInfo.InvariantCulture)),
            new("vehicleWeight", profile.VehicleWeight.ToString(CultureInfo.InvariantCulture)),
            new("vehicleAxleWeight", profile.VehicleAxleWeight.ToString(CultureInfo.InvariantCulture)),
            new("vehicleLength", profile.VehicleLength.ToString(CultureInfo.InvariantCulture)),
            new("vehicleHeight", profile.VehicleHeight.ToString(CultureInfo.InvariantCulture)),
            new("vehicleWidth", profile.VehicleWidth.ToString(CultureInfo.InvariantCulture))
        };
        if (!string.IsNullOrWhiteSpace(profile.VehicleLoadType)) values.Add(new("vehicleLoadType", profile.VehicleLoadType));

        var query = string.Join('&', values.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        return new Uri($"/route/matrix/json?{query}", UriKind.Relative);
    }

    private static async Task<RouteMatrix> ParseMatrixAsync(HttpResponseMessage response, int originCount, int destinationCount, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(stream, cancellationToken: ct); }
        catch (JsonException ex) { throw new MatrixResponseException($"Azure Maps returned malformed JSON: {ex.Message}"); }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("matrix", out var matrixElement) || matrixElement.ValueKind != JsonValueKind.Array)
                throw new MatrixResponseException("Azure Maps matrix response did not contain a matrix array.");
            if (matrixElement.GetArrayLength() != originCount)
                throw new MatrixResponseException($"Azure Maps matrix returned {matrixElement.GetArrayLength()} origin rows; expected {originCount}.");

            var rows = new List<IReadOnlyList<RouteMatrixCell?>>(originCount);
            foreach (var rowElement in matrixElement.EnumerateArray())
            {
                if (rowElement.ValueKind != JsonValueKind.Array || rowElement.GetArrayLength() != destinationCount)
                    throw new MatrixResponseException("Azure Maps matrix destination dimensions did not match the request.");
                var row = new List<RouteMatrixCell?>(destinationCount);
                foreach (var cellElement in rowElement.EnumerateArray()) row.Add(ParseCell(cellElement));
                rows.Add(row);
            }
            return new RouteMatrix(rows);
        }
    }

    private static RouteMatrixCell? ParseCell(JsonElement cell)
    {
        if (cell.TryGetProperty("statusCode", out var statusElement) && statusElement.TryGetInt32(out var status) && status is < 200 or >= 300)
            return null;
        if (!cell.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            return null;
        if (!response.TryGetProperty("routeSummary", out var summary) || summary.ValueKind != JsonValueKind.Object)
            throw new MatrixResponseException("Azure Maps successful matrix cell was missing routeSummary.");
        if (!summary.TryGetProperty("travelTimeInSeconds", out var time) || !time.TryGetInt64(out var seconds) || seconds < 0)
            throw new MatrixResponseException("Azure Maps routeSummary was missing a valid travelTimeInSeconds value.");
        if (!summary.TryGetProperty("lengthInMeters", out var length) || !length.TryGetInt64(out var metres) || metres < 0)
            throw new MatrixResponseException("Azure Maps routeSummary was missing a valid lengthInMeters value.");
        return new RouteMatrixCell((int)Math.Ceiling(seconds / 60d), metres);
    }

    private static async Task<HttpRequestException> CreateHttpExceptionAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 800) body = body[..800];
        return new HttpRequestException($"Azure Maps matrix {operation} failed with {(int)response.StatusCode} ({response.ReasonPhrase}): {body}", null, response.StatusCode);
    }

    private static string ExtractRequestId(Uri location)
    {
        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?', 2)[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.LastOrDefault() ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(location.ToString())))[..12];
    }

    private static string BuildCacheKey(IReadOnlyList<MatrixPoint> origins, IReadOnlyList<MatrixPoint> destinations, HgvVehicleProfile profile)
    {
        var originKey = string.Join(';', origins.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal));
        var destinationKey = string.Join(';', destinations.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal));
        var raw = $"{originKey}|{destinationKey}|{profile.CacheKey}";
        return "azure-matrix:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static void ValidateProfile(HgvVehicleProfile profile)
    {
        if (profile.VehicleMaxSpeed is < 0 or > 250) throw new ArgumentOutOfRangeException(nameof(profile.VehicleMaxSpeed));
        if (profile.VehicleWeight < 0) throw new ArgumentOutOfRangeException(nameof(profile.VehicleWeight));
        if (profile.VehicleAxleWeight < 0) throw new ArgumentOutOfRangeException(nameof(profile.VehicleAxleWeight));
        if (profile.VehicleLength < 0) throw new ArgumentOutOfRangeException(nameof(profile.VehicleLength));
        if (profile.VehicleHeight < 0) throw new ArgumentOutOfRangeException(nameof(profile.VehicleHeight));
        if (profile.VehicleWidth < 0) throw new ArgumentOutOfRangeException(nameof(profile.VehicleWidth));
    }

    private sealed class CachedMatrix(Dictionary<string, RouteMatrixCell?> cells)
    {
        public static CachedMatrix From(RouteMatrix matrix, IReadOnlyList<MatrixPoint> origins, IReadOnlyList<MatrixPoint> destinations)
        {
            var cells = new Dictionary<string, RouteMatrixCell?>(StringComparer.Ordinal);
            for (var i = 0; i < origins.Count; i++)
                for (var j = 0; j < destinations.Count; j++)
                    cells[CellKey(origins[i], destinations[j])] = matrix.Cells[i][j];
            return new CachedMatrix(cells);
        }

        public RouteMatrix Rehydrate(IReadOnlyList<MatrixPoint> origins, IReadOnlyList<MatrixPoint> destinations)
        {
            var rows = origins.Select(origin =>
                (IReadOnlyList<RouteMatrixCell?>)destinations.Select(destination =>
                    cells.TryGetValue(CellKey(origin, destination), out var cell) ? cell : null).ToArray()).ToArray();
            return new RouteMatrix(rows);
        }

        private static string CellKey(MatrixPoint origin, MatrixPoint destination) => $"{origin.Key}>{destination.Key}";
    }
}
