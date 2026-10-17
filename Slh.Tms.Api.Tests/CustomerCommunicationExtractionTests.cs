using System.Text.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class CustomerCommunicationExtractionTests
{
    private readonly CustomerCommunicationExtractionService service = new();

    [Fact]
    public void Classifies_load_plan_and_amendment_from_real_subject_pattern()
    {
        var result = service.Extract(new MailboxEmailIntakeRequest(
            "msg-1", null, "info@lyonshaulage.com", "michael@lyonshaulage.com", "Michael Lyons",
            "RE: UPDATED Barfoots Load Plan-28082026", DateTimeOffset.UtcNow,
            "Please see amended.", null, null,
            [new MailboxAttachmentRequest("Barfoots Collections Load Plan.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", null)]));

        Assert.Equal("LoadPlan", result.Purpose);
        Assert.Equal("Amended", result.PlanVersion);
        Assert.Contains("Barfoots", result.CustomerHints);
        Assert.Single(result.Attachments);
    }

    [Fact]
    public void Extracts_eta_window_exception_and_next_update_from_body()
    {
        var result = service.Extract(new MailboxEmailIntakeRequest(
            "msg-2", null, "info@lyonshaulage.com", "michael@lyonshaulage.com", "Michael Lyons",
            "RE: NWF ALDI NESTON", DateTimeOffset.UtcNow,
            "Load 11, 16 pallets. Breakdown caused delay. Current ETA 19:00 to 19:15. We will have a more accurate ETA at 17:30. Customer can accept until 19:30.", null, null, null));

        Assert.Equal("EtaUpdate", result.Purpose);
        Assert.Equal("11", result.Claims[0].LoadReference);
        Assert.Equal(16, result.Claims[0].Pallets);
        Assert.Equal("19:00", result.Claims[0].EtaFromLocal);
        Assert.Equal("19:15", result.Claims[0].EtaToLocal);
        Assert.Equal("17:30", result.NextUpdateLocal);
        Assert.Contains("breakdown", result.ExceptionSignals, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("19:30", result.AcceptanceUntilLocal);
    }

    [Fact]
    public void Same_message_id_produces_stable_idempotency_key_and_never_orders()
    {
        var result = service.Extract(new MailboxEmailIntakeRequest(
            "same-message", null, "info@lyonshaulage.com", "andrew@lyonshaulage.com", "Andrew Walker",
            "Avonmouth Trunks ETA", DateTimeOffset.UtcNow,
            "Truck 1: Barfoots, Groves farm, Langmeads: ETA 21:00 https://www.falcontracking.co.uk/viewer/individual/abc", null, null, null));

        Assert.Equal("communication:same-message", result.IdempotencyKey);
        Assert.Equal("PendingReview", result.ReviewStatus);
        Assert.DoesNotContain("TransportOrder", JsonSerializer.Serialize(result));
    }
}
