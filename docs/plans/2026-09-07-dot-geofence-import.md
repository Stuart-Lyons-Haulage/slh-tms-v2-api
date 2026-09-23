# DOT Geofence Import Implementation Plan

> **For agentic workers:** Use the host's available task-by-task implementation workflow. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a guided DOT/Falcon JSON geofence import to Master Data → Sites that previews exact Site matches, supports searchable manual linking and Site creation, and idempotently updates operational geofences without losing Site links.

**Architecture:** Extend the existing `GeofencesController` with preview and transactional commit endpoints backed by a focused import service. Reuse the current `SiteGeofence` table, its unique normalized-name index, `SiteGeofenceMasterSync` manual links, and the live engine's database-first operational-fence behavior. Add a React import-review component to the Sites tab and reuse the existing Site creation API before returning the newly created Site to the review row.

**Tech Stack:** .NET 8, ASP.NET Core, Entity Framework Core, xUnit; React 19, TypeScript, Vite, Vitest.

## Global Constraints

- Accept only `falcon.geofence` version 1 JSON with a geofences array.
- Keep longitude/latitude point order exactly as exported.
- Never auto-accept a fuzzy Site-name match; only exact normalized matches may be selected automatically.
- Allow each unmatched row to search/select an existing Site or create a new Site with the DOT name prefilled.
- Re-import must update geometry and DOT-managed timing/category settings while preserving an existing Site link.
- The same import must not create duplicate Sites or geofences.
- Invalid, ambiguous, skipped, or likely renamed rows must not be silently linked.
- A commit is transactional across all selected geofence creates, updates, and link changes.
- Imported `SiteGeofence` rows become active operational fences through the existing database-first `EmbeddedGeofenceEngine.OperationalFencesAsync` behavior.
- Site master address, instructions, aliases, display name, code, active state, and planning profile are not overwritten by geofence re-import.
- Import and link changes are written to the existing master-data audit/outbox path.

---

### Task 1: API parsing, validation, preview, and import transaction

**Files:**
- Create: `Services/FalconGeofenceImportService.cs`
- Modify: `Controllers/GeofencesController.cs`
- Test: `Slh.Tms.Api.Tests/FalconGeofenceImportTests.cs`

**Interfaces:**
- Consumes: `TmsDbContext`, the existing `SiteGeofence` and `Site` entities, raw Falcon v1 JSON, authenticated actor name, and explicit row decisions.
- Produces: `FalconGeofenceImportPreview`, `FalconGeofenceImportRow`, `FalconGeofenceImportRequest`, `FalconGeofenceImportDecision`, and `FalconGeofenceImportResult`; endpoints `POST /api/v1/geofences/import-falcon/preview` and `POST /api/v1/geofences/import-falcon/commit` under `TmsWrite`.

- [ ] **Step 1: Add focused failing API tests**

Test that preview rejects the wrong format/version, missing names, non-array polygons, fewer than three distinct points, non-finite/out-of-range coordinates, and duplicate normalized names. Test that exact normalized Site names produce `Matched`, multiple exact matches produce `NeedsLinking`, an existing geofence with the same normalized name produces `AlreadyImported`, and equal canonical polygon JSON with a different name produces `PossibleRename` without auto-linking.

Test commit creates a new active `SiteGeofence`, updates an existing one, preserves its existing `SiteId`/`SiteNumber`, accepts an explicit Site selection for a differently named Site, rejects nonexistent/inactive Site IDs, refuses unresolved and unconfirmed rename rows, skips explicitly skipped rows, rolls back every selected row if any row fails, and emits `MasterDataAudit` entries for create/update/link actions.

- [ ] **Step 2: Verify the relevant failure**

Run: `dotnet test Slh.Tms.Api.Tests/Slh.Tms.Api.Tests.csproj --filter FalconGeofenceImportTests`
Expected: compilation fails because the import service, contracts, and endpoints do not exist.

- [ ] **Step 3: Implement the minimum API behavior**

Implement strict parsing with `JsonDocument`/DTOs, name normalization identical to existing geofence code, canonical polygon JSON serialization, and SHA-256 polygon fingerprints computed in memory. Preview loads active Sites and existing `SiteGeofences`, classifies every row, includes exact Site candidates and possible-rename IDs, and never mutates data.

