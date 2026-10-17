/* Retired Driver Master rows retain historical Tacho identity evidence but must not
   block the active canonical owner of that Member Code. */
IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
BEGIN
    IF EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Drivers')
          AND name = N'IX_Drivers_TachoMasterDriverId'
    )
        DROP INDEX IX_Drivers_TachoMasterDriverId ON dbo.Drivers;

    IF NOT EXISTS (
        SELECT 1 FROM dbo.Drivers
        WHERE Active = 1 AND TachoMasterDriverId IS NOT NULL AND LTRIM(RTRIM(TachoMasterDriverId)) <> N''
        GROUP BY LTRIM(RTRIM(TachoMasterDriverId))
        HAVING COUNT(*) > 1
    )
        CREATE UNIQUE INDEX IX_Drivers_TachoMasterDriverId
            ON dbo.Drivers(TachoMasterDriverId)
            WHERE Active = 1 AND TachoMasterDriverId IS NOT NULL;
END;
