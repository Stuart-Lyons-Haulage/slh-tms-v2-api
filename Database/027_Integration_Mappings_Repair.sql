/*
  Resilient IntegrationMappings repair.
  Safe on existing production databases: creates the table when absent and
  adds any columns/indexes expected by the current Fleetio/Tacho mapping code.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.IntegrationMappings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IntegrationMappings (
        Id uniqueidentifier NOT NULL,
        Provider nvarchar(40) NOT NULL,
        ExternalKey nvarchar(200) NOT NULL,
        ExternalLabel nvarchar(200) NULL,
        TmsEntityType nvarchar(20) NOT NULL,
        TmsEntityId uniqueidentifier NOT NULL,
        Active bit NOT NULL CONSTRAINT DF_IntegrationMappings_Active_027 DEFAULT (1),
        Notes nvarchar(1000) NULL,
        CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_IntegrationMappings_CreatedAtUtc_027 DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_IntegrationMappings_UpdatedAtUtc_027 DEFAULT (SYSUTCDATETIME()),
        UpdatedBy nvarchar(200) NULL,
        CONSTRAINT PK_IntegrationMappings_027 PRIMARY KEY (Id)
    );
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.IntegrationMappings', 'Provider') IS NULL ALTER TABLE dbo.IntegrationMappings ADD Provider nvarchar(40) NOT NULL CONSTRAINT DF_IntegrationMappings_Provider_Repair DEFAULT (N'Unknown') WITH VALUES;
    IF COL_LENGTH('dbo.IntegrationMappings', 'ExternalKey') IS NULL ALTER TABLE dbo.IntegrationMappings ADD ExternalKey nvarchar(200) NOT NULL CONSTRAINT DF_IntegrationMappings_ExternalKey_Repair DEFAULT (N'') WITH VALUES;
    IF COL_LENGTH('dbo.IntegrationMappings', 'ExternalLabel') IS NULL ALTER TABLE dbo.IntegrationMappings ADD ExternalLabel nvarchar(200) NULL;
    IF COL_LENGTH('dbo.IntegrationMappings', 'TmsEntityType') IS NULL ALTER TABLE dbo.IntegrationMappings ADD TmsEntityType nvarchar(20) NOT NULL CONSTRAINT DF_IntegrationMappings_TmsEntityType_Repair DEFAULT (N'Unknown') WITH VALUES;
    IF COL_LENGTH('dbo.IntegrationMappings', 'TmsEntityId') IS NULL ALTER TABLE dbo.IntegrationMappings ADD TmsEntityId uniqueidentifier NOT NULL CONSTRAINT DF_IntegrationMappings_TmsEntityId_Repair DEFAULT ('00000000-0000-0000-0000-000000000000') WITH VALUES;
    IF COL_LENGTH('dbo.IntegrationMappings', 'Active') IS NULL ALTER TABLE dbo.IntegrationMappings ADD Active bit NOT NULL CONSTRAINT DF_IntegrationMappings_Active_Repair DEFAULT (1) WITH VALUES;
    IF COL_LENGTH('dbo.IntegrationMappings', 'Notes') IS NULL ALTER TABLE dbo.IntegrationMappings ADD Notes nvarchar(1000) NULL;
    IF COL_LENGTH('dbo.IntegrationMappings', 'CreatedAtUtc') IS NULL ALTER TABLE dbo.IntegrationMappings ADD CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_IntegrationMappings_CreatedAtUtc_Repair DEFAULT (SYSUTCDATETIME()) WITH VALUES;
    IF COL_LENGTH('dbo.IntegrationMappings', 'UpdatedAtUtc') IS NULL ALTER TABLE dbo.IntegrationMappings ADD UpdatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_IntegrationMappings_UpdatedAtUtc_Repair DEFAULT (SYSUTCDATETIME()) WITH VALUES;
    IF COL_LENGTH('dbo.IntegrationMappings', 'UpdatedBy') IS NULL ALTER TABLE dbo.IntegrationMappings ADD UpdatedBy nvarchar(200) NULL;
END

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_IntegrationMappings_Provider_ExternalKey_Type'
      AND object_id = OBJECT_ID(N'dbo.IntegrationMappings')
)
AND NOT EXISTS (
    SELECT 1
    FROM dbo.IntegrationMappings
    WHERE Active = 1
    GROUP BY Provider, ExternalKey, TmsEntityType
    HAVING COUNT(*) > 1
)
CREATE UNIQUE INDEX IX_IntegrationMappings_Provider_ExternalKey_Type
    ON dbo.IntegrationMappings(Provider, ExternalKey, TmsEntityType)
    WHERE Active = 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_IntegrationMappings_TmsEntityId'
      AND object_id = OBJECT_ID(N'dbo.IntegrationMappings')
)
CREATE INDEX IX_IntegrationMappings_TmsEntityId ON dbo.IntegrationMappings(TmsEntityId);

COMMIT TRANSACTION;
