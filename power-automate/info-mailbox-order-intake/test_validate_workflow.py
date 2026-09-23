import json
import pathlib
import unittest

from validate_workflow import validate


ROOT = pathlib.Path(__file__).parent


def load_workflow():
    return json.loads((ROOT / "workflow.json").read_text(encoding="utf-8"))


def supported_attachment_actions(workflow):
    foreach_actions = workflow["properties"]["definition"]["actions"]["Apply_to_each"]["actions"]
    return foreach_actions["If_supported_order_attachment"]["actions"]


class WorkflowValidationTests(unittest.TestCase):
    def test_production_workflow_contract(self):
        self.assertEqual([], validate(load_workflow()))

    def test_rejects_live_order_endpoint_and_unbounded_retry(self):
        unsafe = {
            "properties": {
                "definition": {
                    "triggers": {},
                    "actions": {
                        "POST_Live_Order": {
                            "type": "Http",
                            "inputs": {"uri": "https://example/api/v1/orders"},
                            "runtimeConfiguration": {"retryPolicy": {"type": "until-success"}},
                        }
                    },
                }
            }
        }
        errors = validate(unsafe)
        self.assertTrue(any("live-order" in item for item in errors))
        self.assertTrue(any("bounded exponential" in item for item in errors))

    def test_rejects_microsoft_list_or_sharepoint_storage(self):
        workflow = load_workflow()
        workflow["properties"]["connectionReferences"]["shared_sharepoint"] = {}
        errors = validate(workflow)
        self.assertTrue(any("Lists/SharePoint" in item for item in errors))

    def test_rejects_attachment_presence_trigger_filter(self):
        workflow = load_workflow()
        trigger = workflow["properties"]["definition"]["triggers"]["When_New_Email_Arrives_Info_Shared_Mailbox"]
        trigger["inputs"]["parameters"]["hasAttachments"] = True
        errors = validate(workflow)
        self.assertTrue(any("attachment presence" in item for item in errors))

    def test_rejects_any_trigger_condition(self):
        workflow = load_workflow()
        trigger = workflow["properties"]["definition"]["triggers"]["When_New_Email_Arrives_Info_Shared_Mailbox"]
        trigger["conditions"] = [{"expression": "@contains(triggerOutputs()?['body/subject'],'order')"}]
        errors = validate(workflow)
        self.assertTrue(any("must not filter sender, subject or order type" in item for item in errors))

    def test_rejects_get_attachments_v2_requirement(self):
        workflow = load_workflow()
        workflow["properties"]["definition"]["actions"]["Get_Attachment_List"] = {
            "type": "OpenApiConnection",
            "inputs": {"host": {"operationId": "GetAttachments_V2"}},
        }
        errors = validate(workflow)
        self.assertTrue(any("trigger attachment collection" in item for item in errors))

    def test_rejects_loop_not_using_trigger_attachments(self):
        workflow = load_workflow()
        workflow["properties"]["definition"]["actions"]["Apply_to_each"]["foreach"] = "@json('[]')"
        errors = validate(workflow)
        self.assertTrue(any("trigger body/attachments collection" in item for item in errors))

    def test_rejects_missing_attachment_content_fetch(self):
        workflow = load_workflow()
        actions = supported_attachment_actions(workflow)
        actions["Get_Attachment_(V2)"]["inputs"]["host"]["operationId"] = "GetAttachments_V2"
        errors = validate(workflow)
        self.assertTrue(any("GetAttachment_V2" in item for item in errors))

    def test_rejects_manual_json_string_attachment_payload(self):
        workflow = load_workflow()
        actions = supported_attachment_actions(workflow)
        actions["Append_to_array_variable"]["inputs"]["value"] = "@json(concat('{...}'))"
        errors = validate(workflow)
        self.assertTrue(any("JSON object" in item for item in errors))

    def test_rejects_attachment_without_content_bytes(self):
        workflow = load_workflow()
        actions = supported_attachment_actions(workflow)
        del actions["Append_to_array_variable"]["inputs"]["value"]["contentBytes"]
        errors = validate(workflow)
        self.assertTrue(any("missing contentBytes" in item for item in errors))

    def test_rejects_dropping_email_when_attachment_loop_fails(self):
        workflow = load_workflow()
        submit = workflow["properties"]["definition"]["actions"]["Intake_info_mailbox_email"]
        submit["runAfter"] = {"Apply_to_each": ["Succeeded"]}
        errors = validate(workflow)
        self.assertTrue(any("success/failure/timeout/skipped" in item for item in errors))

    def test_rejects_staging_request_without_attachment_array(self):
        workflow = load_workflow()
        params = workflow["properties"]["definition"]["actions"]["Intake_info_mailbox_email"]["inputs"]["parameters"]
        params["body/attachments"] = []
        errors = validate(workflow)
        self.assertTrue(any("NormalizedAttachments array" in item for item in errors))

    def test_rejects_wrong_body_mapping(self):
        workflow = load_workflow()
        params = workflow["properties"]["definition"]["actions"]["Intake_info_mailbox_email"]["inputs"]["parameters"]
        params["body/bodyText"] = "@triggerBody()?['body']"
        params["body/bodyFormat"] = "@triggerBody()?['isHtml']"
        errors = validate(workflow)
        self.assertTrue(any("bodyText must come from Outlook bodyPreview" in item for item in errors))
        self.assertTrue(any("bodyFormat must convert Outlook isHtml" in item for item in errors))


if __name__ == "__main__":
    unittest.main()
