/* Persist Fleetio identity and service state for trailers. */
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Trailers', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Trailers', N'FleetioId') IS NULL
        ALTER TABLE dbo.Trailers ADD FleetioId nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Trailers', N'FleetioName') IS NULL
        ALTER TABLE dbo.Trailers ADD FleetioName nvarchar(160) NULL;
    IF COL_LENGTH(N'dbo.Trailers', N'FleetioStatus') IS NULL
        ALTER TABLE dbo.Trailers ADD FleetioStatus nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Trailers', N'FleetioVor') IS NULL
        ALTER TABLE dbo.Trailers ADD FleetioVor bit NULL;
    IF COL_LENGTH(N'dbo.Trailers', N'FleetioServiceStatus') IS NULL
        ALTER TABLE dbo.Trailers ADD FleetioServiceStatus nvarchar(160) NULL;
    IF COL_LENGTH(N'dbo.Trailers', N'FleetioLastSyncedUtc') IS NULL
        ALTER TABLE dbo.Trailers ADD FleetioLastSyncedUtc datetimeoffset NULL;
END;

COMMIT TRANSACTION;
