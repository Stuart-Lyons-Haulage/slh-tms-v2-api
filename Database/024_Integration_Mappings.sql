/*
  Integration mappings table — lets operations manually link external
  provider keys (Sage employee ID, TachoMaster member code, DOT/RoadTech
  vehicle code, Fleetio vehicle ID) to TMS driver or vehicle records.
  Idempotent: safe to run on an existing database.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.IntegrationMappings', N'U') IS NULL
CREATE TABLE dbo.IntegrationMappings (
    Id uniqueidentifier NOT NULL,
    Provider nvarchar(40) NOT NULL,
    ExternalKey nvarchar(200) NOT NULL,
    ExternalLabel nvarchar(200) NULL,
    TmsEntityType nvarchar(20) NOT NULL,
    TmsEntityId uniqueidentifier NOT NULL,
    Active bit NOT NULL CONSTRAINT DF_IntegrationMappings_Active DEFAULT (1),
    Notes nvarchar(1000) NULL,
    CreatedAtUtc datetimeoffset NOT NULL,
    UpdatedAtUtc datetimeoffset NOT NULL,
    UpdatedBy nvarchar(200) NULL,
    CONSTRAINT PK_IntegrationMappings PRIMARY KEY (Id)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IntegrationMappings_Provider_ExternalKey_Type' AND object_id = OBJECT_ID(N'dbo.IntegrationMappings'))
CREATE UNIQUE INDEX IX_IntegrationMappings_Provider_ExternalKey_Type ON dbo.IntegrationMappings(Provider, ExternalKey, TmsEntityType) WHERE Active = 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_IntegrationMappings_TmsEntityId' AND object_id = OBJECT_ID(N'dbo.IntegrationMappings'))
CREATE INDEX IX_IntegrationMappings_TmsEntityId ON dbo.IntegrationMappings(TmsEntityId);

/*
  Driver status log table — tracks dispatcher-captured status updates
  per load (Dispatched, Accepted, ArrivedCollection, Loaded, ArrivedDelivery,
  Delivered, IssueReported). Phase 1 is internal/dispatcher-captured only;
  public SMS token links will be a separate enhancement.
*/
IF OBJECT_ID(N'dbo.DriverStatusLogs', N'U') IS NULL
CREATE TABLE dbo.DriverStatusLogs (
    Id uniqueidentifier NOT NULL,
    LoadId uniqueidentifier NOT NULL,
    DriverId uniqueidentifier NULL,
    Status nvarchar(40) NOT NULL,
    Notes nvarchar(1000) NULL,
    CapturedBy nvarchar(200) NULL,
    CapturedAtUtc datetimeoffset NOT NULL,
    CONSTRAINT PK_DriverStatusLogs PRIMARY KEY (Id)
);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DriverStatusLogs_LoadId' AND object_id = OBJECT_ID(N'dbo.DriverStatusLogs'))
CREATE INDEX IX_DriverStatusLogs_LoadId ON dbo.DriverStatusLogs(LoadId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DriverStatusLogs_CapturedAtUtc' AND object_id = OBJECT_ID(N'dbo.DriverStatusLogs'))
CREATE INDEX IX_DriverStatusLogs_CapturedAtUtc ON dbo.DriverStatusLogs(CapturedAtUtc);

COMMIT TRANSACTION;
