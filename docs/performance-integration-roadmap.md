# TMS Performance and Integration Roadmap

This roadmap defines the recommended, implementation and test structure for reducing page-load latency while preserving operational safety.

## 1. Recommended changes

### Keep synchronous in the TMS request path

- Authentication and access control.
- Orders, staging approval and promotion.
- Customers, sites, drivers, vehicles and trailers.
- Loads, stops, allocations and dispatch locks.
- Audit history and operational exceptions.
- Reading the latest locally stored run-progress snapshot.

### Move to background processing

- RoadTech/Falcon polling and historical recovery.
- TachoMaster driver, duty and legal-hours enrichment.
- Fleetio walkround imports.
- Sage HR synchronisation.
- SQL master-data audit and downstream export processing.
- Email attachment parsing and intake enrichment.
- SMS delivery and delivery-status polling.
- Reporting exports and non-operational analytics.
- ETA recalculation when it does not affect an immediate dispatch decision.

### Remove or isolate after usage verification

- Legacy wallboard scripts and compatibility styles.
- Duplicate live-runs and TV data-loading paths.
- Browser-side Graph/SharePoint reads.
- Browser-side provider calls.
- Full-table Master Data loads.
- Repeated refresh loops on hidden pages.
- Legacy planner routes that duplicate the resilient import endpoint.

The following must not be removed: staging approval, dispatch locks, authentication, audit history, allocation validation, geofence evidence, or Tacho/driver mismatch warnings.

## 2. Implementation sequence

### Phase A — baseline and observability

1. Add request correlation IDs and elapsed-time logging to dashboard, planner, dispatch, wallboard and master-data endpoints.
2. Record external-provider duration separately from SQL and controller duration.
3. Define production targets:
   - dashboard under 1 second;
   - planner, dispatch and master data under 2 seconds;
   - wallboard refresh under 3 seconds.
4. Capture a baseline before changing request behaviour.

### Phase B — local integration snapshots

1. Ensure RoadTech, TachoMaster, Fleetio and Sage jobs persist their latest successful result locally.
2. Add provider status, last-success time, source timestamp and error state to each snapshot.
3. Change operational screens to read snapshots rather than waiting on providers.
4. Display stale/unavailable evidence explicitly; never replace it with planned values.

### Phase C — prepared run progress

1. Create one read model for run progress containing allocation, current stop, next stop, completed stops, live status, card/Tacho status, ETA and exception state.
2. Update it from geofence, tracking and Tacho background events.
3. Make wallboard, TV wallboard and live-runs consume the same read model.
4. Retain the existing evidence and audit records as the source of truth.

### Phase D — portal simplification

1. Add server-side pagination and filtering to Master Data.
2. Disable duplicate refresh loops on hidden routes.
3. Feature-flag legacy wallboard and planner paths.
4. Remove flagged code only after one stable production release with no usage or error regression.

## 3. Tests required for every phase

### API tests

- A provider outage still returns the latest local snapshot.
- A stale snapshot is labelled stale and is not presented as live.
- A missing snapshot produces an explicit unavailable state.
- Dispatch safety checks still reject missing or mismatched allocation evidence.
- Geofence departure still advances stop and run completion.
- Audit history remains append-only.
- External-provider calls are not made by normal read endpoints.

### Portal tests

- Wallboard, TV wallboard and live-runs show the same run-progress state.
- Provider-unavailable and stale states are visible.
- Planned times are not labelled as live ETA.
- Master Data pagination preserves search and filtering.
- Hidden pages do not continue refresh polling.

### Release checks

- API build succeeds.
- API test suite passes.
- Portal lint, typecheck, unit tests and production build pass.
- Production health endpoint returns the intended revision.
- Authenticated smoke test covers dashboard, planner, dispatch, wallboard and master data.

## 4. Current baseline

Verified on 11 September 2026:

- API build: passed with 0 errors.
- API tests: 659 passed, 0 failed.
- Production portal: reachable and presents Microsoft sign-in.
- Production API: reachable and protected by bearer authentication.
- Portal checks: not runnable in the current environment because Node.js is unavailable.

## 5. Immediate next implementation slice

The first code change should be to identify and remove provider calls from one operational read path, preferably the operations wallboard, by making it consume the existing locally stored tracking/Tacho evidence and returning explicit freshness metadata. The same pattern can then be applied to planner, dispatch and master data.
