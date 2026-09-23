# Master data Sites/Markets auto-merge fix

## Problem

Duplicate review could show a high number of Sites or Markets needing review, while the Auto-merge button showed no safe candidates. Trailer duplicate auto-merge worked because trailer candidates are always marked `CanAutoMerge = true`, but Sites and Markets use stricter confidence rules.

The operational issue was that Site and Market duplicates scored at 94 percent were still blocked from auto-merge even where the duplicate scan had already identified safe same-code or same-market/name/stand groups.

## Change

`MasterDataDuplicateReviewController.AutoMerge` now applies the operational auto-merge threshold used by the UI workflow:

- keep existing `CanAutoMerge` behaviour for all entity types
- allow Site candidates with confidence `>= 94`
- allow Market candidates with confidence `>= 94`
- leave low-confidence Site/Market candidates for manual review
- keep the existing 50-candidate per request safety cap

## Safety

This does not wipe or rebuild SQL master data. The existing merge service still keeps a canonical SQL record, preserves useful fields, archives duplicates and writes the master-data audit trail.

## Regression coverage

Added coverage for:

- Site same-external-code candidates that were previously stuck as review-only
- Market same-market/name/stand candidates without sender values that were previously stuck as review-only
