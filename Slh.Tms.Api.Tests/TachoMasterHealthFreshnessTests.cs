using Slh.Tms.Api.Controllers;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoMasterHealthFreshnessTests
{
    [Theory]
    [InlineData(null, "unknown")]
    [InlineData(0d, "live")]
    [InlineData(15d, "live")]
    [InlineData(15.1d, "delayed")]
    [InlineData(30d, "delayed")]
    [InlineData(30.1d, "stale")]
    [InlineData(1320d, "stale")]
    public void JobFreshness_ClassifiesFiveMinuteSchedulerLag(double? ageMinutes, string expected)
    {
        Assert.Equal(expected, TachoMasterHealthController.JobFreshness(ageMinutes));
    }
}
