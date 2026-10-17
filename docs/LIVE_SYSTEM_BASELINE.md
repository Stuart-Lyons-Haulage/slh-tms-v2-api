# SLH TMS API live system baseline

_Last reviewed: 2026-09-16_

This document defines the clean baseline for the live SLH TMS API. It separates active production code from retired runtime debt so future amendments are made deliberately.

## Active repositories

The live TMS estate uses two GitHub repositories only:

- `Stuart-Lyons-Haulage/slh-tms-api` — .NET 8 API, scheduled jobs, SQL migrations, Power Automate contracts and integrations.
- `Stuart-Lyons-Haulage/slh-tms-web` — React/TypeScript portal and physical TV assets.

There are no separate legacy TMS repositories in the organisation baseline.

## Live production endpoints

- API: `https://slh-tms-api-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io`
- Portal proxy path: `/tms-api/`

## Active API runtime

The active API runtime includes:

- order intake staging;
- staged review/approval;
- order duplicate/amendment comparison;
- transport orders;
- planner/run control;
- live runs and wallboard feeds;
- RoadTech/DOT/TachoMaster integration feeds;
- master-data lookup and reconciliation;
- diagnostics/health endpoints;
- scheduled jobs.

## Order intake baseline

Inbound email-derived orders must be staged first. The API must not auto-create live transport orders from email intake without authorised planner approval.

The clean Order Review contract is:

```text
GET /api/v1/staging/queue?status=PendingReview&entityType=order&planningDate=<yyyy-mm-dd>&page=<n>&pageSize=100
```

The API owns planning-date filtering for pending staged orders. The Web portal should not infer planning-date availability from whichever first page of staged records it happens to load.

## Master-data baseline

SQL is authoritative for TMS master data. External sources may feed reconciliation/import flows, but the operational TMS reads from SQL.

Do not introduce MS Lists as the master-data authority unless there is a signed-off migration plan. MS Lists may be used only for explicitly scoped intake/home-order workflows where agreed.

## Tracking and driver identity baseline

- RoadTech Falcon, DOT tracking and TachoMaster remain API-side integration concerns.
- TachoMaster driver matching uses the unique Tacho member number / memCode to map to driver master identity.
- Historical tracking/backfill and reconciliation code is retained where it protects live tracking and planner evidence.

## Compatibility retained deliberately

The following may mention recovery, fallback, repair or legacy but are retained intentionally:

- SQL migration history and schema repair scripts;
- Azure SQL shape repair logic;
- RoadTech/DOT/TachoMaster historical reconciliation;
- Site/geofence identity repair;
- route/run projection compatibility used by planner and wallboard;
- health/diagnostics used to support deployment and incident recovery.

Do not remove these without proving they are unreferenced and no longer required by live production data.

## Retired/deprecated areas

Retired browser-side runtime patches and legacy wallboard assets live in the Web repo and have been removed there. API-side deletion should remain conservative because much of the old-shape handling exists to protect live production data.

## Deployment baseline

Deployment should remain GitHub Actions based. API changes should pass:

- API build;
- scheduled jobs build;
- Power Automate contract validation;
- xUnit tests;
- CodeQL.

## Open risks and backlog

- `main` branch protection is not currently enforced and must be enabled manually or via a GitHub administration token.
- Stale feature/agent branches should be reviewed and pruned only after confirming merged/superseded state.
- Planner performance should be monitored separately; broad staged-import scans may need targeted optimisation.
- Future changes should be scoped PRs with green CI before merge.
