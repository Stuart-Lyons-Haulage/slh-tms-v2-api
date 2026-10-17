#!/usr/bin/env python3
import json
import pathlib
import sys


REQUIRED_PARAMETER_FIELDS = {
    "body/messageId", "body/internetMessageId", "body/conversationId", "body/mailbox",
    "body/senderAddress", "body/senderName", "body/toRecipients", "body/ccRecipients",
    "body/subject", "body/receivedAtUtc", "body/bodyText", "body/bodyHtml",
    "body/bodyFormat", "body/importance", "body/webLink", "body/correlationId",
    "body/attachments",
}


def _walk(value):
    yield value
    if isinstance(value, dict):
        for child in value.values():
            yield from _walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from _walk(child)


def _find_action(actions, name):
    if name in actions:
        return actions[name]
    for node in actions.values():
        if isinstance(node, dict):
            nested = node.get("actions")
            if isinstance(nested, dict):
                found = _find_action(nested, name)
                if found is not None:
                    return found
    return None


def validate(workflow):
    errors = []
    properties = workflow.get("properties", {})
    definition = properties.get("definition", {})
    actions = definition.get("actions", {})
    triggers = definition.get("triggers", {})

    trigger = triggers.get("When_New_Email_Arrives_Info_Shared_Mailbox")
    if not isinstance(trigger, dict):
        errors.append("shared-mailbox trigger is missing")
        trigger = {}

    trigger_parameters = trigger.get("inputs", {}).get("parameters", {})
    if trigger_parameters.get("mailboxAddress") not in (
        "@parameters('SLH_InfoMailboxUPN')", "info@lyonshaulage.com"
    ):
        errors.append("shared-mailbox trigger must target the Info mailbox")
    if trigger_parameters.get("includeAttachments") is not True:
        errors.append("shared-mailbox trigger must include attachment metadata")
    if "hasAttachments" in trigger_parameters:
        errors.append("shared-mailbox trigger must not filter by attachment presence")
    if trigger.get("conditions"):
        errors.append("shared-mailbox trigger must not filter sender, subject or order type; every inbox email must reach TMS intake")

    serialized = json.dumps(workflow, separators=(",", ":"))
    if "GetAttachments_V2" in serialized:
        errors.append("flow must use the trigger attachment collection; this tenant exposes Get Attachment (V2), not Get Attachments (V2)")
    if "/api/v1/orders" in serialized or '"operationId":"CreateOrder"' in serialized:
        errors.append("live-order endpoint/action is forbidden")
    if any(value.lower() in serialized.lower() for value in ("shared_sharepoint", "CreateItem", "Microsoft List", "SharePoint")):
        errors.append("Microsoft Lists/SharePoint storage is forbidden; TMS SQL is authoritative")
    if "IntakeInfoMailboxEmail" not in serialized:
        errors.append("Pending Review intake operation IntakeInfoMailboxEmail is missing")

    initialise = _find_action(actions, "Initialise_Normalized_Attachments") or {}
    variables = initialise.get("inputs", {}).get("variables", [])
    if not any(v.get("name") == "NormalizedAttachments" and v.get("type") == "array" for v in variables if isinstance(v, dict)):
        errors.append("NormalizedAttachments array initialisation is missing")

    loop = _find_action(actions, "Apply_to_each") or {}
    foreach_expression = str(loop.get("foreach", ""))
    if "triggerOutputs" not in foreach_expression or "body/attachments" not in foreach_expression or "coalesce" not in foreach_expression:
        errors.append("Apply_to_each must enumerate the trigger body/attachments collection with an empty-array fallback")

    loop_actions = loop.get("actions", {})
    condition = _find_action(loop_actions, "If_supported_order_attachment") or {}
    condition_text = json.dumps(condition.get("expression", {}), separators=(",", ":")).lower()
    if "isinline" not in condition_text:
        errors.append("attachment loop must skip inline/signature attachments before Get Attachment (V2)")
    for extension in (".xls", ".xlsx", ".xlsm", ".csv", ".pdf"):
        if extension not in condition_text:
            errors.append(f"attachment loop must allow supported order document extension {extension}")

    get_attachment = _find_action(loop_actions, "Get_Attachment_(V2)") or {}
    if get_attachment.get("inputs", {}).get("host", {}).get("operationId") != "GetAttachment_V2":
        errors.append("Get_Attachment_(V2) must call Outlook GetAttachment_V2")
    get_params = get_attachment.get("inputs", {}).get("parameters", {})
    if "body/id" not in str(get_params.get("messageId", "")):
        errors.append("Get_Attachment_(V2) must use the trigger email Message Id")
    if "Apply_to_each" not in str(get_params.get("attachmentId", "")):
        errors.append("Get_Attachment_(V2) must use the current Apply_to_each attachment Id")

    append = _find_action(loop_actions, "Append_to_array_variable") or {}
    append_inputs = append.get("inputs", {})
    if append_inputs.get("name") != "NormalizedAttachments":
        errors.append("attachment normalisation must append to NormalizedAttachments")
    append_value = append_inputs.get("value", {})
    if not isinstance(append_value, dict):
        errors.append("attachment normalisation must append a JSON object, not a manually concatenated JSON string")
        append_value = {}
    for key in ("id", "name", "contentType", "size", "isInline", "contentId", "contentBytes"):
        if key not in append_value:
            errors.append(f"normalised attachment is missing {key}")
    if "Get_Attachment_(V2)" not in str(append_value.get("contentBytes", "")):
        errors.append("normalised attachment contentBytes must come from Get_Attachment_(V2)")

    submit = _find_action(actions, "Intake_info_mailbox_email") or {}
    submit_params = submit.get("inputs", {}).get("parameters", {})
    missing = sorted(REQUIRED_PARAMETER_FIELDS - set(submit_params))
    if missing:
        errors.append("request evidence fields missing: " + ", ".join(missing))
    if submit_params.get("body/attachments") != "@variables('NormalizedAttachments')":
        errors.append("TMS staging request must submit the NormalizedAttachments array")
    if "bodyPreview" not in str(submit_params.get("body/bodyText", "")):
        errors.append("bodyText must come from Outlook bodyPreview")
    if "body" not in str(submit_params.get("body/bodyHtml", "")):
        errors.append("bodyHtml must contain the full Outlook body")
    body_format = str(submit_params.get("body/bodyFormat", ""))
    if "isHtml" not in body_format or "'html'" not in body_format or "'text'" not in body_format:
        errors.append("bodyFormat must convert Outlook isHtml to the API string values html/text")

    run_after = set(submit.get("runAfter", {}).get("Apply_to_each", []))
    required_states = {"Succeeded", "Failed", "TimedOut", "Skipped"}
    if not required_states.issubset(run_after):
        errors.append("TMS submission must continue after Apply_to_each success/failure/timeout/skipped")

    for node in _walk(actions):
        if not isinstance(node, dict):
            continue
        policy = node.get("runtimeConfiguration", {}).get("retryPolicy", {})
        if policy and not (
            policy.get("type") == "exponential"
            and isinstance(policy.get("count"), int)
            and 1 <= policy["count"] <= 4
        ):
            errors.append("API retry must be bounded exponential with 1-4 attempts")

    trigger_concurrency = trigger.get("runtimeConfiguration", {}).get("concurrency", {})
    if trigger_concurrency.get("runs") != 4:
        errors.append("trigger concurrency must be 4")
    if "secureData" not in serialized:
        errors.append("secure input/output protection is missing")
    if set(properties.get("connectionReferences", {})) != {"shared_office365", "shared_slhtms"}:
        errors.append("flow must use only the Outlook and existing TMS connection references")

    return errors


def main():
    path = pathlib.Path(__file__).with_name("workflow.json")
    errors = validate(json.loads(path.read_text(encoding="utf-8")))
    if errors:
        print("\n".join(f"ERROR: {item}" for item in errors))
        return 1
    print(f"Validated {path.name}: production intake contract satisfied")
    return 0


if __name__ == "__main__":
    sys.exit(main())
