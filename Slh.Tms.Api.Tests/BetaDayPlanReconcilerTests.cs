using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class BetaDayPlanReconcilerTests
{
    [Fact]
    public void Reconcile_ReportsMissingAndExtraOrdersAndSuppressesMisleadingMileageDelta()
    {
        var beta = new[]
        {
            new BetaComparisonOrderLine("PO-1", "Runcton", "Leeds", 9),
            new BetaComparisonOrderLine("PO-2", "Selsey", "Cardiff", 3),
        };
        var lyons = new[]
        {
            new BetaComparisonOrderLine("PO-1", "Runcton", "Leeds", 9),
            new BetaComparisonOrderLine("PO-3", "Merston", "Bristol", 4),
        };

        var result = BetaDayPlanReconciler.Reconcile(
            beta, lyons,
            betaRunCount: 2, lyonsRunCount: 2,
            betaRoutingComplete: true, lyonsRoutingComplete: true,
            betaMiles: 200m, lyonsMiles: 180m,
            betaDriveMinutes: 300, lyonsDriveMinutes: 280);

        Assert.Equal(1, result.MatchedOrderLines);
        Assert.Contains(result.MissingFromLyons, item => item.Contains("PO-2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.OnlyInLyons, item => item.Contains("PO-3", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.OrderCoverageComplete);
        Assert.False(result.ComparableRouting);
        Assert.Null(result.MilesDelta);
        Assert.Null(result.DriveMinutesDelta);
    }
}
