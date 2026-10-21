# Order intake reliability – review handover

## Scope prepared

- Existing mailbox intake remains authoritative; no replacement system was introduced.
- Every mailbox submission continues to be retained before parsing.
- Source direction is now classified separately from parsing:
  - outbound Lyons Collections/SB/load plans are comparison evidence only;
  - an external instruction forwarded by a Lyons user is parsed using the original external sender;
  - customer replies remain inbound and quoted Lyons plans are not reclassified as demand.
- The intake ledger now exposes source identity, recipients, attachments, revision/parser identity, customer/reference, dates, sites, quantity/unit, movement classification, validation warnings, source rows, linked TMS order IDs and saved-run status.
- Independent reconciliation compares retained inbound evidence with staged source rows, promoted orders and saved load stops.
- Changed exceptions are versioned as `intake-reconciliation-exception` staging records. Resolved/changed fingerprints archive the previous active exception, preventing repeat alerts for unchanged discrepancies.
- The weekday worker runs every two hours from 06:00 to 20:00 Europe/London.
- `/api/v1/order-intake/control-centre` supplies the UI and `export.csv` supplies an Excel-compatible reconciliation extract.

## Deployment requirements

1. Build and test the API with the .NET 8 SDK.
2. Build/test the Web repository with the locked pnpm toolchain.
3. Deploy API before Web because the new tab calls the new control-centre endpoint.
4. Confirm the production identity can read/write `StagedImports`, `OrderRevisions`, `OrderSourceLines`, `TransportOrders` and `LoadStops`. No database migration is required for this slice.
5. Smoke-test the endpoint with a short window before enabling normal operational use.
6. Confirm the first scheduled pass creates exceptions only; it must not reparse mail, approve orders, modify live orders or allocate runs.

## Known limitations / follow-up

- Historical evidence created before source-direction fields were added uses a conservative sender-based fallback. Old internally forwarded customer instructions may need a one-off, read-only classification report before any replay is authorised.
- Exact missing-row detection is available where a parser/source payload provides `expectedSourceRowCount`, `sourceRowCount` or `expectedLineCount`. Older formats without a declared source count can expose a parser failure but cannot prove that row 17 of 20 is absent. Each regular workbook parser should emit the expected count as its template is certified.
- Multi-leg detail is retained when parsers emit `movementLegs`/`intermediateStops`, but there is not yet a common relational leg table across every customer format. Existing NWF transfer and planner stop behaviour remains unchanged.
- The export is UTF-8 CSV, designed to open directly in Excel. Native `.xlsx` generation would require adding and governing a workbook-writing dependency.
- No live mailbox replay, production-order update, approval, run allocation or deployment was performed as part of this change.
