using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record TachoDriverMasterSyncResult(
    bool Success,
    int SourceWorkers,
    int CanonicalActiveDrivers,
    int Created,
    int Updated,
    int DuplicateRecordsRetired,
    int DriversArchivedNotInTachoMaster,
    int MatchedByMember,
    int MatchedByCard,
    int MatchedByUniqueName,
    int SameNameDifferentIdentityGroups,
    int WorkersWithoutCard,
    string Message,
    DateTimeOffset CompletedAtUtc);

public sealed record TachoDriverMasterDuplicateDriver(
    Guid DriverId,
    string EmployeeNumber,
    string DisplayName);

public sealed record TachoDriverMasterIdentityDuplicate(
    string IdentityValue,
    IReadOnlyList<TachoDriverMasterDuplicateDriver> Drivers);

public sealed record TachoDriverMasterQuality(
    int ActiveDrivers,
    int ActiveWithMember,
    int ActiveWithCard,
    int DuplicateMemberGroups,
    int DuplicateCardGroups,
    int ActiveWithoutMember,
    int ActiveWithoutCard,
    DateTimeOffset? LatestCanonicalSyncUtc)
{
    public IReadOnlyList<TachoDriverMasterIdentityDuplicate> DuplicateMembers { get; init; } = [];
    public IReadOnlyList<TachoDriverMasterIdentityDuplicate> DuplicateCards { get; init; } = [];
}

