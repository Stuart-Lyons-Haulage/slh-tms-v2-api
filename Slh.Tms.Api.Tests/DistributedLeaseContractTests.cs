using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DistributedLeaseContractTests
{
    [Theory]
    [InlineData(IntegrationLeaseNames.TachoMaster)]
    [InlineData(IntegrationLeaseNames.Fleetio)]
    [InlineData(IntegrationLeaseNames.SageHr)]
    public void Integration_lease_names_are_valid(string leaseId)
    {
        DistributedLeaseManager.Validate(leaseId, TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Lease_rejects_invalid_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DistributedLeaseManager.Validate("job:test", TimeSpan.Zero));
    }
}
