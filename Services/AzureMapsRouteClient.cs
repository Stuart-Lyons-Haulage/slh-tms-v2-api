using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;

namespace Slh.Tms.Api.Services;

public sealed record RouteTravelEstimate(TimeSpan TravelTime, bool IsApproximate, string Provider)
{
    public string Source => IsApproximate ? "Estimated" : "Live";
}

public sealed class AzureMapsRouteClient(HttpClient client, IConfiguration configuration, ILogger<AzureMapsRouteClient> logger)
{
    private static readonly TokenRequestContext TokenContext = new(["https://atlas.microsoft.com/.default"]);
    private static readonly Regex UkPostcode = new(@"\b(GIR\s?0AA|(?:[A-Z]{1,2}\d[A-Z\d]?|[A-Z]{1,2}\d{1,2})\s?\d[A-Z]{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<object> Directions(IReadOnlyList<(decimal Longitude, decimal Latitude)> points, CancellationToken ct)
    {
        if (points.Count < 2) throw new ArgumentException("At least two mapped stops are required.");
        try
        {
            var token = await new DefaultAzureCredential().GetTokenAsync(TokenContext, ct);
            // Azure Maps Route v1 expects latitude,longitude. The TMS tuple is deliberately
            // Longitude,Latitude, so reverse it when building the provider query.
            var query = string.Join(':', points.Select(point => $"{point.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},{point.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
            var endpoint = configuration["Maps:Endpoint"] ?? "https://atlas.microsoft.com";
            var routeUrl = $"{endpoint.TrimEnd('/')}/route/directions/json?api-version=1.0&routeType=fastest&traffic=true&travelMode=truck&vehicleCommercial=true&query={Uri.EscapeDataString(query)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, routeUrl);
            ApplyEntraHeaders(request, token);
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Azure Maps routing unavailable; returning resilient approximate road route.");
            return ApproximateDirections(points);
        }
    }

    public async Task<object> SearchAddress(string address, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("An address is required.");
        var postcode = ExtractPostcode(address);
        if (!string.IsNullOrWhiteSpace(postcode))
        {
            var postcodeResult = await SearchPostcode(postcode, ct);
            if (postcodeResult is not null) return postcodeResult;
        }

        try
        {
            var token = await new DefaultAzureCredential().GetTokenAsync(TokenContext, ct);
            var endpoint = configuration["Maps:Endpoint"] ?? "https://atlas.microsoft.com";
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/search/address/json?api-version=1.0&limit=1&countrySet=GB&query={Uri.EscapeDataString(address.Trim())}");
            ApplyEntraHeaders(request, token);
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Azure Maps address lookup failed for {Address}.", address);
            return new { results = Array.Empty<object>(), source = "Unavailable", query = address.Trim() };
        }
    }

    public async Task<(decimal Latitude, decimal Longitude)?> SearchCoordinate(string address, CancellationToken ct)
    {
        var result = await SearchAddress(address, ct);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        if (!document.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0 ||
            !results[0].TryGetProperty("position", out var position) ||
            !position.TryGetProperty("lat", out var latitude) || !position.TryGetProperty("lon", out var longitude) ||
            !latitude.TryGetDecimal(out var lat) || !longitude.TryGetDecimal(out var lon)) return null;
        return (lat, lon);
    }

    public async Task<RouteTravelEstimate> TravelTimeEstimate((decimal Longitude, decimal Latitude) from, (decimal Longitude, decimal Latitude) to, CancellationToken ct)
    {
        var result = await Directions([from, to], ct);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        if (!document.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0 ||
            !routes[0].TryGetProperty("summary", out var summary) || !summary.TryGetProperty("travelTimeInSeconds", out var seconds))
            throw new InvalidOperationException("Routing did not return a travel time.");

        var approximate = document.RootElement.TryGetProperty("approximate", out var approximateValue) && approximateValue.ValueKind == JsonValueKind.True;
        var provider = document.RootElement.TryGetProperty("source", out var sourceValue) && sourceValue.ValueKind == JsonValueKind.String
            ? sourceValue.GetString() ?? "Azure Maps"
            : "Azure Maps live traffic truck route";
        return new RouteTravelEstimate(TimeSpan.FromSeconds(seconds.GetDouble()), approximate, provider);
    }

    public async Task<TimeSpan> TravelTime((decimal Longitude, decimal Latitude) from, (decimal Longitude, decimal Latitude) to, CancellationToken ct)
        => (await TravelTimeEstimate(from, to, ct)).TravelTime;

    private void ApplyEntraHeaders(HttpRequestMessage request, AccessToken token)
    {
        var mapsClientId = configuration["Maps:ClientId"];
        if (string.IsNullOrWhiteSpace(mapsClientId))
            throw new InvalidOperationException("Azure Maps account client ID is not configured.");
        request.Headers.Authorization = new("Bearer", token.Token);
        request.Headers.TryAddWithoutValidation("x-ms-client-id", mapsClientId.Trim());
    }

    private async Task<object?> SearchPostcode(string postcode, CancellationToken ct)
    {
        try
        {
            var compact = Regex.Replace(postcode.ToUpperInvariant(), "\\s+", string.Empty);
            using var response = await client.GetAsync($"https://api.postcodes.io/postcodes/{Uri.EscapeDataString(compact)}", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind == JsonValueKind.Null ||
                !result.TryGetProperty("latitude", out var latitude) || !result.TryGetProperty("longitude", out var longitude) ||
                !latitude.TryGetDecimal(out var lat) || !longitude.TryGetDecimal(out var lon)) return null;
            return new
            {
                results = new[] { new { position = new { lat, lon }, address = new { freeformAddress = postcode.ToUpperInvariant() } } },
                source = "UK postcode"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Postcode lookup failed for {Postcode}.", postcode);
            return null;
        }
    }

    private static string? ExtractPostcode(string value)
    {
        var match = UkPostcode.Match(value.ToUpperInvariant());
        if (!match.Success) return null;
        var compact = Regex.Replace(match.Value, "\\s+", string.Empty);
        return compact.Length > 3 ? $"{compact[..^3]} {compact[^3..]}" : compact;
    }

    private static object ApproximateDirections(IReadOnlyList<(decimal Longitude, decimal Latitude)> points)
    {
        double totalMiles = 0;
        for (var index = 1; index < points.Count; index++) totalMiles += HaversineMiles(points[index - 1], points[index]) * 1.18;
        var metres = (int)Math.Round(totalMiles * 1609.344);
        var seconds = (int)Math.Round(totalMiles / 45d * 3600d);
        var legs = Enumerable.Range(1, points.Count - 1).Select(index => new
        {
            points = new[]
            {
                new { latitude = points[index - 1].Latitude, longitude = points[index - 1].Longitude },
                new { latitude = points[index].Latitude, longitude = points[index].Longitude }
            }
        }).ToArray();
        return new
        {
            routes = new[] { new { summary = new { lengthInMeters = metres, travelTimeInSeconds = seconds }, legs } },
            approximate = true,
            source = "Resilient road estimate"
        };
    }

    private static double HaversineMiles((decimal Longitude, decimal Latitude) a, (decimal Longitude, decimal Latitude) b)
    {
        const double radius = 3958.7613;
        static double Rad(double value) => value * Math.PI / 180d;
        var lat1 = Rad((double)a.Latitude); var lat2 = Rad((double)b.Latitude);
        var dLat = lat2 - lat1; var dLon = Rad((double)(b.Longitude - a.Longitude));
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * radius * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}
