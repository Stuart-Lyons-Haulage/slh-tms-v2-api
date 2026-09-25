using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Clean-V2 mailbox bridge. The local TMS polls Microsoft Graph outbound, so the
/// private V2 API does not need to be exposed to the internet just to receive orders.
/// Graph application access should be restricted to the Info shared mailbox.
/// </summary>
public sealed class InfoMailboxGraphOptions
{
    public bool Enabled { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Mailbox { get; set; } = "info@lyonshaulage.com";
    public int PollIntervalSeconds { get; set; } = 60;
    public int InitialLookbackHours { get; set; } = 48;
    public int OverlapMinutes { get; set; } = 30;
    public int MaxMessagesPerPoll { get; set; } = 250;
    public long MaxAttachmentBytes { get; set; } = 20 * 1024 * 1024;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(Mailbox);
}

public sealed class InfoMailboxGraphHealthState
{
    private readonly object _gate = new();
    public DateTimeOffset? LastAttemptUtc { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public string? LastError { get; private set; }
    public int LastMessagesSeen { get; private set; }
    public int LastMessagesIngested { get; private set; }

    public void Success(int seen, int ingested)
    {
        lock (_gate)
        {
            LastAttemptUtc = DateTimeOffset.UtcNow;
            LastSuccessUtc = LastAttemptUtc;
            LastError = null;
            LastMessagesSeen = seen;
            LastMessagesIngested = ingested;
        }
    }

    public void Failure(Exception exception)
    {
        lock (_gate)
        {
            LastAttemptUtc = DateTimeOffset.UtcNow;
            LastError = exception.GetBaseException().Message;
        }
    }
}

public sealed class InfoMailboxGraphPollingService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    InfoMailboxGraphOptions options,
    InfoMailboxGraphHealthState health,
    ILogger<InfoMailboxGraphPollingService> logger) : BackgroundService
{
    private readonly SemaphoreSlim pollGate = new(1, 1);
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xls", ".xlsx", ".xlsm", ".csv", ".pdf"
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Local Info mailbox Graph polling is disabled.");
            return;
        }

        if (!options.IsConfigured)
        {
            logger.LogError("Local Info mailbox Graph polling is enabled but TenantId, ClientId, ClientSecret or Mailbox is missing.");
            health.Failure(new InvalidOperationException("Info mailbox Graph integration is enabled but not fully configured."));
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(options.PollIntervalSeconds, 30, 3600));
        logger.LogInformation(
            "Local Info mailbox Graph polling enabled for {Mailbox}; interval {IntervalSeconds}s.",
            options.Mailbox,
            interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                health.Failure(ex);
                logger.LogError(ex, "Info mailbox Graph polling failed. The next scheduled poll will retry.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        await pollGate.WaitAsync(ct);
        try
        {
            await PollCoreAsync(ct);
        }
        finally
        {
            pollGate.Release();
        }
    }

    private async Task PollCoreAsync(CancellationToken ct)
    {
        var credential = new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
        var token = await credential.GetTokenAsync(new TokenRequestContext(GraphScopes), ct);
        var client = httpClientFactory.CreateClient("InfoMailboxGraph");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();

        var lastEvidenceUtc = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "email-evidence")
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Select(item => (DateTimeOffset?)item.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);

        var since = lastEvidenceUtc is null
            ? DateTimeOffset.UtcNow.AddHours(-Math.Clamp(options.InitialLookbackHours, 1, 168))
            : lastEvidenceUtc.Value.AddMinutes(-Math.Clamp(options.OverlapMinutes, 5, 1440));

        var messages = await FetchMessagesAsync(client, token.Token, since, ct);
        var seen = messages.Count;
        var ingested = 0;

        foreach (var message in messages.OrderBy(item => item.ReceivedAtUtc))
        {
            ct.ThrowIfCancellationRequested();

            var evidenceKey = SourceEvidenceKey(message.Id);
            if (await db.StagedImports.AsNoTracking()
                .AnyAsync(item => item.EntityType == "email-evidence" && item.IdempotencyKey == evidenceKey, ct))
                continue;

            var attachments = message.HasAttachments
                ? await FetchAttachmentsAsync(client, token.Token, message.Id, ct)
                : [];

            var request = ToIntakeRequest(message, attachments, options.Mailbox);

            // Reuse the exact canonical intake + evidence-upgrade path used by the
            // Power Automate endpoint, but invoke it internally rather than exposing
            // the local API to inbound internet traffic.
            var staging = scope.ServiceProvider.GetRequiredService<StagingService>();
            var intakeLogger = scope.ServiceProvider.GetRequiredService<ILogger<OrderIntakeController>>();
            var controller = new InfoMailboxIntakeUpgradeController(db, staging, intakeLogger);
            var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("localhost");
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            await controller.Intake(request, ct);
            ingested++;
        }

        health.Success(seen, ingested);
        logger.LogInformation(
            "Info mailbox Graph poll completed: {Seen} message(s) seen, {Ingested} newly ingested.",
            seen,
            ingested);
    }

