import json
import pathlib
import unittest

from validate_workflow import validate


ROOT = pathlib.Path(__file__).parent


class CustomerLoadPlanWorkflowTests(unittest.TestCase):
    def test_production_workflow_contract(self):
        workflow = json.loads((ROOT / "workflow.json").read_text(encoding="utf-8"))
        self.assertEqual([], validate(workflow))

    def test_rejects_missing_shared_mailbox_send(self):
        workflow = json.loads((ROOT / "workflow.json").read_text(encoding="utf-8"))
        workflow["properties"]["definition"]["actions"]["For_Each_Queued_Load_Plan"]["actions"].pop("Send_From_Info_Mailbox")
        errors = validate(workflow)
        self.assertTrue(any("shared Info mailbox send" in error for error in errors))

    def test_rejects_parallel_customer_sends(self):
        workflow = json.loads((ROOT / "workflow.json").read_text(encoding="utf-8"))
        workflow["properties"]["definition"]["actions"]["For_Each_Queued_Load_Plan"]["runtimeConfiguration"]["concurrency"]["repetitions"] = 4
        errors = validate(workflow)
        self.assertTrue(any("serial" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
