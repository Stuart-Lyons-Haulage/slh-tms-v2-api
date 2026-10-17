# ETA location quality hardening

This document tracks the location-quality guardrails used by live ETA, wallboards and optimisation.

Operational routing must prefer a canonical Site Master / approved geofence location over stale coordinates copied onto an imported order. A stop that cannot be confidently resolved must surface a data-quality warning rather than emit a misleading ETA.

Required warning states:
- Physical address/postcode required
- Geofence not linked
- Site coordinates conflict with imported order coordinates

The SLH Assistant should explain the missing data and direct the planner to the affected Site Master record.