public sealed class TachoDriverMasterSyncService(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    IHttpClientFactory httpClientFactory,
    TachoMasterOptions options,
    DistributedLeaseManager leases,
    ILogger<TachoDriverMasterSyncService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private const string DetailType = "masterdetail:driver";
    private const string ProfileType = "tachodriverprofile";

    public async Task<TachoDriverMasterSyncResult> SyncAsync(string actor, CancellationToken ct)
    {
        await using var lease = await leases.TryAcquireAsync(IntegrationLeaseNames.TachoMaster, TimeSpan.FromMinutes(2), ct);
        if (lease is null)
            return new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                "TachoMaster Driver Master sync skipped because another distributed writer currently holds the integration lease.", DateTimeOffset.UtcNow);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.LostToken);
        return await SyncCoreAsync(actor, linked.Token);
    }

    internal async Task<TachoDriverMasterSyncResult> SyncCoreAsync(string actor, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!options.IsConfigured)
            return new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                "TachoMaster is not configured, so the canonical Driver Master was not changed.", now);

        var directory = new TachoLiveWorkerDirectory(httpClientFactory.CreateClient(), options);
        IReadOnlyList<TachoLiveWorker> workers;
        try
        {
            workers = await directory.GetLiveWorkersAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "TachoMaster live worker directory could not be read; canonical driver sync aborted.");
            return new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                $"TachoMaster live worker directory could not be read: {ex.GetBaseException().Message}. No Driver Master records were intentionally changed.", now);
        }
        var rawSourceWorkerCount = workers.Count;

        // A tiny or unexpectedly collapsed provider result must never quarantine the real driver population.
        if (workers.Count < 25)
            return new(false, workers.Count, 0, 0, 0, 0, 0, 0, 0, 0, CountDuplicateNames(workers), workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber)),
                $"TachoMaster returned only {workers.Count} live worker(s). Canonicalisation was stopped because that is below the safety floor.", now);

        IReadOnlyDictionary<int, TachoDriverProfile> profileByMember = new Dictionary<int, TachoDriverProfile>();
        try
        {
            profileByMember = (await tachoMaster.GetDriverProfilesAsync(ct))
                .GroupBy(profile => profile.MemberCode)
                .ToDictionary(group => group.Key, group => group.First());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TachoMaster metrics could not be read during canonical Driver Master sync; identity sync will continue without hours metrics.");
        }

        var drivers = await db.Drivers.OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var activeBefore = drivers.Count(driver => driver.Active);
        var ukToday = TachoDriverCardReadEligibility.UkToday(now);
        workers = workers
            .Where(DriverPopulationRules.IsDriver)
            .Where(worker => TachoDriverCardReadEligibility.IsEligible(worker.CardLastRead, ukToday))
            .ToList();
        if (workers.Count < 25 || (activeBefore > 0 && workers.Count < Math.Max(25, (int)Math.Floor(activeBefore * 0.35m))) )
            return new(false, workers.Count, activeBefore, 0, 0, 0, 0, 0, 0, 0, CountDuplicateNames(workers), workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber)),
                $"Only {workers.Count} driver(s) had a TachoMaster card read within the last six months against {activeBefore} active TMS drivers. The eligibility/population safety check stopped the sync; no records were changed.", now);

        var loadUse = await db.Loads.AsNoTracking()
            .Where(load => load.DriverId != null)
            .GroupBy(load => load.DriverId!.Value)
            .Select(group => new { DriverId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.DriverId, item => item.Count, ct);

        // TachoMaster can expose historical/member aliases as separate live rows even though
        // they carry the same physical tachograph card. Processing those rows independently
        // can retire a duplicate and then reactivate it later in the same run. Collapse the
        // provider directory to one worker per physical card before touching SQL. Workers with
        // no card remain keyed by Member Code and are retained for manual card completion.
        workers = CanonicaliseLiveWorkers(workers, drivers, loadUse);
        if (workers.Count < 25)
            return new(false, workers.Count, activeBefore, 0, 0, 0, 0, 0, 0, 0, CountDuplicateNames(workers), workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber)),
                $"TachoMaster returned {rawSourceWorkerCount} driver row(s), but only {workers.Count} canonical card/member identities remained. The safety floor stopped the cleanse.", now);

        var detailRows = await db.StagedImports
            .Where(row => row.EntityType == DetailType)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .ToListAsync(ct);
        var detailByKey = detailRows
            .GroupBy(row => row.IdempotencyKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var profileRowList = await db.StagedImports.Where(row => row.EntityType == ProfileType).ToListAsync(ct);
        var profileRows = profileRowList.ToDictionary(row => row.IdempotencyKey, StringComparer.OrdinalIgnoreCase);

        var liveNameCounts = workers
            .GroupBy(worker => TachoDriverIdentityRules.NormalisePerson(worker.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var claimedDriverIds = new HashSet<Guid>();
        var created = 0;
        var updated = 0;
        var retired = 0;
        var matchedByMember = 0;
        var matchedByCard = 0;
        var matchedByName = 0;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var worker in workers.OrderBy(worker => worker.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(worker => worker.MemberCode))
        {
            var member = worker.MemberCode.ToString(CultureInfo.InvariantCulture);
            var memberMatches = drivers
                .Where(driver => !claimedDriverIds.Contains(driver.Id))
                .Where(driver => TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, member))
                .Where(driver => string.IsNullOrWhiteSpace(worker.CardNumber) ||
                                 string.IsNullOrWhiteSpace(driver.TachoCardNumber) ||
                                 TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, worker.CardNumber))
                .ToList();
            var cardMatches = string.IsNullOrWhiteSpace(worker.CardNumber)
                ? []
                : drivers
                    .Where(driver => !claimedDriverIds.Contains(driver.Id))
                    .Where(driver => TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, worker.CardNumber))
                    .ToList();
            // The physical tachograph card is the canonical person identifier. Member code is
            // retained as the fallback for workers whose live TachoMaster record has no card.
            var strong = cardMatches.Count > 0 ? cardMatches : memberMatches;

            Driver? canonical = null;
            if (strong.Count > 0)
            {
                canonical = SelectCanonical(strong, worker, loadUse);
                if (cardMatches.Count > 0) matchedByCard++;
                else matchedByMember++;
            }
            else
            {
                var nameKey = TachoDriverIdentityRules.NormalisePerson(worker.DisplayName);
                if (nameKey.Length > 0 && liveNameCounts.GetValueOrDefault(nameKey) == 1)
                {
                    var nameMatches = drivers
                        .Where(driver => !claimedDriverIds.Contains(driver.Id))
                        .Where(driver => IdentityCompatible(driver, worker))
                        .Where(driver => TachoDriverIdentityRules.NormalisePerson(driver.TachoName) == nameKey ||
                                         TachoDriverIdentityRules.NormalisePerson(driver.DisplayName) == nameKey)
                        .ToList();
                    if (nameMatches.Count == 1)
                    {
                        canonical = nameMatches[0];
                        matchedByName++;
                    }
                }
            }

            if (canonical is null)
            {
                // An unmatched TachoMaster worker is staged for human review rather than
                // being immediately created as an active driver. This matches the behaviour
                // of TachoMemberCodeDriverMasterSync (called by the orchestrator) and ensures
                // no driver enters Driver Master without a reviewer confirming their identity.
                var reviewKey = $"driverreview:member:{TachoDriverIdentityRules.NormaliseIdentifier(member)}";
                var existingReview = await db.StagedImports
                    .FirstOrDefaultAsync(row => row.EntityType == "driverreview" && row.IdempotencyKey == reviewKey, ct);
                if (existingReview is null)
                {
                    db.StagedImports.Add(new StagedImport
                    {
                        EntityType = "driverreview",
                        IdempotencyKey = reviewKey,
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            tachoMemberCode = member,
                            displayName = worker.DisplayName,
                            cardNumber = worker.CardNumber,
                            employeeNumber = worker.EmployeeNumber,
                            workerType = worker.WorkerType,
                            agencyName = worker.AgencyName,
                            cardLastRead = worker.CardLastRead,
                            driverCardExpiry = worker.DriverCardExpiry,
                            drivingLicenceExpiry = worker.DrivingLicenceExpiry,
                            cpcExpiry = worker.CpcExpiry,
                            source = "TachoMaster live worker directory (canonical sync path) — no matching Driver Master record",
                            receivedAtUtc = now
                        }, JsonOptions),
                        Source = "TachoMaster canonical Driver Master sync",
                        Status = StagingStatus.PendingReview,
                        ReceivedAtUtc = now,
                        ReviewNote = $"New TachoMaster member {member} ({worker.DisplayName}) has no matching Driver Master record. Review and promote to create driver, or reject to discard."
                    });
                }
                else
                {
                    existingReview.PayloadJson = JsonSerializer.Serialize(new
                    {
                        tachoMemberCode = member,
                        displayName = worker.DisplayName,
                        cardNumber = worker.CardNumber,
                        employeeNumber = worker.EmployeeNumber,
                        workerType = worker.WorkerType,
                        agencyName = worker.AgencyName,
                        cardLastRead = worker.CardLastRead,
                        driverCardExpiry = worker.DriverCardExpiry,
                        drivingLicenceExpiry = worker.DrivingLicenceExpiry,
                        cpcExpiry = worker.CpcExpiry,
                        source = "TachoMaster live worker directory (canonical sync path) — no matching Driver Master record",
                        receivedAtUtc = now
                    }, JsonOptions);
                    existingReview.ReviewedAtUtc = now;
                    existingReview.ReviewNote = $"Updated from TachoMaster canonical sync at {now:u}. Still pending Driver Master review.";
                }

                // Skip this worker — no Driver entity created until a reviewer promotes the item.
                UpsertProfile(profileRows, worker, actor, now);
                continue;
            }
            else updated++;

            // Strong duplicates are always safe. Name-only aliases are merged only when TachoMaster
            // has exactly one live person with that name and the alias has no conflicting member/card.
            var duplicates = cardMatches
                .Concat(memberMatches)
                .Where(driver => driver.Id != canonical.Id)
                .DistinctBy(driver => driver.Id)
                .ToList();
            var workerName = TachoDriverIdentityRules.NormalisePerson(worker.DisplayName);
            if (workerName.Length > 0 && liveNameCounts.GetValueOrDefault(workerName) == 1)
            {
                duplicates.AddRange(drivers
                    .Where(driver => driver.Id != canonical.Id && !claimedDriverIds.Contains(driver.Id))
                    .Where(driver => !duplicates.Any(existing => existing.Id == driver.Id))
                    .Where(driver => IdentityCompatible(driver, worker))
                    .Where(driver => TachoDriverIdentityRules.NormalisePerson(driver.TachoName) == workerName ||
                                     TachoDriverIdentityRules.NormalisePerson(driver.DisplayName) == workerName));
            }

            foreach (var duplicate in duplicates.DistinctBy(driver => driver.Id).ToList())
            {
                await MergeDuplicateAsync(canonical, duplicate, detailRows, actor, ct);
                retired++;
            }

            ApplyWorker(canonical, worker, profileByMember.GetValueOrDefault(worker.MemberCode), now);
            canonical.Active = true;
            claimedDriverIds.Add(canonical.Id);
            UpsertDetail(detailByKey, canonical, worker, actor, now);
            UpsertProfile(profileRows, worker, actor, now);
        }

        var archived = 0;
        foreach (var driver in drivers.Where(driver => driver.Active && !claimedDriverIds.Contains(driver.Id)))
        {
            driver.Active = false;
            archived++;
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Driver",
                EntityId = driver.Id,
                Action = "ArchivedNotInTachoMaster",
                ChangedBy = actor,
                ChangesJson = JsonSerializer.Serialize(new
                {
                    reason = "No qualifying TachoMaster card read within the last six months",
                    driver.EmployeeNumber,
                    driver.DisplayName,
                    driver.TachoMasterDriverId,
                    driver.TachoCardNumber
                }, JsonOptions)
            });
        }

        var activeAfter = drivers.Where(driver => driver.Active).ToList();
        var duplicateMemberGroupsAfter = DuplicateIdentityGroupCount(activeAfter, driver => driver.TachoMasterDriverId);
        var duplicateCardGroupsAfter = DuplicateIdentityGroupCount(activeAfter, driver => driver.TachoCardNumber);
        var activeWithoutMemberAfter = activeAfter.Count(driver => string.IsNullOrWhiteSpace(driver.TachoMasterDriverId));
        var workersWithoutCardAfter = workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber));
        // Unmatched workers are now staged to the review queue rather than auto-created,
        // so the population gate no longer requires claimedDriverIds.Count == workers.Count.
        // A clean sync still requires no duplicate identities and no active drivers without a member code.
        var unclaimedWorkers = workers.Count - claimedDriverIds.Count;
        var canonicalHealthy = duplicateMemberGroupsAfter == 0 &&
                               duplicateCardGroupsAfter == 0 &&
                               activeWithoutMemberAfter == 0;

        var auditPayload = JsonSerializer.Serialize(new
        {
            sourceWorkers = workers.Count,
            rawSourceWorkers = rawSourceWorkerCount,
            canonicalActiveDrivers = activeAfter.Count,
            created,
            updated,
            unclaimedStagedForReview = unclaimedWorkers,
            duplicateRecordsRetired = retired,
            driversArchivedNotInTachoMaster = archived,
            sameNameDifferentIdentityGroups = CountDuplicateNames(workers),
            workersWithoutCard = workersWithoutCardAfter,
            duplicateMemberGroups = duplicateMemberGroupsAfter,
            duplicateCardGroups = duplicateCardGroupsAfter,
            activeWithoutMember = activeWithoutMemberAfter,
            populationAligned = activeAfter.Count == workers.Count
        }, JsonOptions);

        if (!canonicalHealthy)
        {
            await transaction.RollbackAsync(ct);
            await transaction.DisposeAsync();
            db.ChangeTracker.Clear();
            var failureMessage = $"TachoMaster canonical Driver Master was not promoted because the resulting population failed the strict identity gate: source={workers.Count}, claimed={claimedDriverIds.Count}, unclaimed(staged for review)={unclaimedWorkers}, active={activeAfter.Count}, duplicate members={duplicateMemberGroupsAfter}, duplicate cards={duplicateCardGroupsAfter}, active without member code={activeWithoutMemberAfter}, source workers without card={workersWithoutCardAfter}. Cardless workers are allowed when their stable TachoMaster Member Code is present. No partial cleanse was committed.";
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "tachodrivermastersync",
                IdempotencyKey = $"tachodrivermastersync:{now:yyyyMMddHHmmss}:{Guid.NewGuid():N}",
                PayloadJson = auditPayload,
                Source = "TachoMaster live Worker List canonical Driver Master sync",
                Status = StagingStatus.Rejected,
                ReceivedAtUtc = now,
                ReviewedAtUtc = DateTimeOffset.UtcNow,
                ReviewedBy = actor,
                ReviewNote = failureMessage
            });
            await db.SaveChangesAsync(ct);
            return new(false, workers.Count, activeAfter.Count, created, updated, retired, archived, matchedByMember, matchedByCard, matchedByName,
                CountDuplicateNames(workers), workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber)), failureMessage, DateTimeOffset.UtcNow);
        }

        db.StagedImports.Add(new StagedImport
        {
            EntityType = "tachodrivermastersync",
            IdempotencyKey = $"tachodrivermastersync:{now:yyyyMMddHHmmss}:{Guid.NewGuid():N}",
            PayloadJson = auditPayload,
            Source = "TachoMaster live Worker List canonical Driver Master sync",
            Status = StagingStatus.Promoted,
            ReceivedAtUtc = now,
            ReviewedAtUtc = DateTimeOffset.UtcNow,
            ReviewedBy = actor,
            ReviewNote = "Only driver records with a TachoMaster card read in the last six months are refreshed. Member Code remains the stable secondary external reference; older records are retained inactive."
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var message = $"TachoMaster canonical Driver Master: {workers.Count} driver(s) with a qualifying card-read date in the last six months, {activeAfter.Count} active canonical TMS driver(s), {created} created, {retired} duplicate record(s) retired and {archived} old/ineligible TMS driver(s) archived. Identity and duplicate checks passed; {workersWithoutCardAfter} current worker(s) have no card number and remain keyed by stable TachoMaster Member Code.";
        return new(true, workers.Count, activeAfter.Count, created, updated, retired, archived, matchedByMember, matchedByCard, matchedByName,
            CountDuplicateNames(workers), workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber)), message, DateTimeOffset.UtcNow);
    }

    public async Task<TachoDriverMasterQuality> QualityAsync(CancellationToken ct)
    {
        var drivers = await db.Drivers.Where(driver => driver.Active).OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var duplicateMembers = DuplicateIdentities(drivers, driver => driver.TachoMasterDriverId);
        var duplicateCards = DuplicateIdentities(drivers, driver => driver.TachoCardNumber);
        var memberGroups = duplicateMembers.Count;
        var cardGroups = duplicateCards.Count;
        var latest = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == "tachodrivermastersync" && row.Status == StagingStatus.Promoted)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .Select(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);
        return new(
            drivers.Count,
            drivers.Count(driver => !string.IsNullOrWhiteSpace(driver.TachoMasterDriverId)),
            drivers.Count(driver => !string.IsNullOrWhiteSpace(driver.TachoCardNumber)),
            memberGroups,
            cardGroups,
            drivers.Count(driver => string.IsNullOrWhiteSpace(driver.TachoMasterDriverId)),
            drivers.Count(driver => string.IsNullOrWhiteSpace(driver.TachoCardNumber)),
            latest == default ? null : latest)
        {
            DuplicateMembers = duplicateMembers,
            DuplicateCards = duplicateCards
        };
    }

    public async Task<TachoLiveWorker?> ProfileAsync(Guid driverId, CancellationToken ct)
    {
        var driver = await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == driverId, ct);
        if (driver is null) return null;
        await MasterDetailStore.EnrichDriversAsync(db, [driver], ct);
        if (string.IsNullOrWhiteSpace(driver.TachoMasterDriverId)) return null;
        var key = $"tachodriverprofile:{TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoMasterDriverId)}";
        var row = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.IdempotencyKey == key && item.Status == StagingStatus.Promoted, ct);
        if (row is null) return null;
        try { return JsonSerializer.Deserialize<TachoLiveWorker>(row.PayloadJson, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static Driver SelectCanonical(IReadOnlyCollection<Driver> candidates, TachoLiveWorker worker, IReadOnlyDictionary<Guid, int> loadUse)
        => candidates
            .OrderByDescending(driver => CanonicalScore(driver, worker, loadUse.GetValueOrDefault(driver.Id)))
            .ThenBy(driver => driver.EmployeeNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(driver => driver.Id)
            .First();

    internal static IReadOnlyList<TachoLiveWorker> CanonicaliseLiveWorkers(
        IReadOnlyCollection<TachoLiveWorker> workers,
        IReadOnlyCollection<Driver> drivers,
        IReadOnlyDictionary<Guid, int> loadUse)
    {
        var cardWorkers = GroupByPhysicalCard(workers)
            .Select(group => SelectPreferredLiveWorker(group, drivers, loadUse))
            .ToList();

        var representedMembers = cardWorkers.Select(worker => worker.MemberCode).ToHashSet();
        var noCardWorkers = workers
            .Where(worker => TachoDriverIdentityRules.NormaliseIdentifier(worker.CardNumber).Length == 0)
            .Where(worker => !representedMembers.Contains(worker.MemberCode))
            .GroupBy(worker => worker.MemberCode)
            .Select(group => SelectPreferredLiveWorker(group.ToList(), drivers, loadUse));

        return cardWorkers.Concat(noCardWorkers)
            .OrderBy(worker => worker.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(worker => worker.MemberCode)
            .ToList();
    }

    private static IReadOnlyList<IReadOnlyCollection<TachoLiveWorker>> GroupByPhysicalCard(
        IReadOnlyCollection<TachoLiveWorker> workers)
    {
        var groups = new List<List<TachoLiveWorker>>();
        foreach (var worker in workers.Where(worker => TachoDriverIdentityRules.NormaliseIdentifier(worker.CardNumber).Length > 0))
        {
            var group = groups.FirstOrDefault(existing =>
                existing.Any(candidate => TachoDriverIdentityRules.CardsMatch(candidate.CardNumber, worker.CardNumber)));
            if (group is null) groups.Add([worker]);
            else group.Add(worker);
        }
        return groups;
    }

    private static TachoLiveWorker SelectPreferredLiveWorker(
        IReadOnlyCollection<TachoLiveWorker> candidates,
        IReadOnlyCollection<Driver> drivers,
        IReadOnlyDictionary<Guid, int> loadUse)
    {
        return candidates
            .OrderByDescending(worker => drivers
                .Where(driver => TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, worker.CardNumber) ||
                                 TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, worker.MemberCode.ToString(CultureInfo.InvariantCulture)))
                .Select(driver => CanonicalScore(driver, worker, loadUse.GetValueOrDefault(driver.Id)))
                .DefaultIfEmpty(0)
                .Max())
            .ThenByDescending(worker => !string.IsNullOrWhiteSpace(worker.CardNumber))
            .ThenByDescending(worker => ParseDate(worker.CardLastRead))
            .ThenByDescending(worker => !string.IsNullOrWhiteSpace(worker.EmployeeNumber))
            .ThenBy(worker => worker.MemberCode)
            .First();
    }

    private static int CanonicalScore(Driver driver, TachoLiveWorker worker, int loadCount)
    {
        var score = Math.Min(loadCount, 100) * 10;
        if (driver.Active) score += 100;
        if (TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, worker.MemberCode.ToString(CultureInfo.InvariantCulture))) score += 800;
        if (TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, worker.CardNumber)) score += 600;
        if (!driver.EmployeeNumber.StartsWith("TM-", StringComparison.OrdinalIgnoreCase)) score += 40;
        if (!string.IsNullOrWhiteSpace(driver.MobileNumber)) score += 20;
        if (!string.IsNullOrWhiteSpace(driver.DriverType)) score += 10;
        if (!string.IsNullOrWhiteSpace(driver.DriverGroup)) score += 10;
        if (!string.IsNullOrWhiteSpace(driver.Skills)) score += 10;
        if (!string.IsNullOrWhiteSpace(driver.AgencyName)) score += 10;
        if (!string.IsNullOrWhiteSpace(driver.DrivingLicenceNumber) || driver.LicenceExpiry is not null) score += 10;
        return score;
    }

    private static bool IdentityCompatible(Driver driver, TachoLiveWorker worker)
    {
        if (!string.IsNullOrWhiteSpace(driver.TachoMasterDriverId) &&
            !TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, worker.MemberCode.ToString(CultureInfo.InvariantCulture))) return false;
        if (!string.IsNullOrWhiteSpace(driver.TachoCardNumber) && !string.IsNullOrWhiteSpace(worker.CardNumber) &&
            !TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, worker.CardNumber)) return false;
        return true;
    }

    private async Task MergeDuplicateAsync(Driver canonical, Driver duplicate, IReadOnlyCollection<StagedImport> detailRows, string actor, CancellationToken ct)
    {
        canonical.MobileNumber ??= duplicate.MobileNumber;
        canonical.DriverType ??= duplicate.DriverType;
        canonical.DriverGroup ??= duplicate.DriverGroup;
        canonical.Skills ??= duplicate.Skills;
        canonical.Coding ??= duplicate.Coding;
        canonical.AgencyName ??= duplicate.AgencyName;
        canonical.Notes ??= duplicate.Notes;
        canonical.DrivingLicenceNumber ??= duplicate.DrivingLicenceNumber;
        canonical.LicenceExpiry ??= duplicate.LicenceExpiry;
        canonical.LicenceStatus ??= duplicate.LicenceStatus;

        // If the canonical record was created from TachoMaster but the duplicate owns a real
        // payroll/agency reference (including Sage HR employee number), move that stable CRM key
        // onto the canonical record. The retired duplicate receives a unique audit-only key first.
        var canonicalOldEmployee = canonical.EmployeeNumber;
        var duplicateOldEmployee = duplicate.EmployeeNumber;
        if (canonical.EmployeeNumber.StartsWith("TM-", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(duplicate.EmployeeNumber) &&
            !duplicate.EmployeeNumber.StartsWith("TM-", StringComparison.OrdinalIgnoreCase))
        {
            duplicate.EmployeeNumber = RetiredEmployeeNumber(duplicate.Id);
            await db.SaveChangesAsync(ct);
            canonical.EmployeeNumber = duplicateOldEmployee;
            ArchiveDetail(detailRows, canonicalOldEmployee, canonical.EmployeeNumber, canonical.Id);
        }

        foreach (var load in await db.Loads.Where(load => load.DriverId == duplicate.Id).ToListAsync(ct)) load.DriverId = canonical.Id;
        foreach (var status in await db.DriverStatusLogs.Where(status => status.DriverId == duplicate.Id).ToListAsync(ct)) status.DriverId = canonical.Id;
        try
        {
            foreach (var run in await db.PlanProposalRuns.Where(run => run.DriverId == duplicate.Id).ToListAsync(ct)) run.DriverId = canonical.Id;
            foreach (var candidate in await db.PlanProposalCandidates.Where(candidate => candidate.DriverId == duplicate.Id).ToListAsync(ct))
            {
                var canonicalCandidateExists = await db.PlanProposalCandidates.AnyAsync(existing =>
                    existing.Id != candidate.Id &&
                    existing.ProposalRunId == candidate.ProposalRunId &&
                    existing.VehicleId == candidate.VehicleId &&
                    existing.DriverId == canonical.Id, ct);
                if (canonicalCandidateExists) db.PlanProposalCandidates.Remove(candidate);
                else candidate.DriverId = canonical.Id;
            }
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            logger.LogWarning(ex, "Optional planning proposal driver references could not be reassigned while merging driver {DuplicateDriverId}.", duplicate.Id);
        }

        try
        {
            foreach (var allocation in await db.RunResourceAllocations.Where(allocation => allocation.DriverId == duplicate.Id).ToListAsync(ct))
                allocation.DriverId = canonical.Id;
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            logger.LogWarning(ex, "Canonical run driver references could not be reassigned while merging driver {DuplicateDriverId}.", duplicate.Id);
        }

        try
        {
            foreach (var mapping in await db.IntegrationMappings.Where(mapping => mapping.TmsEntityType == "Driver" && mapping.TmsEntityId == duplicate.Id).ToListAsync(ct))
                mapping.TmsEntityId = canonical.Id;
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            logger.LogWarning(ex, "Driver integration mappings could not be reassigned while merging driver {DuplicateDriverId}.", duplicate.Id);
        }

        try
        {
            foreach (var audit in await db.MasterDataAudits.Where(audit => audit.EntityType == "Driver" && audit.EntityId == duplicate.Id).ToListAsync(ct))
                audit.EntityId = canonical.Id;
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            logger.LogWarning(ex, "Driver audit history could not be reassigned while merging driver {DuplicateDriverId}.", duplicate.Id);
        }

        var duplicateIdText = duplicate.Id.ToString();
        var planningRows = await db.StagedImports
            .Where(row => row.EntityType == "planningload" && row.Status == StagingStatus.Promoted && row.PayloadJson.Contains(duplicateIdText))
            .ToListAsync(ct);
        foreach (var row in planningRows)
        {
            try
            {
                var load = JsonSerializer.Deserialize<Load>(row.PayloadJson, JsonOptions);
                if (load?.DriverId != duplicate.Id) continue;
                load.DriverId = canonical.Id;
                row.PayloadJson = JsonSerializer.Serialize(load, JsonOptions);
                row.ReviewedAtUtc = DateTimeOffset.UtcNow;
                row.ReviewedBy = actor;
                row.ReviewNote = $"Driver identity merged from {duplicate.Id} to canonical {canonical.Id}.";
            }
            catch (JsonException) { }
        }

        var documentPrefix = $"masterdocument:Driver:{duplicate.Id:N}:";
        var documents = await db.StagedImports.Where(row => row.EntityType == "masterdocument" && row.IdempotencyKey.StartsWith(documentPrefix)).ToListAsync(ct);
        foreach (var row in documents)
        {
            var suffix = row.IdempotencyKey[documentPrefix.Length..];
            row.IdempotencyKey = $"masterdocument:Driver:{canonical.Id:N}:{suffix}";
            try
            {
                var node = JsonNode.Parse(row.PayloadJson) as JsonObject;
                if (node is not null)
                {
                    node["entityId"] = canonical.Id.ToString();
                    row.PayloadJson = node.ToJsonString(JsonOptions);
                }
            }
            catch (JsonException) { }
        }

        ArchiveDetail(detailRows, duplicateOldEmployee, canonical.EmployeeNumber, canonical.Id);
        duplicate.Active = false;
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Driver",
            EntityId = canonical.Id,
            Action = "MergedDuplicateTachoIdentity",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new
            {
                canonicalDriverId = canonical.Id,
                duplicateDriverId = duplicate.Id,
                duplicateEmployeeNumber = duplicateOldEmployee,
                duplicate.DisplayName,
                duplicate.TachoMasterDriverId,
                duplicate.TachoCardNumber
            }, JsonOptions)
        });
    }

    private static void ArchiveDetail(IReadOnlyCollection<StagedImport> detailRows, string? employeeNumber, string canonicalEmployeeNumber, Guid canonicalId)
    {
        if (string.IsNullOrWhiteSpace(employeeNumber)) return;
        foreach (var detail in detailRows.Where(row => string.Equals(row.IdempotencyKey, DetailKey(employeeNumber), StringComparison.OrdinalIgnoreCase)))
        {
            detail.Status = StagingStatus.Archived;
            detail.ReviewedAtUtc = DateTimeOffset.UtcNow;
            detail.ReviewNote = $"Duplicate Driver Master detail retired into canonical driver {canonicalEmployeeNumber} ({canonicalId}).";
        }
    }

    private static void ApplyWorker(Driver driver, TachoLiveWorker worker, TachoDriverProfile? profile, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(driver.DisplayName)) driver.DisplayName = worker.DisplayName;
        driver.TachoName = worker.DisplayName;
        driver.TachoMasterDriverId = worker.MemberCode.ToString(CultureInfo.InvariantCulture);
        driver.TachoCardNumber = Clean(worker.CardNumber);
        driver.AgencyName = Clean(worker.AgencyName) ?? driver.AgencyName;
        if (string.IsNullOrWhiteSpace(driver.DriverType) ||
            string.Equals(driver.DriverType, "Agency", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(driver.DriverType, "Casual", StringComparison.OrdinalIgnoreCase))
            driver.DriverType = Clean(worker.WorkerType) ?? driver.DriverType;
        driver.LicenceExpiry = ParseDate(worker.DrivingLicenceExpiry) ?? driver.LicenceExpiry;
        driver.CPCExpiry = ParseDate(worker.CpcExpiry) ?? driver.CPCExpiry;
        driver.DigitalTachoCardExpiry = ParseDate(worker.DriverCardExpiry) ?? driver.DigitalTachoCardExpiry;
        driver.TachoDriveAvailableTodayMinutes = profile?.DriveAvailableTodayMinutes ?? driver.TachoDriveAvailableTodayMinutes;
        driver.TachoDriveAvailableWeekMinutes = profile?.DriveAvailableWeekMinutes ?? driver.TachoDriveAvailableWeekMinutes;
        driver.TachoWorkAvailableWeekMinutes = profile?.WorkAvailableWeekMinutes ?? driver.TachoWorkAvailableWeekMinutes;
        driver.LastTachoSyncUtc = now;
    }

    private static string UniqueEmployeeNumber(TachoLiveWorker worker, IReadOnlyCollection<Driver> drivers)
    {
        var used = drivers.Select(driver => driver.EmployeeNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preferred = Clean(worker.EmployeeNumber);
        if (!string.IsNullOrWhiteSpace(preferred) && !used.Contains(preferred)) return Clip(preferred, 40)!;
        var root = $"TM-{worker.MemberCode}";
        if (!used.Contains(root)) return root;
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{root}-{suffix}";
            if (!used.Contains(candidate)) return candidate;
        }
        return $"TM-{Guid.NewGuid():N}"[..40];
    }

    private void UpsertDetail(Dictionary<string, StagedImport> rows, Driver driver, TachoLiveWorker worker, string actor, DateTimeOffset now)
    {
        var key = DetailKey(driver.EmployeeNumber);
        if (!rows.TryGetValue(key, out var row))
        {
            row = new StagedImport
            {
                EntityType = DetailType,
                IdempotencyKey = key,
                PayloadJson = "{}",
                Source = "TachoMaster canonical Driver Master",
                ReceivedAtUtc = now
            };
            db.StagedImports.Add(row);
            rows[key] = row;
        }
        row.EntityType = DetailType;
        row.Status = StagingStatus.Promoted;
        row.Source = "TachoMaster canonical Driver Master";
        row.PayloadJson = JsonSerializer.Serialize(new
        {
            driver.EmployeeNumber,
            driver.DisplayName,
            driver.TachoName,
            driver.MobileNumber,
            driver.DriverType,
            driver.DriverGroup,
            driver.Skills,
            driver.Coding,
            driver.AgencyName,
            driver.Notes,
            driver.TachoMasterDriverId,
            driver.TachoCardNumber,
            driver.TachoDriveAvailableTodayMinutes,
            driver.TachoDriveAvailableWeekMinutes,
            driver.TachoWorkAvailableWeekMinutes,
            driver.DrivingLicenceNumber,
            driver.LicenceExpiry,
            driver.LicenceStatus,
            driver.LastTachoSyncUtc,
            tachoMasterProfile = worker
        }, JsonOptions);
        row.ReviewedAtUtc = now;
        row.ReviewedBy = actor;
        row.ReviewNote = "Canonical TachoMaster identity with TMS CRM enrichment. North/Preload legacy fields are intentionally not retained.";
    }

    private void UpsertProfile(Dictionary<string, StagedImport> rows, TachoLiveWorker worker, string actor, DateTimeOffset now)
    {
        var key = $"tachodriverprofile:{TachoDriverIdentityRules.NormaliseIdentifier(worker.MemberCode.ToString(CultureInfo.InvariantCulture))}";
        if (!rows.TryGetValue(key, out var row))
        {
            row = new StagedImport
            {
                EntityType = ProfileType,
                IdempotencyKey = key,
                PayloadJson = "{}",
                Source = "TachoMaster live Worker List",
                ReceivedAtUtc = now
            };
            db.StagedImports.Add(row);
            rows[key] = row;
        }
        row.Status = StagingStatus.Promoted;
        row.Source = "TachoMaster live Worker List";
        row.PayloadJson = JsonSerializer.Serialize(worker, JsonOptions);
        row.ReviewedAtUtc = now;
        row.ReviewedBy = actor;
        row.ReviewNote = "Latest current/live TachoMaster Worker List profile for the canonical driver identity.";
    }

    private static int CountDuplicateNames(IReadOnlyCollection<TachoLiveWorker> workers) => workers
        .GroupBy(worker => TachoDriverIdentityRules.NormalisePerson(worker.DisplayName), StringComparer.OrdinalIgnoreCase)
        .Count(group => group.Key.Length > 0 && group.Select(worker => worker.MemberCode).Distinct().Count() > 1);

    private static int DuplicateIdentityGroupCount(IEnumerable<Driver> drivers, Func<Driver, string?> selector) =>
        drivers
            .Select(selector)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(TachoDriverIdentityRules.NormaliseIdentifier, StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Key.Length > 0 && group.Count() > 1);

    private static IReadOnlyList<TachoDriverMasterIdentityDuplicate> DuplicateIdentities(
        IEnumerable<Driver> drivers,
        Func<Driver, string?> selector) =>
        drivers
            .Where(driver => !string.IsNullOrWhiteSpace(selector(driver)))
            .GroupBy(driver => TachoDriverIdentityRules.NormaliseIdentifier(selector(driver)), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 && group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new TachoDriverMasterIdentityDuplicate(
                group.Key,
                group.OrderBy(driver => driver.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(driver => new TachoDriverMasterDuplicateDriver(driver.Id, driver.EmployeeNumber, driver.DisplayName))
                    .ToList()))
            .ToList();

    private static string RetiredEmployeeNumber(Guid id) => $"MERGED-{id:N}"[..Math.Min(40, $"MERGED-{id:N}".Length)];
    private static string DetailKey(string employeeNumber) => $"masterdetail:driver:{NormaliseKey(employeeNumber)}";
    private static string NormaliseKey(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParse(value, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AllowWhiteSpaces, out var gb)) return gb;
        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var invariant) ? invariant : null;
    }
    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}

