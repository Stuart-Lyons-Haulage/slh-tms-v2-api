# Import DOT Tracking geofences

Use **Master Data → Sites → Import DOT Geofences** to load a geofence-category JSON file exported from DOT Tracking.

## Accepted file

The importer accepts `falcon.geofence` JSON export version `1`. Each geofence needs a name and at least three distinct longitude/latitude points. The server validates the file before anything is saved.

## Review statuses

- **Matched** — the DOT name exactly matches one active Site after case and spacing normalisation.
- **Needs linking** — choose **Link to Existing Site** and search by Site code/name, or choose **Create New Site**.
- **Already imported** — the existing geofence will be updated; its Site link is retained unless a different Site is explicitly selected.
- **Possible rename** — the polygon matches an existing geofence with a different name. Select the Site and confirm the rename.
- **Invalid** — fix the source data or skip the row. Invalid rows are never imported.

The **Import and Update** button becomes available only after every row is linked or explicitly skipped.

## Re-importing

Re-importing updates DOT-managed polygon, category, wait-time, and entry/exit timing data. It does not replace the linked Site's code, display name, address, instructions, aliases, active state, or planning profile. Existing geofences are updated rather than duplicated.

The import is transactional: if any selected row cannot be saved, none of the selected rows are committed and the review choices remain available for correction and retry. Imported database geofences are used by the live operational geofence engine.
