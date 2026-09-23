using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Middleware;

/// <summary>
/// Keeps read-only Site lookup available when production is temporarily behind the
/// latest optional Site columns. The normal LookupsController response remains
/// authoritative; this only supplies the stable Site core on schema-related failure.
/// </summary>
public sealed class SiteLookupResilienceMiddleware(RequestDelegate next, ILogger<SiteLookupResilienceMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, TmsDbContext db)
    {
        if (!HttpMethods.IsGet(context.Request.Method) ||
            !context.Request.Path.Equals("/api/v1/sites", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        try
        {
            await next(context);
        }
        catch (Exception exception) when (SchemaUnavailable(exception) && !context.Response.HasStarted)
        {
            logger.LogWarning(exception,
                "Site lookup hit unavailable optional schema; returning stable core Site projection.");

            db.ChangeTracker.Clear();
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

            var queryText = context.Request.Query["q"].FirstOrDefault()?.Trim();
            try
            {
                var query = db.Sites.AsNoTracking()
                    .Where(site => site.Active)
                    .Select(site => new Site
                    {
                        Id = site.Id,
                        ExternalCode = site.ExternalCode,
                        Name = site.Name,
                        DriverTextName = site.DriverTextName,
                        Active = site.Active
                    });

                if (!string.IsNullOrWhiteSpace(queryText))
                {
                    query = query.Where(site =>
                        site.Name.Contains(queryText) ||
                        (site.DriverTextName != null && site.DriverTextName.Contains(queryText)) ||
                        site.ExternalCode.Contains(queryText));
                }

                var rows = await query.OrderBy(site => site.Name).Take(5000).ToListAsync(context.RequestAborted);
                await context.Response.WriteAsJsonAsync(rows, context.RequestAborted);
            }
            catch (Exception fallbackException)
            {
                logger.LogError(fallbackException, "Stable Site lookup fallback also failed.");
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        message = "Site lookup is temporarily unavailable while the production schema is being repaired.",
                        code = "SITE_SCHEMA_UNAVAILABLE"
                    }, context.RequestAborted);
                }
            }
        }
    }

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }
}
