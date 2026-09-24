using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Slh.Tms.Api.Models.Integrations;

namespace Slh.Tms.Api.Services;

public sealed class SamsaraClient(HttpClient httpClient, SamsaraOptions options, ILogger<SamsaraClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => options.IsConfigured;
    public string[] MissingSettings => options.MissingSettings;
    public string ExternalIdKey => options.ExternalIdKey;
    public int StopRadiusMeters => Math.Clamp(options.StopRadiusMeters, 50, 5000);

    public async Task<SamsaraConnectionSummary> GetConnectionSummaryAsync(CancellationToken ct)
    {
        EnsureConfigured();
        var vehicles = await GetVehiclesAsync(ct);
        var drivers = await GetDriversAsync(ct);
        return new SamsaraConnectionSummary(true, vehicles.Count, drivers.Count);
    }

    public Task<IReadOnlyList<SamsaraVehicle>> GetVehiclesAsync(CancellationToken ct) =>
        ReadPagedAsync("fleet/vehicles", ParseVehicle, ct);

    public Task<IReadOnlyList<SamsaraDriver>> GetDriversAsync(CancellationToken ct) =>
        ReadPagedAsync("fleet/drivers", ParseDriver, ct);

    public async Task<SamsaraRouteSnapshot?> GetRouteByRunIdAsync(Guid runId, CancellationToken ct)
    {
        EnsureConfigured();
        var external = ExternalRouteId(runId);
        using var request = CreateRequest(HttpMethod.Get, $"fleet/routes/{Uri.EscapeDataString(external)}");
        using var response = await httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body, "route lookup");
        return ParseRoute(body);
    }

    public async Task<SamsaraRouteUpsertResult> UpsertRouteAsync(SamsaraRouteRequest route, CancellationToken ct)
    {
        EnsureConfigured();
        var external = ExternalRouteId(route.RunId);
        var existing = await GetRouteByRunIdAsync(route.RunId, ct);
        var payload = RoutePayload(route, existing);

        if (existing is not null)
        {
            using var patch = CreateRequest(HttpMethod.Patch, $"fleet/routes/{Uri.EscapeDataString(external)}", payload);
            using var patchResponse = await httpClient.SendAsync(patch, ct);
            var patchBody = await patchResponse.Content.ReadAsStringAsync(ct);
            if (patchResponse.StatusCode != HttpStatusCode.NotFound)
            {
                EnsureSuccess(patchResponse, patchBody, "route update");
                var updated = ParseRoute(patchBody);
                return new SamsaraRouteUpsertResult(updated?.Id ?? existing.Id, external, false, true, updated ?? existing);
            }

            logger.LogWarning("Samsara route {ExternalId} disappeared between lookup and update; recreating it.", external);
        }

        using var create = CreateRequest(HttpMethod.Post, "fleet/routes", RoutePayload(route, null));
        using var createResponse = await httpClient.SendAsync(create, ct);
        var createBody = await createResponse.Content.ReadAsStringAsync(ct);
        EnsureSuccess(createResponse, createBody, "route create");
        var created = ParseRoute(createBody);
        return new SamsaraRouteUpsertResult(created?.Id, external, true, false, created);
    }

    private async Task<IReadOnlyList<T>> ReadPagedAsync<T>(
        string resource,
        Func<JsonElement, T?> parser,
        CancellationToken ct) where T : class
    {
        EnsureConfigured();
        var result = new List<T>();
        string? after = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 0; page < 20; page++)
        {
            var path = $"{resource}?limit=512";
            if (!string.IsNullOrWhiteSpace(after))
                path += $"&after={Uri.EscapeDataString(after)}";

            using var request = CreateRequest(HttpMethod.Get, path);
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            EnsureSuccess(response, body, resource);

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    var parsed = parser(item);
                    if (parsed is not null) result.Add(parsed);
                }
            }

            if (!root.TryGetProperty("pagination", out var pagination) || pagination.ValueKind != JsonValueKind.Object)
                break;

            var hasNext = pagination.TryGetProperty("hasNextPage", out var hasNextNode) &&
                          hasNextNode.ValueKind is JsonValueKind.True;
            var next = pagination.TryGetProperty("endCursor", out var cursorNode) &&
                       cursorNode.ValueKind == JsonValueKind.String
                ? cursorNode.GetString()
                : null;
            if (!hasNext || string.IsNullOrWhiteSpace(next) || !seen.Add(next))
                break;
            after = next;
        }

        return result;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? payload = null)
    {
        var request = new HttpRequestMessage(method, $"{options.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        return request;
    }

    private object RoutePayload(SamsaraRouteRequest route, SamsaraRouteSnapshot? existing)
    {
        var externalIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [options.ExternalIdKey] = route.RunId.ToString("N")
        };

        var stopExternalIdKey = $"{options.ExternalIdKey}Stop";
        var stops = route.Stops.Select(stop =>
        {
            var stopExternalValue = stop.StopId.ToString("N");
            var existingStop = existing?.Stops.FirstOrDefault(item =>
                item.ExternalIds.TryGetValue(stopExternalIdKey, out var value) &&
                string.Equals(value, stopExternalValue, StringComparison.OrdinalIgnoreCase));

            var payload = new Dictionary<string, object?>
            {
                ["externalIds"] = new Dictionary<string, string>
                {
                    [stopExternalIdKey] = stopExternalValue
                },
                ["singleUseLocation"] = new
                {
                    address = stop.Address,
                    latitude = stop.Latitude,
                    longitude = stop.Longitude,
                    radiusMeters = stop.RadiusMeters
                },
                ["notes"] = string.IsNullOrWhiteSpace(stop.Notes) ? null : Clip(stop.Notes, 2000)
            };
            if (!string.IsNullOrWhiteSpace(existingStop?.Id))
                payload["id"] = existingStop.Id;
            if (stop.ScheduledArrivalTime is not null)
                payload["scheduledArrivalTime"] = stop.ScheduledArrivalTime.Value.UtcDateTime.ToString("O");
            if (stop.ScheduledDepartureTime is not null)
                payload["scheduledDepartureTime"] = stop.ScheduledDepartureTime.Value.UtcDateTime.ToString("O");
            return payload;
        }).ToList();

        var body = new Dictionary<string, object?>
        {
            ["name"] = Clip(route.Name, 200),
            ["notes"] = Clip(route.Notes, 2000),
            ["externalIds"] = externalIds,
            ["recomputeScheduledTimes"] = true,
            ["stops"] = stops
        };

        if (!string.IsNullOrWhiteSpace(route.DriverId))
            body["driverId"] = route.DriverId;
        else if (!string.IsNullOrWhiteSpace(route.VehicleId))
            body["vehicleId"] = route.VehicleId;

        return body;
    }

    private string ExternalRouteId(Guid runId) => $"{options.ExternalIdKey}:{runId:N}";

    private static SamsaraVehicle? ParseVehicle(JsonElement item)
    {
        var id = Text(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new SamsaraVehicle(
            id,
            Text(item, "name"),
            Text(item, "licensePlate"),
            Text(item, "vin"),
            ExternalIds(item));
    }

    private static SamsaraDriver? ParseDriver(JsonElement item)
    {
        var id = Text(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new SamsaraDriver(
            id,
            Text(item, "name"),
            Text(item, "username"),
            Text(item, "licenseNumber"),
            ExternalIds(item));
    }

    private static SamsaraRouteSnapshot? ParseRoute(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var route = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;
        if (route.ValueKind != JsonValueKind.Object) return null;
        var stops = route.TryGetProperty("stops", out var stopArray) && stopArray.ValueKind == JsonValueKind.Array
            ? stopArray.EnumerateArray()
                .Select(stop => new SamsaraRouteStopSnapshot(
                    Text(stop, "id"),
                    ExternalIds(stop)))
                .ToList()
            : [];
        return new SamsaraRouteSnapshot(
            Text(route, "id"),
            Text(route, "name"),
            Text(route, "driverId"),
            Text(route, "vehicleId"),
            ExternalIds(route),
            stops);
    }

    private static IReadOnlyDictionary<string, string> ExternalIds(JsonElement item)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!item.TryGetProperty("externalIds", out var external) || external.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in external.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                result[property.Name] = property.Value.GetString()!;
        return result;
    }

    private static string? Text(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static string? Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = body.Length > 1200 ? body[..1200] : body;
        throw new HttpRequestException(
            $"Samsara {operation} returned {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}",
            null,
            response.StatusCode);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException($"Samsara runtime settings are incomplete: {string.Join(", ", MissingSettings)}.");
    }
}