public static class TachoDriverCardReadEligibility
{
    private static readonly string[] DayFirstFormats = ["d/M/yyyy", "dd/MM/yyyy", "d/M/yy", "dd/MM/yy", "yyyy-MM-dd"];

    public static bool IsEligible(string? cardLastRead, DateOnly ukToday)
    {
        if (!TryParseUkDate(cardLastRead, out var lastRead)) return false;
        return lastRead >= ukToday.AddMonths(-6) && lastRead <= ukToday;
    }

    public static DateOnly UkToday(DateTimeOffset? nowUtc = null)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc ?? DateTimeOffset.UtcNow, LondonTimeZone()).DateTime);

    private static bool TryParseUkDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var clean = value.Trim();
        if (DateOnly.TryParseExact(clean, DayFirstFormats, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out date))
            return true;
        if (DateTimeOffset.TryParse(clean, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, LondonTimeZone()).DateTime);
            return true;
        }
        return DateOnly.TryParse(clean, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static TimeZoneInfo LondonTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }
}

internal static class TachoDriverIdentityRules
{
    public static bool MemberMatches(string? left, string? right)
    {
        var a = NormaliseIdentifier(left);
        var b = NormaliseIdentifier(right);
        return a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CardsMatch(string? left, string? right)
    {
        var a = NormaliseIdentifier(left);
        var b = NormaliseIdentifier(right);
        if (a.Length < 8 || b.Length < 8) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
               a.EndsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.EndsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormaliseIdentifier(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string NormalisePerson(string? value) => string.Join(' ', (value ?? string.Empty)
        .Replace(',', ' ')
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
        .Where(word => word.Length > 0)
        .OrderBy(word => word, StringComparer.Ordinal));
}

public sealed record TachoLiveWorker(
    int MemberCode,
    string DisplayName,
    string? CardNumber,
    string? EmployeeNumber,
    string? WorkerType,
    string? AgencyName,
    string? Email,
    string? Started,
    string? CardLastRead,
    string? DriverCardExpiry,
    string? LicencePassDate,
    string? DrivingLicenceExpiry,
    string? LicenceCheckDue,
    string? LicencePhotoExpiry,
    string? CpcExpiry,
    string? DqcExpiry,
    string RawSourceJson);

internal sealed class TachoLiveWorkerDirectory(HttpClient httpClient, TachoMasterOptions options)
{
    public async Task<IReadOnlyList<TachoLiveWorker>> GetLiveWorkersAsync(CancellationToken ct)
    {
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.BaseAddress = new Uri(NormaliseBaseUrl(options.BaseUrl));
        var sid = await LoginAsync(ct);
        var result = new List<TachoLiveWorker>();
        var offset = 0;
        for (var page = 0; page < Math.Max(1, options.MaxPages); page++)
        {
            using var response = await SendWithRetryAsync(() =>
            {
                var request = CreateRequest(HttpMethod.Post, "Member/GetMembersLong", sid);
                request.Content = JsonContent.Create(new { Offset = offset, OnlyLiveMembers = true });
                return request;
            }, "Member/GetMembersLong", ct);
            await EnsureSuccessAsync(response, "Member/GetMembersLong", ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var data = Property(root, "Data");
            if (data is not JsonElement rows || rows.ValueKind != JsonValueKind.Array) break;
            var count = 0;
            foreach (var item in rows.EnumerateArray())
            {
                var memberCode = Int(item, "MemCode", "MemberCode");
                if (memberCode <= 0) continue;
                var given = Text(item, "GivenNames", "CName", "Forename", "FirstName");
                var surname = Text(item, "Surname", "SName", "LastName");
                var sourceName = Text(item, "WorkerName", "MemberName", "Name");
                var displayName = string.Join(' ', new[] { given, surname }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
                if (displayName.Length == 0) displayName = DisplayName(sourceName);
                if (displayName.Length == 0) continue;

                result.Add(new TachoLiveWorker(
                    memberCode,
                    displayName,
                    Text(item, "CardNoShort", "CardNo", "DriverCardNo", "DriverCardNumber", "CardNumber", "CardNumberLong", "TachoCardNumber", "TachographCardNumber", "TachographNumber", "DriverCard"),
                    Text(item, "EmployeeNumber", "PayrollNumber"),
                    Text(item, "Type", "WorkerType", "MemberType", "MemType"),
                    Text(item, "Agency", "AgencyName"),
                    Text(item, "Email", "EmailAddress"),
                    Text(item, "Started", "StartDate", "DateStarted"),
                    Text(item, "CardLastRead", "DriverCardLastRead"),
                    Text(item, "DriverCardExp", "DriverCardExpiry", "CardExpiry"),
                    Text(item, "LicencePassDate", "DrivingLicencePassDate"),
                    Text(item, "DrivingLicenceExp", "DrivingLicenceExpiry", "LicenceExpiry"),
                    Text(item, "LicenceCheckDue", "DrivingLicenceCheckDue"),
                    Text(item, "LicencePhotoExp", "LicencePhotoExpiry"),
                    Text(item, "CPCExpiry", "CpcExpiry"),
                    Text(item, "DQCExpiry", "DqcExpiry"),
                    item.GetRawText()));
                count++;
            }

            var moreData = Bool(root, "MoreData");
            var recordCount = Int(root, "RecordCount");
            if (!moreData || recordCount <= 0 || count == 0) break;
            offset += recordCount;
        }

        return result
            .GroupBy(worker => worker.MemberCode)
            .Select(group => group
                .OrderByDescending(worker => !string.IsNullOrWhiteSpace(worker.CardNumber))
                .ThenByDescending(worker => TryParseDate(worker.CardLastRead))
                .ThenByDescending(worker => !string.IsNullOrWhiteSpace(worker.EmployeeNumber))
                .ThenBy(worker => worker.DisplayName, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(worker => worker.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<string> LoginAsync(CancellationToken ct)
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
            }, "login", ct);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadAsStringAsync(ct);
                return ExtractSessionId(payload);
            }
            lastFailure = await FailureDetailAsync(response, "login", ct);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)) break;
        }
        throw new HttpRequestException($"TachoMaster login failed. {lastFailure ?? "No response was received."}");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string sid)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("APIKEY", options.ApiKey);
        request.Headers.Add("SID", sid);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> factory, string operation, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = factory();
                var response = await httpClient.SendAsync(request, ct);
                if (!IsTransient(response.StatusCode) || attempt == 3) return response;
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < 3) { }
            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
        }
        throw new InvalidOperationException($"TachoMaster {operation} failed without a response.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode == HttpStatusCode.RequestTimeout || statusCode == (HttpStatusCode)429 || (int)statusCode >= 500;
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException(await FailureDetailAsync(response, operation, ct), null, response.StatusCode);
    }
    private static async Task<string> FailureDetailAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var detail = await response.Content.ReadAsStringAsync(ct);
        if (detail.Length > 300) detail = detail[..300];
        return $"TachoMaster {operation} returned {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}";
    }
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
    private static string DisplayName(string? source)
    {
        var value = (source ?? string.Empty).Trim();
        var comma = value.IndexOf(',');
        return comma > 0 ? $"{value[(comma + 1)..].Trim()} {value[..comma].Trim()}".Trim() : value;
    }
    private static JsonElement? Property(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))) return property.Value;
        return null;
    }
    private static string? Text(JsonElement element, params string[] names)
    {
        var value = Property(element, names);
        if (value is not JsonElement item) return null;
        return item.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(item.GetString()) ? null : item.GetString()!.Trim(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => item.ToString(),
            _ => null
        };
    }
    private static int Int(JsonElement element, params string[] names) => int.TryParse(Text(element, names), out var value) ? value : 0;
    private static bool Bool(JsonElement element, params string[] names) => bool.TryParse(Text(element, names), out var value) && value;
    private static DateOnly? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParse(value, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AllowWhiteSpaces, out var date))
            return date;
        return null;
    }
}

public sealed class TachoDriverMasterBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<TachoDriverMasterBackgroundService> logger) : BackgroundService
{
    // Enqueues a canonical Driver Master job every 60 minutes. The job is picked up
    // by TachoDriverMasterSyncJobService which runs the orchestrator — the same code path
    // used by manual syncs triggered from the UI. This replaces the old direct SyncAsync
    // call which bypassed the orchestrator and used a separate (now stale) code path.
    private static readonly TimeSpan FullSyncInterval = TimeSpan.FromMinutes(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief startup delay so the app is fully ready before the first enqueue.
        try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<TachoDriverMasterSyncJobService>();
                await jobs.EnqueueAsync("system:tachomaster-canonical-driver-master-scheduled", stoppingToken);
                logger.LogInformation("TachoMaster canonical Driver Master sync job enqueued by scheduled background service.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Scheduled TachoMaster canonical Driver Master job enqueue failed."); }

            try { await Task.Delay(FullSyncInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
