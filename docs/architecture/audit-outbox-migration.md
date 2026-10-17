# Transactional audit outbox migration

The transactional audit outbox is registered in the versioned SQL migration catalogue as migration 45.

- Migration 44: `039_Canonical_Relational_Planning.sql`
- Migration 45: `040_Audit_Outbox.sql`

This ordering is intentional and append-only. The audit outbox branch was refreshed after canonical planning landed, so the original `039_Audit_Outbox.sql` filename was renumbered rather than replacing or modifying migration 44.

The outbox preserves the operational mutation and its durable audit event in the same database `SaveChanges` transaction. A background processor materialises `MasterDataAudit` rows from pending outbox events and marks processed events complete. Replay is idempotent on the audit identifier and failed events are retried up to the configured retry limit.

Site reconciliation treats valid durable site-disposition events in the pending outbox as authoritative immediately. This prevents a newly archived or duplicate-merged Site from being reactivated during the interval before the background processor materialises its `MasterDataAudit` row. Processed audit history continues to be read from `MasterDataAudits` in the normal way.
