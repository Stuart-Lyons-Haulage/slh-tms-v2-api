# Manual geofence Site linking

The Geofence section allows an authenticated TMS operator to select a canonical Site from the dropdown and persist that relationship even when the RoadTech/Falcon geofence name differs from the Site Master name.

Automatic linking of previously unlinked geofences remains conservative and continues to use name matching. Once an operator has explicitly selected a Site, the persisted SiteId is authoritative and later Site Sync runs preserve that manual choice.

Regression coverage: `Slh.Tms.Api.Tests/SiteGeofenceManualDropdownLinkTests.cs`.
