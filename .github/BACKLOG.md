# SLH TMS API – Backlog Status & Dependencies

**Last Updated:** 2026-09-16  
**Repository:** https://github.com/Stuart-Lyons-Haulage/slh-tms-api

---

## Executive Summary

- **13 open issues** across governance, features, and data quality
- **10 open PRs** with 4 stalled for 5+ days
- **4 critical blockers** preventing CI from passing and production hardening from completing
- **Recommended action:** Unblock regressions (#353 → #354) first, then enable governance (#424)

---

## Blocking Issues (Can't Proceed Without Fixing)

### 🔴 CRITICAL: #353 – Fix four EmailOrderIntakeService regressions blocking CI

**Status:** Open, assigned to @danwilliams201302-max, 5 days inactive  
**Impact:** **CI FAILS** – 4 of 659 xUnit tests failing  
**Blocks:** #352, #358 (cannot merge until tests pass)

**What's wrong:**
- `ExtractBodyCollectionPoint()` is too permissive
- Treats bare "Sefter"/"Leythorne"/"Barfoots" mentions as explicit collection sites
- Interprets date/time labels like "Collection : 26.08.2026- 17:00" as site names
- Missing body-only destination recognition for pickup flows

**What needs to happen:**
1. Restrict collection-site parsing to route-oriented labels: "Collection point", "Collection from", "Collect from", "Pickup"
2. Preserve template-provided collection names (don't override with incidental mentions)
3. Add body-only destination recognition: "N pallets to <site> for delivery"
4. Verify 4 failing tests now pass

**PR ready:** #354 (Fix EmailOrderIntakeService precedence regressions) – waiting for review  
**Effort:** ~30 min review + test run  
**Risk:** Low – isolated to email intake parsing logic

---

### 🔴 CRITICAL: #424 – Enable branch protection for live API baseline

**Status:** Open, created 4 hours ago  
**Impact:** **PRODUCTION RISK** – main branch unprotected, anyone can push directly  
**Blocks:** All deployments (should require PR + 2 reviews + CI green)

**Required settings:**
- Require 2 pull request reviews
- Require all status checks pass (build, test, CodeQL, Power Automate validations)
- Require branches up to date
- Restrict direct pushes (no bypass except admins)
- Block force pushes/deletions

**Status:** Governance runbook created (`.github/BRANCH_PROTECTION_SETUP.md`)

**Action:** Manual setup (5 minutes) via GitHub Settings UI – see runbook for step-by-step

---

## Blocked Features (Waiting on #353 to Clear)

### #352 – Modernise SLH SharePoint Hub experience

**Status:** Open, 5 days  
**Depends on:** #353 (CI must be green first)
**Impact:** Frontend/UX – modern Hub navigation, branding, governance statement  
**Effort:** Approved design, ready to merge after #353 fixes

---

### #358 – Document SharePoint markets and fuel-card schema

**Status:** Open, 5 days  
**Depends on:** #353 (CI must pass)
**Impact:** Documentation only – no code changes  
**Effort:** Already written, just needs to merge

---

## Strategic Blockers (Long-Term, Needs Approval)

### #278 – Implement Entra role-based TMS authorisation

**Status:** Open, 15 days  
**Impact:** ⚠️ **High-risk security change**
- Closes legacy anonymous operational feeds (loads, driver assignments, ETAs, progress)
- Implements 7-tier role hierarchy (Viewer → Admin)
- Requires Entra app roles to be configured BEFORE deployment

**Why it's waiting:**
- Entra role manifest (`docs/entra-app-roles.manifest.json`) must be added to API app registration manually
- Users/groups must be assigned roles before API deployment
- Cannot be deployed without coordination with identity/access team

**What YOU need to do:**
1. Review authorization policy map: `docs/authorization-policy-map.md`
2. Decide on role assignments (which users get Planner, Dispatcher, etc.?)
3. Add Entra app roles via Azure Portal
4. Test in staging with assigned users
5. Approve PR

**Effort:** 2-3 hour review + 1 hour Entra setup + testing  
**Risk:** Medium – affects all API endpoints, requires careful testing

---

### #282 – Add end-to-end backend workflow regression tests

**Status:** Open, 15 days  
**Impact:** Quality assurance – xUnit/WebApplicationFactory test coverage
- Email order lifecycle and quantity retention
- Sunday-night/Monday operating-date continuity
- Revised-order allocation impact
- RoadTech-unavailable planner resilience

**Why it's waiting:**
- No blocker, just low priority vs. regressions
- Ready to merge – waiting for slot in sprint

**Effort:** 10 min merge  
**Risk:** Low – test-only, no production code change

---

### #272 – Align paired TV shared wallboard feeds

**Status:** Open, 18 days  
**Impact:** TV wallboard parity – delivery ETAs, run progress, driver assignments, geofence linkage  
**Issue:** Signed-in TMS wallboard current, but paired TV silently loses enrichment when feeds not aligned

**What needs to happen:**
1. Apply `X-TV-Display-Key` validation pattern to all shared read-only feeds
2. Add contract test for paired-TV surface including run-timing
3. Keep signed-in/legacy access intact

**Effort:** 30 min review + test  
**Risk:** Low – isolated to wallboard feed parity, backwards-compatible

---

## Data Quality Issues (Infrastructure)

### #177 – Rebuild full Falcon geofence estate from original source exports

**Status:** Open, 24 days  
**Impact:** Geofence coverage – current estate is 314 verified fences, claimed full estate is 555

**Blocker:** Original source files ("15 category exports") not reproducibly available
- PR #103 and #176 attempted reconstruction but failed checksum validation
- Cannot proceed without verifiable source artifact

**What you need to do:**
1. Locate original Falcon category exports from SLH
2. Provide to dev team
3. Build deterministic payload generation script
4. Validate checksum, polygon count, progression rules

**Effort:** 2-3 days (data archaeology + rebuild)  
**Risk:** High – geofence data directly affects run timing and journey accuracy

**Action:** File access request with SLH for historical category exports

---

### #200 – Require site reference numbers for all delivery locations and geofence sync

**Status:** Open, 21 days  
**Impact:** Master data – geofence-to-site matching, run-timing journey engine reliability

**Problem:** Some delivery locations have no usable site reference number → unreliable geofence linking

**What needs to happen:**
1. Add Site Reference Number field to every delivery location/site
2. Make the field required before a location becomes operational
3. Enforce uniqueness across active sites
4. Support multi-location customers (different reference numbers per location)
5. Link standard and override geofences to same reference
6. Add diagnostics for missing/unlinked references

**Effort:** 2-3 days (schema + migrations + API changes + tests)  
**Risk:** Medium – data migration required, must not break existing geofence links

---

### #186 – Master Data document libraries for Sites, Customers and Drivers

**Status:** Open, 23 days  
**Impact:** UX/Documentation – access maps, RAMS, licences, compliance evidence

**Requirements:**
- Record-level document storage (SharePoint/OneDrive)
- SQL stores metadata/link only
- Support driver expiry/compliance alerts
- HR-sensitive driver doc authorization

**Effort:** 3-5 days (SharePoint integration + schema + RBAC + UI)  
**Risk:** Medium – new integration point with SharePoint

---

## Dependency Graph

```
┌────────────────────────────────────────────────────────────────────────────┐
│ #424 Branch Protection (MANUAL SETUP)                                     │
│ ✓ Runbook ready – YOU DO IT                                               │
└────────────────────────────────────────────────────────────────┬───────────┘
                                                                  │
                                              └──→ All PRs must pass new status checks
                                                  before merging

┌────────────────────────────────────────────────────────────────────────────┐
│ #353 Fix Regressions (CRITICAL BLOCKER)                                   │
│ ✓ PR #354 ready                                                           │
└────────────────────────────────────────────────────────────────┬───────────┘
                                         ┌───────────────────┬───┴─────────────┐
                                         │                   │                 │
                                         ▼                   ▼                 ▼
                                     #352 Hub          #358 Docs            #272 TV Feed
                                     SharePoint        Markets/Fuel         Wallboard Parity
                                     (5d stalled)      (5d stalled)         (18d stalled)

┌────────────────────────────────────────────────────────────────────────────┐
│ #278 Entra RBAC (STRATEGIC, NEEDS APPROVAL)                               │
│ Blocks: Legacy anonymous feed closures                                    │
│ Needs: Entra app role setup + user assignment                             │
└────────────────────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────────────────────┐
│ #282 E2E Tests (READY, WAITING)                                           │
│ No blockers – waiting for sprint slot                                     │
└────────────────────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────────────────────┐
│ #177 Falcon Estate (BLOCKED ON DATA)                                      │
│ Needs: Original 15-category exports from SLH                              │
└────────────────────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────────────────────┐
│ #200 Site References (INFRASTRUCTURE)                                     │
│ Depends on: #177 (geofence estate rebuild)                                │
│ Impact on: Journey timing, run-timing reliability                         │
└────────────────────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────────────────────┐
│ #186 Document Libraries (STANDALONE)                                      │
│ No dependencies – can proceed independently                               │
└────────────────────────────────────────────────────────────────────────────┘
```

---

## Priority Queue (Recommended Order)

### IMMEDIATE (Next 2 Hours)
1. ✅ **#424** – Enable branch protection (manual 5 min)
2. ✅ **#353** → **#354** – Merge regression fix (30 min review + test)
3. ✅ **#352** – Merge SharePoint Hub (5 min, blocked by #353)
4. ✅ **#358** – Merge documentation (5 min, blocked by #353)

### NEXT (Next Sprint, 1-2 Days)
5. **#278** – Entra RBAC (3-4 hours including Entra setup)
6. **#282** – E2E tests (10 min merge)
7. **#272** – TV wallboard parity (30 min review)

### LATER (Infrastructure Work, 3-5 Days)
8. **#177** – Falcon estate rebuild (wait for source files)
9. **#200** – Site reference numbers (2-3 days, depends on #177)
10. **#186** – Document libraries (3-5 days, can run in parallel)

---

## Health Metrics

| Metric | Current | Target | Status |
|--------|---------|--------|--------|
| CI Pass Rate | 4/9 recent runs ✓ | 100% | 🔴 Blocked by #353 |
| Branch Protection | ✗ None | ✅ Required | 🟡 Manual setup pending |
| Open Issues | 13 | <5 | 🔴 Backlog accumulating |
| Open PRs | 10 | <3 | 🔴 Stalled merges |
| Stale PRs (5+ days) | 4 | 0 | 🔴 #352, #354, #358, #388, #390 |
| Test Coverage | 659 tests | All passing | 🔴 4 failing |

---

## Labels in Use (or To Add)

| Label | Purpose | Issues |
|-------|---------|--------|
| `status: blocked-by` | Waiting on another issue/PR | #352, #358 (blocked by #353) |
| `status: ready` | Ready to merge, no blockers | #282, #354 |
| `priority: critical` | Production-blocking | #353, #424, #388, #390 |
| `priority: high` | Significant impact | #278, #177, #200 |
| `priority: medium` | Important but not urgent | #186, #272 |
| `type: bug` | Defect/regression | #353 |
| `type: feature` | New capability | #278, #282, #186, #200, #177, #272 |
| `type: governance` | Process/admin | #424 |
| `dependencies` | .NET packages | #388, #390 |

---

## Next Steps

1. **Immediate (now):**
   - [ ] Review this document for accuracy
   - [ ] Enable branch protection (`.github/BRANCH_PROTECTION_SETUP.md`)
   - [ ] Merge #388, #390 (security patches)
   - [ ] Review & merge #354 (regression fix)

2. **Today:**
   - [ ] Merge #352, #358 (now that #353 is fixed)
   - [ ] Begin Entra RBAC review (#278)

3. **This Sprint:**
   - [ ] Merge #282, #272
   - [ ] Approve & deploy #278 after testing

4. **Next Sprint:**
   - [ ] File request for #177 source files
   - [ ] Plan #200 (site references) after #177 complete
   - [ ] Plan #186 (document libraries)

---

## Questions?

For clarification on any issue or blocker, check:
- Issue details: https://github.com/Stuart-Lyons-Haulage/slh-tms-api/issues
- PR status: https://github.com/Stuart-Lyons-Haulage/slh-tms-api/pulls
- Deployment runbook: `.github/DEPLOYMENT.md`
- Branch protection setup: `.github/BRANCH_PROTECTION_SETUP.md`
