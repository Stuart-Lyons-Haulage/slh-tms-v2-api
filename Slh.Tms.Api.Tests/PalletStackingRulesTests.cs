using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PalletStackingRulesTests
{
    [Fact]
    public void BarfootsIfcoEvidenceIsEligibleButNotApproved()
    {
        var result = PalletHandlingRules.ResolveStacking("Barfoots", "{\"material\":\"4310\",\"description\":\"IFCO Green Plus\",\"palletType\":\"CHEP1210\"}", "CHEP1210 pallets");
        Assert.True(result.Eligible);
        Assert.Equal(2m, result.MaxLevels);
        Assert.Equal(0m, result.StackablePallets);
    }

    [Fact]
    public void OtherCustomersAreNotAutomaticallyStackable()
    {
        var result = PalletHandlingRules.ResolveStacking("Other", "CHEP1210 IFCO", "CHEP1210 pallets");
        Assert.False(result.Eligible);
    }
}
