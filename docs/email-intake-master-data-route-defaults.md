# Email intake master-data route defaults

This restores the intended behaviour for order intake:

- The email parser remains the first source for collection and delivery sites.
- Approved SQL master-data sender mappings can fill missing collection and delivery defaults.
- Approved SQL route rules can fill missing route fields when there is a single unambiguous rule for the customer.
- Parsed email values are never overwritten by sender mappings or route rules.
- Conflicting sender mappings still force planner review.
- Ambiguous route rules still force planner review and do not fill a guessed route.

This keeps Master Data useful as the growing sender/collection/destination list while avoiding the recent behaviour where missing email fields stayed blank even when SQL already had a safe default.