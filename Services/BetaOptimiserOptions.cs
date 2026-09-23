namespace Slh.Tms.Api.Services;

/// <summary>
/// Operational policy for the read-only Beta optimiser. Latitude values are deliberately only
/// coarse directional gates; live Azure Maps HGV routing remains authoritative for road mileage.
/// Defaults preserve the reviewed September 2026 behaviour and can be overridden through
/// BetaOptimiser__* configuration/environment settings without a code deployment.
/// </summary>
public sealed class BetaOptimiserOptions
{
    public const string SectionName = "BetaOptimiser";

    /// <summary>Minimum latitude reduction for a movement to be treated as southbound (~3.5 miles at UK latitudes).</summary>
    public decimal MinSouthboundLatitudeDelta { get; set; } = 0.05m;

    /// <summary>Maximum permitted northward latitude reposition from the outbound terminal to the backhaul collection.</summary>
    public decimal MaxNorthwardBackhaulDetourLatitude { get; set; } = 0.25m;

    /// <summary>Multiplier applied to standalone backhaul road miles when deciding whether the incremental combined route is sensible.</summary>
    public decimal MaxBackhaulIncrementFactor { get; set; } = 1.35m;

    /// <summary>Fixed road-mile allowance added to the standalone backhaul threshold.</summary>
    public decimal BackhaulIncrementAllowanceMiles { get; set; } = 15m;

    public int RouteDeadlineSeconds { get; set; } = 12;
    public int RequestRoutingBudgetSeconds { get; set; } = 60;

    /// <summary>Planning dwell added for each distinct collection or delivery stop.</summary>
    public int AverageDwellMinutes { get; set; } = 20;

    /// <summary>Conservative percentage added to live route travel time for scheduling only.</summary>
    public int TrafficBufferPercent { get; set; } = 15;

    /// <summary>Maximum planned duty span from first collection to final delivery.</summary>
    public int MaxDayLengthMinutes { get; set; } = 900;

    /// <summary>Maximum daily driving time used before a driver-specific Tacho assignment.</summary>
    public int MaxDailyDrivingMinutes { get; set; } = 540;

    public TimeSpan RouteDeadline => TimeSpan.FromSeconds(RouteDeadlineSeconds);
    public TimeSpan RequestRoutingBudget => TimeSpan.FromSeconds(RequestRoutingBudgetSeconds);

    public BetaOptimiserOptions Validate()
    {
        if (MinSouthboundLatitudeDelta <= 0m || MinSouthboundLatitudeDelta > 5m)
            throw new InvalidOperationException("BetaOptimiser:MinSouthboundLatitudeDelta must be greater than 0 and no more than 5 degrees.");
        if (MaxNorthwardBackhaulDetourLatitude < 0m || MaxNorthwardBackhaulDetourLatitude > 5m)
            throw new InvalidOperationException("BetaOptimiser:MaxNorthwardBackhaulDetourLatitude must be between 0 and 5 degrees.");
        if (MaxBackhaulIncrementFactor < 1m || MaxBackhaulIncrementFactor > 10m)
            throw new InvalidOperationException("BetaOptimiser:MaxBackhaulIncrementFactor must be between 1 and 10.");
        if (BackhaulIncrementAllowanceMiles < 0m || BackhaulIncrementAllowanceMiles > 500m)
            throw new InvalidOperationException("BetaOptimiser:BackhaulIncrementAllowanceMiles must be between 0 and 500 miles.");
        if (RouteDeadlineSeconds is < 1 or > 60)
            throw new InvalidOperationException("BetaOptimiser:RouteDeadlineSeconds must be between 1 and 60 seconds.");
        if (RequestRoutingBudgetSeconds is < 5 or > 300)
            throw new InvalidOperationException("BetaOptimiser:RequestRoutingBudgetSeconds must be between 5 and 300 seconds.");
        if (RequestRoutingBudgetSeconds < RouteDeadlineSeconds)
            throw new InvalidOperationException("BetaOptimiser:RequestRoutingBudgetSeconds must not be shorter than RouteDeadlineSeconds.");
        if (AverageDwellMinutes is < 0 or > 240)
            throw new InvalidOperationException("BetaOptimiser:AverageDwellMinutes must be between 0 and 240.");
        if (TrafficBufferPercent is < 0 or > 100)
            throw new InvalidOperationException("BetaOptimiser:TrafficBufferPercent must be between 0 and 100.");
        if (MaxDayLengthMinutes is < 60 or > 1440)
            throw new InvalidOperationException("BetaOptimiser:MaxDayLengthMinutes must be between 60 and 1440.");
        if (MaxDailyDrivingMinutes is < 60 or > 600)
            throw new InvalidOperationException("BetaOptimiser:MaxDailyDrivingMinutes must be between 60 and 600.");
        return this;
    }
}
