/* Pallet Control performance.

   The pallet board read (PalletPlanningControlController.ReadOrderDetails) matched
   staged order records with PayloadJson LIKE '%yyyy-MM-dd%' across a 45-day window.
   That is a leading-wildcard full-text scan that reads the full JSON (email bodies
   included) of every order record in the window on every refresh, and it had grown
   into the ~40-second pallet load that makes the whole site feel slow.

   Add a plain, indexed CollectionDate column so the read is an exact index seek.
   - The column is populated on write when an order is promoted (StagingService).
   - This migration backfills recent existing rows from the payload (bounded window).
   Additive, idempotent and non-destructive. JSON_VALUE returns NULL for a payload with
   no collectionDate, and TRY_CONVERT(..., 23) yields NULL for anything that is not an
   ISO yyyy-MM-dd date, so no existing row can fail the backfill. */

IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.StagedImports', N'CollectionDate') IS NULL
BEGIN
    ALTER TABLE dbo.StagedImports ADD CollectionDate date NULL;
END;
GO

IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.StagedImports', N'CollectionDate') IS NOT NULL
BEGIN
    UPDATE dbo.StagedImports
       SET CollectionDate = TRY_CONVERT(date, LEFT(JSON_VALUE(PayloadJson, '$.collectionDate'), 10), 23)
     WHERE CollectionDate IS NULL
       AND (EntityType = N'order' OR EntityType = N'register:order')
       AND ReceivedAtUtc >= DATEADD(day, -120, SYSDATETIMEOFFSET())
       AND JSON_VALUE(PayloadJson, '$.collectionDate') IS NOT NULL;
END;
GO

IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.StagedImports', N'CollectionDate') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes
       WHERE object_id = OBJECT_ID(N'dbo.StagedImports')
         AND name = N'IX_StagedImports_EntityType_CollectionDate'
   )
BEGIN
    CREATE INDEX IX_StagedImports_EntityType_CollectionDate
        ON dbo.StagedImports(EntityType, CollectionDate)
        INCLUDE (Status);
END;
GO
