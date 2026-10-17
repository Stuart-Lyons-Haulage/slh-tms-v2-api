/*
   Follow-up repair for partially applied driver Tacho identity storage.
   Each column is guarded independently so a partially applied deployment can
   be completed safely without repeating any existing DDL.
*/
IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Drivers', N'TachoMasterDriverId') IS NULL
        ALTER TABLE dbo.Drivers ADD TachoMasterDriverId nvarchar(80) NULL;

    IF COL_LENGTH(N'dbo.Drivers', N'TachoCardNumber') IS NULL
        ALTER TABLE dbo.Drivers ADD TachoCardNumber nvarchar(80) NULL;

    IF COL_LENGTH(N'dbo.Drivers', N'LastTachoSyncUtc') IS NULL
        ALTER TABLE dbo.Drivers ADD LastTachoSyncUtc datetimeoffset(7) NULL;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'IX_Drivers_TachoMasterDriverId'
          AND object_id = OBJECT_ID(N'dbo.Drivers')
    )
        CREATE INDEX IX_Drivers_TachoMasterDriverId
            ON dbo.Drivers(TachoMasterDriverId)
            WHERE TachoMasterDriverId IS NOT NULL;
END;
