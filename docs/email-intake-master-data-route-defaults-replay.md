# Replay note

This change affects newly parsed staged orders. Existing staged records may already have missing or incorrect payload values.

To repair live data after deployment:

1. Replay the affected Info mailbox messages for the required date range.
2. Keep idempotency checks active so exact duplicates remain suppressed.
3. Review staged amendments where the parser now finds collection or delivery values that were missing before.
4. Do not manually bulk-approve until collection, destination, date and quantity are visible in Load Review.