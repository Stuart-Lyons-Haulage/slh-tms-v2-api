# SLH TMS Info mailbox flow

This directory is the source-controlled definition for `SLH-TMS | Info Mailbox | Order Intake | PROD`.

The flow is configured as `Started` and accepts all inbound messages in the shared mailbox. Import it into the existing SLH managed solution, bind the two existing connection references, and verify the acceptance matrix before relying on it operationally.

## Existing components used

- Outlook shared mailbox: `info@lyonshaulage.com`
- Outlook trigger: `When a new email arrives in a shared mailbox (V2)` with attachment metadata included
- Outlook attachment content action: `Get Attachment (V2)`
- TMS custom connector operation: `IntakeInfoMailboxEmail`
- Hosted route: `POST /api/v1/order-intake/email`
- OAuth scope: existing Entra `Tms.Access`
- Review/promotion: existing `/api/v1/staging/{id}/approve` and `/reject`
- Import history: SQL `StagedImportEvents` snapshots plus the source mailbox identifiers
- Live-order trace: SQL `TransportOrders.SourceStagedImportId`
- Governed sender routing: SQL `CustomerEmailRoutes` and SQL `OrderIntakeRouteRules`, applied by the TMS API

## Live Outlook connector pattern

The production Office 365 Outlook connector used by SLH does not expose a separate `Get Attachments (V2)` list action in this flow. The email trigger already supplies the attachment collection.

The supported production pattern is therefore:

1. Trigger on every new Info mailbox Inbox message. Do not filter by sender, subject, order type or attachment presence.
2. Initialise the `NormalizedAttachments` array.
3. `Apply to each` over `@coalesce(triggerOutputs()?['body/attachments'],json('[]'))`.
4. Before downloading content, continue only when `isInline` is false and the attachment name ends `.xls`, `.xlsx`, `.xlsm`, `.csv` or `.pdf`. Signature/social-media images are ignored.
5. For supported order documents only, call `Get Attachment (V2)` using the trigger Message Id and current attachment Id. Keep retries bounded so one bad attachment cannot hold the serial loop for several minutes.
6. Append a structured object containing `id`, `name`, `contentType`, `size`, `isInline`, `contentId` and `contentBytes` to `NormalizedAttachments`.
7. Submit `IntakeInfoMailboxEmail` after the attachment loop whether the loop succeeded, failed, timed out or was skipped. This prevents a single attachment problem from dropping the entire customer email.

Do not build attachment objects with a manually concatenated `json(concat(...))` string in the source-controlled definition. A structured object avoids malformed JSON when Outlook returns values such as inline `contentId` strings. The live flow may still show a designer expression, but its effective payload must match the structured object above.

The TMS request mappings must keep `bodyText` from Outlook `bodyPreview`, `bodyHtml` from the full body, and convert `isHtml` to the string `html` or `text` for `bodyFormat`. `senderName` should prefer `fromName` and fall back to `from`.

## SQL email-intake mappings

The API owns normalised sender/customer mappings and route rules in SQL. Sender mappings identify the customer; route rules separately match the customer, origin, retailer and destination. Planners manage them through the authenticated mapping APIs. SQL remains the sole master-data authority.

The mailbox flow submits every candidate email to `IntakeInfoMailboxEmail`; routing is applied centrally by the API so replays and non-flow callers behave identically. Exact sender addresses outrank domains, subject-specific mappings outrank generic mappings, and conflicts never auto-route. When a planner approves an order, its exact external sender is learned. If the same sender is later approved for a different collection site, the customer mapping is retained but the unsafe site default is cleared. Cross-customer conflicts are marked `RequiresReview`.

Power Automate must submit the complete message to the API and must not maintain a parallel routing store or bypass the API's conflict and approval checks.

No Microsoft List, SharePoint store, Power Automate Approval, secret, bearer token, client secret or live-order endpoint is present in the definition. The Info mailbox retains the original email/attachments under the organisation's mailbox retention policy; TMS SQL is the authoritative import, review and promotion history.

## Import

1. In Power Apps/Power Automate Solutions, open the existing SLH TMS solution.
2. Add a cloud flow from definition and use `workflow.json` as the source definition. If the tenant requires an exported solution wrapper, create an empty solution-aware flow once, unpack it with `pac solution unpack`, replace only that flow's `properties.definition` and `connectionReferences` with this file, then repack with `pac solution pack`.
3. Bind `slh_sharedoffice365_info` to the existing Microsoft 365 Outlook connection that has delegated access to the Info mailbox.
4. Bind `slh_sharedslhtms_prod` to the existing SLH TMS custom connector OAuth connection.
5. Set `SLH_InfoMailboxUPN` to `info@lyonshaulage.com`.
6. Confirm secure inputs/outputs remain enabled on attachment retrieval and TMS submission.
7. Confirm database script `031_Order_Import_Audit_History.sql` has applied successfully.
8. Save with the flow started. Run the tests in the production runbook and confirm the source email remains unchanged.

Power Automate may rewrite connector-internal token names on first save. Use designer dynamic content for Message Id, Internet Message Id, Conversation Id, From, From Name, To, CC, Subject, Received Time, Body, Body Preview, Importance and Web Link if the imported tenant connector exposes a different internal token. Do not change the semantic request field names.

## Validation

```bash
python power-automate/info-mailbox-order-intake/validate_workflow.py
python power-automate/info-mailbox-order-intake/test_validate_workflow.py
```

The full action-by-action deployment, duplicate, exception, security and acceptance-test specification is in `docs/PowerAutomate-InfoMailbox-Order-Intake-Production.md`.
