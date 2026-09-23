# Order intake live start — 20/09/2026

Operational decision: test data before the 20/09/2026 live-start window is not required for recovery.

From 20/09/2026 the intake approach is:

- retain every Info mailbox source email as `email-evidence` before parsing;
- create normal parsed `order` rows where the parser succeeds;
- use `/api/v1/order-intake/cache` to identify cached emails that did not create orders;
- use force-review only to create low-confidence amber `Manual Email Review` rows from cached evidence;
- compare the resulting review queue against the manually built planner plan for 20/09/2026;
- use the failed/manual rows to harden customer-specific parsers afterwards.

This note also intentionally triggers API CI/deploy after the cache force-review endpoint was merged, because the web UI reached production before the API route and returned 404.
