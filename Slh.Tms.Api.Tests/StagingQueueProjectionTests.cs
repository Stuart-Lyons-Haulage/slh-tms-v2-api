using System.Text.Json;
using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class StagingQueueProjectionTests
{
    [Fact]
    public void StagingQueuePage_preserves_paged_queue_contract()
    {
        var page = new StagingQueuePage(
            page: 1,
            pageSize: 100,
            total: 215,
            hasMore: true,
            records: new object[] { new { id = "staged-1" } });

        var json = JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"page\":1", json);
        Assert.Contains("\"pageSize\":100", json);
        Assert.Contains("\"total\":215", json);
        Assert.Contains("\"hasMore\":true", json);
        Assert.Contains("\"records\":[", json);
        Assert.DoesNotContain("\"items\"", json);
    }

    [Fact]
    public void BuildPayloadSummary_keeps_review_fields_and_drops_heavy_email_evidence()
    {
        var payload = JsonSerializer.Serialize(new
        {
            poNumber = "PO-123",
            customerPo = "CUST-77",
            customerCode = "NWF",
            collectionDate = "2026-09-15",
            deliveryDate = "2026-09-15",
            pallets = 12,
            sellerName = "Merston",
            stallNumber = "Aldi Darlington",
            plannerReady = true,
            intakeStatus = "Ready",
            intakeConfidence = "High",
            intakeWarnings = new[] { "Check booking time" },
            emailRouteMatched = true,
            emailRouteRequiresReview = false,
            orderIntakeRouteRuleId = "f558caaa-7f7b-4caa-9e95-cc4dc343cd4f",
            orderIntakeRouteConfidenceScore = 95,
            orderIntakeRouteMatchedDimensions = 3,
            orderIntakeRouteExplanation = new[] { "Customer NWF matched.", "retailer matched ALDI." },
            orderIntakeRouteAlternatives = new[] { new { score = 95, customerCode = "NWF" } },
            sourceMessageId = "message-123",
            sourceInternetMessageId = "<message-123@example.com>",
            sourceSubject = "NWAY pallet order",
            sourceWebLink = "https://outlook.office.com/mail/id/message-123",
            sourceBodyText = new string('x', 20000),
            sourceBodyHtml = "<p>large retained email body</p>",
            sourceToRecipients = new[] { new { address = "info@lyonshaulage.com" } },
            sourceCcRecipients = new[] { new { address = "planner@lyonshaulage.com" } },
            sourceAttachments = new[] { new { name = "orders.xlsx", size = 500000, isInline = false } }
        });

        var summaryJson = StagingQueueProjection.BuildPayloadSummary(payload);
        using var document = JsonDocument.Parse(summaryJson);
        var root = document.RootElement;

        Assert.Equal("PO-123", root.GetProperty("poNumber").GetString());
        Assert.Equal("NWF", root.GetProperty("customerCode").GetString());
        Assert.Equal(12, root.GetProperty("pallets").GetInt32());
        Assert.Equal("message-123", root.GetProperty("sourceMessageId").GetString());
        Assert.Equal("orders.xlsx", root.GetProperty("sourceAttachmentName").GetString());
        Assert.True(root.GetProperty("emailRouteMatched").GetBoolean());
        Assert.Equal(95, root.GetProperty("orderIntakeRouteConfidenceScore").GetInt32());
        Assert.Equal(3, root.GetProperty("orderIntakeRouteMatchedDimensions").GetInt32());
        Assert.Equal("NWF", root.GetProperty("orderIntakeRouteAlternatives")[0].GetProperty("customerCode").GetString());
        Assert.False(root.TryGetProperty("sourceBodyText", out _));
        Assert.False(root.TryGetProperty("sourceBodyHtml", out _));
        Assert.False(root.TryGetProperty("sourceToRecipients", out _));
        Assert.False(root.TryGetProperty("sourceCcRecipients", out _));
        Assert.False(root.TryGetProperty("sourceAttachments", out _));
    }

    [Fact]
    public void BuildPayloadSummary_turns_mapping_review_into_selectable_planner_review()
    {
        var payload = JsonSerializer.Serialize(new
        {
            poNumber = "PORD000676/SITTINGBOURNE",
            customerPo = "PORD000676",
            customerCode = "MORRISONS",
            collectionDate = "2026-09-18",
            deliveryDate = "2026-09-18",
            pallets = 18,
            sellerName = "Groves Farm",
            stallNumber = "Fresh Cut",
            plannerReady = false,
            intakeStatus = "Review",
            emailRouteRequiresReview = true,
            sourceSubject = "ALDI & Morrisons - 18.09.2026"
        });

        var summaryJson = StagingQueueProjection.BuildPayloadSummary(payload);
        using var document = JsonDocument.Parse(summaryJson);
        var root = document.RootElement;

        Assert.True(root.GetProperty("plannerReady").GetBoolean());
        Assert.True(root.GetProperty("orderIntakeRouteRequiresReview").GetBoolean());
        Assert.Equal("Review", root.GetProperty("intakeStatus").GetString());
    }

    [Fact]
    public void BuildPayloadSummary_keeps_genuine_preorder_blocked()
    {
        var payload = JsonSerializer.Serialize(new
        {
            poNumber = "PORD000676/ALDI-TO-FOLLOW",
            customerCode = "ALDI",
            collectionDate = "2026-09-18",
            deliveryDate = "2026-09-18",
            pallets = 6,
            plannerReady = false,
            intakeStatus = "PreOrder",
            emailRouteRequiresReview = true
        });

        var summaryJson = StagingQueueProjection.BuildPayloadSummary(payload);
        using var document = JsonDocument.Parse(summaryJson);
        var root = document.RootElement;

        Assert.False(root.GetProperty("plannerReady").GetBoolean());
        Assert.Equal("PreOrder", root.GetProperty("intakeStatus").GetString());
    }

    [Fact]
    public void BuildPayloadSummary_keeps_delivery_date_for_order_review_priority()
    {
        var payload = JsonSerializer.Serialize(new
        {
            poNumber = "PO-456",
            customerCode = "HHP",
            deliveryDate = "2026-09-16",
            sourceBodyText = new string('x', 20000)
        });

        var summaryJson = StagingQueueProjection.BuildPayloadSummary(payload);
        using var document = JsonDocument.Parse(summaryJson);
        var root = document.RootElement;

        Assert.Equal("2026-09-16", root.GetProperty("deliveryDate").GetString());
        Assert.False(root.TryGetProperty("sourceBodyText", out _));
    }

    [Fact]
    public void MatchesPlanningDate_keeps_cross_date_pm_on_collection_day_only()
    {
        var payload = JsonSerializer.Serialize(new
        {
            poNumber = "PO-789",
            customerCode = "HHP",
            collectionDate = "2026-09-15",
            deliveryDate = "2026-09-16",
            sellerName = "Hall Hunter",
            stallNumber = "Leyland"
        });

        Assert.True(StagingQueueProjection.MatchesPlanningDate(payload, new DateOnly(2026, 9, 15)));
        Assert.False(StagingQueueProjection.MatchesPlanningDate(payload, new DateOnly(2026, 9, 16)));
    }

    [Fact]
    public void MatchesPlanningDate_shows_delivery_only_pm_on_previous_and_delivery_day()
    {
        var payload = JsonSerializer.Serialize(new
        {
            poNumber = "PO-999",
            customerCode = "Barefoots",
            deliveryDate = "2026-09-16",
            requestedTime = "PM load"
        });

        Assert.True(StagingQueueProjection.MatchesPlanningDate(payload, new DateOnly(2026, 9, 15)));
        Assert.True(StagingQueueProjection.MatchesPlanningDate(payload, new DateOnly(2026, 9, 16)));
        Assert.False(StagingQueueProjection.MatchesPlanningDate(payload, new DateOnly(2026, 9, 14)));
    }

    [Fact]
    public void BuildPayloadSummary_returns_empty_object_for_invalid_legacy_json()
    {
        Assert.Equal("{}", StagingQueueProjection.BuildPayloadSummary("not-json"));
    }
}
