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

    public async Task ArchiveApprovedOrderAsync(StagedImport order, JsonElement orderPayload, string? actor, CancellationToken ct)
    {
        if (!options.IsConfigured || !string.Equals(order.EntityType, "order", StringComparison.OrdinalIgnoreCase)) return;

        var evidence = await FindEvidenceAsync(orderPayload, ct);
        if (evidence is null) return;

        var result = await ExportAsync(evidence, orderPayload, ct);
        if (!result.Success)
        {
            db.StagedImportEvents.Add(ArchiveEvent(order, "SharePointArchiveFailed", actor, new
            {
                result.Error,
                result.CustomerFolder,
                result.AttachmentCount,
                result.ArchiveUrl
            }));
            logger.LogWarning("Approved order {OrderId} was retained in SQL because SharePoint archiving failed: {Error}", order.Id, result.Error);
            return;
        }

        if (options.RemoveAttachmentPayloadAfterVerifiedExport && result.AttachmentCount > 0)
            evidence.PayloadJson = RemoveAttachmentBytes(evidence.PayloadJson);

        db.StagedImportEvents.Add(ArchiveEvent(order, "SharePointArchived", actor, new
        {
            result.ArchiveUrl,
            result.CustomerFolder,
            result.AttachmentCount,
            attachmentPayloadRemoved = options.RemoveAttachmentPayloadAfterVerifiedExport && result.AttachmentCount > 0
        }));
        db.StagedImportEvents.Add(new StagedImportEvent
        {
            StagedImportId = evidence.Id,
            EventType = "EvidenceArchived",
            NewStatus = evidence.Status,
            PayloadJson = JsonSerializer.Serialize(new { orderId = order.Id, result.ArchiveUrl, result.CustomerFolder, result.AttachmentCount }),
            Note = "Evidence exported to SharePoint and verified before attachment cleanup.",
            Actor = actor
        });
    }

    internal static string RemoveAttachmentBytes(string evidencePayload)
    {
        var root = JsonNode.Parse(evidencePayload)?.AsObject() ?? throw new JsonException("Source evidence is not a JSON object.");
        if (root["attachments"] is JsonArray attachments)
            foreach (var attachment in attachments.OfType<JsonObject>())
                attachment.Remove("contentBase64");
        return root.ToJsonString();
    }

    private async Task<StagedImport?> FindEvidenceAsync(JsonElement payload, CancellationToken ct)
    {
        var evidenceKey = Text(payload, "sourceEvidenceKey");
        if (!string.IsNullOrWhiteSpace(evidenceKey))
            return await db.StagedImports.SingleOrDefaultAsync(item => item.EntityType == "email-evidence" && item.IdempotencyKey == evidenceKey, ct);

        var messageId = Text(payload, "sourceEmailMessageId") ?? Text(payload, "sourceMessageId");
        if (string.IsNullOrWhiteSpace(messageId)) return null;
        var candidates = await db.StagedImports
            .Where(item => item.EntityType == "email-evidence" && item.PayloadJson.Contains(messageId))
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(2)
            .ToListAsync(ct);
        return candidates.Count == 1 ? candidates[0] : null;
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
        return new DriveItem(Text(item, "id") ?? throw new InvalidOperationException("Graph upload result has no id."), Text(item, "webUrl"));
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
        return new DriveItem(Text(completed, "id") ?? throw new InvalidOperationException("Graph did not return a completed large-file upload."), Text(completed, "webUrl"));
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

    private static StagedImportEvent ArchiveEvent(StagedImport order, string eventType, string? actor, object payload) => new()
    {
        StagedImportId = order.Id, EventType = eventType, NewStatus = order.Status, PayloadJson = JsonSerializer.Serialize(payload),
        Note = eventType == "SharePointArchived" ? "Source evidence exported to SharePoint." : "Source evidence remains in SQL pending SharePoint archive recovery.", Actor = actor
    };
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

    private sealed record ArchiveAttachment(string Name, string ContentType, string? ContentBase64);
    private sealed record DriveItem(string Id, string? WebUrl);
    private sealed record ArchiveResult(bool Success, string? Error, string? ArchiveUrl, string? CustomerFolder, int AttachmentCount)
    {
        public static ArchiveResult Completed(string? url, string customer, int count) => new(true, null, url, customer, count);
        public static ArchiveResult Failure(string error, string? customer = null, int count = 0) => new(false, error, null, customer, count);
    }
}
