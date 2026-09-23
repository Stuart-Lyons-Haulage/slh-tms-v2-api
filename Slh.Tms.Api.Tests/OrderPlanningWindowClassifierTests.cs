using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderPlanningWindowClassifierTests
{
    [Fact]
    public void Cross_date_collection_delivery_is_pm_overnight()
    {
        var result = Classify(new
        {
            customerCode = "HALLHUNTER",
            poNumber = "HH-LEY-001",
            collectionDate = "2026-09-15",
            deliveryDate = "2026-09-16",
            sellerName = "Hall Hunter",
            stallNumber = "Leyland",
            pallets = 26
        });

        Assert.Equal("PM", result.PlanningWindow);
        Assert.True(result.RunsOvernight);
        Assert.Equal("PM Overnight", result.SuggestedRouteType);
        Assert.Equal("High", result.Confidence);
        Assert.Contains("Collection date", result.Reason);
    }

    [Fact]
    public void Barefoots_pm_pattern_sets_pm_window()
    {
        var result = Classify(new
        {
            customerCode = "BAREFOOTS",
            poNumber = "BF-PM-001",
            collectionDate = "2026-09-15",
            deliveryDate = "2026-09-16",
            sellerName = "Barefoots",
            stallNumber = "Customer depot",
            requestedTime = "PM load",
            pallets = 12
        });

        Assert.Equal("PM", result.PlanningWindow);
        Assert.True(result.RunsOvernight);
        Assert.Equal("PM Overnight", result.SuggestedRouteType);
    }

    [Fact]
    public void Barefoots_am_wording_does_not_force_pm_when_same_day()
    {
        var result = Classify(new
        {
            customerCode = "BAREFOOTS",
            poNumber = "BF-AM-001",
            collectionDate = "2026-09-15",
            deliveryDate = "2026-09-15",
            sellerName = "Barefoots AM",
            stallNumber = "Customer depot",
            requestedTime = "08:00",
            pallets = 8
        });

        Assert.Equal("AM", result.PlanningWindow);
        Assert.False(result.RunsOvernight);
    }

    [Fact]
    public void Markets_are_pm_market_routes()
    {
        var result = Classify(new
        {
            customerCode = "MARKETS",
            poNumber = "COVENT-001",
            collectionDate = "2026-09-15",
            deliveryDate = "2026-09-16",
            marketName = "Covent Garden Market",
            sellerName = "Supplier",
            stallNumber = "Covent",
            pallets = 4
        });

        Assert.Equal("Market", result.PlanningWindow);
        Assert.True(result.RunsOvernight);
        Assert.Equal("Market Overnight", result.SuggestedRouteType);
    }

    [Fact]
    public void Delivery_date_without_collection_becomes_pm_candidate_not_blocked_preorder()
    {
        var result = Classify(new
        {
            customerCode = "APS",
            poNumber = "APS-OK-001",
            deliveryDate = "2026-09-16",
            sellerName = "APS",
            stallNumber = "Oliver Kay",
            driverInstructions = "Deliver to Oliver Kay for delivery on the 16th",
            pallets = 4
        });

        Assert.Equal("PM", result.PlanningWindow);
        Assert.Equal("Medium", result.Confidence);
        Assert.True(result.RequiresPlannerReview);
        Assert.Contains("collection date is missing", result.Reason);
    }

    [Fact]
    public void Enrich_releases_cross_date_preorder_to_ready_for_review()
    {
        var enriched = OrderPlanningWindowClassifier.Enrich(JsonSerializer.SerializeToElement(new
        {
            customerCode = "APS",
            poNumber = "APS-OK-002",
            collectionDate = "2026-09-15",
            deliveryDate = "2026-09-16",
            sellerName = "APS",
            stallNumber = "Oliver Kay",
            intakeStatus = "PreOrder",
            plannerReady = false,
            pallets = 4
        }));

        Assert.Equal("PM", enriched.GetProperty("planningWindow").GetString());
        Assert.True(enriched.GetProperty("runsOvernight").GetBoolean());
        Assert.Equal("PM Overnight", enriched.GetProperty("suggestedRouteType").GetString());
        Assert.Equal("ReadyForReview", enriched.GetProperty("intakeStatus").GetString());
        Assert.True(enriched.GetProperty("plannerReady").GetBoolean());
    }

    private static PlanningWindowClassification Classify(object payload) =>
        OrderPlanningWindowClassifier.Classify(JsonSerializer.SerializeToElement(payload));
}