Commit re-parses and revalidates the export, matches existing records by normalized name, requires an explicit decision for every selected unresolved row, verifies selected Sites, and applies all rows inside an EF database transaction. For an existing row, update `Name`, `NormalizedName`, `Category`, category/max wait values, pending entry/exit values, `PolygonJson`, `Active`, and `UpdatedAtUtc`; preserve `SiteId`/`SiteNumber` unless the request explicitly links a different Site. Add audit records through `db.MasterDataAudits` so the existing outbox keeps them in the same save transaction. Return created, updated, linked, skipped, and invalid counts plus affected row IDs.

- [ ] **Step 4: Verify the focused pass**

Run: `dotnet test Slh.Tms.Api.Tests/Slh.Tms.Api.Tests.csproj --filter FalconGeofenceImportTests`
Expected: all Falcon import tests pass.

- [ ] **Step 5: Run the affected integration check**

Run: `dotnet test Slh.Tms.Api.Tests/Slh.Tms.Api.Tests.csproj --filter "SiteGeofenceManualDropdownLinkTests|SiteGeofenceMasterSyncTests|EmbeddedGeofenceEngineTests"`
Expected: all existing Site-link and operational-engine tests pass, proving imported database rows retain manual links and remain usable by live geofence processing.

- [ ] **Step 6: Commit the passing deliverable**

```bash
git add Services/FalconGeofenceImportService.cs Controllers/GeofencesController.cs Slh.Tms.Api.Tests/FalconGeofenceImportTests.cs
git commit -m "feat: add transactional DOT geofence import API"
```

### Task 2: Import review UI and client-side contract tests

**Files:**
- Create: `src/pages/DotGeofenceImport.tsx`
- Create: `src/pages/DotGeofenceImport.test.ts`
- Modify: `src/pages/MasterDataHub.tsx`

**Interfaces:**
- Consumes: `request`, `useAccessToken`, `POST /api/v1/geofences/import-falcon/preview`, `POST /api/v1/geofences/import-falcon/commit`, `/api/v1/sites`, and an `onImported(): void` callback.
- Produces: `DotGeofenceImport` component and typed preview/decision/result contracts local to the component module.

- [ ] **Step 1: Add focused failing component tests**

Test hidden `.json` file selection from an **Import DOT Geofences** button; file-read and invalid-JSON errors; summary counts; row statuses `Matched`, `Needs linking`, `Already imported`, `Possible rename`, and `Invalid`; row skip toggles; searchable active-Site popup; explicit link selection; create-Site action; disabled commit while a selected row is unresolved; successful commit summary; retained review state after API failure; and `onImported` after success.

- [ ] **Step 2: Verify the relevant failure**

Run: `pnpm test -- src/pages/DotGeofenceImport.test.ts`
Expected: test collection fails because `DotGeofenceImport` does not exist.

- [ ] **Step 3: Implement the minimum review component**

Read the chosen file as text, parse JSON only to provide immediate syntax feedback, then send the original object to preview so server validation is authoritative. Render a modal with totals and a compact table. For unresolved rows, provide **Link to Existing Site** with an in-modal text filter over active Sites, and **Create New Site** that opens the Site creation panel described in Task 3. Keep decisions keyed by the preview `clientKey`; send the original export, source filename, explicit `siteId`, confirmed-rename ID, and skip state to commit. On failure, retain the file, preview, filters, and decisions.

- [ ] **Step 4: Verify the focused pass**

Run: `pnpm test -- src/pages/DotGeofenceImport.test.ts`
Expected: all import-review component tests pass.

- [ ] **Step 5: Run the affected integration check**

Run: `pnpm typecheck && pnpm lint`
Expected: TypeScript and ESLint pass with no warnings.

- [ ] **Step 6: Commit the passing deliverable**

```bash
git add src/pages/DotGeofenceImport.tsx src/pages/DotGeofenceImport.test.ts src/pages/MasterDataHub.tsx
git commit -m "feat: add DOT geofence review to Sites"
```

### Task 3: Reusable prefilled Site creation from import review

**Files:**
- Modify: `src/pages/MasterDataAddPanel.tsx`
- Modify: `src/pages/DotGeofenceImport.tsx`
- Modify: `src/pages/DotGeofenceImport.test.ts`
- Test: `src/pages/MasterDataAddPanel.test.ts`

**Interfaces:**
- Consumes: optional `initialValues`, `openRequestKey`, and `onAdded(record?)` props on `MasterDataAddPanel`; existing master-data apply and Site planning-profile endpoints.
- Produces: a reusable Sites-mode creation panel that opens with `name` and `driverTextName` set to the DOT geofence name and returns the created Site identity to the import reviewer.

