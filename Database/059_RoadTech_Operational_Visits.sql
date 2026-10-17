/*
   RoadTech is the authority for breadcrumb/history data.  The TMS keeps only
   the current vehicle state plus durable operational geofence facts.  These
   additive links let the facts update the canonical planner RunStop directly.
*/
IF OBJECT_ID(N'dbo.GeofenceVisits', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.GeofenceVisits', N'RunId') IS NULL
        ALTER TABLE dbo.GeofenceVisits ADD RunId uniqueidentifier NULL;
    IF COL_LENGTH(N'dbo.GeofenceVisits', N'RunStopId') IS NULL
        ALTER TABLE dbo.GeofenceVisits ADD RunStopId uniqueidentifier NULL;
    IF COL_LENGTH(N'dbo.GeofenceVisits', N'SiteId') IS NULL
        ALTER TABLE dbo.GeofenceVisits ADD SiteId uniqueidentifier NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GeofenceVisits_Run_Stop' AND object_id = OBJECT_ID(N'dbo.GeofenceVisits'))
        CREATE INDEX IX_GeofenceVisits_Run_Stop ON dbo.GeofenceVisits(RunId, RunStopId);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GeofenceVisits_Vehicle_Geofence_Entered' AND object_id = OBJECT_ID(N'dbo.GeofenceVisits'))
        CREATE INDEX IX_GeofenceVisits_Vehicle_Geofence_Entered ON dbo.GeofenceVisits(VehicleIdentifier, GeofenceId, EnteredAtUtc DESC);
END;
