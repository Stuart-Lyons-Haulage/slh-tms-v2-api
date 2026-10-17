# SLH TMS V2 clean runtime cutover

This runbook is the required production path for the clean V2 runtime.

## Non-negotiable rules

- Do not restore the historic SLH TMS production database into the clean runtime.
- The live database name is `SLH_TMS_V2`.
- Set `Database__ExpectedDatabaseName=SLH_TMS_V2`; the API refuses startup against another database name.
- Drivers are owned by TachoMaster identity.
- Vehicles are owned by Fleet identity.
- Sage HR is enrichment/leave context only and cannot create Driver Master rows.
- Workbook Driver and Vehicle sheets are overlay/update-only and cannot create those entities.
- Provider secrets are runtime secrets only; never store them in Git or business tables.
- Do not enable archive deletion until the server archive target and a full SQL backup have both been verified.

## 1. Start the fresh runtime

Clone the Web and API repositories as sibling directories and use the standalone compose package in the Web repo.

Create a private `.env.standalone` from the supplied example.

Required secrets before first start:

- `SQL_SA_PASSWORD`
- `TMS_JWT_SIGNING_KEY` (minimum 32 random characters)
- `TMS_BOOTSTRAP_USERNAME`
- `TMS_BOOTSTRAP_PASSWORD`
- provider credentials that are being enabled

Start with archive disabled.

The first API startup creates the clean schema through the registered V2 migrations and creates the initial local Admin account only when there are no TMS users.

## 2. Prove the database is clean

Sign in using the bootstrap Admin account and call:

`GET /api/v1/health/clean-runtime`

Expected initial state:

- no duplicate Tacho Member Code groups
- no duplicate Fleet IDs
- no duplicate normalised vehicle registrations
- zero retained processed AuditOutbox payloads

Do not continue if this endpoint reports duplicate master identities.

## 3. Seed current Drivers from TachoMaster once

Configure and test TachoMaster credentials.

With Driver Master still empty, Admin calls:

`POST /api/v1/bootstrap/tachomaster-drivers?confirm=EMPTY-DRIVER-MASTER`

Safety rules enforced by the endpoint:

- refuses to run unless Driver Master contains exactly zero rows
- only live Tacho workers classified as drivers
- only Member Codes greater than zero
- only card reads within the previous six months
- deduplicated by Tacho Member Code
- safety floor of 25 eligible drivers
- one canonical Driver row per Member Code

After this seed the endpoint can no longer run because Driver Master is no longer empty.

All later unknown Tacho identities are staged in `driverreview` and require review; live polling cannot create Drivers.

## 4. Sync Fleet

Run the canonical Fleet sync once.

Record:

- total vehicles
- unique normalised registrations
- unique Fleet external IDs
- created / updated counts

Immediately run the same Fleet sync again with no upstream changes.

The second run must create zero Vehicles and zero Trailers.

Do not accept the runtime if the second identical sync grows master row counts.

## 5. Enable Sage HR

Sage HR may update existing matched Driver HR fields and leave context.

It must not create Driver Master rows.

Run the same-day Sage sync twice and confirm Driver count does not change.

The daily Sage sync receipt uses one deterministic row per London date rather than a new GUID row per invocation.

## 6. Import reviewed Master Data

Use the existing safe workbook preview/commit path for:

- Sites
- customers / contacts
- markets
- aliases
- planning profiles / cutoffs
- route timing information
- fuel overlays

Driver and Vehicle workbook rows are update-only.

Preview before commit and resolve conflicts rather than creating uncertain duplicate Sites.

## 7. Verify single-writer behavior

All write triggers must converge on one canonical persistence path.

Fleet:
manual / scheduled / resilient compatibility route -> `IntegrationSyncCoordinator.SyncFleetioAsync`

Sage:
manual / scheduled -> `IntegrationSyncCoordinator.SyncSageHrAsync`

Tacho:
scheduled/manual canonical orchestration; unknown identities -> review queue

The Jobs host must not execute runtime CREATE/DROP/DISABLE/ENABLE TRIGGER statements.

## 8. AuditOutbox retention

Pending/failed outbox rows keep replay payloads.

Successful replay clears the large payload immediately.

Existing successful rows with retained payloads are cleared in bounded batches.

The clean-runtime health endpoint should report:

`processedPayloadsStillRetained = 0`

after cleanup catches up.

## 9. Full database backup

Before enabling archive purge, establish and test a full SQL Server backup routine for `SLH_TMS_V2`.

Requirements:

- backup stored outside the SQL data volume
- at least one off-host/off-machine copy
- restore test completed
- retention documented

Archive is not a substitute for a database backup.

## 10. Arm the SLH server archive

Mount the SLH server archive path on the Docker host and point:

`ARCHIVE_HOST_PATH`

to that mounted path.

Keep:

`ARCHIVE_ENABLED=false`

until the mount is verified read/write.

At the root of the real mounted archive create:

`SLH_TMS_ARCHIVE_READY.txt`

Only then enable archive.

If the marker or mounted share is unavailable, the archive worker skips the purge and deletes nothing.

## 11. Nightly archive behavior

After 02:00 Europe/London, one archive cycle may move eligible historical evidence in bounded batches.

Each archive file is:

1. written to a temporary file
2. compressed as JSONL gzip
3. SHA-256 hashed
4. renamed to its final path
5. reopened and hashed again
6. accompanied by a `.sha256` sidecar
7. only then eligible for SQL deletion

Initial retention defaults:

- successful AuditOutbox: 7 days
- raw telemetry evidence: 90 days
- ETA snapshots: 90 days
- geofence visits: 180 days
- completed integration sync receipts: 30 days

Orders/runs/invoices are not blindly purged by this worker.

## 12. Acceptance checks

Before declaring the clean runtime production-ready:

- local login works
- Admin can create/disable users
- API health succeeds
- clean-runtime health has zero identity duplicates
- Driver count matches current recent-card Tacho population
- second Tacho sync creates zero duplicate Drivers
- second Fleet sync creates zero duplicate Vehicles/Trailers
- second Sage sync creates zero Drivers
- workbook cannot create Driver/Vehicle master rows
- Order Review works
- Pallet Order works
- Planner works
- Dispatch works
- archive disabled state deletes nothing
- archive share failure deletes nothing
- archive verification succeeds before purge
- full SQL backup restore has been tested

Only after this acceptance should the historic Azure SQL/database be treated as archive/reference rather than live V2 infrastructure.
