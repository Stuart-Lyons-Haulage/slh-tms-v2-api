using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record TachoDriverHoursRefreshResult(
    string Status,
    bool Updated,
    Guid DriverId,
    string DisplayName,
    string? TachoMasterDriverId,
    string? TachoCardNumber,
    string? TachoVehicleCode,
    DateTimeOffset? CardInsertedUtc,
    DateTimeOffset? CurrentDutyEndUtc,
    bool TachoDutyOpen,
    int? TachoWorkTodayMinutes,
    int? TachoDriveTodayMinutes,
    int? TachoAvailableTodayMinutes,
    int? TachoRestTodayMinutes,
    int? TachoBreakCount,
    int? TachoBreakMinutes,
    int? TachoDriveAvailableTodayMinutes,
    int? TachoDriveAvailableWeekMinutes,
    int? TachoWorkAvailableWeekMinutes,
    DateTimeOffset? LastTachoSyncUtc,
    string IdentitySource,
    string Message);

public sealed class TachoDriverHoursRefreshService(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    TachoMasterOptions options,
    DistributedLeaseManager leases,
    ILogger<TachoDriverHoursRefreshService> logger)
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public async Task<int> RefreshDriverHoursOnlyAsync(string actor, CancellationToken ct)
    {
        await using var lease = await leases.TryAcquireAsync(
            IntegrationLeaseNames.TachoMaster,
            TimeSpan.FromSeconds(30),
            ct);

        if (lease is null)
        {
            logger.LogInformation(
                "TachoMaster lightweight driver-hours refresh skipped because another distributed writer currently holds the integration lease.");
            return 0;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LostToken);

        try
        {
            return await RefreshDriverHoursOnlyCoreAsync(actor, linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("TachoMaster lightweight driver-hours refresh stopped because the distributed lease was lost.");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TachoMaster lightweight driver-hours refresh failed. The next scheduled pass will still run.");
            return 0;
        }
    }

    public async Task<TachoDriverHoursRefreshResult> RefreshDriverHoursOnlyAsync(Guid driverId, string actor, CancellationToken ct)
    {
        await using var lease = await leases.TryAcquireAsync(
            IntegrationLeaseNames.TachoMaster,
            TimeSpan.FromSeconds(30),
            ct);

        if (lease is null)
        {
            return Empty(
                "lease_busy",
                false,
                driverId,
                "TachoMaster driver-hours refresh skipped because another distributed writer currently holds the integration lease.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LostToken);

        try
        {
            return await RefreshSingleDriverHoursOnlyCoreAsync(driverId, actor, linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("TachoMaster single-driver hours refresh for {DriverId} stopped because the distributed lease was lost.", driverId);
            return Empty(
                "lease_lost",
                false,
                driverId,
                "TachoMaster driver-hours refresh stopped because the distributed lease was lost.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TachoMaster single-driver hours refresh failed for {DriverId}. Actor: {Actor}", driverId, actor);
            return Empty(
                "failed",
                false,
                driverId,
                $"TachoMaster driver-hours refresh failed: {ex.GetBaseException().Message}");
        }
    }

    private async Task<int> RefreshDriverHoursOnlyCoreAsync(string actor, CancellationToken ct)
    {
        if (!options.IsConfigured)
        {
            logger.LogInformation("TachoMaster lightweight driver-hours refresh skipped because TachoMaster is not configured.");
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, London).DateTime);
        var profilesTask = tachoMaster.GetDriverProfilesAsync(ct);
        var dutiesTask = tachoMaster.GetDriverDutyStatusesAsync(today, ct);
        await Task.WhenAll(profilesTask, dutiesTask);

        var profiles = await profilesTask;
        var duties = await dutiesTask;
        var profilesByMemberCode = ProfilesByMemberCode(profiles);
        var profilesByCard = ProfilesByCardNumber(profiles);

        if (profilesByMemberCode.Count == 0 && profilesByCard.Count == 0)
        {
            logger.LogWarning("TachoMaster lightweight driver-hours refresh returned no usable driver profiles.");
            return 0;
        }

        var drivers = await db.Drivers
            .Where(driver => driver.Active)
            .OrderBy(driver => driver.DisplayName)
            .ToListAsync(ct);

        var updated = 0;
        foreach (var driver in drivers)
        {
            var persistedCard = await ReadPersistedTachoCardNumberAsync(driver.Id, ct) ?? driver.TachoCardNumber;
            var match = MatchProfile(driver.TachoMasterDriverId, persistedCard, profilesByMemberCode, profilesByCard);
            if (match is null) continue;

            var todayDuties = MatchDuties(duties, match.Profile.MemberCode, persistedCard, match.Profile.CardNumber);
            var state = TachoDriverState.From(driver, persistedCard, match.Profile, BuildDutyEvidence(todayDuties), now, match.IdentitySource);
            ApplyState(driver, state);
            await PersistStateAsync(driver.Id, state, ct);
            updated++;
        }

        if (updated == 0)
        {
            logger.LogInformation(
                "TachoMaster lightweight driver-hours refresh found profiles, but none matched active TMS drivers by Member Code or persisted DB Tacho card number.");
            return 0;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "TachoMaster lightweight driver-hours refresh updated {UpdatedDrivers} active driver(s) by Member Code with DB Tacho card fallback. Actor: {Actor}",
            updated,
            actor);
        return updated;
    }

    private async Task<TachoDriverHoursRefreshResult> RefreshSingleDriverHoursOnlyCoreAsync(Guid driverId, string actor, CancellationToken ct)
    {
        var driver = await db.Drivers.FirstOrDefaultAsync(item => item.Id == driverId, ct);
        if (driver is null)
        {
            return Empty("not_found", false, driverId, "Driver was not found in Driver Master.");
        }

        if (!driver.Active)
        {
            return Result("inactive", false, TachoDriverState.FromDriverOnly(driver, await ReadPersistedTachoCardNumberAsync(driver.Id, ct)),
                "Driver is inactive, so TachoMaster hours were not refreshed.");
        }

        var persistedCard = await ReadPersistedTachoCardNumberAsync(driver.Id, ct) ?? driver.TachoCardNumber;
        var memberCode = TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoMasterDriverId);
        var cardKey = NormaliseCard(persistedCard);
        if (memberCode.Length == 0 && cardKey.Length == 0)
        {
            return Result("missing_tacho_identity", false, TachoDriverState.FromDriverOnly(driver, persistedCard),
                "Driver has no TachoMaster Member Code and no persisted DB Tacho card number, so TachoMaster hours were not refreshed.");
        }

        if (!options.IsConfigured)
        {
            return Result("not_configured", false, TachoDriverState.FromDriverOnly(driver, persistedCard),
                "TachoMaster is not configured, so driver hours were not refreshed.");
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        var profilesTask = tachoMaster.GetDriverProfilesAsync(ct);
        var dutiesTask = tachoMaster.GetDriverDutyStatusesAsync(today, ct);
        await Task.WhenAll(profilesTask, dutiesTask);

        var profiles = await profilesTask;
        var duties = await dutiesTask;
        var match = MatchProfile(driver.TachoMasterDriverId, persistedCard, ProfilesByMemberCode(profiles), ProfilesByCardNumber(profiles));

        if (match is null)
        {
            return Result("profile_not_found", false, TachoDriverState.FromDriverOnly(driver, persistedCard),
                memberCode.Length > 0
                    ? $"TachoMaster did not return a profile for Member Code {driver.TachoMasterDriverId}."
                    : "TachoMaster did not return a profile matching the persisted DB Tacho card number.");
        }

        var todayDuties = MatchDuties(duties, match.Profile.MemberCode, persistedCard, match.Profile.CardNumber);
        var state = TachoDriverState.From(driver, persistedCard, match.Profile, BuildDutyEvidence(todayDuties), DateTimeOffset.UtcNow, match.IdentitySource);
        ApplyState(driver, state);
        await PersistStateAsync(driver.Id, state, ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "TachoMaster single-driver refresh updated hours and duty evidence for {DriverId} / {DriverName}. Identity: {IdentitySource}. Actor: {Actor}",
            driver.Id,
            driver.DisplayName,
            state.IdentitySource,
            actor);

        return Result("updated", true, state,
            todayDuties.Count == 0
                ? $"TachoMaster driver hours refreshed by {state.IdentitySource}, but no card/duty insertion was found for today."
                : $"TachoMaster driver hours refreshed by {state.IdentitySource} with card inserted / first sign-on evidence for ETA and compliance use.");
    }

    private static Dictionary<string, TachoDriverProfile> ProfilesByMemberCode(IReadOnlyList<TachoDriverProfile> profiles) => profiles
        .Where(profile => profile.MemberCode > 0)
        .GroupBy(profile => TachoDriverIdentityRules.NormaliseIdentifier(profile.MemberCode.ToString(CultureInfo.InvariantCulture)), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, TachoDriverProfile> ProfilesByCardNumber(IReadOnlyList<TachoDriverProfile> profiles) => profiles
        .Select(profile => new { Key = NormaliseCard(profile.CardNumber), Profile = profile })
        .Where(item => item.Key.Length >= 8)
        .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().Profile, StringComparer.OrdinalIgnoreCase);

    private static TachoProfileMatch? MatchProfile(
        string? tachoMasterDriverId,
        string? persistedCardNumber,
        IReadOnlyDictionary<string, TachoDriverProfile> profilesByMemberCode,
        IReadOnlyDictionary<string, TachoDriverProfile> profilesByCard)
    {
        var memberCode = TachoDriverIdentityRules.NormaliseIdentifier(tachoMasterDriverId);
        if (memberCode.Length > 0)
        {
            return profilesByMemberCode.TryGetValue(memberCode, out var profile)
                ? new(profile, "TachoMasterDriverId")
                : null;
        }

        var card = NormaliseCard(persistedCardNumber);
        if (card.Length < 8) return null;
        return profilesByCard.TryGetValue(card, out var cardProfile)
            ? new(cardProfile, "TachoCardNumber")
            : null;
    }

    private static IReadOnlyList<TachoDriverDutyStatus> MatchDuties(
        IReadOnlyList<TachoDriverDutyStatus> duties,
        int memberCode,
        string? persistedCard,
        string? profileCard)
    {
        var member = TachoDriverIdentityRules.NormaliseIdentifier(memberCode.ToString(CultureInfo.InvariantCulture));
        var dbCard = NormaliseCard(persistedCard);
        var tachoCard = NormaliseCard(profileCard);
        return duties.Where(duty =>
            string.Equals(
                TachoDriverIdentityRules.NormaliseIdentifier(duty.MemberCode.ToString(CultureInfo.InvariantCulture)),
                member,
                StringComparison.OrdinalIgnoreCase)
            || CardsMatch(dbCard, NormaliseCard(duty.CardNumber))
            || CardsMatch(tachoCard, NormaliseCard(duty.CardNumber)))
            .OrderBy(item => item.DutyStartUtc)
            .ToList();
    }

    private static TachoDutyEvidence? BuildDutyEvidence(IReadOnlyList<TachoDriverDutyStatus> duties)
    {
        if (duties.Count == 0) return null;
        var first = duties.OrderBy(item => item.DutyStartUtc).First();
        var current = duties
            .OrderByDescending(item => item.DutyEndUtc is null)
            .ThenByDescending(item => item.DutyStartUtc)
            .First();

        return new(
            first.CardNumber,
            current.VehicleCode,
            first.DutyStartUtc,
            current.DutyEndUtc,
            current.DutyEndUtc is null,
            duties.Sum(item => item.WorkMinutes),
            duties.Sum(item => item.DriveMinutes),
            duties.Sum(item => item.AvailableMinutes),
            duties.Sum(item => item.RestMinutes),
            duties.Sum(item => item.BreakCount),
            duties.Any(item => item.BreakMinutes is not null) ? duties.Sum(item => item.BreakMinutes ?? 0) : null);
    }

    private static void ApplyState(Driver driver, TachoDriverState state)
    {
        driver.TachoMasterDriverId = string.IsNullOrWhiteSpace(driver.TachoMasterDriverId) ? state.TachoMasterDriverId : driver.TachoMasterDriverId;
        driver.TachoCardNumber = state.TachoCardNumber;
        driver.TachoDriveAvailableTodayMinutes = state.TachoDriveAvailableTodayMinutes;
        driver.TachoDriveAvailableWeekMinutes = state.TachoDriveAvailableWeekMinutes;
        driver.TachoWorkAvailableWeekMinutes = state.TachoWorkAvailableWeekMinutes;
        driver.LastTachoSyncUtc = state.LastTachoSyncUtc;
    }

    private async Task PersistStateAsync(Guid driverId, TachoDriverState state, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE [dbo].[Drivers]
SET [TachoMasterDriverId] = COALESCE(NULLIF([TachoMasterDriverId], N''), {state.TachoMasterDriverId}),
    [TachoCardNumber] = {state.TachoCardNumber},
    [LastTachoVehicleCode] = {state.TachoVehicleCode},
    [LastTachoCardInsertedUtc] = {state.CardInsertedUtc},
    [LastTachoDutyEndUtc] = {state.CurrentDutyEndUtc},
    [LastTachoDutyOpen] = {state.TachoDutyOpen},
    [TachoWorkTodayMinutes] = {state.TachoWorkTodayMinutes},
    [TachoDriveTodayMinutes] = {state.TachoDriveTodayMinutes},
    [TachoAvailableTodayMinutes] = {state.TachoAvailableTodayMinutes},
    [TachoRestTodayMinutes] = {state.TachoRestTodayMinutes},
    [TachoBreakCount] = {state.TachoBreakCount},
    [TachoBreakMinutes] = {state.TachoBreakMinutes},
    [TachoDriveAvailableTodayMinutes] = {state.TachoDriveAvailableTodayMinutes},
    [TachoDriveAvailableWeekMinutes] = {state.TachoDriveAvailableWeekMinutes},
    [TachoWorkAvailableWeekMinutes] = {state.TachoWorkAvailableWeekMinutes},
    [LastTachoSyncUtc] = {state.LastTachoSyncUtc}
WHERE [Id] = {driverId};
""", ct);
    }

    private async Task<string?> ReadPersistedTachoCardNumberAsync(Guid driverId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT [TachoCardNumber] FROM [dbo].[Drivers] WHERE [Id] = @driverId";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@driverId";
            parameter.Value = driverId;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(ct);
            return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static string NormaliseCard(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool CardsMatch(string? left, string? right)
    {
        var a = NormaliseCard(left);
        var b = NormaliseCard(right);
        if (a.Length < 8 || b.Length < 8) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
               a.EndsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.EndsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static TachoDriverHoursRefreshResult Empty(string status, bool updated, Guid driverId, string message) => new(
        status, updated, driverId, string.Empty, null, null, null, null, null, false,
        null, null, null, null, null, null, null, null, null, null, "None", message);

    private static TachoDriverHoursRefreshResult Result(string status, bool updated, TachoDriverState state, string message) => new(
        status,
        updated,
        state.DriverId,
        state.DisplayName,
        state.TachoMasterDriverId,
        state.TachoCardNumber,
        state.TachoVehicleCode,
        state.CardInsertedUtc,
        state.CurrentDutyEndUtc,
        state.TachoDutyOpen,
        state.TachoWorkTodayMinutes,
        state.TachoDriveTodayMinutes,
        state.TachoAvailableTodayMinutes,
        state.TachoRestTodayMinutes,
        state.TachoBreakCount,
        state.TachoBreakMinutes,
        state.TachoDriveAvailableTodayMinutes,
        state.TachoDriveAvailableWeekMinutes,
        state.TachoWorkAvailableWeekMinutes,
        state.LastTachoSyncUtc,
        state.IdentitySource,
        message);

    private sealed record TachoProfileMatch(TachoDriverProfile Profile, string IdentitySource);

    private sealed record TachoDutyEvidence(
        string? CardNumber,
        string? VehicleCode,
        DateTimeOffset? CardInsertedUtc,
        DateTimeOffset? CurrentDutyEndUtc,
        bool DutyOpen,
        int? WorkTodayMinutes,
        int? DriveTodayMinutes,
        int? AvailableTodayMinutes,
        int? RestTodayMinutes,
        int? BreakCount,
        int? BreakMinutes);

    private sealed record TachoDriverState(
        Guid DriverId,
        string DisplayName,
        string? TachoMasterDriverId,
        string? TachoCardNumber,
        string? TachoVehicleCode,
        DateTimeOffset? CardInsertedUtc,
        DateTimeOffset? CurrentDutyEndUtc,
        bool TachoDutyOpen,
        int? TachoWorkTodayMinutes,
        int? TachoDriveTodayMinutes,
        int? TachoAvailableTodayMinutes,
        int? TachoRestTodayMinutes,
        int? TachoBreakCount,
        int? TachoBreakMinutes,
        int? TachoDriveAvailableTodayMinutes,
        int? TachoDriveAvailableWeekMinutes,
        int? TachoWorkAvailableWeekMinutes,
        DateTimeOffset? LastTachoSyncUtc,
        string IdentitySource)
    {
        public static TachoDriverState From(Driver driver, string? persistedCard, TachoDriverProfile profile, TachoDutyEvidence? duty, DateTimeOffset now, string identitySource) => new(
            driver.Id,
            driver.DisplayName,
            string.IsNullOrWhiteSpace(driver.TachoMasterDriverId)
                ? profile.MemberCode.ToString(CultureInfo.InvariantCulture)
                : driver.TachoMasterDriverId,
            FirstNonBlank(duty?.CardNumber, profile.CardNumber, persistedCard, driver.TachoCardNumber),
            duty?.VehicleCode,
            duty?.CardInsertedUtc,
            duty?.CurrentDutyEndUtc,
            duty?.DutyOpen ?? false,
            duty?.WorkTodayMinutes,
            duty?.DriveTodayMinutes,
            duty?.AvailableTodayMinutes,
            duty?.RestTodayMinutes,
            duty?.BreakCount,
            duty?.BreakMinutes,
            profile.DriveAvailableTodayMinutes,
            profile.DriveAvailableWeekMinutes,
            profile.WorkAvailableWeekMinutes,
            now,
            identitySource);

        public static TachoDriverState FromDriverOnly(Driver driver, string? persistedCard) => new(
            driver.Id,
            driver.DisplayName,
            driver.TachoMasterDriverId,
            FirstNonBlank(persistedCard, driver.TachoCardNumber),
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            driver.TachoDriveAvailableTodayMinutes,
            driver.TachoDriveAvailableWeekMinutes,
            driver.TachoWorkAvailableWeekMinutes,
            driver.LastTachoSyncUtc,
            "None");

        private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}

public sealed class TachoDriverHoursRefreshWorker(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    ILogger<TachoDriverHoursRefreshWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (environment.IsEnvironment("Testing")) return;

        var nextRefresh = DateTimeOffset.UtcNow + RefreshInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = nextRefresh - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stoppingToken);

                await using var scope = scopeFactory.CreateAsyncScope();
                var refresh = scope.ServiceProvider.GetRequiredService<TachoDriverHoursRefreshService>();
                await refresh.RefreshDriverHoursOnlyAsync("system:tachomaster-driver-hours-refresh", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TachoMaster lightweight driver-hours refresh pass failed; the next 15-minute pass will still run.");
            }
            finally
            {
                nextRefresh = DateTimeOffset.UtcNow + RefreshInterval;
            }
        }
    }
}
