/*
   Emergency and recurring storage guard.

   Azure SQL quota failures prevent even audit writes.  Keep order/source
   evidence (StagedImports and StagedImportEvents) and master data intact;
   remove only reconstructable telemetry/status history outside the operating
   window.  Each batch is its own autocommit statement so this can make
   progress even when the database is close to its quota.
*/
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.VehicleTrackingEvents', N'U') IS NOT NULL
BEGIN
    DECLARE @trackingCutoff datetimeoffset = DATEADD(day, -35, SYSUTCDATETIME());
    WHILE 1 = 1
    BEGIN
        DELETE TOP (10000)
        FROM dbo.VehicleTrackingEvents
        WHERE EventTimeUtc < @trackingCutoff;
        IF @@ROWCOUNT = 0 BREAK;
    END;
END;

IF OBJECT_ID(N'dbo.GeofenceVisits', N'U') IS NOT NULL
BEGIN
    DECLARE @visitCutoff datetimeoffset = DATEADD(day, -365, SYSUTCDATETIME());
    WHILE 1 = 1
    BEGIN
        DELETE TOP (5000)
        FROM dbo.GeofenceVisits
        WHERE ExitedAtUtc IS NOT NULL
          AND ExitedAtUtc < @visitCutoff;
        IF @@ROWCOUNT = 0 BREAK;
    END;
END;

IF OBJECT_ID(N'dbo.DriverStatusLogs', N'U') IS NOT NULL
BEGIN
    DECLARE @statusCutoff datetimeoffset = DATEADD(day, -365, SYSUTCDATETIME());
    WHILE 1 = 1
    BEGIN
        DELETE TOP (5000)
        FROM dbo.DriverStatusLogs
        WHERE CapturedAtUtc < @statusCutoff;
        IF @@ROWCOUNT = 0 BREAK;
    END;
END;
