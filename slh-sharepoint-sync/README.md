# SLH Microsoft Lists sync - isolated deployment staging
This branch stages a **read-only** preflight, not a working API synchronisation. Do not mark it live.
Existing site ID is prevalidated. Do not publish to the TMS App Service or the existing Node.js driver-register Function.
The actual Fleetio/SageHR upserts require verified source responses, correct SharePoint internal column mapping and a source-specific immutable key. A timer that only runs preflight must not be mistaken for a successful data sync.
