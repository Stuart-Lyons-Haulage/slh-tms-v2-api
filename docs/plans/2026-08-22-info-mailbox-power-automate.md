# Info Mailbox Power Automate Implementation Plan

> **For agentic workers:** Use the host's available task-by-task implementation workflow. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver an importable, solution-aware Power Automate definition that transports complete shared-mailbox emails to the existing TMS staging intake API without creating live orders.

**Architecture:** Outlook supplies immutable message metadata and attachment bytes. The flow builds one `MailboxEmailIntakeRequest` and calls the existing OAuth-protected `IntakeInfoMailboxEmail` custom-connector operation; all extraction, splitting, normalisation, duplicate/amendment handling and Pending Review creation remain in the tested API.

**Tech Stack:** Power Automate workflow-definition JSON, Microsoft 365 Outlook connector, SLH TMS custom connector, Python structural validation, .NET 8 backend.

## Global Constraints

- Never modify the source email or call a live-order endpoint.
- Preserve message IDs, sender/recipient data, subject, body, timestamps, importance, web link, attachment names/types/content and correlation ID.
- Use connection references and environment variables; do not embed credentials.
- Bound retries and rely on API idempotency for safe replay.
- A failed attachment must not silently produce a partial order submission.

---

### Task 1: Deployable flow source and validator

**Files:**
- Create: `power-automate/info-mailbox-order-intake/workflow.json`
- Create: `power-automate/info-mailbox-order-intake/deployment-settings.example.json`
- Create: `power-automate/info-mailbox-order-intake/README.md`
- Create: `power-automate/info-mailbox-order-intake/validate_workflow.py`
- Test: `power-automate/info-mailbox-order-intake/test_validate_workflow.py`

**Interfaces:**
- Consumes: Outlook `SharedMailboxOnNewEmailV2`, `GetAttachments_V2`, `GetAttachment_V2`; custom connector `IntakeInfoMailboxEmail`.
- Produces: one complete `MailboxEmailIntakeRequest` per received message and a persisted flow-run result containing API staging IDs/statuses.

- [ ] Add a focused validator test proving missing evidence fields, direct live-order calls, unbounded retries and absent Pending Review intake are rejected.
- [ ] Run `python -m unittest power-automate/info-mailbox-order-intake/test_validate_workflow.py` and observe failure because the workflow does not exist.
- [ ] Add the minimum workflow definition, settings and deployment instructions.
- [ ] Run the focused tests and structural validator successfully.
- [ ] Import in the SLH Power Platform environment, bind Outlook/custom-connector connection references, and run the mailbox acceptance matrix from the production runbook.

## Unresolved externally observable decisions

- The Power Platform environment and connection-reference IDs are tenant-owned values and must be selected during solution import; they are deliberately absent from source control.
- Production enablement remains a tenant-side action because the available cloud browser cannot be authenticated from the user's mobile session.
