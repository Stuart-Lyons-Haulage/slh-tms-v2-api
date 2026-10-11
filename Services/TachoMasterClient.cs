using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;
 
namespace Slh.Tms.Api.Services;
 
public sealed class TachoMasterClient
{
    private readonly HttpClient httpClient;
    private readonly TachoMasterOptions options;
    private readonly ILogger<TachoMasterClient> logger;
    private readonly DotTrackingClient? dotTrackingClient;
    private readonly IMemoryCache? memoryCache;
    private readonly TmsDbContext? db;
    private const string SnapshotCacheKeyPrefix = "tachomaster:shared-snapshot:";
 
    public TachoMasterClient(HttpClient httpClient, TachoMasterOptions options, ILogger<TachoMasterClient> logger, DotTrackingClient? dotTrackingClient = null, IMemoryCache? memoryCache = null, TmsDbContext? db = null)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.logger = logger;
        this.dotTrackingClient = dotTrackingClient;
        this.memoryCache = memoryCache;
        this.db = db;
        this.httpClient.Timeout = TimeSpan.FromSeconds(30);
        this.httpClient.BaseAddress = new Uri(NormaliseBaseUrl(options.BaseUrl));
    }
 
    public bool IsConfigured => options.IsConfigured;
    public bool UsesSharedRoadTechCredentials => options.UsesSharedRoadTechCredentials;
    public IReadOnlyList<string> MissingSettings => new[]
    {
        !options.Enabled ? "TachoMaster enabled flag" : null,
        string.IsNullOrWhiteSpace(options.BaseUrl) ? "TachoMaster base URL" : null,
        string.IsNullOrWhiteSpace(options.ApiKey) ? "TachoMaster API key" : null,
        string.IsNullOrWhiteSpace(options.Username) ? "TachoMaster username" : null,
        string.IsNullOrWhiteSpace(options.Password) ? "TachoMaster password" : null
    }.Where(value => value is not null).Select(value => value!).ToList();
 
    public async Task<IReadOnlyDictionary<string, string>> GetCurrentDriverNamesByVehicleAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var statuses = await GetCurrentDriverStatusesByVehicleAsync(date, cancellationToken);
        return statuses.ToDictionary(item => item.Key, item => item.Value.DriverName);
    }
 
    /// <summary>
    /// The scheduled refresh is the sole owner of TachoMaster reads. It retrieves one
    /// 31-day duty window plus the member and metrics directories, then publishes the
    /// completed snapshot to SQL for all API instances and warms this process cache.
    /// </summary>
    public async Task<bool> RefreshSnapshotAsync(DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured || memoryCache is null) return false;
        if (toDate < fromDate) throw new ArgumentOutOfRangeException(nameof(toDate), "Snapshot end date must be on or after the start date.");

        var sid = await LoginAsync(cancellationToken);
        var dutiesTask = GetDutiesAsync(sid, fromDate, toDate, cancellationToken);
        var membersTask = GetMembersAsync(sid, cancellationToken);
        var metricsTask = TryGetMemberMetricsAsync(sid, cancellationToken);
        await Task.WhenAll(dutiesTask, membersTask, metricsTask);

        var members = await membersTask;
        var metrics = (await metricsTask)
            .GroupBy(item => item.MemCode)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.DateTimeWhenValid).First());
        var falconDrivers = await TryGetFalconDriverStatusesAsync(members, metrics, cancellationToken);
        var snapshot = new TachoSnapshot(
            fromDate,
            toDate,
            DateTimeOffset.UtcNow,
            await dutiesTask,
            members,
            metrics.Values.ToList(),
            falconDrivers);
        await SaveSharedSnapshotAsync(snapshot, cancellationToken);
        memoryCache.Set(SnapshotCacheKey, snapshot, TimeSpan.FromMinutes(35));
        logger.LogInformation(
            "Published shared TachoMaster snapshot for {FromDate} to {ToDate}: {DutyCount} duties, {MemberCount} members, {MetricCount} metrics.",
            fromDate, toDate, snapshot.Duties.Count, snapshot.Members.Count, snapshot.Metrics.Count);
        return true;
    }

    /// <summary>Loads the latest completed snapshot from SQL into this process cache.</summary>
    public async Task<bool> LoadSharedSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (db is null || memoryCache is null) return TryGetSnapshot(out _);

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Payload, RefreshedAtUtc FROM dbo.TachoMasterSnapshot WHERE SnapshotId = 1;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;

            var payload = reader.GetString(0);
            var refreshedAtUtc = reader.GetFieldValue<DateTimeOffset>(1);
            if (TryGetSnapshot(out var local) && local.RefreshedAtUtc >= refreshedAtUtc) return true;

            var snapshot = JsonSerializer.Deserialize<TachoSnapshot>(payload, JsonOptions);
            if (snapshot is null) return false;
            memoryCache.Set(SnapshotCacheKey, snapshot, TimeSpan.FromMinutes(35));
            logger.LogInformation("Loaded shared TachoMaster snapshot from SQL, refreshed at {RefreshedAtUtc}.", refreshedAtUtc);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not load the shared TachoMaster snapshot from SQL.");
            return false;
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    private async Task SaveSharedSnapshotAsync(TachoSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (db is null) return;
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SET XACT_ABORT ON;
UPDATE dbo.TachoMasterSnapshot
SET Payload = @payload, RefreshedAtUtc = @refreshedAtUtc
WHERE SnapshotId = 1;
IF @@ROWCOUNT = 0
    INSERT dbo.TachoMasterSnapshot (SnapshotId, Payload, RefreshedAtUtc)
    VALUES (1, @payload, @refreshedAtUtc);
""";
            var payload = command.CreateParameter();
            payload.ParameterName = "@payload";
            payload.DbType = DbType.String;
            payload.Size = -1;
            payload.Value = JsonSerializer.Serialize(snapshot, JsonOptions);
            command.Parameters.Add(payload);
            var refreshed = command.CreateParameter();
            refreshed.ParameterName = "@refreshedAtUtc";
            refreshed.DbType = DbType.DateTimeOffset;
            refreshed.Value = snapshot.RefreshedAtUtc;
            command.Parameters.Add(refreshed);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    public DateTimeOffset? SnapshotRefreshedAtUtc => TryGetSnapshot(out var snapshot) ? snapshot.RefreshedAtUtc : null;

    private string SnapshotCacheKey => $"{SnapshotCacheKeyPrefix}{NormaliseBaseUrl(options.BaseUrl)}";

    private bool TryGetSnapshot(out TachoSnapshot snapshot)
    {
        if (memoryCache is not null && memoryCache.TryGetValue(SnapshotCacheKey, out snapshot!) && snapshot is not null)
            return true;
        snapshot = null!;
        return false;
    }

    private TachoSnapshot? GetSnapshotFor(DateOnly date)
        => TryGetSnapshot(out var snapshot) && date >= snapshot.FromDate && date <= snapshot.ToDate ? snapshot : null;

    private static IReadOnlyList<TachoDriverProfile> BuildProfiles(TachoSnapshot snapshot)
    {
        var metrics = snapshot.Metrics.GroupBy(item => item.MemCode)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.DateTimeWhenValid).First());
        return snapshot.Members.GroupBy(item => item.MemCode).Select(group => group.First()).Select(member =>
        {
            metrics.TryGetValue(member.MemCode, out var metric);
            return new TachoDriverProfile(
                member.MemCode,
                DriverName(member),
                member.CardNoShort,
                member.EmployeeNumber,
                metric?.DateTimeWhenValid,
                metric?.DailyDriverPeriodsAvaiable,
                metric?.DriveAvailableToday,
                metric?.DriveAvailableTomorrow,
                metric?.DriveAvailableWeek,
                metric?.DriveAvailableFortnight,
                metric?.LongDaysWorkedThisWeek,
                metric?.ShortDailyRestTakenThisWeek,
                metric?.WorkAvaiableWeek);
        }).Where(profile => !string.IsNullOrWhiteSpace(profile.DriverName)).OrderBy(profile => profile.DriverName).ToList();
    }

    public Task<IReadOnlyList<TachoDriverProfile>> GetDriverProfilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<TachoDriverProfile>>(
            options.IsConfigured && TryGetSnapshot(out var snapshot) ? BuildProfiles(snapshot) : []);
    }

    public Task<IReadOnlyList<TachoDriverDutyStatus>> GetDriverDutyStatusesAsync(DateOnly date, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Kept for API compatibility. Provider reads are only permitted in RefreshSnapshotAsync.
        _ = forceRefresh;
        var snapshot = options.IsConfigured ? GetSnapshotFor(date) : null;
        return Task.FromResult<IReadOnlyList<TachoDriverDutyStatus>>(
            snapshot is null ? [] : BuildDutyStatuses(snapshot, date));
    }

    private IReadOnlyList<TachoDriverDutyStatus> BuildDutyStatuses(TachoSnapshot snapshot, DateOnly date)
    {
        var members = snapshot.Members.GroupBy(item => item.MemCode).ToDictionary(group => group.Key, group => group.First());
        var metrics = snapshot.Metrics.GroupBy(item => item.MemCode)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.DateTimeWhenValid).First());
        var (dayStart, dayEnd) = OperatingWindowUtc(date);
        var result = new List<TachoDriverDutyStatus>();
        foreach (var duty in snapshot.Duties
            .Where(item => item.MemCode > 0)
            .Where(item => item.DutyStart < dayEnd && (item.DutyEnd is null || item.DutyEnd >= dayStart))
            .OrderBy(item => item.DutyStart))
        {
            if (!members.TryGetValue(duty.MemCode, out var member)) continue;
            var name = DriverName(member);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var breaks = (duty.Wtd ?? [])
                .Where(item => string.Equals(item.WtdEvent, "wtdBreak", StringComparison.OrdinalIgnoreCase))
                .Where(item => item.TimeEnd >= item.TimeStart)
                .ToList();
            metrics.TryGetValue(duty.MemCode, out var metric);
            result.Add(new TachoDriverDutyStatus(
                string.IsNullOrWhiteSpace(duty.VehCode) ? string.Empty : NormaliseIdentifier(duty.VehCode),
                duty.MemCode,
                name,
                member.CardNoShort,
                member.EmployeeNumber,
                duty.DutyStart,
                duty.DutyEnd,
                duty.TimeWork,
                duty.TimeRest,
                duty.TimeAvailable,
                duty.TimeDrive,
                breaks.Count,
                breaks.Count == 0 ? null : (int)breaks.Sum(item => (item.TimeEnd - item.TimeStart).TotalMinutes),
                metric?.DateTimeWhenValid,
                metric?.DailyDriverPeriodsAvaiable,
                metric?.DriveAvailableToday,
                metric?.DriveAvailableTomorrow,
                metric?.DriveAvailableWeek,
                metric?.DriveAvailableFortnight,
                metric?.LongDaysWorkedThisWeek,
                metric?.ShortDailyRestTakenThisWeek,
                metric?.WorkAvaiableWeek));
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, TachoVehicleDriverStatus>> GetCurrentDriverStatusesByVehicleAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured) return new Dictionary<string, TachoVehicleDriverStatus>();
 
        var (currentDuties, members, metrics, memberList) = await LoadDutyContextAsync(date, cancellationToken);
        var result = new Dictionary<string, TachoVehicleDriverStatus>();
        foreach (var (vehicle, vehicleDuties) in OpenDuties(currentDuties))
        {
            var status = BuildStatus(vehicle, vehicleDuties[0].MemCode, vehicleDuties, members, metrics);
            if (status is not null) result[vehicle] = status;
        }
 
        var falconDrivers = GetSnapshotFor(date)?.FalconDrivers ?? new Dictionary<string, TachoVehicleDriverStatus>();
        var falconOnly = 0;
        var overlaps = 0;
        var mismatches = 0;
        foreach (var (vehicle, falcon) in falconDrivers)
        {
            if (!result.TryGetValue(vehicle, out var duty))
            {
                result[vehicle] = falcon;
                falconOnly++;
                continue;
            }
 
            overlaps++;
            if (!SameIdentity(duty, falcon))
            {
                mismatches++;
                logger.LogWarning(
                    "Live driver identity mismatch for vehicle {Vehicle}: TachoMaster duty={DutyDriver} card={DutyCard}; Falcon={FalconDriver} card={FalconCard}. Falcon identity retained and Tacho compliance figures suppressed until the identities agree.",
                    vehicle, duty.DriverName, duty.CardNumber, falcon.DriverName, falcon.CardNumber);
                result[vehicle] = falcon;
            }
        }
 
        logger.LogInformation(
            "Tacho continuous enrichment produced {Total} vehicle identities: {DutyCount} TachoMaster duty record(s), {FalconOnly} Falcon-only identity record(s), {OverlapCount} overlap(s), {MismatchCount} live mismatch(es).",
            result.Count, currentDuties.Count, falconOnly, overlaps, mismatches);
        return result;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>> GetOpenDriverStatusesByVehicleAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured) return new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>();

        var (duties, members, metrics, _) = await LoadDutyContextAsync(date, cancellationToken);
        var result = new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>();
        foreach (var (vehicle, vehicleDuties) in OpenDuties(duties))
        {
            var statuses = vehicleDuties
                .Select(duty => duty.MemCode)
                .Distinct()
                .Select(memberCode => BuildStatus(vehicle, memberCode, vehicleDuties, members, metrics))
                .Where(status => status is not null)
                .Select(status => status!)
                .ToList();
            if (statuses.Count > 0) result[vehicle] = statuses;
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>> GetLiveDriverStatusesByVehicleAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured) return new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>();

        var (duties, members, metrics, memberList) = await LoadDutyContextAsync(date, cancellationToken);
        var result = new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>();
        foreach (var (vehicle, vehicleDuties) in OpenDuties(duties))
        {
            var statuses = vehicleDuties
                .Select(duty => duty.MemCode)
                .Distinct()
                .Select(memberCode => BuildStatus(vehicle, memberCode, vehicleDuties, members, metrics))
                .Where(status => status is not null)
                .Select(status => status!)
                .ToList();
            if (statuses.Count > 0) result[vehicle] = statuses;
        }

        var falconDrivers = GetSnapshotFor(date)?.FalconDrivers ?? new Dictionary<string, TachoVehicleDriverStatus>();
        foreach (var (vehicle, falcon) in falconDrivers)
        {
            if (!result.TryGetValue(vehicle, out var existing))
            {
                result[vehicle] = new List<TachoVehicleDriverStatus> { falcon };
                continue;
            }

            if (!existing.Any(status => SameIdentity(status, falcon)))
                result[vehicle] = existing.Append(falcon).ToList();
        }

        return result;
    }
 
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>> GetAllDriverStatusesByVehicleAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured) return new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>();
 
        var (currentDuties, members, metrics, memberList) = await LoadDutyContextAsync(date, cancellationToken);
        var result = new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>();
        foreach (var (vehicle, vehicleDuties) in currentDuties)
        {
            var statuses = vehicleDuties
                .Select(duty => duty.MemCode)
                .Distinct()
                .Select(memberCode => BuildStatus(vehicle, memberCode, vehicleDuties, members, metrics))
                .Where(status => status is not null)
                .Select(status => status!)
                .ToList();
            if (statuses.Count > 0) result[vehicle] = statuses;
        }
 
        var falconDrivers = GetSnapshotFor(date)?.FalconDrivers ?? new Dictionary<string, TachoVehicleDriverStatus>();
        foreach (var (vehicle, falcon) in falconDrivers)
        {
            if (!result.TryGetValue(vehicle, out var existing))
            {
                result[vehicle] = new List<TachoVehicleDriverStatus> { falcon };
                continue;
            }
            if (!existing.Any(status => SameIdentity(status, falcon))) result[vehicle] = existing.Append(falcon).ToList();
        }
 
        return result;
    }
 
    private Task<(
        Dictionary<string, List<TachoDuty>> DutiesByVehicle,
        Dictionary<int, TachoMember> Members,
        Dictionary<int, TachoMemberMetric> Metrics,
        IReadOnlyList<TachoMember> MemberList)> LoadDutyContextAsync(DateOnly date, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = GetSnapshotFor(date);
        if (snapshot is null)
            return Task.FromResult((
                new Dictionary<string, List<TachoDuty>>(),
                new Dictionary<int, TachoMember>(),
                new Dictionary<int, TachoMemberMetric>(),
                (IReadOnlyList<TachoMember>)Array.Empty<TachoMember>()));

        var (dayStart, dayEnd) = OperatingWindowUtc(date);
        var dutiesByVehicle = snapshot.Duties
            .Where(duty => !string.IsNullOrWhiteSpace(duty.VehCode) && duty.MemCode > 0)
            .Where(duty => duty.DutyStart < dayEnd && (duty.DutyEnd is null || duty.DutyEnd >= dayStart))
            .GroupBy(duty => NormaliseIdentifier(duty.VehCode))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(duty => duty.DutyStart).ToList());
        var members = snapshot.Members.GroupBy(member => member.MemCode).ToDictionary(group => group.Key, group => group.First());
        var metrics = snapshot.Metrics.GroupBy(metric => metric.MemCode).ToDictionary(group => group.Key, group => group.OrderByDescending(metric => metric.DateTimeWhenValid).First());
        return Task.FromResult((dutiesByVehicle, members, metrics, (IReadOnlyList<TachoMember>)snapshot.Members));
    }

    private static TachoVehicleDriverStatus? BuildStatus(string vehicle, int memberCode, List<TachoDuty> vehicleDuties, Dictionary<int, TachoMember> members, Dictionary<int, TachoMemberMetric> metrics)
    {
        if (!members.TryGetValue(memberCode, out var member)) return null;
        var name = DriverName(member);
        if (string.IsNullOrWhiteSpace(name)) return null;
 
        var driverDuties = vehicleDuties.Where(duty => duty.MemCode == memberCode).ToList();
        if (driverDuties.Count == 0) return null;
        var wtdBreaks = driverDuties.SelectMany(duty => duty.Wtd ?? [])
            .Where(item => string.Equals(item.WtdEvent, "wtdBreak", StringComparison.OrdinalIgnoreCase))
            .Where(item => item.TimeEnd >= item.TimeStart)
            .ToList();
        metrics.TryGetValue(memberCode, out var metric);
        return new TachoVehicleDriverStatus(
            vehicle,
            memberCode,
            name,
            member.CardNoShort,
            member.EmployeeNumber,
            driverDuties.Min(duty => duty.DutyStart),
            driverDuties.All(duty => duty.DutyEnd is not null) ? driverDuties.Max(duty => duty.DutyEnd) : null,
            driverDuties.Sum(duty => duty.TimeWork),
            driverDuties.Sum(duty => duty.TimeRest),
            driverDuties.Sum(duty => duty.TimeAvailable),
            driverDuties.Sum(duty => duty.TimeDrive),
            wtdBreaks.Count,
            wtdBreaks.Count == 0 ? null : (int)wtdBreaks.Sum(item => (item.TimeEnd - item.TimeStart).TotalMinutes),
            metric?.DateTimeWhenValid,
            metric?.DailyDriverPeriodsAvaiable,
            metric?.DriveAvailableToday,
            metric?.DriveAvailableTomorrow,
            metric?.DriveAvailableWeek,
            metric?.DriveAvailableFortnight,
            metric?.LongDaysWorkedThisWeek,
            metric?.ShortDailyRestTakenThisWeek,
            metric?.WorkAvaiableWeek,
            "TachoMasterDuty");
    }

    private static Dictionary<string, List<TachoDuty>> OpenDuties(Dictionary<string, List<TachoDuty>> dutiesByVehicle)
        => dutiesByVehicle
            .Select(pair => new
            {
                pair.Key,
                Duties = pair.Value.Where(duty => duty.DutyEnd is null).ToList()
            })
            .Where(pair => pair.Duties.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Duties);
 
    private async Task<IReadOnlyDictionary<string, TachoVehicleDriverStatus>> TryGetFalconDriverStatusesAsync(
        IReadOnlyList<TachoMember> members,
        IReadOnlyDictionary<int, TachoMemberMetric> metrics,
        CancellationToken cancellationToken)
    {
        if (dotTrackingClient is null) return new Dictionary<string, TachoVehicleDriverStatus>();
        try
        {
            // GetCurrentTelemetry itself is the receipt-freshness evidence. Do not discard a
            // driver merely because the last movement event timestamp has not changed while the
            // vehicle is stationary at a customer site.
            var telemetry = await dotTrackingClient.GetLatestVehicleEventsAsync(cancellationToken);
            var records = telemetry.Select(DotTelemetryRecord.FromProvider)
                .Where(record => !string.IsNullOrWhiteSpace(record.VehicleIdentifier))
                .Where(record => !string.IsNullOrWhiteSpace(record.DriverName) || !string.IsNullOrWhiteSpace(record.DriverCardNumber))
                .GroupBy(record => NormaliseIdentifier(record.VehicleIdentifier))
                .Select(group => group.OrderByDescending(record => record.EventTimeUtc).First())
                .ToList();
 
            var result = new Dictionary<string, TachoVehicleDriverStatus>();
            foreach (var record in records)
            {
                var member = FindMemberByCard(members, record.DriverCardNumber);
                var resolvedName = !string.IsNullOrWhiteSpace(record.DriverName)
                    ? record.DriverName!.Trim()
                    : member is null
                        ? string.IsNullOrWhiteSpace(record.DriverCardNumber) ? null : "Driver card observed"
                        : DriverName(member);
                if (string.IsNullOrWhiteSpace(resolvedName)) continue;
                var metric = member is null || !metrics.TryGetValue(member.MemCode, out var matchedMetric) ? null : matchedMetric;
 
                var vehicle = NormaliseIdentifier(record.VehicleIdentifier);
                result[vehicle] = new TachoVehicleDriverStatus(
                    vehicle,
                    member?.MemCode ?? 0,
                    resolvedName,
                    member?.CardNoShort ?? record.DriverCardNumber,
                    member?.EmployeeNumber,
                    record.EventTimeUtc,
                    null,
                    0, 0, 0, 0, 0,
                    null,
                    metric?.DateTimeWhenValid,
                    metric?.DailyDriverPeriodsAvaiable,
                    metric?.DriveAvailableToday,
                    metric?.DriveAvailableTomorrow,
                    metric?.DriveAvailableWeek,
                    metric?.DriveAvailableFortnight,
                    metric?.LongDaysWorkedThisWeek,
                    metric?.ShortDailyRestTakenThisWeek,
                    metric?.WorkAvaiableWeek,
                    "FalconLiveCard");
            }
 
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Falcon live driver enrichment was unavailable.");
            return new Dictionary<string, TachoVehicleDriverStatus>();
        }
    }
 
    private static bool SameIdentity(TachoVehicleDriverStatus duty, TachoVehicleDriverStatus falcon)
    {
        var dutyCard = NormaliseCard(duty.CardNumber);
        var falconCard = NormaliseCard(falcon.CardNumber);
        if (dutyCard.Length >= 8 && falconCard.Length >= 8)
            return string.Equals(dutyCard, falconCard, StringComparison.OrdinalIgnoreCase)
                || dutyCard.EndsWith(falconCard, StringComparison.OrdinalIgnoreCase)
                || falconCard.EndsWith(dutyCard, StringComparison.OrdinalIgnoreCase);
 
        return string.Equals(NormaliseName(duty.DriverName), NormaliseName(falcon.DriverName), StringComparison.OrdinalIgnoreCase);
    }
 
    private static string NormaliseName(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
 
    private static TachoMember? FindMemberByCard(IEnumerable<TachoMember> members, string? cardNumber)
    {
        var liveCard = NormaliseCard(cardNumber);
        if (liveCard.Length < 8) return null;
        return members.FirstOrDefault(member =>
        {
            var memberCard = NormaliseCard(member.CardNoShort);
            if (memberCard.Length < 8) return false;
            return string.Equals(memberCard, liveCard, StringComparison.OrdinalIgnoreCase)
                || (memberCard.Length >= 8 && liveCard.EndsWith(memberCard, StringComparison.OrdinalIgnoreCase))
                || (liveCard.Length >= 8 && memberCard.EndsWith(liveCard, StringComparison.OrdinalIgnoreCase));
        });
    }
 
    private async Task<string> LoginAsync(CancellationToken cancellationToken)
    {
        string? lastFailure = null;
        foreach (var password in PasswordAttempts(options.Password))
        {
            using var response = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "auth/login");
                request.Headers.Add("APIKEY", options.ApiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = JsonContent.Create(new
                {
                    User = options.Username,
                    Pass = password,
                    OsVersion = "1.0",
                    OsName = "Azure Container App",
                    PcName = Environment.MachineName,
                    AuthType = "password"
                });
                return request;
            }, "login", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                return ExtractSessionId(payload);
            }

            lastFailure = await FailureDetailAsync(response, "login", cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)) break;
        }

        throw new HttpRequestException($"TachoMaster login failed. {lastFailure ?? "No response was received."}");
    }
 
    private async Task<List<TachoDuty>> GetDutiesAsync(string sid, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        var result = new List<TachoDuty>();
        var offset = 0;
        var fromUtc = OperatingWindowUtc(fromDate).StartUtc;
        var toUtc = OperatingWindowUtc(toDate).EndUtc;
        for (var page = 0; page < options.MaxPages; page++)
        {
            using var response = await SendWithRetryAsync(() =>
            {
                var retryRequest = CreateRequest(HttpMethod.Post, "Duty/GetDutyTransactions", sid);
                retryRequest.Content = JsonContent.Create(new { From = fromUtc.ToString("O"), To = toUtc.ToString("O"), Offset = offset, WithWtd = true });
                return retryRequest;
            }, "Duty/GetDutyTransactions", cancellationToken);
            await EnsureSuccessAsync(response, "Duty/GetDutyTransactions", cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var envelope = await JsonSerializer.DeserializeAsync<TachoDutyEnvelope>(stream, JsonOptions, cancellationToken) ?? new TachoDutyEnvelope();
            var pageData = envelope.DutyNew ?? new TachoPage<TachoDuty>();
            result.AddRange(pageData.Data);
            if (!pageData.MoreData || pageData.RecordCount == 0) break;
            offset += pageData.RecordCount;
        }
 
        return result;
    }
 
    private async Task<List<TachoMemberMetric>> GetMemberMetricsAsync(string sid, CancellationToken cancellationToken)
    {
        var result = new List<TachoMemberMetric>();
        var offset = 0;
        for (var page = 0; page < options.MaxPages; page++)
        {
            using var response = await SendWithRetryAsync(() =>
            {
                var retryRequest = CreateRequest(HttpMethod.Post, "Member/GetMemberMetrics", sid);
                retryRequest.Content = JsonContent.Create(new { Offset = offset });
                return retryRequest;
            }, "Member/GetMemberMetrics", cancellationToken);
            await EnsureSuccessAsync(response, "Member/GetMemberMetrics", cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var pageData = await JsonSerializer.DeserializeAsync<TachoPage<TachoMemberMetric>>(stream, JsonOptions, cancellationToken) ?? new TachoPage<TachoMemberMetric>();
            result.AddRange(pageData.Data);
            if (!pageData.MoreData || pageData.RecordCount == 0) break;
            offset += pageData.RecordCount;
        }
 
        return result;
    }
 
    private async Task<List<TachoMemberMetric>> TryGetMemberMetricsAsync(string sid, CancellationToken cancellationToken)
    {
        try { return await GetMemberMetricsAsync(sid, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster member metrics were unavailable; returning driver and duty data without remaining-time figures.");
            return [];
        }
    }
 
    private async Task<List<TachoMember>> GetMembersAsync(string sid, CancellationToken cancellationToken)
    {
        var result = new List<TachoMember>();
        var offset = 0;
        for (var page = 0; page < options.MaxPages; page++)
        {
            using var response = await SendWithRetryAsync(() =>
            {
                var retryRequest = CreateRequest(HttpMethod.Post, "Member/GetMembersLong", sid);
                retryRequest.Content = JsonContent.Create(new { Offset = offset, OnlyLiveMembers = false });
                return retryRequest;
            }, "Member/GetMembersLong", cancellationToken);
            await EnsureSuccessAsync(response, "Member/GetMembersLong", cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var pageData = await JsonSerializer.DeserializeAsync<TachoPage<TachoMember>>(stream, JsonOptions, cancellationToken) ?? new TachoPage<TachoMember>();
            result.AddRange(pageData.Data);
            if (!pageData.MoreData || pageData.RecordCount == 0) break;
            offset += pageData.RecordCount;
        }
 
        return result;
    }
 
    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string sid)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("APIKEY", options.ApiKey);
        request.Headers.Add("SID", sid);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        string operation,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = requestFactory();
                var response = await httpClient.SendAsync(request, cancellationToken);
                if (!IsTransient(response.StatusCode) || attempt == maxAttempts) return response;

                logger.LogWarning("TachoMaster {Operation} returned transient HTTP {StatusCode}; retrying attempt {Attempt} of {MaxAttempts}.", operation, (int)response.StatusCode, attempt + 1, maxAttempts);
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                logger.LogWarning("TachoMaster {Operation} failed at transport level; retrying attempt {Attempt} of {MaxAttempts}.", operation, attempt + 1, maxAttempts);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
        }

        throw new InvalidOperationException($"TachoMaster {operation} failed without a response.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == (HttpStatusCode)429 ||
        (int)statusCode >= 500;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException(await FailureDetailAsync(response, operation, cancellationToken), null, response.StatusCode);
    }

    private static async Task<string> FailureDetailAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        detail = string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase ?? "no provider detail" : detail.Trim();
        if (detail.Length > 300) detail = detail[..300];
        return $"TachoMaster {operation} returned {(int)response.StatusCode} ({response.ReasonPhrase}). Provider detail: {detail}";
    }
 
    private static string DriverName(TachoMember member)
    {
        var given = string.IsNullOrWhiteSpace(member.GivenNames) ? member.CName : member.GivenNames;
        var surname = string.IsNullOrWhiteSpace(member.Surname) ? member.SName : member.Surname;
        return string.Join(' ', new[] { given, surname }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
    }
 
    private static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) OperatingWindowUtc(DateOnly date)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            var startLocal = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            var endLocal = DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            return (new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, zone)), new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, zone)));
        }
        catch (TimeZoneNotFoundException)
        {
            var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return (start, start.AddDays(1));
        }
    }
 
    private static string NormaliseCard(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
 
    private static IReadOnlyList<string> PasswordAttempts(string password)
    {
        var trimmed = password.Trim();
        if (trimmed.All(Uri.IsHexDigit) && trimmed.Length is 32 or 40 or 64) return [trimmed];
        return [trimmed, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(trimmed))).ToLowerInvariant()];
    }
 
    private static string ExtractSessionId(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind is JsonValueKind.Number or JsonValueKind.String) return root.ToString();
        foreach (var name in new[] { "sid", "SID", "token", "Token" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return value.ToString();
        throw new InvalidOperationException("TachoMaster login response did not contain a SID.");
    }
 
    private static string NormaliseBaseUrl(string value)
    {
        var trimmed = value.Trim().TrimEnd('/');
        return trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? $"{trimmed}/" : $"{trimmed}/api/";
    }
 
    private static string NormaliseIdentifier(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
 
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new NumberOrStringConverter() }
    };
 
    private sealed class NumberOrStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Expected text or number, received {reader.TokenType}.")
        };
 
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
 
    private sealed record TachoSnapshot(
        DateOnly FromDate,
        DateOnly ToDate,
        DateTimeOffset RefreshedAtUtc,
        IReadOnlyList<TachoDuty> Duties,
        IReadOnlyList<TachoMember> Members,
        IReadOnlyList<TachoMemberMetric> Metrics,
        IReadOnlyDictionary<string, TachoVehicleDriverStatus> FalconDrivers);

    private sealed class TachoDutyEnvelope { public TachoPage<TachoDuty>? DutyNew { get; set; } }
    private sealed class TachoPage<T> { public bool MoreData { get; set; } public int RecordCount { get; set; } public List<T> Data { get; set; } = []; }
    private sealed class TachoDuty
    {
        public int MemCode { get; set; }
        public string VehCode { get; set; } = string.Empty;
        public DateTimeOffset DutyStart { get; set; }
        public DateTimeOffset? DutyEnd { get; set; }
        public int TimeWork { get; set; }
        public int TimeRest { get; set; }
        public int TimeAvailable { get; set; }
        public int TimeDrive { get; set; }
        public List<TachoWtdEvent>? Wtd { get; set; }
    }
    private sealed class TachoWtdEvent { public string? WtdEvent { get; set; } public DateTimeOffset TimeStart { get; set; } public DateTimeOffset TimeEnd { get; set; } }
    private sealed class TachoMember
    {
        public int MemCode { get; set; }
        public string? CName { get; set; }
        public string? SName { get; set; }
        public string? GivenNames { get; set; }
        public string? Surname { get; set; }
        public string? CardNoShort { get; set; }
        public string? EmployeeNumber { get; set; }
    }
    private sealed class TachoMemberMetric
    {
        public int MemCode { get; set; }
        public DateTimeOffset DateTimeWhenValid { get; set; }
        public int DailyDriverPeriodsAvaiable { get; set; }
        public int DriveAvailableToday { get; set; }
        public int DriveAvailableTomorrow { get; set; }
        public int DriveAvailableWeek { get; set; }
        public int DriveAvailableFortnight { get; set; }
        public int LongDaysWorkedThisWeek { get; set; }
        public int ShortDailyRestTakenThisWeek { get; set; }
        public int WorkAvaiableWeek { get; set; }
    }
}
 
public sealed record TachoVehicleDriverStatus(
    string VehicleCode,
    int MemberCode,
    string DriverName,
    string? CardNumber,
    string? EmployeeNumber,
    DateTimeOffset DutyStartUtc,
    DateTimeOffset? DutyEndUtc,
    int WorkMinutes,
    int RestMinutes,
    int AvailableMinutes,
    int DriveMinutes,
    int BreakCount,
    int? BreakMinutes,
    DateTimeOffset? MetricsValidAtUtc,
    int? DailyDriverPeriodsAvailable,
    int? DriveAvailableTodayMinutes,
    int? DriveAvailableTomorrowMinutes,
    int? DriveAvailableWeekMinutes,
    int? DriveAvailableFortnightMinutes,
    int? LongDaysWorkedThisWeek,
    int? ShortDailyRestTakenThisWeek,
    int? WorkAvailableWeekMinutes,
    string EvidenceSource = "TachoMasterDuty");
 
public sealed record TachoDriverDutyStatus(
    string VehicleCode,
    int MemberCode,
    string DriverName,
    string? CardNumber,
    string? EmployeeNumber,
    DateTimeOffset DutyStartUtc,
    DateTimeOffset? DutyEndUtc,
    int WorkMinutes,
    int RestMinutes,
    int AvailableMinutes,
    int DriveMinutes,
    int BreakCount,
    int? BreakMinutes,
    DateTimeOffset? MetricsValidAtUtc,
    int? DailyDriverPeriodsAvailable,
    int? DriveAvailableTodayMinutes,
    int? DriveAvailableTomorrowMinutes,
    int? DriveAvailableWeekMinutes,
    int? DriveAvailableFortnightMinutes,
    int? LongDaysWorkedThisWeek,
    int? ShortDailyRestTakenThisWeek,
    int? WorkAvailableWeekMinutes);
 
public sealed record TachoDriverProfile(
    int MemberCode,
    string DriverName,
    string? CardNumber,
    string? EmployeeNumber,
    DateTimeOffset? MetricsValidAtUtc,
    int? DailyDriverPeriodsAvailable,
    int? DriveAvailableTodayMinutes,
    int? DriveAvailableTomorrowMinutes,
    int? DriveAvailableWeekMinutes,
    int? DriveAvailableFortnightMinutes,
    int? LongDaysWorkedThisWeek,
    int? ShortDailyRestTakenThisWeek,
    int? WorkAvailableWeekMinutes);
