# Stuart Lyons Haulage TMS API

Production .NET 8 API for the Stuart Lyons Haulage transport management system.

## Source of truth

Azure SQL is the single authoritative store for TMS master data and operational data. Customers, sites, drivers, vehicles, trailers, markets, fuel data, email sender mappings and route rules are validated and written to SQL before they are used anywhere else.

External systems may contribute the fields or evidence they own, but they do not become a competing master:

- TachoMaster supplies tachograph identity, card and duty evidence.
- RoadTech/Falcon supplies vehicle tracking and execution evidence.
- Fleetio supplies fleet and walkround evidence where configured.
- Sage HR supplies HR context where configured.
- Email intake and Home Orders may use their dedicated workflow stores, but any master-data change they propose must resolve against and write through the SQL master.

Master-data writes are audited. Duplicate prevention and stable external identities are enforced as close to the SQL boundary as practical. A downstream integration must never overwrite a verified SQL master record simply because its own source value differs.

## Email intake

Mailbox intake is approval-first. Email-derived orders are staged as `PendingReview`, retain their source evidence and must be reviewed before promotion into live operational work.

Sender identity and route selection are deliberately separate:

1. Customer email mappings identify the likely SQL customer from an exact sender, sender domain and optional subject rule.
2. Route rules use customer plus available origin, retailer and destination evidence.
3. Weak, conflicting or tied matches remain planner-review items.
4. Generic sender/domain mappings cannot silently create collection or delivery defaults.
5. Route scoring, explanation and alternatives are retained in the staged payload for review and audit.

Management endpoints:

- `GET/POST /api/v1/customer-email-mappings`
- `PATCH/DELETE /api/v1/customer-email-mappings/{id}`
- `GET/POST /api/v1/order-intake-route-rules`
- `PATCH/DELETE /api/v1/order-intake-route-rules/{id}`

## Production

| Item | Value |
| --- | --- |
| Portal | `https://slh-tms-portal-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io/` |
| API | `https://slh-tms-api-prod.gentlepond-08dba66b.uksouth.azurecontainerapps.io` |
| Region | UK South |
| Runtime | Azure Container Apps |
| Database | Azure SQL |
| Authentication | Microsoft Entra JWT bearer tokens |
| Deployment | GitHub Actions using Azure OIDC |

## Operational integrations

The API combines planned SQL data with live provider evidence. Planned times must not be represented as live ETA, and a planned allocation alone is not proof of driver sign-on.

RoadTech/Falcon and TachoMaster share the RoadTech API host family but provide different evidence. Falcon supplies live vehicle/location and card identity where available; TachoMaster supplies driver profiles, card and duty/legal-hours information.

## Planner imports and order lifecycle

Planner JSON posts to:

`POST /api/v1/planning/import-plan`

The import path supports idempotent re-import, source-line preservation, allocation reconciliation, capacity warnings and audited fallback storage. Planning-day reset is controlled through the planning-day reset endpoints rather than by deleting database history.

## Security and configuration

Keep provider credentials server-side in Azure configuration/Key Vault references. Never commit passwords, SQL connection strings, API keys, customer attachments, live exports or provider payloads.

Core production settings include:

- `ConnectionStrings__TmsDb`
- `Entra__TenantId`
- `Entra__Audience`
- `Entra__AllowedDomains__0`
- `Cors__AllowedOrigins__0`
- `Deployment__Revision`
- RoadTech/Falcon, TachoMaster, Fleetio and Sage HR settings where enabled.

## Health

Useful endpoints include:

- `GET /api/v1/health`
- `GET /api/v1/health/ready`
- `GET /api/v1/health/tracking`
- `GET /api/v1/health/tachomaster`
- `GET /api/v1/health/geofences`
- `GET /api/v1/diagnostics/data-readiness`
- `GET /api/v1/integrations/status`

## Development

```bash
dotnet restore
dotnet build Slh.Tms.Api.csproj -c Release
dotnet test Slh.Tms.Api.Tests/Slh.Tms.Api.Tests.csproj -c Release
```

Production changes are complete only when CI and CodeQL pass, the tested image is deployed, SQL readiness is healthy and the production health endpoint reports the deployed revision.