    private async Task<List<GraphMailboxMessage>> FetchMessagesAsync(
        HttpClient client,
        string accessToken,
        DateTimeOffset since,
        CancellationToken ct)
    {
        var mailbox = Uri.EscapeDataString(options.Mailbox);
        var filter = Uri.EscapeDataString($"receivedDateTime ge {since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
        string? next =
            $"users/{mailbox}/mailFolders/inbox/messages" +
            "?$select=id,internetMessageId,conversationId,subject,receivedDateTime,body,bodyPreview,from,toRecipients,ccRecipients,importance,webLink,hasAttachments" +
            $"&$filter={filter}&$top=50";

        var messages = new List<GraphMailboxMessage>();
        var limit = Math.Clamp(options.MaxMessagesPerPoll, 1, 1000);

        while (!string.IsNullOrWhiteSpace(next) && messages.Count < limit)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"html\"");
            using var response = await client.SendAsync(request, ct);
            await EnsureGraphSuccessAsync(response, request.RequestUri, ct);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (messages.Count >= limit) break;
                    var parsed = ParseMessage(item);
                    if (parsed is not null) messages.Add(parsed);
                }
            }

            next = root.TryGetProperty("@odata.nextLink", out var nextLink) && nextLink.ValueKind == JsonValueKind.String
                ? nextLink.GetString()
                : null;
        }

        return messages;
    }

    private async Task<List<MailboxAttachmentRequest>> FetchAttachmentsAsync(
        HttpClient client,
        string accessToken,
        string messageId,
        CancellationToken ct)
    {
        var mailbox = Uri.EscapeDataString(options.Mailbox);
        var encodedMessageId = Uri.EscapeDataString(messageId);
        var metadataUrl =
            $"users/{mailbox}/messages/{encodedMessageId}/attachments" +
            "?$select=id,name,contentType,size,isInline";

        using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, metadataUrl);
        metadataRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var metadataResponse = await client.SendAsync(metadataRequest, ct);
        await EnsureGraphSuccessAsync(metadataResponse, metadataRequest.RequestUri, ct);

        using var document = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync(ct));
        var result = new List<MailboxAttachmentRequest>();
        if (!document.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in value.EnumerateArray())
        {
            var name = Text(item, "name");
            var isInline = Bool(item, "isInline") ?? false;
            if (isInline || string.IsNullOrWhiteSpace(name) || !SupportedExtensions.Contains(Path.GetExtension(name)))
                continue;

            var size = Long(item, "size");
            if (size is > 0 && size > options.MaxAttachmentBytes)
                throw new InvalidOperationException($"Supported order attachment '{name}' is {size:N0} bytes and exceeds the configured Graph intake limit.");

            var attachmentId = Text(item, "id");
            if (string.IsNullOrWhiteSpace(attachmentId))
                continue;

            var payloadUrl = $"users/{mailbox}/messages/{encodedMessageId}/attachments/{Uri.EscapeDataString(attachmentId)}";
            using var payloadRequest = new HttpRequestMessage(HttpMethod.Get, payloadUrl);
            payloadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var payloadResponse = await client.SendAsync(payloadRequest, ct);
            await EnsureGraphSuccessAsync(payloadResponse, payloadRequest.RequestUri, ct);

            using var payloadDocument = JsonDocument.Parse(await payloadResponse.Content.ReadAsStringAsync(ct));
            var payload = payloadDocument.RootElement;
            var contentBytes = Text(payload, "contentBytes");
            if (string.IsNullOrWhiteSpace(contentBytes))
                throw new InvalidOperationException($"Graph did not return file content for supported order attachment '{name}'.");

            result.Add(new MailboxAttachmentRequest(
                name,
                Text(payload, "contentType") ?? Text(item, "contentType"),
                null,
                false,
                Text(payload, "contentId") ?? Text(item, "contentId"),
                Long(payload, "size") ?? size,
                contentBytes));
        }

        return result;
    }

    private static async Task EnsureGraphSuccessAsync(HttpResponseMessage response, Uri? requestUri, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body;
        if (detail?.Length > 600) detail = detail[..600];
        throw new HttpRequestException(
            $"Microsoft Graph returned {(int)response.StatusCode} ({response.ReasonPhrase}) for {requestUri?.AbsolutePath}: {detail}",
            null,
            response.StatusCode);
    }

    internal static GraphMailboxMessage? ParseMessage(JsonElement item)
    {
        var id = Text(item, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var received = DateTimeOffset.TryParse(Text(item, "receivedDateTime"), out var parsedReceived)
            ? parsedReceived
            : DateTimeOffset.UtcNow;

        var from = item.TryGetProperty("from", out var fromNode) &&
                   fromNode.ValueKind == JsonValueKind.Object &&
                   fromNode.TryGetProperty("emailAddress", out var emailAddress)
            ? emailAddress
            : default;

        var body = item.TryGetProperty("body", out var bodyNode) && bodyNode.ValueKind == JsonValueKind.Object
            ? bodyNode
            : default;

        return new GraphMailboxMessage(
            id,
            Text(item, "internetMessageId"),
            Text(item, "conversationId"),
            Text(item, "subject"),
            received,
            body.ValueKind == JsonValueKind.Object ? Text(body, "content") : null,
            Text(item, "bodyPreview"),
            from.ValueKind == JsonValueKind.Object ? Text(from, "address") : null,
            from.ValueKind == JsonValueKind.Object ? Text(from, "name") : null,
            Text(item, "importance"),
            Text(item, "webLink"),
            Bool(item, "hasAttachments") ?? false,
            CloneArray(item, "toRecipients"),
            CloneArray(item, "ccRecipients"));
    }

    internal static MailboxEmailIntakeRequest ToIntakeRequest(
        GraphMailboxMessage message,
        List<MailboxAttachmentRequest> attachments,
        string mailbox) =>
        new(
            message.Id,
            message.InternetMessageId,
            mailbox,
            message.SenderAddress,
            message.SenderName,
            message.Subject,
            message.ReceivedAtUtc,
            message.BodyPreview,
            message.BodyHtml,
            message.WebLink,
            attachments,
            message.ConversationId,
            message.ToRecipients,
            message.CcRecipients,
            "html",
            message.Importance,
            $"graph:{Guid.NewGuid():N}");

    private static JsonElement? CloneArray(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.Clone()
            : null;

    private static string? Text(JsonElement item, string propertyName) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Bool(JsonElement item, string propertyName) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(propertyName, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    private static long? Long(JsonElement item, string propertyName) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(propertyName, out var value) &&
        value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static string SourceEvidenceKey(string messageId)
    {
        var compact = new string(messageId.Where(char.IsLetterOrDigit).ToArray());
        if (compact.Length > 96) compact = compact[^96..];
        var key = $"email-evidence:{compact}";
        return key.Length <= 200 ? key : key[..200];
    }
}

public sealed record GraphMailboxMessage(
    string Id,
    string? InternetMessageId,
    string? ConversationId,
    string? Subject,
    DateTimeOffset ReceivedAtUtc,
    string? BodyHtml,
    string? BodyPreview,
    string? SenderAddress,
    string? SenderName,
    string? Importance,
    string? WebLink,
    bool HasAttachments,
    JsonElement? ToRecipients,
    JsonElement? CcRecipients);
