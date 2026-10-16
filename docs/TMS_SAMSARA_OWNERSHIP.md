# TMS V2 / Samsara ownership decision (16 October 2026)

**System boundary:** TMS V2 handles customer order capture, review, canonical master data, run and trailer **capacity** planning, pallet/allocation ledger, stop instructions and addresses, dispatch handover, and audited receipt of execution outcomes. Samsara owns the live journey.

**Samsara owns:** vehicle position, route mapping, journey distance/optimisation, ETAs/ETOs, driver/vehicle assignment after dispatch, geofences and live progress. TMS must not estimate, reproduce, approve or block dispatch based on these features. Do not substitute a different route engine or Haversine approximation.

**Must remain:** send-to-Samsara action for a run with no local driver/vehicle, stable export and correlation IDs, delivery stop addresses and sequence, incoming Samsara assignment/status/completion/POD events, idempotent ingestion, exception reconciliation and preserved history for audit and invoicing.

**Decommission safely:** remove ETA/map/geofence controllers, optimisers, jobs, DI and HTTP clients, secrets and infrastructure. Validate no TMS feature still requires or bills Azure Maps; delete associated Azure resource only with explicit approval. Keep historical SQL evidence and migrations unless retention/deletion has separate approval.

**Status of this draft API change:** stops the local ETA recalculation, nightly ETA accuracy and ETA hub runtime paths. **Other Azure Maps controller/service references and geofence processing remain and must be removed in subsequent reviewed changes before claiming complete decommissioning.** Avoid merging this draft ahead of its coordinated API removal work and successful CI.
