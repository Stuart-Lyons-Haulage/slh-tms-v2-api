using System.Text;
using System.Text.Json;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Free, keyless UK postcode geocoder (api.postcodes.io). Turns a postcode into
/// WGS84 coordinates so Site and Depot records can carry latitude/longitude.
/// Chosen over a keyed map provider because the TMS only needs UK postcode
/// centroids and must not depend on Azure Maps. Unknown or malformed postcodes
/// resolve to null rather than throwing, so a backfill degrades to "left blank"
/// instead of failing the whole batch.
/// </summary>
public sealed class PostcodesIoClient(HttpClient httpClient, ILogger<PostcodesIoClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Looks up a single postcode. Returns null when it is blank, malformed, or unknown to postcodes.io.</summary>
    public async Task<GeoPoint?> GeocodeAsync(string? postcode, CancellationToken ct)
    {
        var normalised = NormalisePostcode(postcode);
        if (normalised is null) return null;
        try
        {
            using var response = await httpClient.GetAsync($"postcodes/{Uri.EscapeDataString(normalised)}", ct);
            if (!response.IsSuccessStatusCode) return null; // 404 = unknown postcode
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var payload = await JsonSerializer.DeserializeAsync<SingleResponse>(stream, Json, ct);
            return ToPoint(payload?.Result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "postcodes.io lookup failed for {Postcode}", normalised);
            return null;
        }
    }

    /// <summary>
    /// Bulk geocode. Returns a map keyed by the normalised postcode; a postcode that
    /// could not be resolved is simply absent from the map. postcodes.io caps a bulk
    /// request at 100 postcodes, so the input is sent in chunks.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, GeoPoint>> BulkGeocodeAsync(IEnumerable<string?> postcodes, CancellationToken ct)
    {
        var distinct = postcodes.Select(NormalisePostcode).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var results = new Dictionary<string, GeoPoint>(StringComparer.Ordinal);
        foreach (var chunk in distinct.Chunk(100))
        {
            var body = JsonSerializer.Serialize(new { postcodes = chunk }, Json);
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync("postcodes", content, ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("postcodes.io bulk lookup returned {Status} for {Count} postcodes", (int)response.StatusCode, chunk.Length);
                    continue;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var payload = await JsonSerializer.DeserializeAsync<BulkResponse>(stream, Json, ct);
                foreach (var item in payload?.Result ?? [])
                {
                    var key = NormalisePostcode(item.Query);
                    var point = ToPoint(item.Result);
                    if (key is not null && point is not null)
                        results[key] = point.Value;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "postcodes.io bulk lookup failed for {Count} postcodes", chunk.Length);
            }
        }
        return results;
    }

    /// <summary>
    /// Normalises to the canonical uppercase single-spaced form postcodes.io echoes
    /// back (e.g. "po22 0aq" -> "PO22 0AQ"). The inward code is always the final three
    /// characters of a UK postcode, so the space is inserted before them.
    /// </summary>
    public static string? NormalisePostcode(string? postcode)
    {
        if (string.IsNullOrWhiteSpace(postcode)) return null;
        var compact = new string(postcode.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (compact.Length is < 5 or > 7) return null;
        return $"{compact[..^3]} {compact[^3..]}";
    }

    private static GeoPoint? ToPoint(PostcodeResult? result) =>
        result is { Latitude: { } lat, Longitude: { } lng } ? new GeoPoint((decimal)lat, (decimal)lng) : null;

    public readonly record struct GeoPoint(decimal Latitude, decimal Longitude);

    private sealed record SingleResponse(PostcodeResult? Result);
    private sealed record BulkResponse(List<BulkItem>? Result);
    private sealed record BulkItem(string? Query, PostcodeResult? Result);
    private sealed record PostcodeResult(double? Latitude, double? Longitude);
}
