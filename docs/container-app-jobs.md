# Scheduled integration jobs

The API no longer owns the TachoMaster, Fleetio or Sage HR timers. `Slh.Tms.Jobs` is a dedicated console host used by four independently scheduled Azure Container Apps Jobs, with the optional SharePoint driver register job available when explicitly enabled:

| Job kind | Example cron (UTC) | Purpose |
| --- | --- | --- |
| `tachomaster` | `*/5 * * * *` | Five-minute Tacho identity refresh. At/after 04:30 Europe/London it runs the full canonical Driver Master once per local day. |
| `fleetio` | `5 * * * *` | Hourly canonical fleet/trailer sync. |
| `sagehr` | `30 4,5 * * *` | Fires at both possible UTC equivalents of 05:30 Europe/London. The job checks the durable `sagehrsync` ledger and executes only once per London date, preserving the 05:30 local schedule across BST/GMT. |
| `eta` | `*/5 * * * *` | Recalculate delivery ETAs through the existing live ETA engine and persist precision snapshots. |
| `sharepoint-drivers` | `0 6 * * *` | Optional daily projection of TMS driver name, employee number and active status into the SLH Driver Register. Disabled by default. |

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


## Optional SharePoint driver register job

The Bicep template can create `slh-tms-job-sharepoint-drivers`, scheduled daily at 06:00 UTC. It is disabled unless the deployment parameter `sharePointDriverRegisterEnabled` is explicitly set to `true`. The default is false, so normal deployments do not create or run it.

Before enabling it, assign the required list-scoped Microsoft Graph permission to the dedicated user-assigned managed identity created by the Bicep template. The shared TMS job identity must be able to resolve the SQL connection string from Key Vault. No Graph client secret or mailbox app credentials are used.

The job reads driver records from TMS SQL and writes only Title, Employee Number, and Driver Status. It leaves Work Email alone and does not delete rows. The list IDs are prefilled with the known SLH site and SLH Driver Register IDs, but can be overridden at deployment. Confirm the field internal names and the Active/Inactive choice values before the first enabled run.

The API's GitHub deployment workflow deploys the API App Service; it does not create or schedule this Container Apps Job. Deploy the Jobs image and Bicep template through the existing Container Apps deployment process. Keep the parameter disabled until the Graph list assignment and SQL Key Vault access are confirmed and a controlled first run has been completed.
