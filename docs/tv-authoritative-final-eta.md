# Paired TV authoritative final ETA

The paired TV keeps `/api/v1/tv-display/live-runs` authoritative for which runs remain visible. Active runs may additionally read `/api/v1/run-timing` with the same `X-TV-Display-Key` so the displayed final ETA uses the cumulative RoadTech/geofence timing engine.

Completed-run filtering is unchanged. Final-arrival presentation remains owned by the TV live-run state and must not be overwritten by a calculated ETA.
