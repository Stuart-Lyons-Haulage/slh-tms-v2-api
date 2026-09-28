using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TimesheetEvidenceRulesTests
{
    [Fact]
    public void First_movement_is_selected_from_the_duty_vehicle_after_card_insert()
    {
        var start = DateTimeOffset.Parse("2026-09-23T18:00:00Z");
        var result = TimesheetEvidenceRules.SelectMovementWindow(new[]
        {
            Event("OTHER", start.AddMinutes(1)),
            Event("FN69AHY", start.AddMinutes(35)),
            Event("FN69AHY", start.AddHours(4))
        }, new[] { "FN69AHY" }, start, start.AddHours(5));

        Assert.Equal(start.AddMinutes(35), result.FirstUtc);
    }

    [Fact]
    public void Last_movement_is_before_duty_finish_and_not_a_later_global_event()
    {
        var start = DateTimeOffset.Parse("2026-09-23T18:00:00Z");
        var result = TimesheetEvidenceRules.SelectMovementWindow(new[]
        {
            Event("FN69AHY", start.AddMinutes(30)),
            Event("FN69AHY", start.AddHours(5)),
            Event("FN69AHY", start.AddHours(8))
        }, new[] { "FN69AHY" }, start, start.AddHours(6));

        Assert.Equal(start.AddHours(5), result.LastUtc);
    }

    [Fact]
    public void Night_out_regular_rest_requires_eleven_hours_away_from_depot()
    {
        var end = DateTimeOffset.Parse("2026-09-24T04:00:00Z");
        var result = TimesheetEvidenceRules.AssessNightOut(end, end.AddHours(11), end.AddMinutes(-5), 53.8m, -1.2m, new[] { new DepotPoint(52.0m, -1.0m) }, true);
        Assert.Equal("Confirmed Night Out - Regular Rest", result.Status);
    }

    [Fact]
    public void Night_out_reduced_rest_is_between_nine_and_eleven_hours()
    {
        var end = DateTimeOffset.Parse("2026-09-24T04:00:00Z");
        var result = TimesheetEvidenceRules.AssessNightOut(end, end.AddHours(10), end, 53.8m, -1.2m, Array.Empty<DepotPoint>(), true);
        Assert.Equal("Confirmed Night Out - Reduced Rest", result.Status);
    }

    [Fact]
    public void Returning_to_home_depot_is_not_a_night_out()
    {
        var end = DateTimeOffset.Parse("2026-09-24T04:00:00Z");
        var result = TimesheetEvidenceRules.AssessNightOut(end, end.AddHours(11), end, 52m, -1m, new[] { new DepotPoint(52m, -1m) }, true);
        Assert.Equal("No Night Out", result.Status);
    }

    [Fact]
    public void Incomplete_evidence_is_possible_night_out_not_auto_payable()
    {
        var end = DateTimeOffset.Parse("2026-09-24T04:00:00Z");
        var result = TimesheetEvidenceRules.AssessNightOut(end, end.AddHours(11), null, null, null, Array.Empty<DepotPoint>(), true);
        Assert.Equal("Possible Night Out", result.Status);
    }

    private static DotTelemetryRecord Event(string vehicle, DateTimeOffset time) => new("id-" + vehicle + time.Ticks, vehicle, time, 53.8m, -1.2m, 20m, true, true, "Received", "{}");
}
