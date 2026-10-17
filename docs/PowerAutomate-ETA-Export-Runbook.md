# Power Automate to TMS and ETA export runbook

This runbook uses the existing `SLH-TMS | Info Mailbox | Order Intake | PROD` flow. Do not create a second intake flow.

## 1. Receive the customer email

Use **When a new email arrives in a shared mailbox (V2)** for `info@lyonshaulage.com`, Inbox, with attachment filtering disabled. Keep the original message unchanged. Retrieve attachment content in a sequential loop and protect attachment actions with secure inputs/outputs.

## 2. Submit to the TMS

Call the existing custom connector operation `IntakeInfoMailboxEmail`, which posts to:

```text
POST /api/v1/order-intake/email
```

Pass the complete message envelope: message IDs, conversation ID, sender, recipients, subject, received time, body, web link, correlation ID and attachment objects containing name, content type, base64 content, inline flag, content ID and size.

The inbound API stages one or more transport orders as `PendingReview`. A separate Sent Items flow should call `POST /api/v1/customer-communications/ingest` for outbound ETA/load-plan/exception emails; that endpoint uses `communication:{messageId}` as its idempotency key.

Retries are safe. The original email and attachments remain in Outlook. Attachment bytes are not copied into the communication evidence payload.

## 3. Review in the TMS

- Use **Load Review** (`/staging`) to approve or reject extracted transport orders.
- Use **Customer communications** (`/communications`) to inspect the source email, ETA claims, load-plan version, pallet count, exception signals, next-update time, acceptance cutoff and tracking links.
- Approving a communication records the review decision; it never creates a live order.
- Approving an order is the only route that promotes it into operational planning.

## 4. Plan and operate

After order approval, continue through the existing workflow: Planner, Pallet Control, Driver Dispatch, live tracking and operations wallboard. The ETA evidence chain uses the allocated run, TachoMaster, DOT/Falcon movement, geofence execution and route calculation.

## 5. Export customer ETA evidence

### TMS portal

Open **Exports → Customer ETA proof**, select the operating date and choose **Download customer ETA proof CSV**.

### Power Automate/API

Call:

```text
GET /api/v1/operations/customer-eta-evidence/export.csv?date=YYYY-MM-DD
```

The response is a CSV named `SLH-customer-ETA-evidence-YYYY-MM-DD.csv`. It includes run, order, customer, driver, vehicle, tracking, Tacho, geofence, ETA source, delivery-window risk, legal-hours evidence and customer-promise readiness.

For JSON inspection, call:

```text
GET /api/v1/operations/customer-eta-evidence?date=YYYY-MM-DD
```

## 6. Optional outbound customer email flow

Keep outbound sending separate from intake. A scheduled flow can retrieve the approved ETA dataset, group rows by approved customer contacts, create drafts or send according to the agreed policy, and record the provider message ID.

Existing acknowledgement operations remain:

```text
GET  /api/v1/customer-communications/pending
POST /api/v1/customer-communications/{communicationKey}/sent
```

Only call the sent endpoint after Outlook has successfully sent the message. Never send directly from the browser and never use a Power Automate Approval action as the TMS approval authority.

## Acceptance checks

Test one normal order, one Excel load plan, one amended load plan, one ETA exception, multiple attachments, a duplicate replay and a rejected order. Confirm that the source message remains unchanged, the communication record is idempotent, orders remain `PendingReview` until approved, pallet quantities are unchanged and the ETA CSV contains the correct operating date.