public sealed record SamsaraConnectionSummary(bool Connected, int VehicleCount, int DriverCount);
public sealed record SamsaraVehicle(string Id, string? Name, string? LicensePlate, string? Vin, IReadOnlyDictionary<string, string> ExternalIds);
public sealed record SamsaraDriver(string Id, string? Name, string? Username, string? LicenseNumber, IReadOnlyDictionary<string, string> ExternalIds);
public sealed record SamsaraRouteStopRequest(
    Guid StopId,
    string Address,
    double Latitude,
    double Longitude,
    int RadiusMeters,
    DateTimeOffset? ScheduledArrivalTime,
    DateTimeOffset? ScheduledDepartureTime,
    string? Notes);
public sealed record SamsaraRouteRequest(
    Guid RunId,
    string Name,
    string? Notes,
    string? DriverId,
    string? VehicleId,
    IReadOnlyList<SamsaraRouteStopRequest> Stops);
public sealed record SamsaraRouteSnapshot(
    string? Id,
    string? Name,
    string? DriverId,
    string? VehicleId,
    IReadOnlyDictionary<string, string> ExternalIds,
    IReadOnlyList<SamsaraRouteStopSnapshot> Stops);
public sealed record SamsaraRouteStopSnapshot(
    string? Id,
    IReadOnlyDictionary<string, string> ExternalIds);
public sealed record SamsaraRouteUpsertResult(
    string? RouteId,
    string ExternalId,
    bool Created,
    bool Updated,
    SamsaraRouteSnapshot? Route);
