using System.Diagnostics;
using System.Text.Json;

namespace Slh.Tms.Api.Services;

public sealed record BetaRoutePoint(string Name, decimal Latitude, decimal Longitude);

public sealed record BetaHgvRouteCost(decimal Miles, int DriveMinutes, string Source);

public interface IBetaHgvRouteProvider
{
    Task<BetaHgvRouteCost?> GetRouteAsync(IReadOnlyList<BetaRoutePoint> points, CancellationToken ct);
}

/// <summary>
/// Supplies route evidence for the Beta Optimiser. Only a real Azure Maps truck route
/// is accepted. AzureMapsRouteClient's resilient Haversine fallback is deliberately
/// rejected here so approximate straight-line evidence can never drive an optimiser
/// recommendation.
/// </summary>
public sealed class AzureMapsHgvRouteProvider(
    AzureMapsRouteClient maps,
    ILogger<AzureMapsHgvRouteProvider> logger,
    BetaOptimiserOptions? optimiserOptions = null) : IBetaHgvRouteProvider
{
    private readonly BetaOptimiserOptions options = optimiserOptions ?? new BetaOptimiserOptions();

    public async Task<BetaHgvRouteCost?> GetRouteAsync(
        IReadOnlyList<BetaRoutePoint> points,
        CancellationToken ct)
    {
        if (points.Count < 2) return new BetaHgvRouteCost(0m, 0, "AzureMapsHgv");

        try
        {
            var response = await maps.Directions(
                points.Select(point => (point.Longitude, point.Latitude)).ToList(),
                ct);

            using var document = JsonDocument.Parse(JsonSerializer.Serialize(response));
            var root = document.RootElement;

            if (root.TryGetProperty("approximate", out var approximate) &&
                approximate.ValueKind == JsonValueKind.True)
            {
                logger.LogWarning(
                    "Beta Optimiser rejected approximate routing evidence for {StopCount} stops.",
                    points.Count);
                return null;
            }

            if (!root.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0 ||
                !routes[0].TryGetProperty("summary", out var summary) ||
                !summary.TryGetProperty("lengthInMeters", out var metres) ||
                !summary.TryGetProperty("travelTimeInSeconds", out var seconds))
            {
                logger.LogWarning("Azure Maps HGV routing did not return a usable route summary.");
                return null;
            }

            var miles = Math.Round((decimal)metres.GetDouble() / 1609.344m, 2);
            var minutes = (int)Math.Ceiling(seconds.GetDouble() / 60d);
            var provider = root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
                ? source.GetString()
                : null;

            return new BetaHgvRouteCost(
                miles,
                minutes,
                string.IsNullOrWhiteSpace(provider) ? "AzureMapsHgv" : provider!);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Azure Maps HGV evidence exceeded the {DeadlineSeconds}s Beta route deadline for {StopCount} stops; the run will be returned as unrouted rather than failing the comparison.", options.RouteDeadline.TotalSeconds, points.Count);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Azure Maps HGV evidence was unavailable to the Beta Optimiser.");
            return null;
        }
    }
}

/// <summary>
/// Applies one wall-clock routing budget to the whole read-only Beta request. Once the budget
/// is exhausted, later route checks return unavailable immediately so the API can still return
/// reconciliation and workload evidence before the upstream gateway timeout. No approximate
/// route is substituted. A fresh instance must be created for each Beta request.
/// </summary>
public sealed class BudgetedBetaHgvRouteProvider : IBetaHgvRouteProvider
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(60);

    private readonly IBetaHgvRouteProvider inner;
    private readonly ILogger<BudgetedBetaHgvRouteProvider> logger;
    private readonly TimeSpan budget;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private int budgetWarningLogged;

    public BudgetedBetaHgvRouteProvider(
        IBetaHgvRouteProvider inner,
        ILogger<BudgetedBetaHgvRouteProvider> logger,
        TimeSpan? budget = null)
    {
        this.inner = inner;
        this.logger = logger;
        this.budget = budget ?? DefaultBudget;
    }

    public async Task<BetaHgvRouteCost?> GetRouteAsync(IReadOnlyList<BetaRoutePoint> points, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var remaining = budget - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            LogBudgetExhausted();
            return null;
        }

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(remaining);
        try
        {
            return await inner.GetRouteAsync(points, budgetCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budgetCts.IsCancellationRequested)
        {
            LogBudgetExhausted();
            return null;
        }
    }

    private void LogBudgetExhausted()
    {
        if (Interlocked.Exchange(ref budgetWarningLogged, 1) != 0) return;
        logger.LogWarning(
            "Beta Optimiser live HGV routing budget of {BudgetSeconds}s was exhausted. Remaining route checks will be returned as unavailable so the comparison can complete before the gateway timeout.",
            budget.TotalSeconds);
    }
}
