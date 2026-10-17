# Canonical planning schema

The canonical relational planning model is additive and does not switch production planning reads or writes away from the legacy planning model.

Its SQL schema is registered with the versioned startup migration runner as migration 44:

- `Database/039_Canonical_Relational_Planning.sql`
- `SchemaMigrationRunner` appends this resource after the original 43 immutable migrations.
- The matching EF Core migration remains `20260831214900_CanonicalRelationalPlanning` for model metadata and tooling.

Future schema-bearing changes must append a new `Database/*.sql` resource and a new catalogue entry. Existing migration names or contents must not be reordered or edited after application because their SHA-256 checksums are part of the database migration history.
