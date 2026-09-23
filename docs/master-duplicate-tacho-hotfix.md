# Master duplicate scan and Tacho reconcile hotfix

## Reason

Manual TachoMaster driver reconciliation returned the existing singleton job state when a prior sync was still marked as running. A manual click should attempt stale-job recovery before returning status.

The master-data duplicate API supported sites, drivers, vehicles and markets only. The Master Data UI also contains a Trailers tab, so trailer duplicate scan/merge support is now first-class.

## Changes

- Manual `POST /api/v1/driver-master/tachomaster/sync` calls `RecoverInterruptedAsync` before enqueueing or returning the singleton job status.
- `MasterDataDuplicateReviewService` now supports `trailer` and `trailers` for candidate scan, auto-merge and manual merge.
- Trailer merge keeps one canonical SQL trailer record, preserves type/capacity fields where available and marks duplicates inactive.

## Notes

Fuel-card duplicate merging is intentionally not included because those records include restricted card/PIN data and need a separate governed reconciliation flow rather than generic duplicate merge.
