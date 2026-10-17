IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StagedImports_Status_EntityType_ReceivedAtUtc' AND object_id = OBJECT_ID(N'dbo.StagedImports'))
    CREATE INDEX IX_StagedImports_Status_EntityType_ReceivedAtUtc
        ON dbo.StagedImports(Status, EntityType, ReceivedAtUtc DESC);

IF OBJECT_ID(N'dbo.Loads', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Loads_PlanningDate_Status' AND object_id = OBJECT_ID(N'dbo.Loads'))
    CREATE INDEX IX_Loads_PlanningDate_Status
        ON dbo.Loads(PlanningDate, Status);

IF OBJECT_ID(N'dbo.Loads', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Loads', N'DriverId') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Loads_PlanningDate_DriverId' AND object_id = OBJECT_ID(N'dbo.Loads'))
    CREATE INDEX IX_Loads_PlanningDate_DriverId
        ON dbo.Loads(PlanningDate, DriverId);

IF OBJECT_ID(N'dbo.DriverStatusLogs', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DriverStatusLogs_LoadId_CapturedAtUtc' AND object_id = OBJECT_ID(N'dbo.DriverStatusLogs'))
    CREATE INDEX IX_DriverStatusLogs_LoadId_CapturedAtUtc
        ON dbo.DriverStatusLogs(LoadId, CapturedAtUtc DESC);

IF OBJECT_ID(N'dbo.VehicleTrackingEvents', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_VehicleTrackingEvents_VehicleIdentifier_EventTimeUtc' AND object_id = OBJECT_ID(N'dbo.VehicleTrackingEvents'))
    CREATE INDEX IX_VehicleTrackingEvents_VehicleIdentifier_EventTimeUtc
        ON dbo.VehicleTrackingEvents(VehicleIdentifier, EventTimeUtc DESC);
