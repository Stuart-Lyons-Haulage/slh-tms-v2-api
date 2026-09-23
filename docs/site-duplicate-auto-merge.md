# Safe site duplicate auto-merge

The duplicate review screen can now automatically merge safe same-name Site Master duplicates.

## Why

The duplicate finder already surfaced same-name site groups, but the previous automatic merge path only merged candidates where `CanAutoMerge` was true. Same-name-only site groups were normally scored around 86 and were therefore displayed for review but skipped by the automatic button.

## Behaviour

`POST /api/v1/operational-master-data/duplicates/auto-merge?entityType=sites` now uses `SafeSiteDuplicateAutoMergeService`.

The service will auto-merge a site candidate when:

- the candidate is a site candidate;
- all rows share the same normalised site name;
- non-empty postcodes do not conflict;
- non-empty normalised addresses do not conflict;
- non-empty customer codes do not conflict.

This handles the broken-import case where the same site has been created many times with the same name and little or no extra detail.

## Safety

The existing merge routine still performs the actual merge. That means linked data is preserved and reassigned as before, including:

- geofences;
- run stops;
- integration mappings;
- aliases and operational site detail.

Candidates with conflicting address, postcode, customer or identity evidence remain visible for manual review and are not auto-merged.

