/* Clean V2 canonical identity guardrails.
   Each unique index is only promoted where the current data already satisfies it.
   Fresh V2 databases will therefore enforce these rules immediately, while a dirty
   legacy database is never modified destructively merely by applying this migration. */

IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM dbo.Drivers
       WHERE TachoMasterDriverId IS NOT NULL AND LTRIM(RTRIM(TachoMasterDriverId)) <> N''
       GROUP BY LTRIM(RTRIM(TachoMasterDriverId))
       HAVING COUNT(*) > 1
   )
BEGIN
    IF EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Drivers')
          AND name = N'IX_Drivers_TachoMasterDriverId'
    )
        DROP INDEX IX_Drivers_TachoMasterDriverId ON dbo.Drivers;

    CREATE UNIQUE INDEX IX_Drivers_TachoMasterDriverId
        ON dbo.Drivers(TachoMasterDriverId)
        WHERE TachoMasterDriverId IS NOT NULL;
END;

IF OBJECT_ID(N'dbo.Vehicles', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Vehicles', N'NormalizedRegistration') IS NULL
        ALTER TABLE dbo.Vehicles ADD NormalizedRegistration AS
            UPPER(REPLACE(REPLACE(LTRIM(RTRIM(Registration)), N' ', N''), N'-', N'')) PERSISTED;

    IF NOT EXISTS (
        SELECT 1 FROM dbo.Vehicles
        GROUP BY NormalizedRegistration
        HAVING COUNT(*) > 1
    )
       AND NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'dbo.Vehicles')
          AND name = N'UX_Vehicles_NormalizedRegistration'
       )
        CREATE UNIQUE INDEX UX_Vehicles_NormalizedRegistration
            ON dbo.Vehicles(NormalizedRegistration);

    IF COL_LENGTH(N'dbo.Vehicles', N'FleetioId') IS NOT NULL
       AND NOT EXISTS (
           SELECT 1 FROM dbo.Vehicles
           WHERE FleetioId IS NOT NULL AND LTRIM(RTRIM(FleetioId)) <> N''
           GROUP BY LTRIM(RTRIM(FleetioId))
           HAVING COUNT(*) > 1
       )
       AND NOT EXISTS (
           SELECT 1 FROM sys.indexes
           WHERE object_id = OBJECT_ID(N'dbo.Vehicles')
             AND name = N'UX_Vehicles_FleetioId'
       )
        CREATE UNIQUE INDEX UX_Vehicles_FleetioId
            ON dbo.Vehicles(FleetioId)
            WHERE FleetioId IS NOT NULL;
END;
