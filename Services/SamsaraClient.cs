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
    public string StopExternalIdKey => options.StopExternalIdKey;
    public string SiteExternalIdKey => options.SiteExternalIdKey;
    public bool AddressSyncEnabled => options.EnableAddressSync;
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

    public async Task<bool> DeleteRouteByRunIdAsync(Guid runId, CancellationToken ct)
    {
        EnsureConfigured();
        using var request = CreateRequest(HttpMethod.Delete, $"fleet/routes/{Uri.EscapeDataString(ExternalRouteId(runId))}");
        using var response = await httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body, "route delete");
        return true;
    }

    public async Task<SamsaraAddressSnapshot?> GetAddressBySiteIdAsync(Guid siteId, CancellationToken ct)
    {
        EnsureConfigured();
        using var request = CreateRequest(HttpMethod.Get, $"addresses/{Uri.EscapeDataString(ExternalSiteId(siteId))}");
        using var response = await httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body, "address lookup");
        return ParseAddress(body);
    }

    public async Task<SamsaraAddressUpsertResult> UpsertAddressAsync(SamsaraAddressRequest address, CancellationToken ct)
    {
        EnsureConfigured();
        var external = ExternalSiteId(address.SiteId);
        var existing = await GetAddressBySiteIdAsync(address.SiteId, ct);
        var payload = AddressPayload(address);

        if (existing is not null)
        {
            using var patch = CreateRequest(HttpMethod.Patch, $"addresses/{Uri.EscapeDataString(external)}", payload);
            using var patchResponse = await httpClient.SendAsync(patch, ct);
            var patchBody = await patchResponse.Content.ReadAsStringAsync(ct);
            if (patchResponse.StatusCode != HttpStatusCode.NotFound)
            {
                EnsureSuccess(patchResponse, patchBody, "address update");
                var updated = ParseAddress(patchBody);
                return new SamsaraAddressUpsertResult(updated?.Id ?? existing.Id, external, false, true, updated ?? existing);
            }

            logger.LogWarning("Samsara address {ExternalId} disappeared between lookup and update; recreating it.", external);
        }

        using var create = CreateRequest(HttpMethod.Post, "addresses", payload);
        using var createResponse = await httpClient.SendAsync(create, ct);
        var createBody = await createResponse.Content.ReadAsStringAsync(ct);
        EnsureSuccess(createResponse, createBody, "address create");
        var created = ParseAddress(createBody);
        return new SamsaraAddressUpsertResult(created?.Id, external, true, false, created);
    }

    public async Task<SamsaraRouteAuditFeed> GetRouteAuditFeedAsync(string? after, CancellationToken ct)
    {
        EnsureConfigured();
        var path = "fleet/routes/audit-logs/feed";
        if (!string.IsNullOrWhiteSpace(after))
            path += $"?after={Uri.EscapeDataString(after)}";

        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body, "route audit feed");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var entries = new List<SamsaraRouteAuditEntry>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                entries.Add(new SamsaraRouteAuditEntry(
                    Text(item, "id"),
                    Text(item, "operation"),
                    ParseDate(item, "createdAtTime") ?? ParseDate(item, "time"),
                    item.GetRawText()));
            }
        }

        string? cursor = null;
        var hasNext = false;
        if (root.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Object)
        {
            cursor = Text(pagination, "endCursor");
            hasNext = pagination.TryGetProperty("hasNextPage", out var hasNextNode) &&
                      hasNextNode.ValueKind == JsonValueKind.True;
        }

        return new SamsaraRouteAuditFeed(entries, cursor, hasNext);
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
                          hasNextNode.ValueKind == JsonValueKind.True;
            var next = Text(pagination, "endCursor");
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

        var stops = route.Stops.Select(stop =>
        {
            var stopExternalValue = stop.StopId.ToString("N");
            var existingStop = existing?.Stops.FirstOrDefault(item =>
                item.ExternalIds.TryGetValue(options.StopExternalIdKey, out var value) &&
                string.Equals(value, stopExternalValue, StringComparison.OrdinalIgnoreCase));

            var payload = new Dictionary<string, object?>
            {
                ["externalIds"] = new Dictionary<string, string>
                {
                    [options.StopExternalIdKey] = stopExternalValue
                },
                ["notes"] = string.IsNullOrWhiteSpace(stop.Notes) ? null : Clip(stop.Notes, 2000),
                ["sequenceNumber"] = stop.SequenceNumber
            };

            if (!string.IsNullOrWhiteSpace(existingStop?.Id))
                payload["id"] = existingStop.Id;

            if (!string.IsNullOrWhiteSpace(stop.AddressId))
            {
                payload["addressId"] = stop.AddressId;
            }
            else
            {
                payload["singleUseLocation"] = new
                {
                    address = stop.Address,
                    latitude = stop.Latitude,
                    longitude = stop.Longitude,
                    radiusMeters = stop.RadiusMeters
                };
            }

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
            ["recomputeScheduledTimes"] = options.RecomputeScheduledTimes,
            ["settings"] = new
            {
                routeStartingCondition = NormaliseStartingCondition(options.RouteStartingCondition),
                routeCompletionCondition = NormaliseCompletionCondition(options.RouteCompletionCondition),
                sequencingMethod = NormaliseSequencingMethod(options.SequencingMethod)
            },
            ["stops"] = stops
        };

        // Samsara permits a route to be assigned to a driver OR a vehicle, never both.
        // The TMS still retains both allocations; driver assignment is preferred.
        if (!string.IsNullOrWhiteSpace(route.DriverId))
            body["driverId"] = route.DriverId;
        else if (!string.IsNullOrWhiteSpace(route.VehicleId))
            body["vehicleId"] = route.VehicleId;

        return body;
    }

    private object AddressPayload(SamsaraAddressRequest address)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = Clip(address.Name, 200),
            ["formattedAddress"] = Clip(address.FormattedAddress, 500),
            ["externalIds"] = new Dictionary<string, string>
            {
                [options.SiteExternalIdKey] = address.SiteId.ToString("N")
            },
            ["geofence"] = new
            {
                circle = new
                {
                    radiusMeters = Math.Clamp(address.RadiusMeters, 50, 5000)
                }
            }
        };

        if (address.Latitude is not null) body["latitude"] = address.Latitude.Value;
        if (address.Longitude is not null) body["longitude"] = address.Longitude.Value;
        return body;
    }

    private string ExternalRouteId(Guid runId) => $"{options.ExternalIdKey}:{runId:N}";
    private string ExternalSiteId(Guid siteId) => $"{options.SiteExternalIdKey}:{siteId:N}";

    private static string NormaliseStartingCondition(string? value) =>
        string.Equals(value, "arriveFirstStop", StringComparison.OrdinalIgnoreCase)
            ? "arriveFirstStop"
            : "departFirstStop";

    private static string NormaliseCompletionCondition(string? value) =>
        string.Equals(value, "arriveLastStop", StringComparison.OrdinalIgnoreCase)
            ? "arriveLastStop"
            : "departLastStop";

    private static string NormaliseSequencingMethod(string? value) =>
        string.Equals(value, "scheduledArrivalTime", StringComparison.OrdinalIgnoreCase)
            ? "scheduledArrivalTime"
            : "manual";

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

    private static SamsaraAddressSnapshot? ParseAddress(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var address = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;
        if (address.ValueKind != JsonValueKind.Object) return null;

        return new SamsaraAddressSnapshot(
            Text(address, "id"),
            Text(address, "name"),
            Text(address, "formattedAddress"),
            Number(address, "latitude"),
            Number(address, "longitude"),
            ExternalIds(address));
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
                    ExternalIds(stop),
                    Integer(stop, "sequenceNumber"),
                    Text(stop, "name"),
                    Text(stop, "state"),
                    Text(stop, "liveSharingUrl"),
                    ParseDate(stop, "scheduledArrivalTime"),
                    ParseDate(stop, "scheduledDepartureTime"),
                    ParseDate(stop, "actualArrivalTime"),
                    ParseDate(stop, "actualDepartureTime"),
                    ParseDate(stop, "eta") ?? ParseDate(stop, "estimatedArrivalTime"),
                    NestedText(stop, "address", "id")))
                .ToList()
            : [];

        return new SamsaraRouteSnapshot(
            Text(route, "id"),
            Text(route, "name"),
            Text(route, "driverId") ?? NestedText(route, "driver", "id"),
            Text(route, "vehicleId") ?? NestedText(route, "vehicle", "id"),
            ExternalIds(route),
            stops,
            ParseDate(route, "scheduledRouteStartTime"),
            ParseDate(route, "scheduledRouteEndTime"));
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

    private static string? NestedText(JsonElement item, string objectName, string propertyName) =>
        item.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? Text(nested, propertyName)
            : null;

    private static double? Number(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetDouble(out var number) ? number : null;
    }

    private static long? Integer(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetInt64(out var number) ? number : null;
    }

    private static DateTimeOffset? ParseDate(JsonElement item, string propertyName)
    {
        var value = Text(item, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

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

public sealed record SamsaraAddressRequest(
    Guid SiteId,
    string Name,
    string FormattedAddress,
    double? Latitude,
    double? Longitude,
    int RadiusMeters);

public sealed record SamsaraAddressSnapshot(
    string? Id,
    string? Name,
    string? FormattedAddress,
    double? Latitude,
    double? Longitude,
    IReadOnlyDictionary<string, string> ExternalIds);

public sealed record SamsaraAddressUpsertResult(
    string? AddressId,
    string ExternalId,
    bool Created,
    bool Updated,
    SamsaraAddressSnapshot? Address);

public sealed record SamsaraRouteStopRequest(
    Guid StopId,
    int SequenceNumber,
    string? AddressId,
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
    IReadOnlyList<SamsaraRouteStopSnapshot> Stops,
    DateTimeOffset? ScheduledRouteStartTime,
    DateTimeOffset? ScheduledRouteEndTime);

public sealed record SamsaraRouteStopSnapshot(
    string? Id,
    IReadOnlyDictionary<string, string> ExternalIds,
    long? SequenceNumber,
    string? Name,
    string? State,
    string? LiveSharingUrl,
    DateTimeOffset? ScheduledArrivalTime,
    DateTimeOffset? ScheduledDepartureTime,
    DateTimeOffset? ActualArrivalTime,
    DateTimeOffset? ActualDepartureTime,
    DateTimeOffset? EstimatedArrivalTime,
    string? AddressId);

public sealed record SamsaraRouteUpsertResult(
    string? RouteId,
    string ExternalId,
    bool Created,
    bool Updated,
    SamsaraRouteSnapshot? Route);

public sealed record SamsaraRouteAuditEntry(
    string? Id,
    string? Operation,
    DateTimeOffset? OccurredAtUtc,
    string RawJson);

public sealed record SamsaraRouteAuditFeed(
    IReadOnlyList<SamsaraRouteAuditEntry> Entries,
    string? EndCursor,
    bool HasNextPage);
