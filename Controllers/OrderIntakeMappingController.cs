using System.Data;
using System.Data.Common;
using System.Net.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/customer-email-mappings")]
[Authorize]
public sealed class CustomerEmailMappingsController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? customerCode, [FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var query = db.CustomerEmailRoutes.AsNoTracking().AsQueryable();
        if (!includeInactive) query = query.Where(x => x.Active);
        if (!string.IsNullOrWhiteSpace(customerCode))
            query = query.Where(x => x.CustomerCode == customerCode.Trim().ToUpper());

        var rows = await query
            .OrderBy(x => x.CustomerCode)
            .ThenBy(x => x.SenderDomain)
            .ThenBy(x => x.SenderEmail)
            .Select(x => new
            {
                x.Id,
                x.CustomerCode,
                emailAddress = x.SenderEmail,
                emailDomain = x.SenderDomain,
                subjectContains = x.SubjectContains,
                parserType = x.ParserType,
                x.RequiresReview,
                x.Active
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpPost, Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Create(CustomerEmailMappingRequest request, CancellationToken ct)
    {
        var validation = await Validate(request.CustomerCode, request.EmailAddress, request.EmailDomain, request.SubjectContains, null, ct);
        if (validation.Error is not null) return validation.Error;

        var row = new CustomerEmailRoute
        {
            CustomerCode = validation.CustomerCode!,
            SenderEmail = validation.Email,
            SenderDomain = validation.Domain,
            SubjectContains = Clip(request.SubjectContains, 200),
            ParserType = Clip(request.ParserType, 120),
            RequiresReview = request.RequiresReview ?? true,
            Active = request.Active ?? true
        };
        db.CustomerEmailRoutes.Add(row);
        AddAudit(row.Id, "CustomerEmailMappingCreated", row.CustomerCode, row);
        try
        {
            await db.SaveChangesAsync(ct);
            CustomerEmailRouteService.InvalidateCache();
            return Created($"/api/v1/customer-email-mappings/{row.Id}", new { row.Id });
        }
        catch (DbUpdateException ex) when (LooksLikeDuplicate(ex))
        {
            return Conflict(new { error = "duplicate_customer_email_mapping", message = "An active mapping with the same customer, sender/domain and subject rule already exists.", correlationId = HttpContext.TraceIdentifier });
        }
    }

    [HttpPatch("{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Update(Guid id, CustomerEmailMappingRequest request, CancellationToken ct)
    {
        var row = await db.CustomerEmailRoutes.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return NotFound();

        var customerCode = request.CustomerCode ?? row.CustomerCode;
        var email = request.EmailAddress ?? row.SenderEmail;
        var domain = request.EmailDomain ?? row.SenderDomain;
        var subject = request.SubjectContains ?? row.SubjectContains;
        var validation = await Validate(customerCode, email, domain, subject, id, ct);
        if (validation.Error is not null) return validation.Error;

        var before = new { row.CustomerCode, row.SenderEmail, row.SenderDomain, row.SubjectContains, row.ParserType, row.RequiresReview, row.Active };
        row.CustomerCode = validation.CustomerCode!;
        row.SenderEmail = validation.Email;
        row.SenderDomain = validation.Domain;
        row.SubjectContains = Clip(subject, 200);
        if (request.ParserType is not null) row.ParserType = Clip(request.ParserType, 120);
        if (request.RequiresReview.HasValue) row.RequiresReview = request.RequiresReview.Value;
        if (request.Active.HasValue) row.Active = request.Active.Value;
        AddAudit(row.Id, "CustomerEmailMappingUpdated", row.CustomerCode, new { before, after = row });

        try
        {
            await db.SaveChangesAsync(ct);
            CustomerEmailRouteService.InvalidateCache();
            return Ok(new { row.Id });
        }
        catch (DbUpdateException ex) when (LooksLikeDuplicate(ex))
        {
            return Conflict(new { error = "duplicate_customer_email_mapping", message = "An active mapping with the same customer, sender/domain and subject rule already exists.", correlationId = HttpContext.TraceIdentifier });
        }
    }

    [HttpDelete("{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var row = await db.CustomerEmailRoutes.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return NotFound();
        row.Active = false;
        AddAudit(row.Id, "CustomerEmailMappingDeactivated", row.CustomerCode, row);
        await db.SaveChangesAsync(ct);
        CustomerEmailRouteService.InvalidateCache();
        return NoContent();
    }

    private async Task<(string? CustomerCode, string? Email, string? Domain, IActionResult? Error)> Validate(string? customerCode, string? email, string? domain, string? subjectContains, Guid? existingId, CancellationToken ct)
    {
        var code = customerCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code))
            return (null, null, null, BadRequest(new { error = "customer_required", message = "CustomerCode is required.", correlationId = HttpContext.TraceIdentifier }));
        if (!await db.Customers.AsNoTracking().AnyAsync(x => x.Active && x.Code == code, ct))
            return (null, null, null, BadRequest(new { error = "unknown_customer", message = $"Customer {code} does not exist as an active SQL master customer.", correlationId = HttpContext.TraceIdentifier }));

        var normalizedEmail = CustomerEmailRouteService.NormalizeEmail(email);
        var normalizedDomain = NormalizeDomain(domain);
        if (normalizedEmail is null && normalizedDomain is null)
            return (null, null, null, BadRequest(new { error = "sender_required", message = "At least one of EmailAddress or EmailDomain is required.", correlationId = HttpContext.TraceIdentifier }));
        if (!string.IsNullOrWhiteSpace(email) && normalizedEmail is null)
            return (null, null, null, BadRequest(new { error = "invalid_email", message = "EmailAddress is not a valid email address.", correlationId = HttpContext.TraceIdentifier }));
        if (normalizedEmail is not null) normalizedDomain ??= normalizedEmail[(normalizedEmail.IndexOf('@') + 1)..];

        var subject = Clip(subjectContains, 200);
        var duplicate = await db.CustomerEmailRoutes.AsNoTracking().AnyAsync(x =>
            x.Active && x.Id != existingId && x.CustomerCode == code && x.SenderEmail == normalizedEmail && x.SenderDomain == normalizedDomain && x.SubjectContains == subject, ct);
        if (duplicate)
            return (null, null, null, Conflict(new { error = "duplicate_customer_email_mapping", message = "An active mapping with the same customer, sender/domain and subject rule already exists.", correlationId = HttpContext.TraceIdentifier }));

        return (code, normalizedEmail, normalizedDomain, null);
    }

    private void AddAudit(Guid id, string action, string customerCode, object changes) => db.MasterDataAudits.Add(new MasterDataAudit
    {
        EntityType = "CustomerEmailRoute",
        EntityId = id,
        Action = action,
        ChangedBy = User.Identity?.Name ?? User.FindFirst("oid")?.Value ?? "TMS user",
        ChangesJson = JsonSerializer.Serialize(new { customerCode, changes })
    });

    private static string? NormalizeDomain(string? value)
    {
        var normalized = value?.Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (normalized.Contains(' ') || !normalized.Contains('.')) return null;
        return normalized;
    }

    private static string? Clip(string? value, int length)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed.Length <= length ? trimmed : trimmed[..length];
    }

    private static bool LooksLikeDuplicate(DbUpdateException ex)
    {
        var message = ex.GetBaseException().Message;
        return message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) || message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record CustomerEmailMappingRequest(
    string? CustomerCode,
    string? EmailAddress,
    string? EmailDomain,
    string? SubjectContains,
    string? ParserType,
    bool? RequiresReview,
    bool? Active);

[ApiController]
[Route("api/v1/order-intake-route-rules")]
[Authorize]
public sealed class OrderIntakeRouteRulesController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? customerCode, [FromQuery] bool includeInactive = false, CancellationToken ct = default)
    {
        var sql = "SELECT Id, CustomerCode, OriginSiteCode, OriginSiteName, RetailerCode, DestinationSiteCode, DestinationCode, DestinationName, DestinationPostcode, Priority, ConfidenceScore, Active, EffectiveFrom, EffectiveTo, Notes, CreatedAtUtc, UpdatedAtUtc FROM dbo.OrderIntakeRouteRules WHERE (@includeInactive = 1 OR Active = 1) AND (@customerCode IS NULL OR CustomerCode = @customerCode) ORDER BY CustomerCode, Priority, OriginSiteName, RetailerCode, DestinationCode, DestinationName";
        var rows = await ReadRows(sql, [
            ("@includeInactive", includeInactive ? 1 : 0),
            ("@customerCode", string.IsNullOrWhiteSpace(customerCode) ? DBNull.Value : customerCode.Trim().ToUpperInvariant())
        ], ct);
        return Ok(rows);
    }

    [HttpPost, Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Create(OrderIntakeRouteRuleRequest request, CancellationToken ct)
    {
        var normalized = await ValidateAndNormalize(request, null, ct);
        if (normalized.Error is not null) return normalized.Error;
        var row = normalized.Row!;
        row.Id = Guid.NewGuid();
        await Execute("""
            INSERT dbo.OrderIntakeRouteRules
                (Id, CustomerCode, OriginSiteCode, OriginSiteName, RetailerCode, DestinationSiteCode, DestinationCode, DestinationName, DestinationPostcode, Priority, ConfidenceScore, Active, EffectiveFrom, EffectiveTo, Notes, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                (@Id, @CustomerCode, @OriginSiteCode, @OriginSiteName, @RetailerCode, @DestinationSiteCode, @DestinationCode, @DestinationName, @DestinationPostcode, @Priority, @ConfidenceScore, @Active, @EffectiveFrom, @EffectiveTo, @Notes, SYSUTCDATETIME(), SYSUTCDATETIME())
            """, Parameters(row), ct);
        await Audit(row.Id, "OrderIntakeRouteRuleCreated", row, ct);
        CustomerEmailRouteService.InvalidateCache();
        return Created($"/api/v1/order-intake-route-rules/{row.Id}", row);
    }

    [HttpPatch("{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Update(Guid id, OrderIntakeRouteRuleRequest request, CancellationToken ct)
    {
        var current = (await ReadRows("SELECT Id, CustomerCode, OriginSiteCode, OriginSiteName, RetailerCode, DestinationSiteCode, DestinationCode, DestinationName, DestinationPostcode, Priority, ConfidenceScore, Active, EffectiveFrom, EffectiveTo, Notes, CreatedAtUtc, UpdatedAtUtc FROM dbo.OrderIntakeRouteRules WHERE Id = @id", [("@id", id)], ct)).SingleOrDefault();
        if (current is null) return NotFound();

        var merged = request.Merge(current);
        var normalized = await ValidateAndNormalize(merged, id, ct);
        if (normalized.Error is not null) return normalized.Error;
        var row = normalized.Row!;
        row.Id = id;
        try
        {
            await Execute("""
                UPDATE dbo.OrderIntakeRouteRules SET
                    CustomerCode=@CustomerCode, OriginSiteCode=@OriginSiteCode, OriginSiteName=@OriginSiteName,
                    RetailerCode=@RetailerCode, DestinationSiteCode=@DestinationSiteCode, DestinationCode=@DestinationCode,
                    DestinationName=@DestinationName, DestinationPostcode=@DestinationPostcode, Priority=@Priority,
                    ConfidenceScore=@ConfidenceScore, Active=@Active, EffectiveFrom=@EffectiveFrom, EffectiveTo=@EffectiveTo,
                    Notes=@Notes, UpdatedAtUtc=SYSUTCDATETIME()
                WHERE Id=@Id
                """, Parameters(row), ct);
        }
        catch (Exception ex) when (LooksLikeDuplicate(ex))
        {
            return Conflict(new { error = "duplicate_route_rule", message = "An active route rule with the same customer/origin/retailer/destination combination already exists.", correlationId = HttpContext.TraceIdentifier });
        }
        await Audit(id, "OrderIntakeRouteRuleUpdated", new { before = current, after = row }, ct);
        CustomerEmailRouteService.InvalidateCache();
        return Ok(row);
    }

    [HttpDelete("{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var changed = await Execute("UPDATE dbo.OrderIntakeRouteRules SET Active=0, UpdatedAtUtc=SYSUTCDATETIME() WHERE Id=@id AND Active=1", [("@id", id)], ct);
        if (changed == 0) return NotFound();
        await Audit(id, "OrderIntakeRouteRuleDeactivated", new { active = false }, ct);
        CustomerEmailRouteService.InvalidateCache();
        return NoContent();
    }

    private async Task<(OrderIntakeRouteRuleRow? Row, IActionResult? Error)> ValidateAndNormalize(OrderIntakeRouteRuleRequest request, Guid? id, CancellationToken ct)
    {
        var code = request.CustomerCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code)) return (null, BadRequest(new { error = "customer_required", message = "CustomerCode is required.", correlationId = HttpContext.TraceIdentifier }));
        if (!await db.Customers.AsNoTracking().AnyAsync(x => x.Active && x.Code == code, ct))
            return (null, BadRequest(new { error = "unknown_customer", message = $"Customer {code} does not exist as an active SQL master customer.", correlationId = HttpContext.TraceIdentifier }));
        if (request.ConfidenceScore is < 0 or > 100) return (null, BadRequest(new { error = "invalid_confidence", message = "ConfidenceScore must be between 0 and 100.", correlationId = HttpContext.TraceIdentifier }));
        if (request.Priority is < 0 or > 10000) return (null, BadRequest(new { error = "invalid_priority", message = "Priority must be between 0 and 10000.", correlationId = HttpContext.TraceIdentifier }));
        if (request.EffectiveFrom.HasValue && request.EffectiveTo.HasValue && request.EffectiveTo < request.EffectiveFrom)
            return (null, BadRequest(new { error = "invalid_effective_dates", message = "EffectiveTo cannot be before EffectiveFrom.", correlationId = HttpContext.TraceIdentifier }));

        var row = new OrderIntakeRouteRuleRow
        {
            Id = id ?? Guid.Empty,
            CustomerCode = code,
            OriginSiteCode = Upper(request.OriginSiteCode, 80),
            OriginSiteName = Text(request.OriginSiteName, 200),
            RetailerCode = Upper(request.RetailerCode, 80),
            DestinationSiteCode = Upper(request.DestinationSiteCode, 80),
            DestinationCode = Upper(request.DestinationCode, 80),
            DestinationName = Text(request.DestinationName, 200),
            DestinationPostcode = Upper(request.DestinationPostcode, 20),
            Priority = request.Priority ?? 100,
            ConfidenceScore = request.ConfidenceScore ?? 80,
            Active = request.Active ?? true,
            EffectiveFrom = request.EffectiveFrom,
            EffectiveTo = request.EffectiveTo,
            Notes = Text(request.Notes, 1000)
        };
        if (new[] { row.OriginSiteCode, row.OriginSiteName, row.RetailerCode, row.DestinationSiteCode, row.DestinationCode, row.DestinationName, row.DestinationPostcode }.All(string.IsNullOrWhiteSpace))
            return (null, BadRequest(new { error = "route_dimension_required", message = "A route rule must contain at least one origin, retailer or destination dimension in addition to CustomerCode.", correlationId = HttpContext.TraceIdentifier }));
        return (row, null);
    }

    private async Task<List<OrderIntakeRouteRuleRow>> ReadRows(string sql, IReadOnlyCollection<(string Name, object Value)> parameters, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            var result = new List<OrderIntakeRouteRuleRow>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) result.Add(Read(reader));
            return result;
        }
        finally { if (openedHere) await connection.CloseAsync(); }
    }

    private async Task<int> Execute(string sql, IReadOnlyCollection<(string Name, object Value)> parameters, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally { if (openedHere) await connection.CloseAsync(); }
    }

    private async Task Audit(Guid id, string action, object changes, CancellationToken ct)
    {
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "OrderIntakeRouteRule",
            EntityId = id,
            Action = action,
            ChangedBy = User.Identity?.Name ?? User.FindFirst("oid")?.Value ?? "TMS user",
            ChangesJson = JsonSerializer.Serialize(changes)
        });
        await db.SaveChangesAsync(ct);
    }

    private static IReadOnlyCollection<(string Name, object Value)> Parameters(OrderIntakeRouteRuleRow row) =>
    [
        ("@Id", row.Id), ("@CustomerCode", row.CustomerCode), ("@OriginSiteCode", Db(row.OriginSiteCode)),
        ("@OriginSiteName", Db(row.OriginSiteName)), ("@RetailerCode", Db(row.RetailerCode)),
        ("@DestinationSiteCode", Db(row.DestinationSiteCode)), ("@DestinationCode", Db(row.DestinationCode)),
        ("@DestinationName", Db(row.DestinationName)), ("@DestinationPostcode", Db(row.DestinationPostcode)),
        ("@Priority", row.Priority), ("@ConfidenceScore", row.ConfidenceScore), ("@Active", row.Active),
        ("@EffectiveFrom", Db(row.EffectiveFrom)), ("@EffectiveTo", Db(row.EffectiveTo)), ("@Notes", Db(row.Notes))
    ];

    private static void AddParameters(DbCommand command, IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    private static OrderIntakeRouteRuleRow Read(DbDataReader r) => new()
    {
        Id = r.GetGuid(0), CustomerCode = r.GetString(1), OriginSiteCode = NullString(r, 2), OriginSiteName = NullString(r, 3),
        RetailerCode = NullString(r, 4), DestinationSiteCode = NullString(r, 5), DestinationCode = NullString(r, 6),
        DestinationName = NullString(r, 7), DestinationPostcode = NullString(r, 8), Priority = r.GetInt32(9), ConfidenceScore = r.GetInt32(10),
        Active = r.GetBoolean(11), EffectiveFrom = NullDate(r, 12), EffectiveTo = NullDate(r, 13), Notes = NullString(r, 14),
        CreatedAtUtc = r.IsDBNull(15) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(15), DateTimeKind.Utc)),
        UpdatedAtUtc = r.IsDBNull(16) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(16), DateTimeKind.Utc))
    };

    private static string? NullString(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateOnly? NullDate(DbDataReader r, int i) => r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i));
    private static object Db(object? value) => value ?? DBNull.Value;
    private static string? Text(string? value, int length) { var x = value?.Trim(); return string.IsNullOrWhiteSpace(x) ? null : x.Length <= length ? x : x[..length]; }
    private static string? Upper(string? value, int length) => Text(value, length)?.ToUpperInvariant();
    private static bool LooksLikeDuplicate(Exception ex) { var m = ex.GetBaseException().Message; return m.Contains("duplicate", StringComparison.OrdinalIgnoreCase) || m.Contains("unique", StringComparison.OrdinalIgnoreCase); }
}

