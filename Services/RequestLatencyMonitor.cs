using System.Collections.Concurrent;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Keeps a small, readable picture of how slow the API has been recently, so "the system is
/// slow" can be pinned to specific endpoints without needing Application Insights access.
/// Per-route rolling stats over a short window, plus the slowest individual calls.
/// </summary>
public sealed class RequestLatencyMonitor
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, RouteStats> routes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<SlowCall> slowest = new();

    public void Record(string method, string route, double ms, int statusCode)
    {
        if (route.StartsWith("/api/v1/diagnostics", StringComparison.OrdinalIgnoreCase)) return;
        var key = $"{method} {route}";
        routes.AddOrUpdate(key, _ => RouteStats.First(ms), (_, existing) => existing.Add(ms)).Trim();
        if (ms >= 1500)
        {
            slowest.Enqueue(new SlowCall(DateTimeOffset.UtcNow, method, route, Math.Round(ms), statusCode));
            while (slowest.Count > 50 && slowest.TryDequeue(out _)) { }
        }
    }

    public Summary Read(DateTimeOffset now)
    {
        var since = now - Window;
        var rows = routes
            .Select(pair => pair.Value.Snapshot(pair.Key))
            .Where(row => row.Count > 0)
            .OrderByDescending(row => row.P95Ms)
            .Take(25)
            .ToList();
        var recentSlow = slowest.Where(call => call.AtUtc >= since).OrderByDescending(call => call.Ms).Take(20).ToList();
        return new Summary(now, (int)Window.TotalMinutes, rows, recentSlow);
    }

    public sealed record Row(string Endpoint, int Count, double AvgMs, double P95Ms, double MaxMs);
    public sealed record SlowCall(DateTimeOffset AtUtc, string Method, string Route, double Ms, int StatusCode);
    public sealed record Summary(DateTimeOffset GeneratedAtUtc, int WindowMinutes, IReadOnlyList<Row> Endpoints, IReadOnlyList<SlowCall> RecentSlow);

    private sealed class RouteStats
    {
        private readonly object sync = new();
        private readonly List<double> samples = [];
        private DateTimeOffset firstAt = DateTimeOffset.UtcNow;

        public static RouteStats First(double ms) { var stats = new RouteStats(); stats.samples.Add(ms); return stats; }

        public RouteStats Add(double ms)
        {
            lock (sync)
            {
                samples.Add(ms);
                if (samples.Count > 2000) samples.RemoveRange(0, samples.Count - 2000);
            }
            return this;
        }

        public RouteStats Trim()
        {
            // Reset a route's window after 15 quiet minutes so old spikes don't linger.
            if (DateTimeOffset.UtcNow - firstAt > TimeSpan.FromMinutes(30))
                lock (sync) { if (DateTimeOffset.UtcNow - firstAt > TimeSpan.FromMinutes(30)) { samples.Clear(); firstAt = DateTimeOffset.UtcNow; } }
            return this;
        }

        public Row Snapshot(string endpoint)
        {
            lock (sync)
            {
                if (samples.Count == 0) return new Row(endpoint, 0, 0, 0, 0);
                var sorted = samples.OrderBy(value => value).ToList();
                var p95 = sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * 0.95))];
                return new Row(endpoint, sorted.Count, Math.Round(sorted.Average()), Math.Round(p95), Math.Round(sorted[^1]));
            }
        }
    }
}
