# slhmastersync — staged integration

Deployed on 28 October 2026 to the existing Function App `slhmastersync` in `slh-lists-integrations-rg`, subscription `85a2b78f-31ee-42ab-b535-7aef28c79424`. Flex Consumption, Linux, 512 MB, Python 3.12; no always-ready entries configured. Deployment ID: `9a91f63f-7cc0-4ab1-907a-e76b6af7911d`.

## Verified through the deployed Function identity

- Key Vault: all eight provider secrets accessible from `slh-lists-kv-261028` without returning their values.
- Fleetio: limited API request returned 5 records.
- Sage HR: configured endpoint `https://stuartlyonshaulagelimited.sage.hr/api` returned 50 employees on its first page. Only field names/counts returned by preflight.
- Samsara EU: limited API request returned 5 vehicles.
- RoadTech: documented `/api/auth/login` returns a `Token` property; `/api/Falcon/GetCurrentTelemetry` returned 66 records. Confirmed GPS, CAN and gazetteer fields. This feed did not expose available driving hours or driver assignments.
- Graph: token acquisition works but all target List schema calls return HTTP 403. `Sites.Selected` application role was assigned, but root-site explicit write grant remains missing.
- Health: `/api/health` returned status `ok`, mode `validate`.
- Seven local safety tests passed using mocked network and Azure SDK boundaries.

## Access scope

System identity principal: `c6999213-45ed-43bd-babf-b8e1390cec7c`.
Application/client ID: `702b6266-b279-41ad-933a-65529b469fcb`.
Key Vault Secrets User assigned at the existing integration vault.
Microsoft Graph application role: `Sites.Selected` only. No tenant-wide SharePoint application role assigned.

The supplied `/sites/slhtms` URL returned 404. Existing masters reside on the root Team Portal, site ID `stuartlyonshaulage.sharepoint.com,e350e872-0b2b-43a1-8569-270045690fa5,b94ef994-16aa-443b-888b-419b014bbd1f`. User approved this corrected target on 28 October. Azure CLI delegated session cannot create the explicit site grant (403). Automated approval review rejected requesting delegated `Sites.FullControl.All` for administrator tooling; no workaround attempted.

## Safety state and unfinished work

**This is a deployed validation package, not a live operational sync.**

All four timers have `AzureWebJobs.<function>.Disabled=true`; provider enable flags are false; `SLH_SYNC_MODE=validate`; `SLH_WRITES_VERIFIED=false`. Production upserts are locked, automatic column creation is blocked, and no records were written or deleted during this deployment. The reconciliation code is inherited from the earlier isolated implementation and still requires complete column/type/ownership review, concurrency/idempotency verification and controlled live tests. Missing fields, maintenance/inspection details, HR leave/leaving dates, driver telemetry, dispatch/geofence events and complete workbook integration remain unresolved. Do not enable writes merely because preflight succeeds.

Configured timer expressions (all disabled): Fleetio `0 */30 * * * *`; RoadTech `0 */15 * * * *`; Sage `0 5 */4 * * *`; Samsara `0 7,22,37,52 * * * *` (UTC).

Registered functions: `Health`, `IntegrationPreflight`, `ManualMasterDataSync`, `ScheduledFleetioSync`, `ScheduledRoadTechSync`, `ScheduledSageSync`, `ScheduledSamsaraSync`.

The live AM collection workbook inspected has no direct SharePoint List connection and links externally to `Master_RouteTimes_TEMPLATE.xlsx`; no workbook edits were made. Complete the remaining AM/PM connection audit before promising automatic master refresh.

No existing resources were deleted. The old TMS and SQL databases were untouched. No TMS runtime/API dependency is required by this Python package, but independent operational behaviour is not yet verified.

## Validation

`python -m unittest discover -s slhmastersync/tests -v`

Deploy package root contains `function_app.py`, `host.json` and `requirements.txt`; remote build is required. Secrets are retrieved with `ManagedIdentityCredential` and `SecretClient`. Never put API secret values into code, deployment output or configuration.
