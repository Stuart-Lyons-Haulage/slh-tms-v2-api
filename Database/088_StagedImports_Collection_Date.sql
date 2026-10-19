/* Pallet Control performance.

   The pallet board read (PalletPlanningControlController.ReadOrderDetails) matched
   staged order records with PayloadJson LIKE '%yyyy-MM-dd%' across a 45-day window.
   That is a leading-wildcard full-text scan that reads the full JSON (email bodies
   included) of every order record in the window on every refresh, and it had grown
   into the ~40-second pallet load that makes the whole site feel slow.

   Add a PERSISTED computed CollectionDate parsed from the payload and index it, so the
   read becomes an exact (EntityType, CollectionDate) index seek instead of a JSON scan.

   Additive, idempotent and non-destructive:
   - JSON_VALUE is deterministic and returns NULL for a payload with no collectionDate.
   - TRY_CONVERT(date, ..., 23) returns NULL for anything that is not an ISO yyyy-MM-dd
     date, so no existing row can fail the computation.
   Both are deterministic and precise, which is what SQL Server requires to PERSIST and
   index a computed column. The expression matches the EF mapping in TmsDbContext. */

IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.StagedImports', N'CollectionDate') IS NULL
BEGIN
    ALTER TABLE dbo.StagedImports ADD CollectionDate AS
        TRY_CONVERT(date, LEFT(JSON_VALUE(PayloadJson, '$.collectionDate'), 10), 23) PERSISTED;
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
