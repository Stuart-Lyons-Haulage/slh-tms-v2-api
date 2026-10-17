# SLH TMS V2 architecture and rebuild rules

## Why V2 exists

The current application contains proven business knowledge, but several concerns have become coupled through repeated revisions. V2 preserves known-good behaviour while removing duplicated code paths, overloaded staging storage and integration-specific domain logic.

## Deployment approach

V1 remains live while V2 is built and proven side-by-side.

V2 is now **local-first and private-network hosted**. Azure is not a runtime dependency.

Target deployment:
- dedicated always-on Windows server or business mini-PC on the Lyons office network
- SLH TMS V2 API hosted on the server
- SLH TMS V2 portal hosted on the same server
- Microsoft SQL Server hosted locally on the server
- private DNS name such as `slh-tms` for office users
- VPN access for authorised staff outside the office
- GitHub remains the source repository and CI/test system
- integrations call outward from the server wherever possible

Remote access must require the VPN. The normal portal and API are not exposed directly to the public internet.

If a future integration genuinely requires an inbound webhook, expose only the dedicated webhook ingress endpoint through a hardened tunnel/reverse proxy. Do not publish the full TMS portal, database or general API.

## Network model

Office users:
`Office device -> LAN -> SLH TMS server`

Remote users:
`Authorised device -> VPN -> Lyons LAN / SLH TMS server`

Outbound integrations:
`SLH TMS server -> HTTPS -> Microsoft Graph / TachoMaster / RoadTech / Sage HR / other provider APIs`

Inbound integrations, only when unavoidable:
`External provider -> authenticated narrow ingress -> integration adapter -> application service`

A VPN product such as Tailscale/WireGuard may be used for the first deployment. The application must not depend on a specific VPN vendor.

## Canonical data ownership

### Master Data owns
- Customer
- Site
- SiteAlias / external site identities
- Market
- Driver
- Vehicle
- Trailer
- external identities linking these records to TachoMaster, Sage HR, RoadTech/Roadrunner or source systems

No intake parser or integration may insert a duplicate core entity as a side effect.

### Intake owns
- immutable source evidence metadata
- attachment/document metadata
- extraction result
- confidence and validation issues
- resolved canonical master IDs
- review state

### Orders owns
- canonical transport order
- source revision lineage
- references / PO numbers
- collection and delivery master IDs
- dates/times
- pallet/case/crate/tray quantities
- temperature / trailer requirements
- customer notes and planner notes

### Planning owns
- run
- stop
- order allocation
- driver/vehicle/trailer assignment
- operational planning notes
- night-out / trailer-swap state

### Live Operations owns
- telemetry observations
- geofence events
- ETA snapshots
- exceptions and status history

## One-writer rule

Each concern has exactly one canonical writer. Integrations call application services; they do not write domain tables directly.

Examples:
- TachoMaster adapter -> DriverMasterSync -> Master Data
- Sage HR adapter -> WorkforceEnrichment -> Master Data
- Outlook adapter -> IntakePipeline -> Intake
- RoadTech adapter -> TrackingIngest -> Live Operations
- Roadrunner export -> ExportProjection reads Orders + Planning + Master Data

## Intake pipeline

`Evidence -> Extract -> Classify -> Resolve master IDs -> Validate -> Review/Approve -> Promote order`

Customer-specific logic is allowed only in extraction adapters. Persistence, matching, deduplication, review and promotion remain generic.

Resolution order:
1. explicit stable external ID / alias
2. exact canonical code
3. normalized address + customer relationship
4. approved alias
5. unresolved review item

Fuzzy matching may suggest a candidate but must not silently create or merge master records.

## References and updates

PO/reference values are first-class and never discarded. A stable source key identifies a movement. Subsequent emails or files create revisions of that movement. Approved amendments update the order revision while preserving history; exports keep orders separate even when PO or destination alignment is used by Roadrunner for charge/deduplication rules.

## V1 capability classification

### KEEP behaviour, reimplement behind V2 boundaries
- Entra authentication pattern where useful
- master-data concepts and operational fields
- TachoMaster identity reconciliation rules that have proved correct
- site identity/alias knowledge
- tracking/geofence concepts
- ETA concepts
- planning/run concepts
- Roadrunner CSV mapping knowledge
- customer order-format knowledge
- auditability and source evidence

### REBUILD implementation
- master persistence and master-detail storage
- order intake/staging/review
- TachoMaster scheduling/orchestration
- planner generations into one planner
- integration/background scheduling
- master duplicate handling
- order amendment/deduplication
- frontend API layer and data loading
- deployment around a local server + VPN rather than Azure Container Apps

### DROP from V2
- Azure-only runtime assumptions
- patch middleware whose purpose is schema drift recovery
- duplicate background writers
- legacy planner variants
- generic `StagedImports` as storage for unrelated operational concerns
- hidden fallback persistence
- production-repair endpoints in normal application surface
- parser-specific master creation
- obsolete feature flags and runtime guards

## Database strategy

Use one local SQL Server database for V2 initially, with separate schemas and EF Core DbContexts by bounded area.

Initial schemas:
- `master`
- `intake`
- `ops`
- `live`
- `integration`

V2 migrations are created only from V2 projects and never target the V1 database.

Database access is private to the server/LAN and must not be exposed publicly.

## Backups and recovery

The local-first design must still be production-safe:
- automated nightly SQL backup
- encrypted off-machine backup copy
- application/config backup excluding plaintext secrets
- documented restore test
- UPS recommended for the host
- health monitoring and Windows service/container auto-restart

## Acceptance gates

A feature is not considered migrated because the UI exists. It must pass:
- build and static checks
- unit tests for matching/rules
- integration tests against SQL
- representative historic fixture tests
- observable health/telemetry
- no second writer for the same concern
- side-by-side comparison with V1 where applicable
- explicit production smoke test before cutover

## Initial delivery sequence

1. Foundation: V2 app shell, auth, health, telemetry, isolated config.
2. Master Data: canonical entities, imports, aliases, duplicate suggestions, audit.
3. Intake: immutable evidence, extraction adapters, master resolution, review.
4. Orders: promotion, revisions, amendment rules, PO/reference integrity.
5. Planning: one planning board/run model.
6. Drivers/vehicles: TachoMaster canonical sync + Sage HR enrichment.
7. Tracking/ETA: RoadTech observations, geofences, live run projection.
8. Roadrunner export: deterministic CSV from canonical run/order/master data.
9. Operational dashboard/TV once core read models are stable.
10. Local server deployment, VPN rollout, cutover and rollback window.
11. V1 retirement after an agreed stability period.

## Manual connections required later

Do not put credentials in GitHub.

- Local server: administrator access for the dedicated host.
- VPN: install/authorise the VPN client on the server and approved user devices.
- Microsoft/Outlook: authorise the mailbox/Graph connection used for intake.
- TachoMaster, RoadTech/Roadrunner and Sage HR: provide/authorise existing credentials in protected local server configuration.
- Entra ID: optional for application sign-in if we retain Microsoft identity inside the VPN.
- Inbound webhooks: only configure a secure narrow ingress if a provider cannot be polled outbound.
- Backups: choose the protected off-machine destination.

See `docs/V2_LOCAL_DEPLOYMENT.md` for the target local/VPN topology.
