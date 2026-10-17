# SLH TMS Architecture

## Authority

Azure SQL is the authoritative source for TMS master data and operational data. The TMS API is the controlled write boundary and the React portal is the operational interface.

External providers contribute evidence or provider-owned attributes, but do not own the TMS master record. Any proposed master-data update is resolved against SQL, deduplicated and audited before it becomes available to planning, dispatch, intake or reporting.

## Data ownership

| Domain | Source/evidence | TMS authority |
|---|---|---|
| Customers and sites | TMS users, approved imports and intake evidence | Azure SQL |
| Drivers | TMS master plus TachoMaster identity/card evidence | Azure SQL |
| Vehicles and trailers | TMS master plus Fleetio/RoadTech evidence where configured | Azure SQL |
| Markets and contacts | TMS master | Azure SQL |
| Email sender mappings and route rules | TMS master | Azure SQL |
| Inbound email/source attachments | Outlook/Power Automate | SQL staging metadata and extracted payload; original retained as evidence |
| Orders | TMS/API | Azure SQL |
| Loads/runs/stops/allocations | TMS planner | Azure SQL |
| Tracking/geofence/ETA | RoadTech/Falcon + TMS | Azure SQL integration evidence |
| Tacho/legal-hours evidence | TachoMaster | Azure SQL integration evidence |
| Audit/review/approval history | TMS | Azure SQL append-only history |

## Inbound order contract

Power Automate is the mailbox boundary. Every inbound message is classified and staged before it can become live operational work.

The flow/API preserve source mailbox, Outlook identifiers, sender, subject, received timestamp, attachment identity, extracted references, candidate customer/site data, parser/version information and processing errors.

The API treats source message/attachment identity as an idempotency key. Retries update the existing staged evidence rather than silently creating a second order. Amendments remain linked to their source lineage.

## Review-first lifecycle

`Inbound email → Power Automate → API staging → Pending Review → Planner review/approval → Live Order → Load/Run → Dispatch → Tracking/ETA → Complete`

Email automation must never promote work directly to a live order.

## Master-data rules

1. SQL is the only TMS master-data authority.
2. Stable provider identities and business keys are used for reconciliation.
3. Duplicate prevention is enforced at SQL/API boundaries.
4. Provider data may update only the fields that provider owns or can prove.
5. Conflicting or ambiguous updates are held for review rather than overwriting verified data.
6. Deactivation and merge activity remains auditable.
7. Sites remain distinct when they represent different physical locations, even where addresses or postcodes overlap.
8. TachoMaster member number is a driver identity link, not by itself proof that a person belongs in the operational driver population.

## Email intake mapping

Sender/customer identification is separated from route selection.

- Customer email mappings resolve exact sender/domain and optional subject evidence to an existing SQL customer.
- Route rules match customer plus origin, retailer and destination evidence.
- Generic sender mappings cannot silently create collection or delivery defaults.
- Weak, conflicting or tied route matches remain planner-review items.
- Confidence, explanation and candidate alternatives are stored with staged order evidence.

## Integration boundary

RoadTech/Falcon, TachoMaster, Fleetio and Sage HR remain external integration sources. Provider credentials stay server-side and provider payloads are normalised before they affect operational SQL records.

## Reliability rules

- Preserve source evidence rather than deleting it.
- Use idempotent keys for inbound integrations.
- Retry transient provider failures without duplicating operational records.
- Distinguish planned schedule times from live ETA.
- Never infer legal-hours compliance from vehicle movement alone.
- Keep operational APIs authenticated through Microsoft Entra.
- Keep production secrets in Azure/Key Vault-backed configuration.
- Expose dependency freshness and SQL readiness separately.

## Deployment acceptance

A production architecture change is accepted only after CI, CodeQL and container deployment succeed and the live API reports the new revision. SQL readiness, tracking, TachoMaster, geofence and portal/proxy checks must then pass.