- [ ] **Step 1: Add focused failing tests**

Test that a changed `openRequestKey` opens the Sites form, applies the requested name without overwriting later user edits, still requires Site code/name, saves through existing endpoints, and calls `onAdded` with the newly resolved Site. Test that the import reviewer closes the creation panel, selects that Site for the originating row, and preserves every other row decision.

- [ ] **Step 2: Verify the relevant failure**

Run: `pnpm test -- src/pages/MasterDataAddPanel.test.ts src/pages/DotGeofenceImport.test.ts`
Expected: tests fail because the creation panel does not expose prefill/open/result interfaces.

- [ ] **Step 3: Implement the minimum reusable creation flow**

Extend `MasterDataAddPanel` with optional props while preserving all existing callers. In Sites mode, use the supplied name/driver text defaults only when opening for a new request. After the existing apply call, resolve the created Site by external code, apply its planning profile as today, and return `{ id, externalCode, name, active }`. Mount the panel inside the import modal only while creating a Site, then bind the returned ID to the requesting geofence row.

- [ ] **Step 4: Verify the focused pass**

Run: `pnpm test -- src/pages/MasterDataAddPanel.test.ts src/pages/DotGeofenceImport.test.ts`
Expected: all Site creation and import-review tests pass.

- [ ] **Step 5: Run the affected integration check**

Run: `pnpm test && pnpm build`
Expected: all Vitest tests pass and Vite produces a production bundle.

- [ ] **Step 6: Commit the passing deliverable**

```bash
git add src/pages/MasterDataAddPanel.tsx src/pages/MasterDataAddPanel.test.ts src/pages/DotGeofenceImport.tsx src/pages/DotGeofenceImport.test.ts
git commit -m "feat: create Sites from DOT geofence review"
```

### Task 4: Cross-repository verification and operator-facing result

**Files:**
- Modify: `src/pages/MasterDataOperational.tsx`
- Test: `e2e/master-data-dot-geofence-import.spec.ts`
- Create: `docs/dot-geofence-import.md` in `slh-tms-api`

**Interfaces:**
- Consumes: the completed API endpoints and review component.
- Produces: refreshed Sites/geofence status after import, one end-to-end operator scenario, and concise runbook documentation for exporting and importing DOT JSON.

- [ ] **Step 1: Add the focused failing end-to-end test**

Mock or seed one exact-name Site, one differently named Site, and one absent Site; upload a representative Falcon v1 file; verify automatic matching, searchable manual linking, Site creation, commit counts, Sites-table refresh, and a second import that updates polygon/timing fields without duplicates or lost links.

- [ ] **Step 2: Verify the relevant failure**

Run: `pnpm test:e2e -- e2e/master-data-dot-geofence-import.spec.ts`
Expected: the scenario fails before the Sites view exposes and refreshes the completed workflow.

- [ ] **Step 3: Complete integration and documentation**

On successful import, refresh the Sites rows, geofence options, and Site/geofence status without requiring a page reload. Document the accepted export format, review statuses, manual-link and create-Site paths, re-import semantics, skipped/invalid behavior, and the fact that the database-backed imported set is the live operational set.

- [ ] **Step 4: Verify the focused pass**

Run: `pnpm test:e2e -- e2e/master-data-dot-geofence-import.spec.ts`
Expected: the full initial-import and re-import scenario passes.

- [ ] **Step 5: Run complete repository checks**

Run in `slh-tms-api`: `dotnet test Slh.Tms.Api.Tests/Slh.Tms.Api.Tests.csproj`
Expected: all API tests pass.

Run in `slh-tms-web`: `pnpm test && pnpm typecheck && pnpm lint && pnpm build`
Expected: all web tests, type checks, lint checks, and production build pass.

- [ ] **Step 6: Commit the passing deliverable**

```bash
git add src/pages/MasterDataOperational.tsx e2e/master-data-dot-geofence-import.spec.ts
git commit -m "test: verify DOT geofence import workflow"
```

```bash
git add docs/dot-geofence-import.md
git commit -m "docs: add DOT geofence import runbook"
```

## Unresolved externally observable decisions

None. The approved design and existing repository conventions settle import format, permissions, matching, Site creation, transactionality, auditing, re-import, and failure behavior.
