# Scheduled integration jobs

The API no longer owns the TachoMaster, Fleetio or Sage HR timers. `Slh.Tms.Jobs` is a dedicated console host used by four independently scheduled Azure Container Apps Jobs:

| Job kind | Example cron (UTC) | Purpose |
| --- | --- | --- |
| `tachomaster` | `*/5 * * * *` | Five-minute Tacho identity refresh. At/after 04:30 Europe/London it runs the full canonical Driver Master once per local day. |
| `fleetio` | `5 * * * *` | Hourly canonical fleet/trailer sync. |
| `sagehr` | `30 4,5 * * *` | Fires at both possible UTC equivalents of 05:30 Europe/London. The job checks the durable `sagehrsync` ledger and executes only once per London date, preserving the 05:30 local schedule across BST/GMT. |
| `eta` | `*/5 * * * *` | Recalculate delivery ETAs through the existing live ETA engine and persist precision snapshots. |

Every execution first acquires a SQL row in `dbo.DistributedLease`. The row contains `LeaseId`, `AcquiredAt`, `ExpiresAt` and `InstanceId`. Acquisition uses a serializable transaction and `UPDLOCK/HOLDLOCK`; a crashed execution becomes eligible after `ExpiresAt`. Normal completion and failure release only the row owned by that instance.

There are two lease layers by design. The outer `job:*` lease suppresses duplicate Container Apps Job executions. Integration service methods also use `integration:*` leases so a manual API sync cannot overlap the scheduled job after the process-local `SemaphoreSlim` gates are removed.

The Tacho job keeps the historical 04:30 Europe/London canonical pass without a second in-process timer: the five-minute job checks the durable orchestration ledger and runs the canonical pass only when one successful pass has not yet completed for the current London date.

Container Apps scheduled-job cron expressions are evaluated in UTC. The Sage job therefore uses two UTC trigger times plus the durable local-date guard instead of relying on a fixed UTC hour.

## Versioned schema ordering

The distributed lease is part of the authoritative append-only schema catalogue. Canonical planning is migration 44 (`039_Canonical_Relational_Planning.sql`), the transactional audit outbox is migration 45 (`040_Audit_Outbox.sql`), and the distributed integration lease is migration 46 (`041_Distributed_Integration_Lease.sql`). Existing migration numbers and checksums must not be rewritten.

Build the jobs image with:

```bash
docker build -f Slh.Tms.Jobs/Dockerfile -t slhtmsacrprod.azurecr.io/slh-tms-jobs:<sha> .
```

Deploy the four scheduled resources with `infra/container-app-jobs.bicep`. Production deployment should use an immutable image tag and ensure `Database/041_Distributed_Integration_Lease.sql` has been applied before enabling schedules.
