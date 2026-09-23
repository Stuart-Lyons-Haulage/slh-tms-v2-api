-- Repairs schema dependencies used by Management, Needs Attention, Morning Readiness and Plan Stability.
-- Safe for existing production databases: tables/columns/indexes are only created when absent.

IF OBJECT_ID(N'dbo.PlanBaselines', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PlanBaselines (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PlanBaselines PRIMARY KEY,
        PlanningDate date NOT NULL,
        LockedAtUtc datetimeoffset NOT NULL,
        LockedBy nvarchar(200) NULL,
        SnapshotJson nvarchar(max) NOT NULL
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PlanBaselines_PlanningDate' AND object_id = OBJECT_ID(N'dbo.PlanBaselines'))
    CREATE UNIQUE INDEX IX_PlanBaselines_PlanningDate ON dbo.PlanBaselines(PlanningDate);

IF OBJECT_ID(N'dbo.PlanChangeEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PlanChangeEvents (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PlanChangeEvents PRIMARY KEY,
        PlanningDate date NOT NULL,
        LoadId uniqueidentifier NULL,
        ChangeType nvarchar(80) NOT NULL,
        Reason nvarchar(1000) NOT NULL,
        ChangedBy nvarchar(200) NULL,
        ChangedAtUtc datetimeoffset NOT NULL,
        BeforeJson nvarchar(max) NULL,
        AfterJson nvarchar(max) NULL
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PlanChangeEvents_PlanningDate' AND object_id = OBJECT_ID(N'dbo.PlanChangeEvents'))
    CREATE INDEX IX_PlanChangeEvents_PlanningDate ON dbo.PlanChangeEvents(PlanningDate, ChangedAtUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PlanChangeEvents_LoadId' AND object_id = OBJECT_ID(N'dbo.PlanChangeEvents'))
    CREATE INDEX IX_PlanChangeEvents_LoadId ON dbo.PlanChangeEvents(LoadId);

IF OBJECT_ID(N'dbo.SiteGeofences', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SiteGeofences (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_SiteGeofences PRIMARY KEY,
        Name nvarchar(200) NOT NULL,
        NormalizedName nvarchar(200) NOT NULL,
        Category nvarchar(80) NULL,
        CategoryMaxWaitMinutes int NULL,
        MaxWaitMinutes int NULL,
        PendingEntryMinutes int NOT NULL CONSTRAINT DF_SiteGeofences_PendingEntryMinutes_026 DEFAULT(0),
        PendingExitMinutes int NOT NULL CONSTRAINT DF_SiteGeofences_PendingExitMinutes_026 DEFAULT(0),
        SiteNumber nvarchar(40) NULL,
        SiteId uniqueidentifier NULL,
        PolygonJson nvarchar(max) NOT NULL,
        Active bit NOT NULL CONSTRAINT DF_SiteGeofences_Active_026 DEFAULT(1),
        CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_SiteGeofences_CreatedAtUtc_026 DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_SiteGeofences_UpdatedAtUtc_026 DEFAULT(SYSUTCDATETIME())
    );
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.SiteGeofences', 'Name') IS NULL ALTER TABLE dbo.SiteGeofences ADD Name nvarchar(200) NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'NormalizedName') IS NULL ALTER TABLE dbo.SiteGeofences ADD NormalizedName nvarchar(200) NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'Category') IS NULL ALTER TABLE dbo.SiteGeofences ADD Category nvarchar(80) NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'CategoryMaxWaitMinutes') IS NULL ALTER TABLE dbo.SiteGeofences ADD CategoryMaxWaitMinutes int NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'MaxWaitMinutes') IS NULL ALTER TABLE dbo.SiteGeofences ADD MaxWaitMinutes int NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'PendingEntryMinutes') IS NULL ALTER TABLE dbo.SiteGeofences ADD PendingEntryMinutes int NOT NULL CONSTRAINT DF_SiteGeofences_PendingEntryMinutes_Repair DEFAULT(0);
    IF COL_LENGTH('dbo.SiteGeofences', 'PendingExitMinutes') IS NULL ALTER TABLE dbo.SiteGeofences ADD PendingExitMinutes int NOT NULL CONSTRAINT DF_SiteGeofences_PendingExitMinutes_Repair DEFAULT(0);
    IF COL_LENGTH('dbo.SiteGeofences', 'SiteNumber') IS NULL ALTER TABLE dbo.SiteGeofences ADD SiteNumber nvarchar(40) NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'SiteId') IS NULL ALTER TABLE dbo.SiteGeofences ADD SiteId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'PolygonJson') IS NULL ALTER TABLE dbo.SiteGeofences ADD PolygonJson nvarchar(max) NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'Active') IS NULL ALTER TABLE dbo.SiteGeofences ADD Active bit NOT NULL CONSTRAINT DF_SiteGeofences_Active_Repair DEFAULT(1);
    IF COL_LENGTH('dbo.SiteGeofences', 'CreatedAtUtc') IS NULL ALTER TABLE dbo.SiteGeofences ADD CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_SiteGeofences_CreatedAtUtc_Repair DEFAULT(SYSUTCDATETIME());
    IF COL_LENGTH('dbo.SiteGeofences', 'UpdatedAtUtc') IS NULL ALTER TABLE dbo.SiteGeofences ADD UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_SiteGeofences_UpdatedAtUtc_Repair DEFAULT(SYSUTCDATETIME());
END;
IF COL_LENGTH('dbo.SiteGeofences', 'NormalizedName') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SiteGeofences_NormalizedName' AND object_id = OBJECT_ID(N'dbo.SiteGeofences'))
    CREATE INDEX IX_SiteGeofences_NormalizedName ON dbo.SiteGeofences(NormalizedName);
IF COL_LENGTH('dbo.SiteGeofences', 'SiteId') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SiteGeofences_SiteId' AND object_id = OBJECT_ID(N'dbo.SiteGeofences'))
    CREATE INDEX IX_SiteGeofences_SiteId ON dbo.SiteGeofences(SiteId);

IF OBJECT_ID(N'dbo.GeofenceVisits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.GeofenceVisits (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_GeofenceVisits PRIMARY KEY,
        GeofenceId uniqueidentifier NOT NULL,
        LoadId uniqueidentifier NULL,
        LoadStopId uniqueidentifier NULL,
        VehicleId uniqueidentifier NULL,
        VehicleIdentifier nvarchar(80) NOT NULL,
        EnteredAtUtc datetimeoffset NOT NULL,
        ConfirmedAtUtc datetimeoffset NULL,
        ExitedAtUtc datetimeoffset NULL,
        LastInsideAtUtc datetimeoffset NOT NULL,
        DwellMinutes int NOT NULL CONSTRAINT DF_GeofenceVisits_DwellMinutes_026 DEFAULT(0),
        Status nvarchar(40) NOT NULL,
        StatusReason nvarchar(500) NULL,
        CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_GeofenceVisits_CreatedAtUtc_026 DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_GeofenceVisits_UpdatedAtUtc_026 DEFAULT(SYSUTCDATETIME())
    );
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.GeofenceVisits', 'GeofenceId') IS NULL ALTER TABLE dbo.GeofenceVisits ADD GeofenceId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'LoadId') IS NULL ALTER TABLE dbo.GeofenceVisits ADD LoadId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'LoadStopId') IS NULL ALTER TABLE dbo.GeofenceVisits ADD LoadStopId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'VehicleId') IS NULL ALTER TABLE dbo.GeofenceVisits ADD VehicleId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'VehicleIdentifier') IS NULL ALTER TABLE dbo.GeofenceVisits ADD VehicleIdentifier nvarchar(80) NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'EnteredAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD EnteredAtUtc datetimeoffset NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'ConfirmedAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD ConfirmedAtUtc datetimeoffset NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'ExitedAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD ExitedAtUtc datetimeoffset NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'LastInsideAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD LastInsideAtUtc datetimeoffset NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'DwellMinutes') IS NULL ALTER TABLE dbo.GeofenceVisits ADD DwellMinutes int NOT NULL CONSTRAINT DF_GeofenceVisits_DwellMinutes_Repair DEFAULT(0);
    IF COL_LENGTH('dbo.GeofenceVisits', 'Status') IS NULL ALTER TABLE dbo.GeofenceVisits ADD Status nvarchar(40) NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'StatusReason') IS NULL ALTER TABLE dbo.GeofenceVisits ADD StatusReason nvarchar(500) NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'CreatedAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_GeofenceVisits_CreatedAtUtc_Repair DEFAULT(SYSUTCDATETIME());
    IF COL_LENGTH('dbo.GeofenceVisits', 'UpdatedAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_GeofenceVisits_UpdatedAtUtc_Repair DEFAULT(SYSUTCDATETIME());
END;
IF COL_LENGTH('dbo.GeofenceVisits', 'VehicleIdentifier') IS NOT NULL AND COL_LENGTH('dbo.GeofenceVisits', 'ExitedAtUtc') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GeofenceVisits_Vehicle_Open' AND object_id = OBJECT_ID(N'dbo.GeofenceVisits'))
    CREATE INDEX IX_GeofenceVisits_Vehicle_Open ON dbo.GeofenceVisits(VehicleIdentifier, ExitedAtUtc);
IF COL_LENGTH('dbo.GeofenceVisits', 'LoadId') IS NOT NULL AND COL_LENGTH('dbo.GeofenceVisits', 'LoadStopId') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GeofenceVisits_Load_Stop' AND object_id = OBJECT_ID(N'dbo.GeofenceVisits'))
    CREATE INDEX IX_GeofenceVisits_Load_Stop ON dbo.GeofenceVisits(LoadId, LoadStopId);
IF COL_LENGTH('dbo.GeofenceVisits', 'EnteredAtUtc') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GeofenceVisits_Entered' AND object_id = OBJECT_ID(N'dbo.GeofenceVisits'))
    CREATE INDEX IX_GeofenceVisits_Entered ON dbo.GeofenceVisits(EnteredAtUtc);

IF OBJECT_ID(N'dbo.DriverStatusLogs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DriverStatusLogs (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_DriverStatusLogs PRIMARY KEY,
        LoadId uniqueidentifier NOT NULL,
        DriverId uniqueidentifier NULL,
        Status nvarchar(40) NOT NULL,
        Notes nvarchar(1000) NULL,
        CapturedBy nvarchar(200) NULL,
        CapturedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_DriverStatusLogs_CapturedAtUtc_026 DEFAULT(SYSUTCDATETIME())
    );
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.DriverStatusLogs', 'LoadId') IS NULL ALTER TABLE dbo.DriverStatusLogs ADD LoadId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.DriverStatusLogs', 'DriverId') IS NULL ALTER TABLE dbo.DriverStatusLogs ADD DriverId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.DriverStatusLogs', 'Status') IS NULL ALTER TABLE dbo.DriverStatusLogs ADD Status nvarchar(40) NULL;
    IF COL_LENGTH('dbo.DriverStatusLogs', 'Notes') IS NULL ALTER TABLE dbo.DriverStatusLogs ADD Notes nvarchar(1000) NULL;
    IF COL_LENGTH('dbo.DriverStatusLogs', 'CapturedBy') IS NULL ALTER TABLE dbo.DriverStatusLogs ADD CapturedBy nvarchar(200) NULL;
    IF COL_LENGTH('dbo.DriverStatusLogs', 'CapturedAtUtc') IS NULL ALTER TABLE dbo.DriverStatusLogs ADD CapturedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_DriverStatusLogs_CapturedAtUtc_Repair DEFAULT(SYSUTCDATETIME());
END;
IF COL_LENGTH('dbo.DriverStatusLogs', 'LoadId') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DriverStatusLogs_LoadId' AND object_id = OBJECT_ID(N'dbo.DriverStatusLogs'))
    CREATE INDEX IX_DriverStatusLogs_LoadId ON dbo.DriverStatusLogs(LoadId);
IF COL_LENGTH('dbo.DriverStatusLogs', 'CapturedAtUtc') IS NOT NULL
AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DriverStatusLogs_CapturedAtUtc' AND object_id = OBJECT_ID(N'dbo.DriverStatusLogs'))
    CREATE INDEX IX_DriverStatusLogs_CapturedAtUtc ON dbo.DriverStatusLogs(CapturedAtUtc);
