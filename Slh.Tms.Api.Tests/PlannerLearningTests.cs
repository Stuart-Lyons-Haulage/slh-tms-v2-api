using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlannerLearningTests
{
    private static PlannerLearning.Decision D(string customer, string destination, string run, int pallets, int dayOffset = 0, string weekday = "Tue")
    {
        var at = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero).AddDays(dayOffset);
        return new PlannerLearning.Decision(DateOnly.FromDateTime(at.UtcDateTime), Guid.NewGuid(), $"REF{dayOffset}", customer, "SLH", destination,
            "Southbound collections", Guid.NewGuid(), run, PlannerLearning.RunPattern(run), pallets, weekday, at, "planner");
    }

    [Fact]
    public void Run_reference_normalises_to_a_repeatable_pattern()
    {
        Assert.Equal("RUN 2 AM", PlannerLearning.RunPattern("RUN 2 AM 19.10.26"));
        Assert.Equal("RUN 2 AM", PlannerLearning.RunPattern("Run 2 AM"));
        Assert.Equal(PlannerLearning.RunPattern("RUN 4 PM 20/10/2026"), PlannerLearning.RunPattern("RUN 4 PM 21/10/2026"));
    }

    [Fact]
    public void Learns_the_usual_run_for_a_customer_destination_with_confidence()
    {
        var decisions = new List<PlannerLearning.Decision>
        {
            D("VITACRESS", "Runcton", "RUN 2 AM 19.10", 6, 0),
            D("VITACRESS", "Runcton", "RUN 2 AM 20.10", 6, 1),
            D("VITACRESS", "Runcton", "RUN 2 AM 21.10", 4, 2),
            D("VITACRESS", "Runcton", "RUN 5 PM 22.10", 6, 3), // one override
        };
        var insights = PlannerLearning.Learn(decisions, 90, DateTimeOffset.UtcNow);
        var preference = Assert.Single(insights.Preferences);
        Assert.Equal("VITACRESS", preference.Customer);
        Assert.Equal("RUN 2 AM", preference.Pattern);
        Assert.Equal(75, preference.Confidence);
        Assert.Equal(4, preference.Samples);
        Assert.Equal(6, preference.TypicalPallets);
        Assert.Equal(75, insights.Consistency);
    }

    [Fact]
    public void A_group_seen_only_once_or_inconsistently_is_not_claimed_as_a_preference()
    {
        var decisions = new List<PlannerLearning.Decision> { D("ONEOFF", "Nowhere", "RUN 1 AM", 3, 0) };
        Assert.Empty(PlannerLearning.Learn(decisions, 90, DateTimeOffset.UtcNow).Preferences);
    }

    [Fact]
    public void Order_hint_points_at_todays_matching_run_and_flags_a_size_jump()
    {
        var history = new List<PlannerLearning.Decision>
        {
            D("VITACRESS", "Runcton", "RUN 2 AM 19.10", 6, 0),
            D("VITACRESS", "Runcton", "RUN 2 AM 20.10", 6, 1),
            D("VITACRESS", "Runcton", "RUN 2 AM 21.10", 6, 2),
        };
        var todaysRun = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var hints = PlannerLearning.HintsFor(history,
            [(orderId, "VITACRESS", "Runcton", 60)],
            [(todaysRun, "RUN 2 AM 22.10.26")],
            DateTimeOffset.UtcNow);
        var hint = Assert.Single(hints);
        Assert.Equal(orderId, hint.OrderId);
        Assert.Equal(todaysRun, hint.MatchingLoadId);
        Assert.Contains("RUN 2 AM 22.10.26", hint.Message);
        Assert.NotNull(hint.PalletWarning);
        Assert.Contains("this is 60", hint.PalletWarning);
    }
}
