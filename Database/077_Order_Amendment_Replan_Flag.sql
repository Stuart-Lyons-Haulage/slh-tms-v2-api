/*
   Additive schema change for approved amendments to already planned orders.
   The order remains linked to its existing run, but the planner is explicitly
   told that the current run must be checked/replanned.
*/
IF OBJECT_ID(N'dbo.TransportOrders', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.TransportOrders', N'NeedsReplan') IS NULL
BEGIN
    ALTER TABLE dbo.TransportOrders
        ADD NeedsReplan bit NOT NULL
            CONSTRAINT DF_TransportOrders_NeedsReplan DEFAULT (0);
END;
