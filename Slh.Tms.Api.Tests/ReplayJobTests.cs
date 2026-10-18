using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class ReplayJobTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public ReplayJobTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Re_parse_runs_as_a_job_and_finishes()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write,Tms.Approve");
        var start = await client.PostAsJsonAsync("/api/v1/order-intake/replay-jobs", new
        {
            receivedFromUtc = "2026-10-17T00:00:00Z", receivedToUtc = "2026-10-20T00:00:00Z",
            minimumPlanningDate = "2026-10-19", maximumPlanningDate = "2026-10-19", refreshUnamendedPending = true
        });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var id = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        JsonElement status = default;
        for (var i = 0; i < 100; i++)
        {
            status = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/order-intake/replay-jobs/{id}")).RootElement.Clone();
            if (status.GetProperty("done").GetBoolean()) break;
            await Task.Delay(100);
        }
        Assert.True(status.GetProperty("done").GetBoolean());
    }
}
