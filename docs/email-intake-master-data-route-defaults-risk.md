# Risk note

This is deliberately not a full rollback.

A full rollback would remove the SQL-authoritative mapping work and risk breaking sender learning, duplicate protection and audit trails. The safer correction is to restore master-data route fallback only where values are missing.

Guardrails:

- mapped defaults only fill blank route fields;
- parsed email values are preserved;
- conflicting sender/customer mappings still block automation;
- tied route-rule matches do not fill a guessed route;
- planner review remains the control point before live planning.