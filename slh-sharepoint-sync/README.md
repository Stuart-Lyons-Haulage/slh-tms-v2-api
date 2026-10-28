# SLH SharePoint master-data sync

Isolated Python Azure Function for duplicate-safe Fleetio vehicle/trailer and Sage HR driver synchronisation. It uses managed identity for Microsoft Graph and Key Vault, defaults to validation-only mode, refuses ambiguous matches, never deletes records, and does not blank manual fields.

Fleetio runs daily at 03:15 UTC. Sage HR has an independent 03:45 UTC timer controlled by `SAGE_SYNC_ENABLED`, so an expired HR credential cannot block fleet refreshes. `POST /api/sync?source=all&dry_run=true` performs a read-only reconciliation. Live writes require `SLH_SYNC_MODE=live` or an explicit `dry_run=false` invocation.
