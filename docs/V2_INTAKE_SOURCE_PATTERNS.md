# V2 order-intake source patterns

This is a V2 design reference built from recent operational mailbox samples. It records formats and extraction rules without making sender-specific code responsible for persistence.

## Core pipeline

Every source follows the same path:

1. Capture immutable source evidence.
2. Extract one or more order candidates.
3. Resolve customer, collection site, delivery site and market against canonical master data.
4. Validate required fields and confidence.
5. Send unresolved items to review.
6. Promote approved candidates into canonical orders.
7. Preserve source lineage and later amendments as revisions.

A parser may understand a source format. It must not create customers, sites, drivers or markets.

## Natures Way / NWAY pallet order reports

Recent examples provide a structured body table and may also include attachments. Useful fields include:
- requested ship date
- collection site
- customer name
- depot ID
- depot description
- delivery postcode/address
- sales order ID
- customer reference
- pallet type/name
- pallet quantity
- PO reference

Implementation:
- Prefer structured attachment rows when present and readable.
- Fall back to the body table when the attachment is absent or unreadable.
- Treat each collection-site + destination + source-order row as a separate candidate.
- Preserve Sales Order ID, CustomerRef and PO independently.
- Resolve depot ID/postcode against Site Master.
- A later row with the same stable source identity is an amendment, not a second master record.

## Summer Berry / TSBC

Recent examples are attachment-led. The email body can contain operational context and a transport/invoice PO while the detailed Aldi/Morrisons movements are in the attachment.

Implementation:
- Capture PO/reference from the body even when the movement detail comes from an attachment.
- Do not reject an email because the body alone lacks collection/delivery rows.
- Use known TSBC collection-site aliases only as Master Data aliases.
- Resolve Aldi and Morrisons destinations from Site Master.
- A subject such as an Aldi/Morrisons daily file is evidence classification, not a reason to hard-code destinations.

## Barfoots

Recent mail includes both structured load-plan/delivery-note attachments and free-text collection instructions.

Implementation:
- Support structured documents as the high-confidence path.
- Support explicit free-text jobs where collection, delivery, date and instructions are present.
- A new delivery address that cannot be resolved must become a Master Data review candidate; it must not be silently inserted.
- Preserve working-hour and call-ahead instructions as order/site notes without using them as identity keys.
- Keep special trailer requirements as operational constraints.

## Waitrose-related traffic

Recent examples include:
- delivery notes attached to short body messages
- booking/reference threads
- load-plan style documents

Implementation:
- Do not assume every message containing "Waitrose" is a new order.
- Classify delivery notes, booking correspondence, confirmations and transport-order evidence separately.
- Only promote when the evidence contains enough movement data or links to an existing movement revision.
- Preserve booking references and PO/reference values.

## Operational source classification

Before parsing, classify evidence into:
- NewOrder
- Amendment
- Cancellation
- DeliveryNote
- BookingConfirmation
- CollectionConfirmation
- ETACommunication
- LoadPlan
- Informational
- Unknown

Only NewOrder/Amendment/Cancelled candidates can change canonical order state.

## Matching rules

Master resolution order:
1. stable external code/depot ID
2. approved source alias
3. exact canonical code/name within customer context
4. postcode/address match
5. suggested fuzzy match requiring review

Never auto-create a canonical site from fuzzy text.

## Duplicate and amendment identity

Use source-specific stable identity parts where available:
- source message/document identifier
- Sales Order ID / customer order ID
- PO/reference
- collection date
- collection master ID
- destination master ID

A PO may cover multiple separate movements, so PO equality alone must not merge orders. Roadrunner export may use PO/destination alignment for its own charging/import behaviour without collapsing V2 orders.

## Regression fixtures

Build anonymised fixtures for:
- NWAY body table with multiple collection sites and destinations
- TSBC attachment-led Aldi/Morrisons order with body PO
- Barfoots free-text collection/delivery instruction
- Waitrose delivery-note-only email that must not create a duplicate order
- amendment that changes pallet quantity while retaining stable identity
- unresolved destination that must enter review
