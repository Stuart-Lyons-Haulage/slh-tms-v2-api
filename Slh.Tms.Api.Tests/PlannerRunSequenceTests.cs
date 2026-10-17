using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlannerRunSequenceTests
{
    [Theory]
    [InlineData("2026-08-24T11:59:00+01:00", "AM")]
    [InlineData("2026-08-24T12:00:00+01:00", "PM")]
    [InlineData("2026-08-24T17:00:00+01:00", "PM")]
    public void Period_uses_noon_boundary(string timestamp, string expected)
    {
        Assert.Equal(expected, PlannerRunSequenceController.Period(DateTimeOffset.Parse(timestamp)));
    }
}
