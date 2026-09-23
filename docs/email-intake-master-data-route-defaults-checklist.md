# Deployment checklist

After this change reaches production:

1. Re-run the Info mailbox intake for the affected date range.
2. Open Load Review and check several staged orders from different senders.
3. Confirm collection and destination are present where either the email body or SQL master data can identify them.
4. Confirm inline signatures/images remain hidden from the planner document list.
5. Confirm genuine PDF/Excel/CSV order documents still appear as order documents.
6. Approve one corrected order and confirm the sender/customer mapping remains in Master Data.
7. Add or adjust Order Intake Route Rules only when a sender/customer has repeated, reliable collection and destination patterns.

Do not use broad sender defaults to override email text. If the email gives a collection or destination, the parsed email value wins.