# SLH TMS customer load-plan dispatch

This flow mirrors the existing daily planner process from the TMS rather than rebuilding a spreadsheet by hand.

## Operator flow

1. Planner opens **Driver Dispatch** for the planning date.
2. Select **Customer load plans**.
3. TMS groups the live planned orders by customer and builds the PDF from the current runs, driver/vehicle/trailer allocations and order references.
4. Planner reviews recipients, subject, email body and **Preview PDF**.
5. **SEND FROM INFO MAILBOX** creates an immutable outbound snapshot in SQL.
6. This Power Automate flow polls the reviewed outbox once per minute, sends the PDF from `info@lyonshaulage.com`, then marks the snapshot Sent in the TMS.
7. A failed Outlook send is retained as Failed instead of disappearing or being marked sent.

The inbound Info-mailbox intake has a separate guard for Lyons-originated load-plan emails, so these outbound messages cannot loop back into Order Review.

## Connector update

`connector-extension.yaml` contains the three operations required by this flow:

- `GetCustomerLoadPlanOutbox`
- `MarkCustomerLoadPlanSent`
- `MarkCustomerLoadPlanFailed`

Merge those paths and definitions into the existing `openapi-power-automate.yaml`, update the existing SLH TMS custom connector, then refresh the `shared_slhtms` connection reference before importing/enabling `workflow.json`.

Do not create a second order-intake connector or point the outbound flow at `/api/v1/orders`.

## Outlook connection

The `shared_office365` connection must have permission to send from the Info shared mailbox. The mailbox address is supplied by the TMS outbox item and is not duplicated in the flow definition.

## Validation

```bash
python power-automate/customer-load-plan-dispatch/validate_workflow.py
python power-automate/customer-load-plan-dispatch/test_validate_workflow.py
```

The flow processes customer messages serially to avoid duplicate parallel sends. API and Outlook retries are bounded exponential retries.
