# Web XLSX upload blocker for master import

API PR 438 adds the backend workbook endpoints:

- `POST /api/v1/master-data/workbook/preview`
- `POST /api/v1/master-data/workbook/commit`

The live web screen still says `Master data CSV` and `Choose a CSV file` because `slh-tms-web/src/pages/MasterDataCsvImport.tsx` blocks non-CSV files before they reach the API.

Required web repo amendment:

1. Update `src/pages/MasterDataCsvImport.tsx` visible screen from CSV to workbook upload.
2. Accept `.xlsx,.xls,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,application/vnd.ms-excel`.
3. Keep old CSV parser exports only if existing tests still import them.
4. Send the selected workbook as multipart `FormData` field named `file`.
5. Do not manually set `Content-Type`; the browser must set the multipart boundary.
6. Preview should post to `${apiBaseUrl}/api/v1/master-data/workbook/preview`.
7. Commit should post to `${apiBaseUrl}/api/v1/master-data/workbook/commit`.
8. Update `src/pages/ImportCentre.tsx` tab text from `Master data CSV` to `Master data workbook`.
9. Update `src/operationsHousekeeping.test.ts` expectation from `Master data CSV` to `Master data workbook`.

Why this matters:
The SLH master data file is a multi-sheet XLSX. Converting to CSV would lose Sites, Site Cutoffs, Run Times, Vehicles & Fuel, Market Contacts, Customer Contacts, Fuel Price History and Driver overlay.

Connector limitation seen from ChatGPT:
The web repository rejected branch/file writes with `Repository rule violations found - Changes must be made through a pull request`, even on newly-created branches. API repo writes are unaffected.
