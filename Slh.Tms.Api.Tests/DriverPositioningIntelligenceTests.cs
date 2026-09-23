using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DriverPositioningIntelligenceTests
{
    private static readonly PositionPoint Chichester = new(50.8367, -0.7792);

    [Fact]
    public void DayFiveDriverGetsStrongPreferenceForRouteEndingCloserToHome()
    {
        var current = new PositionPoint(54.9783, -1.6178); // Newcastle area
        var northStart = new PositionPoint(53.8008, -1.5491); // Leeds area
        var southEnd = new PositionPoint(51.5074, -0.1278); // London area
        var northEnd = new PositionPoint(54.9783, -1.6178);

        var homeward = DriverPositioningIntelligence.Score(
            consecutiveDutyDays: 5,
            livePosition: current,
            previousFinish: null,
            candidateStart: northStart,
            candidateEnd: southEnd,
            homeBase: Chichester);

        var staysNorth = DriverPositioningIntelligence.Score(
            consecutiveDutyDays: 5,
            livePosition: current,
            previousFinish: null,
            candidateStart: northStart,
            candidateEnd: northEnd,
            homeBase: Chichester);

        Assert.True(homeward.TotalScore > staysNorth.TotalScore);
        Assert.True(homeward.IsHomewardBackload);
        Assert.Contains(homeward.Reasons, reason => reason.Contains("home", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EarlyCycleDriverDoesNotReceiveDayFourFiveHomewardBonus()
    {
        var current = new PositionPoint(54.9783, -1.6178);
        var start = new PositionPoint(53.8008, -1.5491);
        var end = new PositionPoint(51.5074, -0.1278);

        var early = DriverPositioningIntelligence.Score(2, current, null, start, end, Chichester);
        var late = DriverPositioningIntelligence.Score(5, current, null, start, end, Chichester);

        Assert.True(late.TotalScore > early.TotalScore);
        Assert.False(early.IsHomewardBackload);
        Assert.True(late.IsHomewardBackload);
    }

    [Fact]
    public void LiveTrackingTakesPriorityOverPreviousFinishForStartProximity()
    {
        var live = new PositionPoint(53.8008, -1.5491); // close to candidate work
        var previousFinish = new PositionPoint(50.8367, -0.7792); // home area
        var start = new PositionPoint(53.7457, -0.3367); // Hull area
        var end = new PositionPoint(52.4862, -1.8904); // Birmingham area

        var withLive = DriverPositioningIntelligence.Score(4, live, previousFinish, start, end, Chichester);
        var withoutLive = DriverPositioningIntelligence.Score(4, null, previousFinish, start, end, Chichester);

        Assert.True(withLive.TotalScore > withoutLive.TotalScore);
        Assert.Contains(withLive.Reasons, reason => reason.Contains("live tracking", StringComparison.OrdinalIgnoreCase));
    }
}
