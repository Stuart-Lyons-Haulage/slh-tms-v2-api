# Master workbook upload checklist

Before committing a workbook upload:

1. Upload the workbook to `POST /api/v1/master-data/workbook/preview` using form field `file`.
2. Review all `conflict`, `review` and `skipped` rows.
3. Do not run commit if any expected live Site Master row is showing as `new` incorrectly.
4. Correct the workbook or live Site Master first where needed.
5. Upload the same corrected workbook to `POST /api/v1/master-data/workbook/commit`.
6. Check the returned summary and row results.
7. Re-run duplicate review for Sites after commit.

Expected behaviour:

- Existing sites are updated/enriched.
- Weak or conflicting sites are held for review.
- Drivers are update-only.
- Run Times become `masterdetail:sitetimingrule` rows for the existing dispatch timing matcher.
