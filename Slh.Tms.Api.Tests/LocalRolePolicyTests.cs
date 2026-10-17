using System.Security.Claims;
using Slh.Tms.Api.Authorization;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class LocalRolePolicyTests
{
    private static ClaimsPrincipal Local(string role) => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.Name, "Test User"),
        new Claim("preferred_username", "test"),
        new Claim("auth_source", "local"),
        new Claim(ClaimTypes.Role, role),
        new Claim("roles", role)
    }, "test"));

    [Fact]
    public void Read_only_user_can_read_but_cannot_write_or_approve()
    {
        var user = Local("TMS.ReadOnly");
        Assert.True(TmsLocalRolePolicy.CanRead(user, ["lyonshaulage.com"]));
        Assert.False(TmsLocalRolePolicy.CanWrite(user, ["lyonshaulage.com"]));
        Assert.False(TmsLocalRolePolicy.CanApprove(user, ["lyonshaulage.com"]));
    }

    [Theory]
    [InlineData("TMS.Admin", true)]
    [InlineData("TMS.Management", true)]
    [InlineData("TMS.Planner", true)]
    [InlineData("TMS.Transport", true)]
    [InlineData("TMS.Warehouse", false)]
    [InlineData("TMS.Accounts", false)]
    public void Approval_roles_are_explicit(string role, bool expected)
    {
        Assert.Equal(expected, TmsLocalRolePolicy.CanApprove(Local(role), ["lyonshaulage.com"]));
    }

    [Theory]
    [InlineData("TMS.Admin")]
    [InlineData("TMS.Management")]
    [InlineData("TMS.Planner")]
    [InlineData("TMS.Transport")]
    [InlineData("TMS.Warehouse")]
    [InlineData("TMS.Accounts")]
    public void Operational_roles_can_write(string role)
    {
        Assert.True(TmsLocalRolePolicy.CanWrite(Local(role), ["lyonshaulage.com"]));
    }
}
