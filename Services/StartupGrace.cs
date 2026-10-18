namespace Slh.Tms.Api.Services;

/// <summary>
/// Background jobs (tacho, telematics, ETAs, integration syncs) wait briefly after start-up
/// so the first page loads after a deploy or restart get the database to themselves.
/// </summary>
public static class StartupGrace
{
    private static readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;
    private static TimeSpan grace = TimeSpan.FromSeconds(90);

    public static void Configure(TimeSpan value) => grace = value < TimeSpan.Zero ? TimeSpan.Zero : value;

    public static async Task WaitAsync(CancellationToken ct, TimeSpan? extra = null)
    {
        var remaining = StartedAtUtc + grace + (extra ?? TimeSpan.Zero) - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
    }
}
