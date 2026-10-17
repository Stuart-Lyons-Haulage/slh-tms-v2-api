-- Repairs legacy geofence rows so EF Core can materialise them safely and
-- RoadTech telemetry can progress live runs through geofence arrival/dwell/exit.
-- Data-preserving and idempotent: no operational history is deleted.

IF OBJECT_ID(N'dbo.SiteGeofences', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SiteGeofences (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_SiteGeofences_028 PRIMARY KEY,
        Name nvarchar(200) NOT NULL,
        NormalizedName nvarchar(200) NOT NULL,
        Category nvarchar(80) NULL,
        CategoryMaxWaitMinutes int NULL,
        MaxWaitMinutes int NULL,
        PendingEntryMinutes int NOT NULL CONSTRAINT DF_SiteGeofences_PendingEntryMinutes_028 DEFAULT(0),
        PendingExitMinutes int NOT NULL CONSTRAINT DF_SiteGeofences_PendingExitMinutes_028 DEFAULT(0),
        SiteNumber nvarchar(40) NULL,
        SiteId uniqueidentifier NULL,
        PolygonJson nvarchar(max) NOT NULL,
        Active bit NOT NULL CONSTRAINT DF_SiteGeofences_Active_028 DEFAULT(1),
        CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_SiteGeofences_CreatedAtUtc_028 DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_SiteGeofences_UpdatedAtUtc_028 DEFAULT(SYSUTCDATETIME())
    );
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.SiteGeofences', 'Name') IS NULL ALTER TABLE dbo.SiteGeofences ADD Name nvarchar(200) NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'NormalizedName') IS NULL ALTER TABLE dbo.SiteGeofences ADD NormalizedName nvarchar(200) NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'Category') IS NULL ALTER TABLE dbo.SiteGeofences ADD Category nvarchar(80) NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'CategoryMaxWaitMinutes') IS NULL ALTER TABLE dbo.SiteGeofences ADD CategoryMaxWaitMinutes int NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'MaxWaitMinutes') IS NULL ALTER TABLE dbo.SiteGeofences ADD MaxWaitMinutes int NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'PendingEntryMinutes') IS NULL ALTER TABLE dbo.SiteGeofences ADD PendingEntryMinutes int NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'PendingExitMinutes') IS NULL ALTER TABLE dbo.SiteGeofences ADD PendingExitMinutes int NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'SiteNumber') IS NULL ALTER TABLE dbo.SiteGeofences ADD SiteNumber nvarchar(40) NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'SiteId') IS NULL ALTER TABLE dbo.SiteGeofences ADD SiteId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'PolygonJson') IS NULL ALTER TABLE dbo.SiteGeofences ADD PolygonJson nvarchar(max) NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'Active') IS NULL ALTER TABLE dbo.SiteGeofences ADD Active bit NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'CreatedAtUtc') IS NULL ALTER TABLE dbo.SiteGeofences ADD CreatedAtUtc datetimeoffset NULL;
    IF COL_LENGTH('dbo.SiteGeofences', 'UpdatedAtUtc') IS NULL ALTER TABLE dbo.SiteGeofences ADD UpdatedAtUtc datetimeoffset NULL;

    UPDATE dbo.SiteGeofences
       SET Name = COALESCE(NULLIF(LTRIM(RTRIM(Name)), ''), CONCAT('Legacy geofence ', LEFT(CONVERT(varchar(36), Id), 8))),
           PolygonJson = CASE WHEN NULLIF(LTRIM(RTRIM(PolygonJson)), '') IS NULL THEN '[]' ELSE PolygonJson END,
           PendingEntryMinutes = COALESCE(PendingEntryMinutes, 0),
           PendingExitMinutes = COALESCE(PendingExitMinutes, 0),
           Active = COALESCE(Active, 1),
           CreatedAtUtc = COALESCE(CreatedAtUtc, SYSUTCDATETIME()),
           UpdatedAtUtc = COALESCE(UpdatedAtUtc, CreatedAtUtc, SYSUTCDATETIME());

    UPDATE dbo.SiteGeofences
       SET NormalizedName = COALESCE(NULLIF(LTRIM(RTRIM(NormalizedName)), ''), UPPER(LTRIM(RTRIM(Name))));
END;

IF OBJECT_ID(N'dbo.GeofenceVisits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.GeofenceVisits (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_GeofenceVisits_028 PRIMARY KEY,
        GeofenceId uniqueidentifier NOT NULL,
        LoadId uniqueidentifier NULL,
        LoadStopId uniqueidentifier NULL,
        VehicleId uniqueidentifier NULL,
        VehicleIdentifier nvarchar(80) NOT NULL,
        EnteredAtUtc datetimeoffset NOT NULL,
        ConfirmedAtUtc datetimeoffset NULL,
        ExitedAtUtc datetimeoffset NULL,
        LastInsideAtUtc datetimeoffset NOT NULL,
        DwellMinutes int NOT NULL CONSTRAINT DF_GeofenceVisits_DwellMinutes_028 DEFAULT(0),
        Status nvarchar(40) NOT NULL,
        StatusReason nvarchar(500) NULL,
        CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_GeofenceVisits_CreatedAtUtc_028 DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_GeofenceVisits_UpdatedAtUtc_028 DEFAULT(SYSUTCDATETIME())
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
    IF COL_LENGTH('dbo.GeofenceVisits', 'DwellMinutes') IS NULL ALTER TABLE dbo.GeofenceVisits ADD DwellMinutes int NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'Status') IS NULL ALTER TABLE dbo.GeofenceVisits ADD Status nvarchar(40) NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'StatusReason') IS NULL ALTER TABLE dbo.GeofenceVisits ADD StatusReason nvarchar(500) NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'CreatedAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD CreatedAtUtc datetimeoffset NULL;
    IF COL_LENGTH('dbo.GeofenceVisits', 'UpdatedAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD UpdatedAtUtc datetimeoffset NULL;

    UPDATE dbo.GeofenceVisits
       SET GeofenceId = COALESCE(GeofenceId, '00000000-0000-0000-0000-000000000000'),
           VehicleIdentifier = COALESCE(NULLIF(LTRIM(RTRIM(VehicleIdentifier)), ''), 'UNKNOWN'),
           EnteredAtUtc = COALESCE(EnteredAtUtc, CreatedAtUtc, UpdatedAtUtc, SYSUTCDATETIME()),
           LastInsideAtUtc = COALESCE(LastInsideAtUtc, EnteredAtUtc, CreatedAtUtc, UpdatedAtUtc, SYSUTCDATETIME()),
           DwellMinutes = COALESCE(DwellMinutes, 0),
           Status = COALESCE(NULLIF(LTRIM(RTRIM(Status)), ''), 'LegacyImported'),
           StatusReason = COALESCE(StatusReason, 'Legacy geofence visit repaired for runtime compatibility.'),
           CreatedAtUtc = COALESCE(CreatedAtUtc, EnteredAtUtc, SYSUTCDATETIME()),
           UpdatedAtUtc = COALESCE(UpdatedAtUtc, ExitedAtUtc, LastInsideAtUtc, EnteredAtUtc, SYSUTCDATETIME());
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SiteGeofences_NormalizedName' AND object_id = OBJECT_ID(N'dbo.SiteGeofences'))
    CREATE INDEX IX_SiteGeofences_NormalizedName ON dbo.SiteGeofences(NormalizedName);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SiteGeofences_SiteId' AND object_id = OBJECT_ID(N'dbo.SiteGeofences'))
    CREATE INDEX IX_SiteGeofences_SiteId ON dbo.SiteGeofences(SiteId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GeofenceVisits_Vehicle_Open' AND object_id = OBJECT_ID(N'dbo.GeofenceVisits'))
    CREATE INDEX IX_GeofenceVisits_Vehicle_Open ON dbo.GeofenceVisits(VehicleIdentifier, ExitedAtUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GeofenceVisits_Load_Stop' AND object_id = OBJECT_ID(N'dbo.GeofenceVisits'))
    CREATE INDEX IX_GeofenceVisits_Load_Stop ON dbo.GeofenceVisits(LoadId, LoadStopId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GeofenceVisits_Entered' AND object_id = OBJECT_ID(N'dbo.GeofenceVisits'))
    CREATE INDEX IX_GeofenceVisits_Entered ON dbo.GeofenceVisits(EnteredAtUtc);