public sealed record OrderIntakeRouteRuleRequest(
    string? CustomerCode,
    string? OriginSiteCode,
    string? OriginSiteName,
    string? RetailerCode,
    string? DestinationSiteCode,
    string? DestinationCode,
    string? DestinationName,
    string? DestinationPostcode,
    int? Priority,
    int? ConfidenceScore,
    bool? Active,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    string? Notes)
{
    public OrderIntakeRouteRuleRequest Merge(OrderIntakeRouteRuleRow current) => new(
        CustomerCode ?? current.CustomerCode,
        OriginSiteCode ?? current.OriginSiteCode,
        OriginSiteName ?? current.OriginSiteName,
        RetailerCode ?? current.RetailerCode,
        DestinationSiteCode ?? current.DestinationSiteCode,
        DestinationCode ?? current.DestinationCode,
        DestinationName ?? current.DestinationName,
        DestinationPostcode ?? current.DestinationPostcode,
        Priority ?? current.Priority,
        ConfidenceScore ?? current.ConfidenceScore,
        Active ?? current.Active,
        EffectiveFrom ?? current.EffectiveFrom,
        EffectiveTo ?? current.EffectiveTo,
        Notes ?? current.Notes);
}

public sealed class OrderIntakeRouteRuleRow
{
    public Guid Id { get; set; }
    public string CustomerCode { get; set; } = string.Empty;
    public string? OriginSiteCode { get; set; }
    public string? OriginSiteName { get; set; }
    public string? RetailerCode { get; set; }
    public string? DestinationSiteCode { get; set; }
    public string? DestinationCode { get; set; }
    public string? DestinationName { get; set; }
    public string? DestinationPostcode { get; set; }
    public int Priority { get; set; }
    public int ConfidenceScore { get; set; }
    public bool Active { get; set; }
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? CreatedAtUtc { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}
