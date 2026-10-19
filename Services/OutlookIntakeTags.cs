using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Shows in Outlook what the TMS did with each email in info@, as a colour category the
/// person watching the inbox can see at a glance:
///   green  "TMS: orders in"         - orders from this email are in the TMS
///   red    "TMS: needs attention"   - recognised as an order but couldn't be read; on Orders as a card
///   grey   "TMS: no orders"         - read, nothing to plan (or every order from it was rejected)
/// Categories update when things change (a card re-read, amended, approved or rejected).
/// Needs the Graph application permission Mail.ReadWrite on the mailbox; without it the TMS
/// carries on as before and System health says tagging is off.
/// </summary>
public sealed class OutlookIntakeTags(ILogger<OutlookIntakeTags> logger)
{
    public const string OrdersIn = "TMS: orders in";
    public const string NeedsAttention = "TMS: needs attention";
    public const string NoOrders = "TMS: no orders";
    private static readonly string[] All = [OrdersIn, NeedsAttention, NoOrders];
    private static readonly (string Name, string Colour)[] Master = [(OrdersIn, "preset4"), (NeedsAttention, "preset0"), (NoOrders, "preset12")];

    private readonly ConcurrentDictionary<string, string> applied = new(StringComparer.Ordinal);
    private bool masterChecked;
    private DateTimeOffset lastSweepUtc = DateTimeOffset.MinValue;

    /// <summary>Null when tagging works; otherwise why it doesn't (shown on System health).</summary>
    public string? Problem { get; private set; }
    public DateTimeOffset? LastTaggedUtc { get; private set; }

    /// <summary>The category an email should have, from the order records the TMS holds for it.</summary>
    public static async Task<Dictionary<string, string>> StatesAsync(TmsDbContext db, DateTimeOffset since, IReadOnlyCollection<string>? onlyMessageIds, CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "order" && item.ReceivedAtUtc >= since)
            .Select(item => new { item.IdempotencyKey, item.Status, item.PayloadJson, item.Source })
            .ToListAsync(ct);
        var states = new Dictionary<string, (bool Open, bool In)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            string? messageId; bool undated; bool placeholder;
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                messageId = Text(root, "sourceMessageId") ?? Text(root, "sourceEmailMessageId");
                undated = Text(root, "collectionDate") is null && Text(root, "deliveryDate") is null;
                placeholder = row.IdempotencyKey.EndsWith(":mapping-exception", StringComparison.Ordinal) ||
                              (row.Source?.StartsWith("Info mailbox mapping exception", StringComparison.Ordinal) ?? false) ||
                              string.Equals(Text(root, "intakeStatus"), "MappingException", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException) { continue; }
            if (messageId is null || (onlyMessageIds is not null && !onlyMessageIds.Contains(messageId))) continue;
            var current = states.GetValueOrDefault(messageId);
            var live = row.Status is StagingStatus.PendingReview or StagingStatus.Approved or StagingStatus.Promoted;
            if (live && placeholder && undated) current.Open = true;
            else if (live) current.In = true;
            states[messageId] = current;
        }
        return states.ToDictionary(item => item.Key, item => item.Value.Open ? NeedsAttention : item.Value.In ? OrdersIn : NoOrders, StringComparer.Ordinal);
    }

    /// <summary>Tag one email right after intake.</summary>
    public async Task TagAfterIntakeAsync(HttpClient client, string token, string mailbox, TmsDbContext db, string messageId, CancellationToken ct)
    {
        var states = await StatesAsync(db, DateTimeOffset.UtcNow.AddDays(-30), [messageId], ct);
        await ApplyAsync(client, token, mailbox, messageId, states.GetValueOrDefault(messageId, NoOrders), ct);
    }

    /// <summary>Every ten minutes: bring the last three days of order emails up to date.</summary>
    public async Task SweepAsync(HttpClient client, string token, string mailbox, TmsDbContext db, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - lastSweepUtc < TimeSpan.FromMinutes(10)) return;
        lastSweepUtc = DateTimeOffset.UtcNow;
        var states = await StatesAsync(db, DateTimeOffset.UtcNow.AddDays(-3), null, ct);
        foreach (var (messageId, category) in states)
        {
            if (applied.TryGetValue(messageId, out var already) && already == category) continue;
            await ApplyAsync(client, token, mailbox, messageId, category, ct);
            if (Problem is not null) return;
        }
    }

    private async Task ApplyAsync(HttpClient client, string token, string mailbox, string messageId, string category, CancellationToken ct)
    {
        if (applied.TryGetValue(messageId, out var already) && already == category) return;
        var box = Uri.EscapeDataString(mailbox);
        try
        {
            await EnsureMasterCategoriesAsync(client, token, box, ct);
            var url = $"users/{box}/messages/{Uri.EscapeDataString(messageId)}";
            using var get = new HttpRequestMessage(HttpMethod.Get, $"{url}?$select=categories");
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var current = await client.SendAsync(get, ct);
            if (current.StatusCode == HttpStatusCode.NotFound) { applied[messageId] = category; return; }
            if (!current.IsSuccessStatusCode) { Fail(current.StatusCode); return; }
            using var document = JsonDocument.Parse(await current.Content.ReadAsStringAsync(ct));
            var keep = document.RootElement.TryGetProperty("categories", out var existing) && existing.ValueKind == JsonValueKind.Array
                ? existing.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(name => name.Length > 0 && !All.Contains(name)).ToList()
                : [];
            keep.Add(category);

            using var patch = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { categories = keep }), Encoding.UTF8, "application/json")
            };
            patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(patch, ct);
            if (!response.IsSuccessStatusCode) { Fail(response.StatusCode); return; }
            applied[messageId] = category;
            Problem = null;
            LastTaggedUtc = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not tag info@ email {MessageId} in Outlook.", messageId);
        }
    }

    private async Task EnsureMasterCategoriesAsync(HttpClient client, string token, string box, CancellationToken ct)
    {
        if (masterChecked) return;
        masterChecked = true;
        try
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, $"users/{box}/outlook/masterCategories");
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(get, ct);
            if (!response.IsSuccessStatusCode) return; // colours are a nicety; tags still apply without them
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var names = document.RootElement.TryGetProperty("value", out var value)
                ? value.EnumerateArray().Select(item => item.TryGetProperty("displayName", out var name) ? name.GetString() : null).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            foreach (var (name, colour) in Master.Where(item => !names.Contains(item.Name)))
            {
                using var post = new HttpRequestMessage(HttpMethod.Post, $"users/{box}/outlook/masterCategories")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { displayName = name, color = colour }), Encoding.UTF8, "application/json")
                };
                post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var created = await client.SendAsync(post, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Outlook category colours could not be set up; emails are still tagged.");
        }
    }

    private void Fail(HttpStatusCode status)
    {
        var message = status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
            ? "Outlook tagging is off: the TMS app needs the Microsoft Graph permission Mail.ReadWrite (application) on info@. An Azure admin can grant it in Entra ID > App registrations > API permissions."
            : $"Outlook tagging failed (HTTP {(int)status}); it will try again.";
        if (Problem != message) logger.LogWarning("{Problem}", message);
        Problem = message;
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
}
