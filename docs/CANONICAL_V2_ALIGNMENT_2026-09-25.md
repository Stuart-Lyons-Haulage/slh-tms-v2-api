# Canonical V2 alignment ledger — 25 September 2026

This ledger records how the functional work on `v2/canonical-hardening-2026-09-24` is preserved while consolidating the deployable API onto `main`.

## Rules

- `main` remains the only canonical API line after this cleanup.
- The Web/API contract remains `/api/v1`.
- The local standalone deployment is the supported runtime.
- RoadTech is presented as one integration source. Falcon tracking and TachoMaster evidence remain separate internal capabilities under that source.
- Azure-era deployment workflows, local username/password authentication and duplicate nested V2 scaffolding are not reintroduced.
- The hardening branch must not be deleted until this cleanup passes CI and is merged.

## Hardening-line reconciliation

| Commit | Change | Canonical treatment |
| --- | --- | --- |
| `25973c8` | Consolidate portal / startup hardening | Superseded by current root-level V2 main; do not restore nested `v2/` duplicate application or local-auth scaffolding. |
| `582f517` | Run CI on hardening branches | Superseded by PR CI on the cleanup branch and canonical main workflow. |
| `f7af4b4` | Replay retained orders across parser-key changes | Already present in current main replay controller. |
| `09f3a61` | Fresh SQL readiness gate | Superseded by `ops/bootstrap-fresh-db.sh` in current main CI. |
| `12b515b` | Direct Info mailbox Graph polling | Already present in current main and retained. |
| `1b9872a` | Register Graph poller | Retained; registration aligned so the hosted service can also be injected for manual polling. |
| `004d031` | Graph local settings | Already present in current main. |
| `cc4e07e` | Graph-aware intake health | Already present in current main. |
| `f69a6e9` | Graph mapping tests | Already present in current main. |
| `1d009e3` | Local Graph deployment documentation | Ported and corrected to the canonical `/api/v1` contract. |
| `164f7c0` | Graph runtime compatibility | Ported: removed unsupported ordering/projection assumptions and added useful Graph error detail. The retired-migration workaround is not copied because migrations 074–076 are canonical in current main. |
| `066d906` | Tolerate retired local migration 076 | Superseded: migration 076 is canonical in current main rather than retired. |
| `7a34b3f` | API container health contract | Already present in current main: curl installed, port 8080. |
| `41d49c4` | Manual Graph poll / sync status / 20-minute tacho cadence | Ported to `/api/v1`; RoadTech status is consolidated; 20-minute Tacho evidence cadence retained. Current main already has the newer Samsara adapter status. |
| `0f1bdf8` | Require master-ready orders before approval | Ported to canonical staging approval. |
| `03eea61` | Parse PDF order attachments | Ported to canonical email intake parser. |

## Deletion gate

Only after CI passes and this branch is merged:
1. delete `v2/canonical-hardening-2026-09-24`;
2. delete `v2/runtime-hardening` if it contains no unique unported work;
3. remove/rename old local checkouts so Docker cannot resolve to `/Users/danielwilliams/SLH-TMS-V2/...`;
4. keep only the canonical local repositories under `~/Desktop/SLH TMS/Repositories/`.
