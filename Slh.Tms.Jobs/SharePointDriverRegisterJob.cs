using Azure.Core;
using Azure.Identity;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Jobs;

/// <summary>
/// Optional projection from the authoritative TMS driver table into the SLH Driver Register.
/// Provider data remains owned by TMS SQL; this job never calls Sage or TachoMaster directly.
/// </summary>
public sealed class SharePointDriverRegisterJob(
    TmsDbContext db,
    IConfiguration configuration,
    IHttpClientFactory clients,
    ILogger<SharePointDriverRegisterJob> logger)
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";

    public async Task<JobExecutionResult> RunAsync(CancellationToken ct)
    {
        if (!bool.TryParse(configuration["SharePoint:DriverRegister:Enabled"], out var enabled) || !enabled)
            return new JobExecutionResult(true, "SharePoint driver-register sync is disabled.");

        var managedIdentityClientId = Required("SharePoint:DriverRegister:ManagedIdentityClientId");
        var siteId = Required("SharePoint:DriverRegister:SiteId");
        var listId = Required("SharePoint:DriverRegister:ListId");
        var employeeField = configuration["SharePoint:DriverRegister:EmployeeNumberField"] ?? "Employee_x0020_Number";
        var statusField = configuration["SharePoint:DriverRegister:DriverStatusField"] ?? "Driver_x0020_Status";
        var activeLabel = configuration["SharePoint:DriverRegister:ActiveStatusValue"] ?? "Active";
        var inactiveLabel = configuration["SharePoint:DriverRegister:InactiveStatusValue"] ?? "Inactive";

        var drivers = await db.Drivers.AsNoTracking()
            .OrderBy(driver => driver.EmployeeNumber)
            .ToListAsync(ct);
        if (drivers.Count == 0)
            return new JobExecutionResult(false, "TMS returned no driver records; SharePoint was not changed.");

        var http = clients.CreateClient(nameof(SharePointDriverRegisterJob));
        var credential = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId));
        var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), ct);
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

        // Fail closed if the SharePoint internal names differ from the configured mapping.
        await EnsureColumnsAsync(http, siteId, listId, employeeField, statusField, ct);
        var listItems = await ReadListItemsAsync(http, siteId, listId, ct);
        var indexed = listItems
            .Select(item => new ListMatch(item.Id, ReadField(item.Fields, employeeField)))
            .Where(item => !string.IsNullOrWhiteSpace(item.EmployeeNumber))
            .GroupBy(item => Normalise(item.EmployeeNumber), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var updated = 0;
        var skipped = 0;
        foreach (var driver in drivers)
        {
            ct.ThrowIfCancellationRequested();
            var employeeNumber = driver.EmployeeNumber?.Trim();
            var displayName = driver.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(employeeNumber) || string.IsNullOrWhiteSpace(displayName))
            {
                skipped++;
                continue;
            }

            var key = Normalise(employeeNumber);
            var fields = new Dictionary<string, object?>
            {
                ["Title"] = displayName,
                [employeeField] = employeeNumber,
                [statusField] = driver.Active ? activeLabel : inactiveLabel
            };

            if (indexed.TryGetValue(key, out var matches))
            {
                if (matches.Count != 1)
                {
                    skipped++;
                    logger.LogWarning("SharePoint driver sync skipped EmployeeNumber={EmployeeNumber}: {Count} matching rows exist.", employeeNumber, matches.Count);
                    continue;
                }

                await PatchFieldsAsync(http, siteId, listId, matches[0].Id, fields, ct);
                updated++;
            }
            else
            {
                var itemId = await CreateItemAsync(http, siteId, listId, fields, ct);
                indexed[key] = [new ListMatch(itemId, employeeNumber)];
                created++;
            }
        }

        var message = $"SharePoint driver register sync completed: {created} created, {updated} updated, {skipped} skipped. Work email and other SharePoint-owned fields were left unchanged.";
        logger.LogInformation("{Message}", message);
        return new JobExecutionResult(true, message, created + updated);
    }

    private string Required(string key) =>
        string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException($"Required setting {key} is missing.")
            : configuration[key]!.Trim();

    private static async Task EnsureColumnsAsync(HttpClient http, string siteId, string listId, string employeeField, string statusField, CancellationToken ct)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Title", employeeField, statusField };
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var next = $"{GraphRoot}/sites/{Uri.EscapeDataString(siteId)}/lists/{Uri.EscapeDataString(listId)}/columns?$top=200";
        while (!string.IsNullOrWhiteSpace(next))
        {
            using var response = await http.GetAsync(next, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            EnsureSuccess(response, body, "read SharePoint driver-register columns");
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
                foreach (var column in values.EnumerateArray())
                    if (column.TryGetProperty("name", out var name) && name.GetString() is { } internalName)
                        found.Add(internalName);
            next = document.RootElement.TryGetProperty("@odata.nextLink", out var nextValue) ? nextValue.GetString() : null;
        }

        var missing = required.Where(name => !found.Contains(name)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"SharePoint driver register is missing configured columns: {string.Join(", ", missing)}. No list items were changed.");
    }

    private static async Task<List<ListItem>> ReadListItemsAsync(HttpClient http, string siteId, string listId, CancellationToken ct)
    {
        var result = new List<ListItem>();
        var next = $"{GraphRoot}/sites/{Uri.EscapeDataString(siteId)}/lists/{Uri.EscapeDataString(listId)}/items?$expand=fields&$top=200";
        while (!string.IsNullOrWhiteSpace(next))
        {
            using var response = await http.GetAsync(next, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            EnsureSuccess(response, body, "read SharePoint driver register");
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in value.EnumerateArray())
                {
                    var id = row.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                    var fields = row.TryGetProperty("fields", out var fieldObject) && fieldObject.ValueKind == JsonValueKind.Object
                        ? fieldObject.EnumerateObject().ToDictionary(pair => pair.Name, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(id)) result.Add(new ListItem(id!, fields));
                }
            }
            next = document.RootElement.TryGetProperty("@odata.nextLink", out var nextValue) ? nextValue.GetString() : null;
        }
        return result;
    }

    private static async Task<string> CreateItemAsync(HttpClient http, string siteId, string listId, Dictionary<string, object?> fields, CancellationToken ct)
    {
        var url = $"{GraphRoot}/sites/{Uri.EscapeDataString(siteId)}/lists/{Uri.EscapeDataString(listId)}/items";
        using var response = await http.PostAsJsonAsync(url, new { fields }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body, "create SharePoint driver register item");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("id", out var id)
            ? id.GetString() ?? throw new InvalidOperationException("SharePoint returned an empty list item ID.")
            : throw new InvalidOperationException("SharePoint create response did not contain an item ID.");
    }

    private static async Task PatchFieldsAsync(HttpClient http, string siteId, string listId, string itemId, Dictionary<string, object?> fields, CancellationToken ct)
    {
        var url = $"{GraphRoot}/sites/{Uri.EscapeDataString(siteId)}/lists/{Uri.EscapeDataString(listId)}/items/{Uri.EscapeDataString(itemId)}/fields";
        using var response = await http.PatchAsJsonAsync(url, fields, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body, "update SharePoint driver register item");
    }

    private static string? ReadField(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ToString()
            : null;

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Microsoft Graph could not {operation}: HTTP {(int)response.StatusCode}. {Clip(body)}", null, response.StatusCode);
    }

    private static string Clip(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Length <= 300 ? value.Trim() : value.Trim()[..300];

    private sealed record ListItem(string Id, Dictionary<string, JsonElement> Fields);
    private sealed record ListMatch(string Id, string? EmployeeNumber);
}
