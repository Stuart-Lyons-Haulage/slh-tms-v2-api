using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class IntegrationIdentityTests
{
    [Theory]
    [InlineData("UK1234567890123456", "567890123456", true)]
    [InlineData("567890123456", "UK1234567890123456", true)]
    [InlineData("UK1234567890123456", "UK1234567890123456", true)]
    [InlineData("UK1234567890123456", "999999999999", false)]
    [InlineData("1234", "1234", false)]
    public void Tachograph_card_matching_accepts_full_and_short_forms(string left, string right, bool expected)
        => Assert.Equal(expected, IntegrationSyncCoordinator.CardsMatch(left, right));
}
