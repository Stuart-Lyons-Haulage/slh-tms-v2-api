using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Caching.Memory;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Implements distance matrix computation using Samsara's /distances/matrix endpoint.
/// </summary>
public sealed class SamsaraDistanceMatrixAdapter(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IMemoryCache cache,
    TelemetryClient telemetry,
    ILogger<SamsaraDistanceMatrixAdapter> logger) : IDistanceMatrixService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(20);

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

        var cacheKey = BuildCacheKey(origins, destinations, profile);
        if (cache.TryGetValue<CachedMatrix>(cacheKey, out var cached) && cached is not null)
            return cached.Rehydrate(origins, destinations);

        var started = Stopwatch.GetTimestamp();
        var matrix = await SubmitMatrixRequestAsync(origins, destinations, profile, ct);
        var canonical = CachedMatrix.From(matrix, origins, destinations);
        cache.Set(cacheKey, canonical, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl });

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        telemetry.TrackEvent("RouteMatrixComputed", new Dictionary<string, string>
        {
            ["origins"] = origins.Count.ToString(CultureInfo.InvariantCulture),
            ["destinations"] = destinations.Count.ToString(CultureInfo.InvariantCulture),
            ["provider"] = "Samsara"
        });
        telemetry.TrackMetric("MatrixComputeTimeMs", elapsedMs);
        logger.LogDebug("Samsara matrix {Origins}x{Destinations} completed in {ElapsedMs:F0}ms.", origins.Count, destinations.Count, elapsedMs);

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

    private async Task<RouteMatrix> SubmitMatrixRequestAsync(
        IReadOnlyList<MatrixPoint> origins,
        IReadOnlyList<MatrixPoint> destinations,
        HgvVehicleProfile profile,
        CancellationToken ct)
    {
        var samsaraApiToken = configuration["Integrations:Samsara:ApiToken"]
            ?? throw new InvalidOperationException("Samsara API token is required but not configured (Integrations:Samsara:ApiToken).");

        var payload = new
        {
            origins = origins.Select(p => new { latitude = (double)p.Latitude, longitude = (double)p.Longitude }).ToArray(),
            destinations = destinations.Select(p => new { latitude = (double)p.Latitude, longitude = (double)p.Longitude }).ToArray(),
            vehicleClass = NormaliseSamsaraVehicleClass(profile)
        };

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.samsara.com/api/v1/distances/matrix");
        request.Content = JsonContent.Create(payload);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", samsaraApiToken);

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await ParseMatrixAsync(stream, origins.Count, destinations.Count, ct);
    }

    private static async Task<RouteMatrix> ParseMatrixAsync(
        Stream stream,
        int originCount,
        int destinationCount,
        CancellationToken ct)
    {
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(stream, cancellationToken: ct); }
        catch (JsonException ex) { throw new MatrixResponseException($"Samsara returned malformed JSON: {ex.Message}"); }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
                throw new MatrixResponseException("Samsara matrix response did not contain a data array.");

            var rows = new List<IReadOnlyList<RouteMatrixCell?>>(originCount);
            var matrixArray = dataElement.EnumerateArray().ToArray();

            if (matrixArray.Length != originCount)
                throw new MatrixResponseException($"Samsara matrix returned {matrixArray.Length} origin rows; expected {originCount}.");

            foreach (var rowElement in matrixArray)
            {
                if (rowElement.ValueKind != JsonValueKind.Array || rowElement.GetArrayLength() != destinationCount)
                    throw new MatrixResponseException("Samsara matrix destination dimensions did not match the request.");

                var row = new List<RouteMatrixCell?>(destinationCount);
                foreach (var cellElement in rowElement.EnumerateArray())
                    row.Add(ParseCell(cellElement));
                rows.Add(row);
            }

            return new RouteMatrix(rows);
        }
    }

    private static RouteMatrixCell? ParseCell(JsonElement cell)
    {
        if (cell.ValueKind == JsonValueKind.Null)
            return null;

        if (!cell.TryGetProperty("distanceMeters", out var distance) || !distance.TryGetInt64(out var metres) || metres < 0)
            throw new MatrixResponseException("Samsara matrix cell missing or invalid distanceMeters.");

        if (!cell.TryGetProperty("durationSeconds", out var duration) || !duration.TryGetInt64(out var seconds) || seconds < 0)
            throw new MatrixResponseException("Samsara matrix cell missing or invalid durationSeconds.");

        return new RouteMatrixCell((int)Math.Ceiling(seconds / 60d), metres);
    }

    private static string NormaliseSamsaraVehicleClass(HgvVehicleProfile profile)
    {
        // Samsara vehicle classes: light_duty, medium_duty, heavy_duty
        // Map based on vehicle weight (tonnes)
        var weightTonnes = profile.VehicleWeight / 1000m;
        return weightTonnes switch
        {
            < 3.5m => "light_duty",
            < 12m => "medium_duty",
            _ => "heavy_duty"
        };
    }

    private static string BuildCacheKey(IReadOnlyList<MatrixPoint> origins, IReadOnlyList<MatrixPoint> destinations, HgvVehicleProfile profile)
    {
        var originKey = string.Join(';', origins.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal));
        var destinationKey = string.Join(';', destinations.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal));
        var raw = $"{originKey}|{destinationKey}|{profile.CacheKey}";
        return "samsara-matrix:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
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
