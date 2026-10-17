using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

public static class GeofenceRuntimeRepair
{
    public static async Task EnsureAsync(TmsDbContext db, CancellationToken ct)
    {
        const string sql = """
IF OBJECT_ID(N'dbo.Sites', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('dbo.Sites','ExternalCode') IS NULL ALTER TABLE dbo.Sites ADD ExternalCode nvarchar(40) NULL;
    IF COL_LENGTH('dbo.Sites','Name') IS NULL ALTER TABLE dbo.Sites ADD Name nvarchar(200) NULL;
    IF COL_LENGTH('dbo.Sites','DriverTextName') IS NULL ALTER TABLE dbo.Sites ADD DriverTextName nvarchar(200) NULL;
    IF COL_LENGTH('dbo.Sites','Active') IS NULL ALTER TABLE dbo.Sites ADD Active bit NULL;

    UPDATE dbo.Sites SET Active = COALESCE(Active, 1);
    UPDATE dbo.Sites
       SET Active = 0
     WHERE NULLIF(LTRIM(RTRIM(Name)), '') IS NULL;
    UPDATE dbo.Sites
       SET ExternalCode = CONCAT('LEGACY-', LEFT(CONVERT(varchar(36), Id), 8))
     WHERE NULLIF(LTRIM(RTRIM(ExternalCode)), '') IS NULL;
END;

IF OBJECT_ID(N'dbo.SiteGeofences', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SiteGeofences (
        Id uniqueidentifier NOT NULL PRIMARY KEY,
        Name nvarchar(200) NOT NULL,
        NormalizedName nvarchar(200) NOT NULL,
        Category nvarchar(80) NULL,
        CategoryMaxWaitMinutes int NULL,
        MaxWaitMinutes int NULL,
        PendingEntryMinutes int NOT NULL DEFAULT(0),
        PendingExitMinutes int NOT NULL DEFAULT(0),
        SiteNumber nvarchar(40) NULL,
        SiteId uniqueidentifier NULL,
        PolygonJson nvarchar(max) NOT NULL,
        Active bit NOT NULL DEFAULT(1),
        CreatedAtUtc datetimeoffset NOT NULL DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetimeoffset NOT NULL DEFAULT(SYSUTCDATETIME())
    );
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.SiteGeofences','Name') IS NULL ALTER TABLE dbo.SiteGeofences ADD Name nvarchar(200) NULL;
    IF COL_LENGTH('dbo.SiteGeofences','NormalizedName') IS NULL ALTER TABLE dbo.SiteGeofences ADD NormalizedName nvarchar(200) NULL;
    IF COL_LENGTH('dbo.SiteGeofences','Category') IS NULL ALTER TABLE dbo.SiteGeofences ADD Category nvarchar(80) NULL;
    IF COL_LENGTH('dbo.SiteGeofences','CategoryMaxWaitMinutes') IS NULL ALTER TABLE dbo.SiteGeofences ADD CategoryMaxWaitMinutes int NULL;
    IF COL_LENGTH('dbo.SiteGeofences','MaxWaitMinutes') IS NULL ALTER TABLE dbo.SiteGeofences ADD MaxWaitMinutes int NULL;
    IF COL_LENGTH('dbo.SiteGeofences','PendingEntryMinutes') IS NULL ALTER TABLE dbo.SiteGeofences ADD PendingEntryMinutes int NULL;
    IF COL_LENGTH('dbo.SiteGeofences','PendingExitMinutes') IS NULL ALTER TABLE dbo.SiteGeofences ADD PendingExitMinutes int NULL;
    IF COL_LENGTH('dbo.SiteGeofences','SiteNumber') IS NULL ALTER TABLE dbo.SiteGeofences ADD SiteNumber nvarchar(40) NULL;
    IF COL_LENGTH('dbo.SiteGeofences','SiteId') IS NULL ALTER TABLE dbo.SiteGeofences ADD SiteId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.SiteGeofences','PolygonJson') IS NULL ALTER TABLE dbo.SiteGeofences ADD PolygonJson nvarchar(max) NULL;
    IF COL_LENGTH('dbo.SiteGeofences','Active') IS NULL ALTER TABLE dbo.SiteGeofences ADD Active bit NULL;
    IF COL_LENGTH('dbo.SiteGeofences','CreatedAtUtc') IS NULL ALTER TABLE dbo.SiteGeofences ADD CreatedAtUtc datetimeoffset NULL;
    IF COL_LENGTH('dbo.SiteGeofences','UpdatedAtUtc') IS NULL ALTER TABLE dbo.SiteGeofences ADD UpdatedAtUtc datetimeoffset NULL;
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
        Id uniqueidentifier NOT NULL PRIMARY KEY,
        GeofenceId uniqueidentifier NOT NULL,
        LoadId uniqueidentifier NULL,
        LoadStopId uniqueidentifier NULL,
        VehicleId uniqueidentifier NULL,
        VehicleIdentifier nvarchar(80) NOT NULL,
        EnteredAtUtc datetimeoffset NOT NULL,
        ConfirmedAtUtc datetimeoffset NULL,
        ExitedAtUtc datetimeoffset NULL,
        LastInsideAtUtc datetimeoffset NOT NULL,
        DwellMinutes int NOT NULL DEFAULT(0),
        Status nvarchar(40) NOT NULL,
        StatusReason nvarchar(500) NULL,
        CreatedAtUtc datetimeoffset NOT NULL DEFAULT(SYSUTCDATETIME()),
        UpdatedAtUtc datetimeoffset NOT NULL DEFAULT(SYSUTCDATETIME())
    );
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.GeofenceVisits','GeofenceId') IS NULL ALTER TABLE dbo.GeofenceVisits ADD GeofenceId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','LoadId') IS NULL ALTER TABLE dbo.GeofenceVisits ADD LoadId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','LoadStopId') IS NULL ALTER TABLE dbo.GeofenceVisits ADD LoadStopId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','VehicleId') IS NULL ALTER TABLE dbo.GeofenceVisits ADD VehicleId uniqueidentifier NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','VehicleIdentifier') IS NULL ALTER TABLE dbo.GeofenceVisits ADD VehicleIdentifier nvarchar(80) NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','EnteredAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD EnteredAtUtc datetimeoffset NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','ConfirmedAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD ConfirmedAtUtc datetimeoffset NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','ExitedAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD ExitedAtUtc datetimeoffset NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','LastInsideAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD LastInsideAtUtc datetimeoffset NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','DwellMinutes') IS NULL ALTER TABLE dbo.GeofenceVisits ADD DwellMinutes int NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','Status') IS NULL ALTER TABLE dbo.GeofenceVisits ADD Status nvarchar(40) NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','StatusReason') IS NULL ALTER TABLE dbo.GeofenceVisits ADD StatusReason nvarchar(500) NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','CreatedAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD CreatedAtUtc datetimeoffset NULL;
    IF COL_LENGTH('dbo.GeofenceVisits','UpdatedAtUtc') IS NULL ALTER TABLE dbo.GeofenceVisits ADD UpdatedAtUtc datetimeoffset NULL;
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
""";
        await db.Database.ExecuteSqlRawAsync(sql, ct);
        db.ChangeTracker.Clear();
    }
}
