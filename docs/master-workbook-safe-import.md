# Safe master workbook import

This branch adds a safe import route for `TMS Master Data Import.xlsx`.

## Endpoints

- `POST /api/v1/master-data/workbook/preview`
- `POST /api/v1/master-data/workbook/commit`

Both endpoints accept a multipart form upload with field name `file`.

## Site Master source of truth

The import treats the live SQL `Sites` table as the source of truth. A workbook row is matched before any insert is allowed.

Site matching order:

1. SiteID / ExternalCode
2. Normalised site name + postcode
3. Normalised driver text name + postcode
4. Normalised collection address + postcode
5. Alias match
6. Map link match
7. Weak possible duplicate review

Weak or conflicting site rows are held for review and are not written during commit.

## Workbook sections

The import supports:

- Sites
- Site Cutoffs
- Run Times
- Vehicles & Fuel
- Market Contacts
- Customer Contacts
- Fuel Price History
- Driver operational overlay

## Driver ownership

Driver identity remains owned by TachoMaster. The workbook import is update-only for drivers and will not create new driver rows. It only overlays operational detail such as phone, email, code, group and skills when it can match an existing live driver.

## Timing rules

Run Times rows are stored as promoted `masterdetail:sitetimingrule` records so the existing `SiteTimingRuleStore` can feed dispatch and planning timing rules without a database migration.

Site Cutoffs are stored as promoted `masterdetail:sitecutoff` records and only commit when the site row matches live Site Master.
