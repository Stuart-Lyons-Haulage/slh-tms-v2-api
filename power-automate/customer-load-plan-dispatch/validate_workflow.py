#!/usr/bin/env python3
import json
import pathlib
import sys


def validate(workflow):
    errors = []
    props = workflow.get("properties", {})
    definition = props.get("definition", {})
    triggers = definition.get("triggers", {})
    actions = definition.get("actions", {})
    serialized = json.dumps(workflow, separators=(",", ":"))

    recurrence = triggers.get("Every_Minute", {}).get("recurrence", {})
    if recurrence.get("frequency") != "Minute" or recurrence.get("interval") != 1:
        errors.append("outbound load-plan flow must poll once per minute")
    if "GetCustomerLoadPlanOutbox" not in serialized:
        errors.append("TMS customer load-plan outbox action is missing")
    if "SharedMailboxSendEmailV2" not in serialized:
        errors.append("shared Info mailbox send action is missing")
    if "MarkCustomerLoadPlanSent" not in serialized:
        errors.append("sent acknowledgement back to TMS is missing")
    if "MarkCustomerLoadPlanFailed" not in serialized:
        errors.append("failed-send audit action is missing")
    if "info@lyonshaulage.com" in serialized:
        errors.append("mailbox must come from the reviewed TMS outbox item, not be duplicated in the flow")

    connections = set(props.get("connectionReferences", {}))
    if connections != {"shared_office365", "shared_slhtms"}:
        errors.append("flow must use only Outlook and the existing SLH TMS connector")

    foreach = actions.get("For_Each_Queued_Load_Plan", {})
    if foreach.get("runtimeConfiguration", {}).get("concurrency", {}).get("repetitions") != 1:
        errors.append("outbound sends must be serial to avoid duplicate parallel delivery")
    send = foreach.get("actions", {}).get("Send_From_Info_Mailbox", {})
    send_params = send.get("inputs", {}).get("parameters", {})
    if send_params.get("emailMessage/MailboxAddress") != "@items('For_Each_Queued_Load_Plan')?['mailbox']":
        errors.append("shared-mailbox sender must be supplied by the TMS outbox item")
    if "emailMessage/Attachments" not in send_params:
        errors.append("load-plan PDF attachment is missing")

    for name in ("Get_Customer_Load_Plan_Outbox", "Send_From_Info_Mailbox", "Mark_Load_Plan_Sent", "Mark_Load_Plan_Failed"):
        node = actions.get(name) if name in actions else foreach.get("actions", {}).get(name, {})
        if not node:
            continue
        policy = node.get("runtimeConfiguration", {}).get("retryPolicy", {})
        if policy and not (policy.get("type") == "exponential" and 1 <= policy.get("count", 0) <= 4):
            errors.append(f"{name} retry must be bounded exponential")
    return errors


def main():
    path = pathlib.Path(__file__).with_name("workflow.json")
    errors = validate(json.loads(path.read_text(encoding="utf-8")))
    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        return 1
    print(f"Validated {path.name}: customer load-plan outbound contract satisfied")
    return 0


if __name__ == "__main__":
    sys.exit(main())
