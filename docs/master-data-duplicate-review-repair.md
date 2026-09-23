# Master data duplicate review repair

## What was wrong

The duplicate review function was too narrow and could appear to do nothing even when duplicate rows existed:

- Site duplicate detection required address/postcode-style evidence and could miss duplicated external codes or blank-address rows.
- Driver duplicate detection used either TachoMaster ID or employee number, so a TachoMaster-enriched duplicate could be separated from the original employee-number row.
- Trailer duplicate detection did not treat `SLH1`, `SLH001` and `1` as the same operational trailer identity.
- Rejected/kept-separate duplicate candidates were only removed in the browser state and could reappear after a refresh.
- Driver, vehicle and trailer merges archived duplicate rows but did not consistently reassign existing load/planning/resource/integration references back to the canonical SQL row.

## Repair

- Broadened duplicate candidate detection for sites, drivers, vehicles, trailers and markets.
- Added rejection suppression so kept-separate candidates stay out of future scans.
- Reassigned linked operational records during driver, vehicle, trailer and site merges.
- Preserved SQL as the single operational master and kept merge actions audited.
- Added regression tests for the above scenarios.
