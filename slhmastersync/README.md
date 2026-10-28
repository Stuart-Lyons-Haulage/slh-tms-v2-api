# slhmastersync — integration validation

Existing Function App `slhmastersync`, resource group `slh-lists-integrations-rg`, subscription `85a2b78f-31ee-42ab-b535-7aef28c79424`. Flex Consumption, Linux, Python 3.12, 512 MB, no always-ready entries. Latest successful deployment ID `e9f63888-3598-4632-aaa7-ca7f135aea54` on 28 October 2026. Source SHA256 `13232975cc20a13a1369428527f9d09ec13dd9851b2e0756cd52772388bb719c`.

## Verified access

System identity principal `c6999213-45ed-43bd-babf-b8e1390cec7c`; client ID `702b6266-b279-41ad-933a-65529b469fcb`. Key Vault Secrets User at `slh-lists-kv-261028`; all eight provider secrets retrieved through the deployed managed identity without exposing values. Microsoft Graph application permission remains Sites.Selected only.

The supplied `/sites/slhtms` URL returned 404. User approved the existing root Team Portal as the corrected target: `stuartlyonshaulage.sharepoint.com,e350e872-0b2b-43a1-8569-270045690fa5,b94ef994-16aa-443b-888b-419b014bbd1f`.

Explicit root-site write grant for slhmastersync was created through Microsoft Graph Explorer (201 Created). All five integration list schemas then returned successful results through the Function managed identity. The user specifically approved temporary delegated Sites.FullControl.All for Graph Explorer. After applying the site grant, only that added scope was removed from the user's Principal consent (PATCH 204, GET verified scope now `openid profile offline_access`), preserving all pre-existing consent. Graph Explorer was signed out. No tenant-wide SharePoint application permission was granted to the Function.

## Controlled production proof

At `2026-10-28T23:54:14.366873+00:00`, ManualMasterDataSync ran Fleetio with max_writes=1 and dry_run=false. It updated Master_Vehicles item 49 (`VEH0012`, BL70RHO): Status (`field_10`) to Active and FleetioLastSync to the test time. An immediate Graph read-back verified the update and preserved all unrelated Title/field_* values. The connector independently confirmed item 49 after the update. Sync_Log item 5 recorded the controlled run. No master records were inserted or deleted. This bounded test is not a full provider run or scheduled-execution proof.

## Read-only matching results

| Source | Retrieved | Existing matches | New candidates | Ambiguous/unmatched |
|---|---:|---:|---:|---:|
| Fleetio vehicles | 69 | 68 | 1 | 0 |
| Fleetio trailers | 71 | 70 | 1 | 0 |
| Sage drivers | 79 | 47 | 29 | 3 ambiguous |
| Samsara vehicles | 75 | 72 | 0 | 3 unmatched |
| Samsara geofences | 65 | 1 | 64 | 0 |

Before the controlled test, vehicle master contained 88 rows (60 Fleetio IDs, none duplicated), trailer master 77 rows (39 Fleetio IDs, none duplicated). Fleetio candidates are training vehicle 1010757/AY61EOW and training trailer 1010871/TRAINING TRAILER SLH52. Do not create blindly: the latter needs matching against existing trailer 52 naming. Sage's API has 105 employees across three pages; pagination uses total_pages, not the old last_page assumption. Driver-only filter selected 79. Review all new/ambiguous employee matches before enabling creation.

## Provider fields and scope

