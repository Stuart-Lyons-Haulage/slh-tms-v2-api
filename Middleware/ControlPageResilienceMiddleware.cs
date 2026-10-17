namespace Slh.Tms.Api.Middleware;

/// <summary>
/// Compatible fallback payloads for read-only operational intelligence routes.
/// The original exception is logged with a trace ID; authentication and write
/// operations are never bypassed.
/// </summary>
internal static class ControlPageFallback
{
    private static readonly HashSet<string> ProtectedGetPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/intelligence/attention",
        "/api/v1/intelligence/readiness",
        "/api/v1/intelligence/plan-stability",
        "/api/v1/intelligence/freshness",
        "/api/v1/management/resilient-summary",
        "/api/v1/management/eta-precision"
    };

    public static bool IsProtectedGet(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) && ProtectedGetPaths.Contains(request.Path.Value ?? string.Empty);

    public static async Task WriteAsync(HttpContext context, Exception exception)
    {
        if (context.Response.HasStarted) throw exception;

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Tms.ControlPageResilience");
        logger.LogError(exception,
            "Control page query failed for {Path}. Returning degraded operational response. Trace {TraceId}",
            context.Request.Path, context.TraceIdentifier);

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers["X-SLH-Degraded"] = "true";
        context.Response.Headers["X-SLH-Trace"] = context.TraceIdentifier;
        await context.Response.WriteAsJsonAsync(Build(context), context.RequestAborted);
    }

    private static object Build(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var now = DateTimeOffset.UtcNow;
        var warning = $"Some live management data is temporarily unavailable. Core TMS operations remain available. Reference {context.TraceIdentifier}.";

        if (path.EndsWith("/attention", StringComparison.OrdinalIgnoreCase))
        {
            var day = QueryDate(context, "date", DateOnly.FromDateTime(DateTime.UtcNow));
            var items = new[]
            {
                new
                {
                    id = "degraded-live-intelligence",
                    severity = "High",
                    type = "DataUnavailable",
                    title = "Live attention data temporarily unavailable",
                    detail = warning,
                    entityId = (string?)null,
                    entityType = (string?)null,
                    href = "/operations-control"
                }
            };
            return new { planningDate = day, generatedAtUtc = now, count = items.Length, items, degraded = true, warnings = new[] { warning } };
        }

        if (path.EndsWith("/readiness", StringComparison.OrdinalIgnoreCase))
        {
            var day = QueryDate(context, "date", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)));
            return new
            {
                planningDate = day,
                generatedAtUtc = now,
                ready = false,
                runs = 0,
                assignedDrivers = 0,
                activeDrivers = 0,
                assignedVehicles = 0,
                activeVehicles = 0,
                missingAllocations = 0,
                vorConflicts = 0,
                tachoConcerns = 0,
                geofenceGaps = 0,
                unreviewedOrders = 0,
                planLock = (object?)null,
                degraded = true,
                warnings = new[] { warning }
            };
        }

        if (path.EndsWith("/plan-stability", StringComparison.OrdinalIgnoreCase))
        {
            var to = QueryDate(context, "to", DateOnly.FromDateTime(DateTime.UtcNow));
            var from = QueryDate(context, "from", to.AddDays(-29));
            return new
            {
                from,
                to,
                lockedDays = 0,
                baselineRuns = 0,
                changedRuns = 0,
                stabilityPercent = (decimal?)null,
                driverSwaps = 0,
                vehicleSwaps = 0,
                routeAmendments = 0,
                runChanges = 0,
                changes = Array.Empty<object>(),
                dataAvailable = false,
                degraded = true,
                warnings = new[] { warning }
            };
        }

        if (path.EndsWith("/freshness", StringComparison.OrdinalIgnoreCase))
        {
            var sources = new[]
            {
                new { name = "Tracking", lastUpdatedUtc = (DateTimeOffset?)null, ageMinutes = (double?)null, state = "red" },
                new { name = "Tacho", lastUpdatedUtc = (DateTimeOffset?)null, ageMinutes = (double?)null, state = "red" },
                new { name = "Info mailbox", lastUpdatedUtc = (DateTimeOffset?)null, ageMinutes = (double?)null, state = "red" },
                new { name = "Sage HR", lastUpdatedUtc = (DateTimeOffset?)null, ageMinutes = (double?)null, state = "red" }
            };
            return new { generatedAtUtc = now, sources, degraded = true, warnings = new[] { warning } };
        }

        if (path.EndsWith("/eta-precision", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                dataAvailable = false,
                samples = 0,
                within10MinutesPercent = (decimal?)null,
                within15MinutesPercent = (decimal?)null,
                within30MinutesPercent = (decimal?)null,
                meanAbsoluteErrorMinutes = (double?)null,
                message = warning,
                degraded = true
            };
        }

        var managementTo = QueryDate(context, "to", DateOnly.FromDateTime(DateTime.UtcNow));
        var managementFrom = QueryDate(context, "from", managementTo.AddDays(-6));
        return new
        {
            from = managementFrom,
            to = managementTo,
            generatedAtUtc = now,
            degraded = true,
            warnings = new[] { warning },
            headline = new
            {
                orders = 0,
                runs = 0,
                completedRuns = 0,
                runCompletionPercent = (decimal?)null,
                plannedStops = 0,
                completedStops = 0,
                stopCompletionPercent = (decimal?)null,
                measuredDeliveries = 0,
                onTimeDeliveries = 0,
                onTimeDeliveryPercent = (decimal?)null,
                averageSiteDwellMinutes = (double?)null,
                siteDelays = 0,
                passThroughs = 0,
                attentionRuns = 0,
                attentionRatePercent = (decimal?)null
            },
            efficiency = new
            {
                allocatedRuns = 0,
                allocationPercent = (decimal?)null,
                assignedVehicles = 0,
                activeVehicles = 0,
                fleetUtilisationPercent = (decimal?)null,
                assignedDrivers = 0,
                activeDrivers = 0,
                driverUtilisationPercent = (decimal?)null,
                loadUtilisationPercent = (decimal?)null,
                totalMiles = 0m,
                emptyMiles = 0m,
                emptyMilesPercent = (decimal?)null
            },
            etaPrecision = new
            {
                dataAvailable = false,
                samples = 0,
                within10MinutesPercent = (decimal?)null,
                within15MinutesPercent = (decimal?)null,
                within30MinutesPercent = (decimal?)null,
                meanAbsoluteErrorMinutes = (double?)null,
                message = warning
            },
            customers = Array.Empty<object>(),
            sites = Array.Empty<object>(),
            days = Array.Empty<object>()
        };
    }

    private static DateOnly QueryDate(HttpContext context, string key, DateOnly fallback) =>
        DateOnly.TryParse(context.Request.Query[key].FirstOrDefault(), out var parsed) ? parsed : fallback;
}
