-- Add RowVersion concurrency token to Loads table.
-- This enables optimistic concurrency on Load updates so that concurrent
-- planner saves do not silently overwrite each other.
IF COL_LENGTH(N'dbo.Loads', N'RowVersion') IS NULL
    ALTER TABLE dbo.Loads ADD RowVersion rowversion NOT NULL;
