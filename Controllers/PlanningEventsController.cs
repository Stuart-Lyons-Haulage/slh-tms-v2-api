using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/planning-events")]
[Authorize]
public sealed class PlanningEventsController(PlanningChangeNotifier notifier) : ControllerBase
{
    [HttpGet("stream")]
    public async Task Stream(CancellationToken ct)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no";

        var subscription = notifier.Subscribe();
        try
        {
            await Response.WriteAsync(": connected\n\n", ct);
            await Response.Body.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var readReady = subscription.Reader.WaitToReadAsync(cycle.Token).AsTask();
                var heartbeat = Task.Delay(TimeSpan.FromSeconds(25), cycle.Token);
                var completed = await Task.WhenAny(readReady, heartbeat);

                if (completed == readReady && await readReady)
                {
                    cycle.Cancel();
                    while (subscription.Reader.TryRead(out var change))
                        await Response.WriteAsync($"event: planning-data-changed\nid: {change.Sequence}\ndata: {change.ChangedAtUtc:O}\n\n", ct);
                }
                else
                {
                    cycle.Cancel();
                    await Response.WriteAsync($": heartbeat {DateTimeOffset.UtcNow:O}\n\n", ct);
                }

                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            notifier.Unsubscribe(subscription.Id);
        }
    }
}
