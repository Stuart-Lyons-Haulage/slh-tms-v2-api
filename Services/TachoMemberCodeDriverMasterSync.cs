using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Canonical Driver Master synchronisation where the TachoMaster Member Code is the person identity.
/// Tachograph card, employee number and name are supporting matching evidence only. A card change or
/// historical card reuse must never cause one TachoMaster member to be merged into another member.
/// </summary>
public static class TachoMemberCodeDriverMasterSync
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private const string DetailType = "masterdetail:driver";
    private const string ProfileType = "tachodriverprofile";

    public static async Task<TachoDriverMasterSyncResult> RunAsync(
        TmsDbContext db,
        TachoMasterClient tachoMaster,
        IHttpClientFactory httpClientFactory,
        TachoMasterOptions options,
        ILogger logger,
        string actor,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!options.IsConfigured)
            return Failed("TachoMaster is not configured, so the canonical Driver Master was not changed.", now);

        IReadOnlyList<TachoLiveWorker> rawWorkers;
        try
        {
            rawWorkers = await new TachoLiveWorkerDirectory(httpClientFactory.CreateClient(), options).GetLiveWorkersAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "TachoMaster Member Code canonical driver sync could not read the live worker directory.");
            return Failed($"TachoMaster live worker directory could not be read: {ex.GetBaseException().Message}. No Driver Master records were changed.", now);
        }

        if (rawWorkers.Count < 25)
            return Failed($"TachoMaster returned only {rawWorkers.Count} live worker(s). Member Code canonicalisation stopped below the safety floor.", now, rawWorkers.Count);

        var drivers = await db.Drivers.OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var activeBefore = drivers.Count(driver => driver.Active);
        var ukToday = TachoDriverCardReadEligibility.UkToday(now);

        // Member Code is the identity. Eligibility remains the agreed operational population rule:
        // only drivers with a card read in the previous six months are promoted as current drivers.
        var workers = rawWorkers
            .Where(DriverPopulationRules.IsDriver)
            .Where(worker => worker.MemberCode > 0)
            .Where(worker => TachoDriverCardReadEligibility.IsEligible(worker.CardLastRead, ukToday))
            .GroupBy(worker => worker.MemberCode)
            .Select(group => PreferredWorker(group))
            .OrderBy(worker => worker.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(worker => worker.MemberCode)
            .ToList();

        if (workers.Count < 25 || (activeBefore > 0 && workers.Count < Math.Max(25, (int)Math.Floor(activeBefore * 0.35m))))
            return Failed(
                $"Only {workers.Count} unique TachoMaster Member Code driver(s) had a qualifying card read within the last six months against {activeBefore} active TMS driver rows. The safety check stopped the sync; no records were changed.",
                now,
                workers.Count);

        IReadOnlyDictionary<int, TachoDriverProfile> profilesByMember = new Dictionary<int, TachoDriverProfile>();
        try
        {
            profilesByMember = (await tachoMaster.GetDriverProfilesAsync(ct))
                .Where(profile => profile.MemberCode > 0)
                .GroupBy(profile => profile.MemberCode)
                .ToDictionary(group => group.Key, group => group.First());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TachoMaster hours metrics were unavailable; Member Code identity sync will continue without hours enrichment.");
        }

        var loadUse = await db.Loads.AsNoTracking()
            .Where(load => load.DriverId != null)
            .GroupBy(load => load.DriverId!.Value)
            .Select(group => new { DriverId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.DriverId, item => item.Count, ct);

        var detailRows = await db.StagedImports
            .Where(row => row.EntityType == DetailType)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .ToListAsync(ct);
        var detailByKey = detailRows
            .GroupBy(row => row.IdempotencyKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var profileRows = (await db.StagedImports.Where(row => row.EntityType == ProfileType).ToListAsync(ct))
            .GroupBy(row => row.IdempotencyKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var liveNameCounts = workers
            .GroupBy(worker => TachoDriverIdentityRules.NormalisePerson(worker.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var claimed = new HashSet<Guid>();
        var created = 0;
        var updated = 0;
        var retired = 0;
        var matchedByMember = 0;
        var matchedByCard = 0;
        var matchedByName = 0;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var worker in workers)
            {
                var member = worker.MemberCode.ToString(CultureInfo.InvariantCulture);
                var memberMatches = drivers
                    .Where(driver => !claimed.Contains(driver.Id))
                    .Where(driver => TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, member))
                    .ToList();

                Driver? canonical = null;
                var aliases = new List<Driver>();

                if (memberMatches.Count > 0)
                {
                    canonical = SelectCanonical(memberMatches, worker, loadUse);
                    aliases.AddRange(memberMatches.Where(driver => driver.Id != canonical.Id));
                    matchedByMember++;
                }
                else
                {
                    // Card is a fallback only where the local row has no conflicting Member Code.
                    var cardMatches = string.IsNullOrWhiteSpace(worker.CardNumber)
                        ? []
                        : drivers
                            .Where(driver => !claimed.Contains(driver.Id))
                            .Where(driver => string.IsNullOrWhiteSpace(driver.TachoMasterDriverId) ||
                                             TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, member))
                            .Where(driver => TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, worker.CardNumber))
                            .ToList();
                    if (cardMatches.Count > 0)
                    {
                        canonical = SelectCanonical(cardMatches, worker, loadUse);
                        aliases.AddRange(cardMatches.Where(driver => driver.Id != canonical.Id));
                        matchedByCard++;
                    }
                }

                if (canonical is null && !string.IsNullOrWhiteSpace(worker.EmployeeNumber))
                {
                    var employeeMatches = drivers
                        .Where(driver => !claimed.Contains(driver.Id))
                        .Where(driver => string.Equals(driver.EmployeeNumber, worker.EmployeeNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                        .Where(driver => string.IsNullOrWhiteSpace(driver.TachoMasterDriverId) ||
                                         TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, member))
                        .ToList();
                    if (employeeMatches.Count == 1)
                        canonical = employeeMatches[0];
                }

                if (canonical is null)
                {
                    var nameKey = TachoDriverIdentityRules.NormalisePerson(worker.DisplayName);
                    if (nameKey.Length > 0 && liveNameCounts.GetValueOrDefault(nameKey) == 1)
                    {
                        var nameMatches = drivers
                            .Where(driver => !claimed.Contains(driver.Id))
                            .Where(driver => string.IsNullOrWhiteSpace(driver.TachoMasterDriverId) ||
                                             TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, member))
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
                    // A new TachoMaster member with no matching Driver Master record is staged
                    // for human review rather than being created as an active driver immediately.
                    // The review queue (StagedImports, EntityType "driverreview") is surfaced in
                    // the UI and picked up by the Assistant suggestion "drivers-tacho-new".
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
                                source = "TachoMaster live worker directory — no matching Driver Master record",
                                receivedAtUtc = now
                            }, JsonOptions),
                            Source = "TachoMaster Member Code canonical Driver Master sync",
                            Status = StagingStatus.PendingReview,
                            ReceivedAtUtc = now,
                            ReviewNote = $"New TachoMaster member {member} ({worker.DisplayName}) has no matching Driver Master record. Review and promote to create driver, or reject to discard."
                        });
                    }
                    else
                    {
                        // Refresh the payload so the review item always reflects the latest Tacho data.
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
                            source = "TachoMaster live worker directory — no matching Driver Master record",
                            receivedAtUtc = now
                        }, JsonOptions);
                        existingReview.ReviewedAtUtc = now;
                        existingReview.ReviewNote = $"Updated from TachoMaster sync at {now:u}. Still pending Driver Master review.";
                    }

                    // Skip claiming this worker — it has no canonical driver record yet.
                    UpsertProfile(db, profileRows, worker, actor, now);
                    continue;
                }
                else
                {
                    updated++;
                }

                // Once a canonical row is selected by Member Code, merge any remaining unclaimed
                // local aliases that carry the same Member Code. A different nonblank Member Code
                // is never merged merely because card/name data happens to match.
                aliases.AddRange(drivers
                    .Where(driver => driver.Id != canonical.Id && !claimed.Contains(driver.Id))
                    .Where(driver => TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, member)));

                foreach (var duplicate in aliases.DistinctBy(driver => driver.Id).ToList())
                {
                    await MergeDuplicateAsync(db, canonical, duplicate, detailRows, actor, logger, ct);
                    retired++;
                }

                ApplyWorker(canonical, worker, profilesByMember.GetValueOrDefault(worker.MemberCode), now);
                canonical.Active = true;
                claimed.Add(canonical.Id);
                UpsertDetail(db, detailByKey, canonical, worker, actor, now);
                UpsertProfile(db, profileRows, worker, actor, now);
            }

            var archived = 0;
            foreach (var driver in drivers.Where(driver => driver.Active && !claimed.Contains(driver.Id)))
            {
                driver.Active = false;
                archived++;
                db.MasterDataAudits.Add(new MasterDataAudit
                {
                    EntityType = "Driver",
                    EntityId = driver.Id,
                    Action = "ArchivedNotInCurrentTachoMemberPopulation",
                    ChangedBy = actor,
                    ChangesJson = JsonSerializer.Serialize(new
                    {
                        reason = "No qualifying TachoMaster Member Code/card-read identity in the last six months",
                        driver.EmployeeNumber,
                        driver.DisplayName,
                        driver.TachoMasterDriverId,
                        driver.TachoCardNumber
                    }, JsonOptions)
                });
            }

            // Card numbers are supporting evidence, not identity. If two distinct current members
            // present the same card value, retain it on the strongest row and clear it from the
            // other member(s) instead of merging two different people.
            var activeAfter = drivers.Where(driver => driver.Active).ToList();
            foreach (var group in activeAfter
                .Where(driver => !string.IsNullOrWhiteSpace(driver.TachoCardNumber))
                .GroupBy(driver => TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoCardNumber), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Key.Length > 0 && group.Count() > 1))
            {
                var survivor = group
                    .OrderByDescending(driver => loadUse.GetValueOrDefault(driver.Id))
                    .ThenByDescending(driver => driver.LastTachoSyncUtc)
                    .ThenBy(driver => driver.Id)
                    .First();

                foreach (var duplicateCard in group.Where(driver => driver.Id != survivor.Id))
                {
                    var cleared = duplicateCard.TachoCardNumber;
                    duplicateCard.TachoCardNumber = null;
                    db.MasterDataAudits.Add(new MasterDataAudit
                    {
                        EntityType = "Driver",
                        EntityId = duplicateCard.Id,
                        Action = "ClearedDuplicateSupportingTachoCard",
                        ChangedBy = actor,
                        ChangesJson = JsonSerializer.Serialize(new
                        {
                            memberCode = duplicateCard.TachoMasterDriverId,
                            clearedCard = cleared,
                            retainedOnMemberCode = survivor.TachoMasterDriverId,
                            reason = "TachoMaster Member Code is canonical; duplicate card evidence must not merge different members."
                        }, JsonOptions)
                    });
                }
            }

            activeAfter = drivers.Where(driver => driver.Active).ToList();
            var duplicateMembers = DuplicateIdentityGroupCount(activeAfter, driver => driver.TachoMasterDriverId);
            var duplicateCards = DuplicateIdentityGroupCount(activeAfter, driver => driver.TachoCardNumber);
            var activeWithoutMember = activeAfter.Count(driver => string.IsNullOrWhiteSpace(driver.TachoMasterDriverId));
            var workersWithoutCard = workers.Count(worker => string.IsNullOrWhiteSpace(worker.CardNumber));
            var healthy = claimed.Count == workers.Count &&
                          activeAfter.Count == workers.Count &&
                          duplicateMembers == 0 &&
                          activeWithoutMember == 0;

            var payload = JsonSerializer.Serialize(new
            {
                identityAuthority = "TachoMaster Member Code",
                sourceWorkers = workers.Count,
                rawSourceWorkers = rawWorkers.Count,
                canonicalActiveDrivers = activeAfter.Count,
                created,
                updated,
                duplicateRecordsRetired = retired,
                driversArchivedNotInTachoMaster = archived,
                duplicateMemberGroups = duplicateMembers,
                duplicateCardGroups = duplicateCards,
                activeWithoutMember,
                workersWithoutCard,
                populationAligned = activeAfter.Count == workers.Count
            }, JsonOptions);

            if (!healthy)
            {
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                var failure = $"TachoMaster Member Code canonical Driver Master failed its identity gate: source={workers.Count}, claimed={claimed.Count}, active={activeAfter.Count}, duplicate members={duplicateMembers}, active without Member Code={activeWithoutMember}. No partial cleanse was committed.";
                await RecordAttemptAsync(db, actor, now, payload, false, failure, ct);
                return new(false, workers.Count, activeAfter.Count, created, updated, retired, archived, matchedByMember, matchedByCard, matchedByName,
                    CountDuplicateNames(workers), workersWithoutCard, failure, DateTimeOffset.UtcNow);
            }

            await RecordAttemptTrackedAsync(db, actor, now, payload, true,
                $"TachoMaster Member Code canonical Driver Master completed: {activeAfter.Count} current unique member(s), {retired} duplicate row(s) retired, {archived} old/ineligible row(s) archived. Member Code is the canonical identity; tachograph card is supporting evidence.");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            var message = $"TachoMaster Member Code canonical Driver Master: {workers.Count} current member(s), {activeAfter.Count} active canonical driver(s), {created} created, {retired} duplicate row(s) retired and {archived} old/ineligible row(s) archived. Member Code identity checks passed.";
            return new(true, workers.Count, activeAfter.Count, created, updated, retired, archived, matchedByMember, matchedByCard, matchedByName,
                CountDuplicateNames(workers), workersWithoutCard, message, DateTimeOffset.UtcNow);
        }
        catch
        {
            try { await transaction.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    private static TachoLiveWorker PreferredWorker(IEnumerable<TachoLiveWorker> group) => group
        .OrderByDescending(worker => !string.IsNullOrWhiteSpace(worker.CardLastRead))
        .ThenByDescending(worker => !string.IsNullOrWhiteSpace(worker.CardNumber))
        .ThenByDescending(worker => !string.IsNullOrWhiteSpace(worker.EmployeeNumber))
        .First();

    private static Driver SelectCanonical(IReadOnlyCollection<Driver> candidates, TachoLiveWorker worker, IReadOnlyDictionary<Guid, int> loadUse) => candidates
        .OrderByDescending(driver => CanonicalScore(driver, worker, loadUse.GetValueOrDefault(driver.Id)))
        .ThenBy(driver => driver.EmployeeNumber, StringComparer.OrdinalIgnoreCase)
        .ThenBy(driver => driver.Id)
        .First();

    private static int CanonicalScore(Driver driver, TachoLiveWorker worker, int loadCount)
    {
        var score = Math.Min(loadCount, 100) * 10;
        if (driver.Active) score += 100;
        if (TachoDriverIdentityRules.MemberMatches(driver.TachoMasterDriverId, worker.MemberCode.ToString(CultureInfo.InvariantCulture))) score += 1000;
        if (TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, worker.CardNumber)) score += 100;
        if (!driver.EmployeeNumber.StartsWith("TM-", StringComparison.OrdinalIgnoreCase)) score += 50;
        if (!string.IsNullOrWhiteSpace(driver.MobileNumber)) score += 20;
        if (!string.IsNullOrWhiteSpace(driver.DrivingLicenceNumber)) score += 10;
        return score;
    }

    private static async Task MergeDuplicateAsync(
        TmsDbContext db,
        Driver canonical,
        Driver duplicate,
        IReadOnlyCollection<StagedImport> detailRows,
        string actor,
        ILogger logger,
        CancellationToken ct)
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

        var duplicateEmployee = duplicate.EmployeeNumber;
        var canonicalOldEmployee = canonical.EmployeeNumber;
        if (canonical.EmployeeNumber.StartsWith("TM-", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(duplicateEmployee) &&
            !duplicateEmployee.StartsWith("TM-", StringComparison.OrdinalIgnoreCase))
        {
            duplicate.EmployeeNumber = RetiredEmployeeNumber(duplicate.Id);
            await db.SaveChangesAsync(ct);
            canonical.EmployeeNumber = duplicateEmployee;
            ArchiveDetail(detailRows, canonicalOldEmployee, canonical.EmployeeNumber, canonical.Id);
        }

        foreach (var load in await db.Loads.Where(load => load.DriverId == duplicate.Id).ToListAsync(ct)) load.DriverId = canonical.Id;
        foreach (var status in await db.DriverStatusLogs.Where(status => status.DriverId == duplicate.Id).ToListAsync(ct)) status.DriverId = canonical.Id;

        try
        {
            foreach (var run in await db.PlanProposalRuns.Where(run => run.DriverId == duplicate.Id).ToListAsync(ct)) run.DriverId = canonical.Id;
            foreach (var candidate in await db.PlanProposalCandidates.Where(candidate => candidate.DriverId == duplicate.Id).ToListAsync(ct))
            {
                var exists = await db.PlanProposalCandidates.AnyAsync(existing => existing.Id != candidate.Id && existing.ProposalRunId == candidate.ProposalRunId && existing.VehicleId == candidate.VehicleId && existing.DriverId == canonical.Id, ct);
                if (exists) db.PlanProposalCandidates.Remove(candidate);
                else candidate.DriverId = canonical.Id;
            }
        }
        catch (Exception ex) when (SchemaUnavailable(ex)) { logger.LogWarning(ex, "Optional planning driver references could not be reassigned while merging {DuplicateDriverId}.", duplicate.Id); }

        try
        {
            foreach (var allocation in await db.RunResourceAllocations.Where(allocation => allocation.DriverId == duplicate.Id).ToListAsync(ct)) allocation.DriverId = canonical.Id;
        }
        catch (Exception ex) when (SchemaUnavailable(ex)) { logger.LogWarning(ex, "Run allocation driver references could not be reassigned while merging {DuplicateDriverId}.", duplicate.Id); }

        try
        {
            foreach (var mapping in await db.IntegrationMappings.Where(mapping => mapping.TmsEntityType == "Driver" && mapping.TmsEntityId == duplicate.Id).ToListAsync(ct)) mapping.TmsEntityId = canonical.Id;
        }
        catch (Exception ex) when (SchemaUnavailable(ex)) { logger.LogWarning(ex, "Driver integration mappings could not be reassigned while merging {DuplicateDriverId}.", duplicate.Id); }

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
                row.ReviewNote = $"Driver Member Code identity merged from {duplicate.Id} to canonical {canonical.Id}.";
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

        ArchiveDetail(detailRows, duplicateEmployee, canonical.EmployeeNumber, canonical.Id);
        duplicate.Active = false;
        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Driver",
            EntityId = canonical.Id,
            Action = "MergedDuplicateTachoMemberCode",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new
            {
                canonicalDriverId = canonical.Id,
                canonicalMemberCode = canonical.TachoMasterDriverId,
                duplicateDriverId = duplicate.Id,
                duplicateEmployeeNumber = duplicateEmployee,
                duplicate.DisplayName,
                duplicate.TachoMasterDriverId,
                duplicate.TachoCardNumber
            }, JsonOptions)
        });
    }

    private static void ApplyWorker(Driver driver, TachoLiveWorker worker, TachoDriverProfile? profile, DateTimeOffset now)
    {
        driver.TachoMasterDriverId = worker.MemberCode.ToString(CultureInfo.InvariantCulture);
        driver.TachoCardNumber = Clean(worker.CardNumber);
        if (!string.IsNullOrWhiteSpace(worker.DisplayName))
        {
            driver.DisplayName = string.IsNullOrWhiteSpace(driver.DisplayName) ? worker.DisplayName : driver.DisplayName;
            driver.TachoName = worker.DisplayName;
        }
        driver.AgencyName = Clean(worker.AgencyName) ?? driver.AgencyName;
        driver.DriverType = Clean(worker.WorkerType) ?? driver.DriverType;
        driver.LicenceExpiry = ParseDate(worker.DrivingLicenceExpiry) ?? driver.LicenceExpiry;
        driver.CPCExpiry = ParseDate(worker.CpcExpiry) ?? driver.CPCExpiry;
        driver.DigitalTachoCardExpiry = ParseDate(worker.DriverCardExpiry) ?? driver.DigitalTachoCardExpiry;
        driver.TachoDriveAvailableTodayMinutes = profile?.DriveAvailableTodayMinutes ?? driver.TachoDriveAvailableTodayMinutes;
        driver.TachoDriveAvailableWeekMinutes = profile?.DriveAvailableWeekMinutes ?? driver.TachoDriveAvailableWeekMinutes;
        driver.TachoWorkAvailableWeekMinutes = profile?.WorkAvailableWeekMinutes ?? driver.TachoWorkAvailableWeekMinutes;
        driver.LastTachoSyncUtc = now;
    }

    private static void UpsertDetail(TmsDbContext db, Dictionary<string, StagedImport> rows, Driver driver, TachoLiveWorker worker, string actor, DateTimeOffset now)
    {
        var key = DetailKey(driver.EmployeeNumber);
        if (!rows.TryGetValue(key, out var row))
        {
            row = new StagedImport { EntityType = DetailType, IdempotencyKey = key, PayloadJson = "{}", Source = "TachoMaster Member Code canonical Driver Master", ReceivedAtUtc = now };
            db.StagedImports.Add(row);
            rows[key] = row;
        }
        row.Status = StagingStatus.Promoted;
        row.Source = "TachoMaster Member Code canonical Driver Master";
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
        row.ReviewNote = "Canonical identity is TachoMaster Member Code; card number is supporting evidence.";
    }

    private static void UpsertProfile(TmsDbContext db, Dictionary<string, StagedImport> rows, TachoLiveWorker worker, string actor, DateTimeOffset now)
    {
        var key = $"tachodriverprofile:{TachoDriverIdentityRules.NormaliseIdentifier(worker.MemberCode.ToString(CultureInfo.InvariantCulture))}";
        if (!rows.TryGetValue(key, out var row))
        {
            row = new StagedImport { EntityType = ProfileType, IdempotencyKey = key, PayloadJson = "{}", Source = "TachoMaster live Worker List", ReceivedAtUtc = now };
            db.StagedImports.Add(row);
            rows[key] = row;
        }
        row.Status = StagingStatus.Promoted;
        row.Source = "TachoMaster live Worker List";
        row.PayloadJson = JsonSerializer.Serialize(worker, JsonOptions);
        row.ReviewedAtUtc = now;
        row.ReviewedBy = actor;
        row.ReviewNote = "Latest live TachoMaster Worker profile keyed by Member Code.";
    }

    private static async Task RecordAttemptAsync(TmsDbContext db, string actor, DateTimeOffset started, string payload, bool success, string message, CancellationToken ct)
    {
        db.StagedImports.Add(Attempt(actor, started, payload, success, message));
        await db.SaveChangesAsync(ct);
    }

    private static Task RecordAttemptTrackedAsync(TmsDbContext db, string actor, DateTimeOffset started, string payload, bool success, string message)
    {
        db.StagedImports.Add(Attempt(actor, started, payload, success, message));
        return Task.CompletedTask;
    }

    private static StagedImport Attempt(string actor, DateTimeOffset started, string payload, bool success, string message) => new()
    {
        EntityType = "tachodrivermastersync",
        IdempotencyKey = $"tachodrivermastersync:{started:yyyyMMddHHmmss}:{Guid.NewGuid():N}",
        PayloadJson = payload,
        Source = "TachoMaster Member Code canonical Driver Master sync",
        Status = success ? StagingStatus.Promoted : StagingStatus.Rejected,
        ReceivedAtUtc = started,
        ReviewedAtUtc = DateTimeOffset.UtcNow,
        ReviewedBy = actor,
        ReviewNote = message
    };

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

    private static int DuplicateIdentityGroupCount(IEnumerable<Driver> drivers, Func<Driver, string?> selector) => drivers
        .Select(selector)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .GroupBy(TachoDriverIdentityRules.NormaliseIdentifier, StringComparer.OrdinalIgnoreCase)
        .Count(group => group.Key.Length > 0 && group.Count() > 1);

    private static int CountDuplicateNames(IReadOnlyCollection<TachoLiveWorker> workers) => workers
        .GroupBy(worker => TachoDriverIdentityRules.NormalisePerson(worker.DisplayName), StringComparer.OrdinalIgnoreCase)
        .Count(group => group.Key.Length > 0 && group.Select(worker => worker.MemberCode).Distinct().Count() > 1);

    private static void ArchiveDetail(IReadOnlyCollection<StagedImport> detailRows, string? employeeNumber, string canonicalEmployeeNumber, Guid canonicalId)
    {
        if (string.IsNullOrWhiteSpace(employeeNumber)) return;
        foreach (var detail in detailRows.Where(row => string.Equals(row.IdempotencyKey, DetailKey(employeeNumber), StringComparison.OrdinalIgnoreCase)))
        {
            detail.Status = StagingStatus.Archived;
            detail.ReviewedAtUtc = DateTimeOffset.UtcNow;
            detail.ReviewNote = $"Duplicate Driver Master detail retired into canonical Member Code driver {canonicalEmployeeNumber} ({canonicalId}).";
        }
    }

    private static TachoDriverMasterSyncResult Failed(string message, DateTimeOffset now, int sourceWorkers = 0) =>
        new(false, sourceWorkers, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, message, now);

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
