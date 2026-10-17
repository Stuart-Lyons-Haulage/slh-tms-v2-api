namespace Slh.Tms.Api.Services;

/// <summary>
/// Shares identical live-route work inside one Beta request. Only successful route evidence is
/// retained. Null/unavailable results and faulted/cancelled tasks are evicted so a transient
/// Azure Maps failure cannot poison the same route key for the rest of the request.
/// </summary>
internal sealed class BetaRequestRouteCache
{
    private readonly Dictionary<string, Task<BetaHgvRouteCost?>> entries = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public async Task<BetaHgvRouteCost?> GetOrCreateAsync(
        string key,
        Func<Task<BetaHgvRouteCost?>> factory)
    {
        Task<BetaHgvRouteCost?> task;
        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing))
            {
                task = existing;
            }
            else
            {
                task = factory();
                entries[key] = task;
            }
        }

        try
        {
            var result = await task.ConfigureAwait(false);
            if (result is null) RemoveIfCurrent(key, task);
            return result;
        }
        catch
        {
            RemoveIfCurrent(key, task);
            throw;
        }
    }

    private void RemoveIfCurrent(string key, Task<BetaHgvRouteCost?> task)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var current) && ReferenceEquals(current, task))
                entries.Remove(key);
        }
    }
}
