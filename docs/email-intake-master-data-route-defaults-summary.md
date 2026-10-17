# Summary

The recent SQL-authoritative mapping work made sender mappings identity-only and made route rules too cautious. That protected against bad assumptions, but it also meant that an order with blank collection or destination could remain blank even when Master Data had a clear sender/default route.

This change restores the practical fallback:

- email body/attachment parser first;
- sender/customer master data second;
- route-rule defaults third;
- no overwrite of parsed collection or destination;
- planner review retained for conflicts or ambiguous routes.

This is intended to restore last week's working behaviour without reverting the full intake mapping system.