- Fleetio core: existing FleetioId, FleetioName, FleetioVIN, FleetioMake, FleetioModel, FleetioType, FleetioLastSync and existing Status (vehicles field_10, trailers field_7). Existing registrations, active flags, notes, capacity, maintenance dates and operational assignments are preserved. Inspection/defect/maintenance-specific endpoints remain to be verified; no claim these fields are complete.
- RoadTech: documented login returns Token. Current telemetry returned 66 records with GPS/CAN/gazetteer fields. Reuses existing DotTrackingVehicleId (field_9). Intended missing columns: RoadTechVehicleCode, RoadTechLastTelemetryUtc, RoadTechIgnition, RoadTechMoving, RoadTechOdometerKm, RoadTechLatitude, RoadTechLongitude, RoadTechLocation, RoadTechSpeedKmh, RoadTechLastSync. Current telemetry does not contain available driving hours or driver assignments. RoadTech list sync is blocked by missing columns.
- Sage: correct base `https://stuartlyonshaulagelimited.sage.hr/api`; existing SageHrId, SageEmployeeNumber, SageEmploymentStatus, SageEmail, SageMobile, SageLastSync; start date needs new SageStartDate. Uses mobile_phone response field. Preserves existing operational DisplayName. Leave/leaving-date capabilities and roster integration remain unfinished.
- Samsara EU: vehicle and address APIs authenticated. Vehicle name can match registration exactly when licensePlate is absent. Geofences expose latitude/longitude, circle and externalIds.slhTmsSite. Needed columns are explicitly listed in COLS; existing geofence list has only Title and system columns. Assignment/dispatch/event endpoints remain unfinished; no duplicate vehicle location polling is introduced.

## Current safety state

All four timers remain disabled, provider enable flags false, sync mode validate. Production writes were temporarily unlocked for the bounded Fleetio test, then relocked while administrator sign-in is pending. New-record creation remains disabled. Do not enable live schedules yet.

The Function's site write role does not permit the missing schema changes; ProvisionReviewedSchema returned 500 on its first column attempt, so the sequence stopped. Existing administrator SharePoint UI requires fresh sign-in. Secure browser sign-in did not complete and the UI again showed Please sign in. Complete administrator authentication to add only reviewed missing fields; do not expand the Function to tenant-wide access.

Safety implementation now includes source-specific storage leases in the existing host storage account, lease-loss checks, bounded writes, ETag conditional PATCH, read-back validation, duplicate-ID conflict rejection, no retry of potentially non-idempotent creates, and in-batch cache updates. Thirteen mocked safety tests pass. These tests do not replace remaining live verification. Review count/status semantics before activation: blocked creations currently increment errors, while execute still labels non-exception runs success. Scope creation switches per provider before allowing unattended inserts. Check stale telemetry and full API pagination edge cases.

## Schedules configured, not activated

Fleetio and Sage: 06:00 and 16:00 Europe/London daily. Cron `0 0 5,6,15,16 * * *` with UK hour guard handles GMT/BST without unsupported Linux Flex timezone settings. RoadTech: `0 */30 * * * *`. Samsara: `0 7,22,37,52 * * * *`. No actual scheduled execution has been verified.

Functions: Health, IntegrationPreflight, ManualMasterDataSync, ScheduledFleetioSync, ScheduledRoadTechSync, ScheduledSageSync, ScheduledSamsaraSync, ProvisionReviewedSchema.

List IDs: vehicles 8d96865a-b7dc-40c0-9947-468542800bd4; trailers a2fb1103-b9fe-48a8-90fd-9f6fcdd2d74c; drivers 2b89c3fc-d83e-424b-81b9-8e2546fabb78; geofences 0d8cd682-0603-4224-9acb-db49ba289475; Sync_Log ff965828-ca04-4374-aec9-b8b500e234b1.

Application Insights is connected, but repeated-failure alerts remain to be configured. The inspected live AM workbook has no direct SharePoint List/Power Query connection and references Master_RouteTimes_TEMPLATE.xlsx. Complete AM/PM connection audit and safe configuration; no formulas, macros or operational entries have been modified.

No existing resources were deleted; no SQL database or old TMS was modified. Python integrations have no TMS runtime/API dependency, but independent operational behaviour is not yet verified. Exact ongoing costs remain unmeasured; no always-ready compute was added.

## Validation and deployment

Run `python -m unittest discover -s slhmastersync/tests -v`. Deploy ZIP root files function_app.py, host.json, requirements.txt using remote build; preserve readable 0644 ZIP entry modes. Code is staged in this branch/draft PR and not merged into the old application's deployment workflow. Provider secrets are fetched through ManagedIdentityCredential and SecretClient; never print their values or put them in source/configuration.
