using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Moves approved order evidence to the operational SharePoint archive.  The SQL copy is only
/// trimmed after every non-inline attachment and the manifest have been accepted by Graph.
/// </summary>
public sealed class SharePointOrderArchiveOptions
{
    public bool Enabled { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string SiteHost { get; set; } = "stuartlyonshaulage.sharepoint.com";
    public string LibraryName { get; set; } = "Documents";
    public string ArchiveRoot { get; set; } = "TMS Order Archive";
    public bool RemoveAttachmentPayloadAfterVerifiedExport { get; set; }

    public bool IsConfigured => Enabled &&
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(SiteHost) &&
        !string.IsNullOrWhiteSpace(LibraryName) &&
        !string.IsNullOrWhiteSpace(ArchiveRoot);
}

public sealed class SharePointOrderArchiveService(
    TmsDbContext db,
    IHttpClientFactory httpClientFactory,
    SharePointOrderArchiveOptions options,
    ILogger<SharePointOrderArchiveService> logger)
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];
    /// <summary>
    /// Archives source attachments once their 72-hour SQL retention window has elapsed.
    /// The evidence row is only trimmed after every customer archive upload succeeds.
    /// </summary>
    /// <summary>Archives delivered orders whose completion time is more than 72 hours old.</summary>
    public async Task<SharePointOrderArchiveSummary> ArchiveOrdersOlderThan72Hours(CancellationToken ct)
    {
        if (!options.IsConfigured)
        {
            logger.LogInformation("SharePoint order archive is disabled or not configured.");
            return new SharePointOrderArchiveSummary(0, 0, true);
        }

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(72);
        var orders = await db.TransportOrders
            .Where(order => order.Status == OrderStatus.Delivered &&
                order.CompletedAtUtc != null && order.CompletedAtUtc <= cutoff && order.ArchivedAtUtc == null)
            .OrderBy(order => order.CompletedAtUtc)
            .Take(100)
            .ToListAsync(ct);

        var succeeded = 0;
        var failed = 0;
        foreach (var order in orders)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var client = await CreateGraphClientAsync(ct);
                var driveId = await ResolveDriveIdAsync(client, ct);
                var year = (order.CompletedAtUtc ?? order.CreatedAtUtc).UtcDateTime.ToString("yyyy", CultureInfo.InvariantCulture);
                var month = (order.CompletedAtUtc ?? order.CreatedAtUtc).UtcDateTime.ToString("MM", CultureInfo.InvariantCulture);
                var folder = await EnsureFolderAsync(client, driveId, [options.ArchiveRoot, year, month], ct);
                var uploaded = await UploadOrderToSharePoint(client, driveId, folder.Id, order, ct);
                order.ArchivedAtUtc = DateTimeOffset.UtcNow;
                order.ArchiveLocation = uploaded.WebUrl ?? $"{options.ArchiveRoot}/{year}/{month}/Order-{SafeFileName(order.Reference)}.json";
                await db.SaveChangesAsync(ct);
                succeeded++;
                logger.LogInformation("Archived order {OrderId} ({Reference}) to SharePoint at {ArchiveLocation}.", order.Id, order.Reference, order.ArchiveLocation);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failed++;
                db.Entry(order).State = EntityState.Detached;
                logger.LogError(ex, "SharePoint archive failed for delivered order {OrderId} ({Reference}); it remains in SQL for retry.", order.Id, order.Reference);
            }
        }

        logger.LogInformation("SharePoint order archive pass completed. Candidates {Candidates}, succeeded {Succeeded}, failed {Failed}.", orders.Count, succeeded, failed);
        return new SharePointOrderArchiveSummary(succeeded, failed, false);
    }

    /// <summary>Serialises and uploads one order manifest after its target folder exists.</summary>
    public async Task<DriveItem> UploadOrderToSharePoint(HttpClient client, string driveId, string folderId, TransportOrder order, CancellationToken ct)
    {
        var stops = await db.LoadStops.AsNoTracking()
            .Where(stop => stop.OrderId == order.Id)
            .OrderBy(stop => stop.Sequence)
            .Select(stop => new { stop.Sequence, stop.Name, stop.Address, stop.Latitude, stop.Longitude })
            .ToListAsync(ct);
        var payload = new
        {
            order.Id,
            order.Reference,
            order.CustomerCode,
            order.CollectionDate,
            order.DeliveryDate,
            order.CompletedAtUtc,
            order.TotalValue,
            order.ItemCount,
            order.Pallets,
            order.Status,
            order.CreatedAtUtc,
            Stops = stops
        };
        var json = JsonSerializer.Serialize(payload, JsonOptionsIndented);
        var fileName = $"Order-{SafeFileName(order.Reference)}.json";
        logger.LogDebug("Uploading order {OrderId} archive {FileName} to SharePoint.", order.Id, fileName);
        return await UploadAsync(client, driveId, folderId, fileName, Encoding.UTF8.GetBytes(json), "application/json", ct);
    }

    /// <summary>Creates each folder segment and returns the final folder.</summary>
    public async Task<DriveItem> EnsureSharePointFolder(HttpClient client, string driveId, string path, CancellationToken ct)
    {
        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return await EnsureFolderAsync(client, driveId, segments, ct);
    }

    private static readonly JsonSerializerOptions JsonOptionsIndented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<bool> ArchiveExpiredEvidenceAsync(StagedImport evidence, CancellationToken ct)
    {
        if (!options.IsConfigured || !options.RemoveAttachmentPayloadAfterVerifiedExport ||
            !string.Equals(evidence.EntityType, "email-evidence", StringComparison.OrdinalIgnoreCase)) return false;

        JsonObject source;
        try { source = JsonNode.Parse(evidence.PayloadJson)?.AsObject() ?? throw new JsonException("Evidence payload is not an object."); }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Expired email evidence {EvidenceId} could not be parsed; bytes were retained in SQL.", evidence.Id);
            return false;
        }

        var attachments = source["attachments"] as JsonArray ?? [];
        var hasBytes = attachments.OfType<JsonObject>().Any(item => !Bool(item, "isInline") && !string.IsNullOrWhiteSpace(Text(item, "contentBase64")));
        if (!hasBytes) return false;

        var messageId = Text(source, "messageId") ?? Text(source, "internetMessageId");
        var matchingOrders = await FindRelatedOrdersAsync(evidence, messageId, ct);
        if (matchingOrders.Count == 0)
        {
            logger.LogWarning("Expired email evidence {EvidenceId} has no linked order/customer; bytes were retained in SQL.", evidence.Id);
            return false;
        }

        var customerOrders = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in matchingOrders)
        {
            var customer = SafeSegment(Text(payload, "customerCode") ?? Text(payload, "customerName") ?? "Unassigned customer");
            customerOrders.TryAdd(customer, payload);
        }

        using var exportCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        exportCts.CancelAfter(TimeSpan.FromMinutes(5));
        var archived = new List<object>();
        foreach (var (customer, payload) in customerOrders)
        {
            using var orderDocument = JsonDocument.Parse(payload.ToJsonString());
            var result = await ExportAsync(evidence, orderDocument.RootElement, exportCts.Token);
            if (!result.Success)
            {
                logger.LogWarning("Expired email evidence {EvidenceId} was retained in SQL because SharePoint archive failed: {Error}", evidence.Id, result.Error);
                db.StagedImportEvents.Add(new StagedImportEvent
                {
                    StagedImportId = evidence.Id,
                    EventType = "SharePointArchiveFailed",
                    NewStatus = evidence.Status,
                    PayloadJson = JsonSerializer.Serialize(new { result.Error, customerFolder = customer, retentionHours = 72 }),
                    Note = "Expired source evidence remains in Azure SQL until its SharePoint archive succeeds.",
                    Actor = "system:sharepoint-retention"
                });
                await db.SaveChangesAsync(ct);
                return false;
            }
            archived.Add(new { result.ArchiveUrl, customerFolder = customer, result.AttachmentCount });
        }

        evidence.PayloadJson = RemoveAttachmentBytes(evidence.PayloadJson);
        evidence.ReviewNote = ClipNote(evidence.ReviewNote, "Source attachment bytes archived to SharePoint after 72 hours; metadata retained in Azure SQL.");
        db.StagedImportEvents.Add(new StagedImportEvent
        {
            StagedImportId = evidence.Id,
            EventType = "EvidenceArchivedAfter72Hours",
            NewStatus = evidence.Status,
            PayloadJson = JsonSerializer.Serialize(new { retentionHours = 72, customerArchives = archived }),
            Note = "SharePoint accepted the source attachments and manifests. Attachment bytes were then removed from Azure SQL; evidence metadata was retained.",
            Actor = "system:sharepoint-retention"
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Archived expired source evidence {EvidenceId} to {CustomerCount} SharePoint customer folder(s); SQL attachment bytes removed.", evidence.Id, archived.Count);
        return true;
    }

    private async Task<List<JsonObject>> FindRelatedOrdersAsync(StagedImport evidence, string? messageId, CancellationToken ct)
    {
        var candidates = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "order" && item.PayloadJson.Contains(evidence.IdempotencyKey))
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(100)
            .Select(item => item.PayloadJson)
            .ToListAsync(ct);

        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(messageId))
            candidates = await db.StagedImports.AsNoTracking()
                .Where(item => item.EntityType == "order" && item.PayloadJson.Contains(messageId))
                .OrderByDescending(item => item.ReceivedAtUtc)
                .Take(100)
                .Select(item => item.PayloadJson)
                .ToListAsync(ct);

        var result = new List<JsonObject>();
        foreach (var candidate in candidates)
        {
            try
            {
                var payload = JsonNode.Parse(candidate)?.AsObject();
                if (payload is not null && (!string.IsNullOrWhiteSpace(Text(payload, "customerCode")) || !string.IsNullOrWhiteSpace(Text(payload, "customerName"))))
                    result.Add(payload);
            }
            catch (JsonException) { }
        }
        return result;
    }

    private static string ClipNote(string? existing, string note)
    {
        var combined = string.IsNullOrWhiteSpace(existing) ? note : $"{existing} {note}";
        return combined.Length <= 1000 ? combined : combined[..1000];
    }

    internal static string RemoveAttachmentBytes(string evidencePayload)
    {
        var root = JsonNode.Parse(evidencePayload)?.AsObject() ?? throw new JsonException("Source evidence is not a JSON object.");
        if (root["attachments"] is JsonArray attachments)
            foreach (var attachment in attachments.OfType<JsonObject>())
                attachment.Remove("contentBase64");
        return root.ToJsonString();
    }

    private async Task<ArchiveResult> ExportAsync(StagedImport evidence, JsonElement orderPayload, CancellationToken ct)
    {
        JsonObject source;
        try { source = JsonNode.Parse(evidence.PayloadJson)?.AsObject() ?? throw new JsonException("Evidence payload is not an object."); }
        catch (JsonException ex) { return ArchiveResult.Failure(ex.Message); }

        var customer = SafeSegment(Text(orderPayload, "customerCode") ?? Text(orderPayload, "customerName") ?? "Unassigned customer");
        var serviceDate = Text(orderPayload, "collectionDate") ?? Text(orderPayload, "deliveryDate") ?? evidence.ReceivedAtUtc.ToString("yyyy-MM-dd");
        var reference = SafeSegment(Text(orderPayload, "customerPo") ?? Text(orderPayload, "poNumber") ?? "No reference");
        var sender = SenderLabel(Text(source, "senderAddress"));
        var prefix = $"{SafeSegment(serviceDate)}_{sender}_{reference}";
        var attachments = source["attachments"] as JsonArray ?? [];
        var files = attachments.OfType<JsonObject>()
            .Where(item => !Bool(item, "isInline"))
            .Select(item => new ArchiveAttachment(Text(item, "name") ?? "attachment.bin", Text(item, "contentType") ?? "application/octet-stream", Text(item, "contentBase64")))
            .ToList();

        if (files.Any(file => string.IsNullOrWhiteSpace(file.ContentBase64)))
            return ArchiveResult.Failure("At least one source attachment has no retained bytes; SQL evidence was left untouched.", customer, files.Count);

        try
        {
            var client = await CreateGraphClientAsync(ct);
            var driveId = await ResolveDriveIdAsync(client, ct);
            var folder = await EnsureFolderAsync(client, driveId, new[] { options.ArchiveRoot, customer }, ct);
            foreach (var file in files)
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(file.ContentBase64!); }
                catch (FormatException) { return ArchiveResult.Failure($"Attachment '{file.Name}' is not valid Base64.", customer, files.Count); }
                await UploadAsync(client, driveId, folder.Id, $"{prefix}_{SafeFileName(file.Name)}", bytes, file.ContentType, ct);
            }

            var manifest = new JsonObject
            {
                ["sourceEvidenceKey"] = evidence.IdempotencyKey,
                ["messageId"] = Text(source, "messageId"),
                ["internetMessageId"] = Text(source, "internetMessageId"),
                ["senderAddress"] = Text(source, "senderAddress"),
                ["subject"] = Text(source, "subject"),
                ["receivedAtUtc"] = Text(source, "receivedAtUtc"),
                ["bodyText"] = Text(source, "bodyText"),
                ["orderReference"] = Text(orderPayload, "poNumber"),
                ["customerPo"] = Text(orderPayload, "customerPo"),
                ["customerCode"] = Text(orderPayload, "customerCode"),
                ["collectionDate"] = Text(orderPayload, "collectionDate"),
                ["deliveryDate"] = Text(orderPayload, "deliveryDate"),
                ["attachments"] = new JsonArray(files.Select(file => (JsonNode?)file.Name).ToArray())
            };
            var manifestFile = await UploadAsync(client, driveId, folder.Id, $"{prefix}_source-email.json", Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), "application/json", ct);
            return ArchiveResult.Completed(manifestFile.WebUrl, customer, files.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ArchiveResult.Failure(ex.GetBaseException().Message, customer, files.Count);
        }
    }

    private async Task<HttpClient> CreateGraphClientAsync(CancellationToken ct)
    {
        var credential = new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
        var token = await credential.GetTokenAsync(new TokenRequestContext(GraphScopes), ct);
        var client = httpClientFactory.CreateClient("SharePointOrderArchive");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

    private async Task<string> ResolveDriveIdAsync(HttpClient client, CancellationToken ct)
    {
        var site = await ReadJsonAsync(client, $"sites/{Uri.EscapeDataString(options.SiteHost)}", ct);
        var siteId = Text(site, "id") ?? throw new InvalidOperationException("Graph did not return the SharePoint site id.");
        var drives = await ReadJsonAsync(client, $"sites/{Uri.EscapeDataString(siteId)}/drives", ct);
        var drive = (drives["value"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault(item => string.Equals(Text(item, "name"), options.LibraryName, StringComparison.OrdinalIgnoreCase));
        return Text(drive, "id") ?? throw new InvalidOperationException($"SharePoint library '{options.LibraryName}' was not found.");
    }

    private async Task<DriveItem> EnsureFolderAsync(HttpClient client, string driveId, IEnumerable<string> segments, CancellationToken ct)
    {
        var current = "root";
        foreach (var segment in segments.Select(SafeSegment))
        {
            var encodedParent = Uri.EscapeDataString(current);
            using var response = await client.GetAsync($"drives/{Uri.EscapeDataString(driveId)}/items/{encodedParent}/children?$filter=name eq '{segment.Replace("'", "''", StringComparison.Ordinal)}'", ct);
            JsonObject? child = null;
            if (response.IsSuccessStatusCode)
            {
                var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?.AsObject();
                child = (json?["value"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item => item["folder"] is not null && string.Equals(Text(item, "name"), segment, StringComparison.OrdinalIgnoreCase));
            }
            if (child is null)
            {
                var created = await SendJsonAsync(client, HttpMethod.Post, $"drives/{Uri.EscapeDataString(driveId)}/items/{encodedParent}/children", new JsonObject { ["name"] = segment, ["folder"] = new JsonObject(), ["@microsoft.graph.conflictBehavior"] = "fail" }, ct, allowConflict: true);
                child = created;
                if (child is null) throw new InvalidOperationException($"SharePoint folder '{segment}' could not be created.");
            }
            current = Text(child, "id") ?? throw new InvalidOperationException($"SharePoint folder '{segment}' has no id.");
        }
        return new DriveItem(current, null);
    }

    private async Task<DriveItem> UploadAsync(HttpClient client, string driveId, string folderId, string fileName, byte[] content, string contentType, CancellationToken ct)
    {
        if (content.Length > 4 * 1024 * 1024)
            return await UploadLargeFileAsync(client, driveId, folderId, fileName, content, ct);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(folderId)}:/{Uri.EscapeDataString(fileName)}:/content")
        {
            Content = new ByteArrayContent(content)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"SharePoint upload of '{fileName}' failed ({(int)response.StatusCode}): {body}");
        var item = JsonNode.Parse(body)?.AsObject() ?? throw new InvalidOperationException("Graph returned an empty upload result.");
        var uploaded = new DriveItem(Text(item, "id") ?? throw new InvalidOperationException("Graph upload result has no id."), Text(item, "webUrl"));
        await VerifyUploadedAsync(client, driveId, uploaded.Id, content.LongLength, fileName, ct);
        return uploaded;
    }

    private async Task<DriveItem> UploadLargeFileAsync(HttpClient client, string driveId, string folderId, string fileName, byte[] content, CancellationToken ct)
    {
        var session = await SendJsonAsync(client, HttpMethod.Post,
            $"drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(folderId)}:/{Uri.EscapeDataString(fileName)}:/createUploadSession",
            new JsonObject { ["item"] = new JsonObject { ["@microsoft.graph.conflictBehavior"] = "replace", ["name"] = fileName } }, ct, allowConflict: false)
            ?? throw new InvalidOperationException("Graph did not return an upload session.");
        var uploadUrl = Text(session, "uploadUrl") ?? throw new InvalidOperationException("Graph upload session has no upload URL.");
        const int chunkSize = 320 * 1024; // Required Graph multiple and comfortably below request limits.
        JsonObject? completed = null;
        for (var offset = 0; offset < content.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, content.Length - offset);
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new ByteArrayContent(content, offset, length)
            };
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, content.Length);
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Accepted)
                throw new InvalidOperationException($"SharePoint upload of '{fileName}' failed ({(int)response.StatusCode}): {body}");
            if (response.StatusCode != HttpStatusCode.Accepted)
                completed = JsonNode.Parse(body)?.AsObject();
        }
        var uploaded = new DriveItem(Text(completed, "id") ?? throw new InvalidOperationException("Graph did not return a completed large-file upload."), Text(completed, "webUrl"));
        await VerifyUploadedAsync(client, driveId, uploaded.Id, content.LongLength, fileName, ct);
        return uploaded;
    }

    private static async Task VerifyUploadedAsync(HttpClient client, string driveId, string itemId, long expectedLength, string fileName, CancellationToken ct)
    {
        var item = await ReadJsonAsync(client, $"drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}?$select=id,size", ct);
        if (!long.TryParse(Text(item, "size"), out var actualLength) || actualLength != expectedLength)
            throw new InvalidOperationException($"SharePoint upload verification failed for '{fileName}': expected {expectedLength} bytes, received {Text(item, "size") ?? "no size"}.");
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Microsoft Graph request '{path}' failed ({(int)response.StatusCode}): {body}");
        return JsonNode.Parse(body)?.AsObject() ?? throw new InvalidOperationException("Microsoft Graph returned an empty JSON response.");
    }

    private static async Task<JsonObject?> SendJsonAsync(HttpClient client, HttpMethod method, string path, JsonObject body, CancellationToken ct, bool allowConflict)
    {
        using var request = new HttpRequestMessage(method, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Conflict && allowConflict) return null;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Microsoft Graph request '{path}' failed ({(int)response.StatusCode}): {text}");
        return JsonNode.Parse(text)?.AsObject();
    }

    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()?.Trim() : null;
    private static string? Text(JsonObject? value, string name) => value is null || value[name] is null ? null : value[name]!.ToString().Trim();
    private static bool Bool(JsonObject value, string name) => bool.TryParse(Text(value, name), out var result) && result;
    private static string SafeSegment(string value) => SafeFileName(value).Replace('.', '_').Trim('_').PadRight(1, '_')[..Math.Min(SafeFileName(value).Replace('.', '_').Trim('_').PadRight(1, '_').Length, 80)];
    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['"', '*', ':', '<', '>', '?', '/', '\\', '|', '#', '%']).ToHashSet();
        var safe = new string(value.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(safe) ? "untitled" : safe[..Math.Min(safe.Length, 120)];
    }
    private static string SenderLabel(string? address)
    {
        var domain = address?.Split('@').LastOrDefault()?.Split('.').FirstOrDefault();
        return SafeSegment(string.IsNullOrWhiteSpace(domain) ? "unknown-sender" : domain);
    }

    public sealed record SharePointOrderArchiveSummary(int Succeeded, int Failed, bool Disabled);
    public sealed record ArchiveAttachment(string Name, string ContentType, string? ContentBase64);
    public sealed record DriveItem(string Id, string? WebUrl);
    private sealed record ArchiveResult(bool Success, string? Error, string? ArchiveUrl, string? CustomerFolder, int AttachmentCount)
    {
        public static ArchiveResult Completed(string? url, string customer, int count) => new(true, null, url, customer, count);
        public static ArchiveResult Failure(string error, string? customer = null, int count = 0) => new(false, error, null, customer, count);
    }
}
