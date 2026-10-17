/*
  SLH TMS initial production schema.
  Safe to run once on an empty database. It is deliberately idempotent so a
  stopped deployment can be resumed without recreating tables or indexes.
*/
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.Customers', N'U') IS NULL
CREATE TABLE dbo.Customers (
    Id uniqueidentifier NOT NULL,
    Code nvarchar(40) NOT NULL,
    Name nvarchar(200) NOT NULL,
    Active bit NOT NULL CONSTRAINT DF_Customers_Active DEFAULT (1),
    CONSTRAINT PK_Customers PRIMARY KEY (Id)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Customers_Code' AND object_id = OBJECT_ID(N'dbo.Customers'))
CREATE UNIQUE INDEX IX_Customers_Code ON dbo.Customers(Code);

IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NULL
CREATE TABLE dbo.CustomerContacts (
    Id uniqueidentifier NOT NULL,
    CustomerCode nvarchar(40) NOT NULL,
    Name nvarchar(200) NOT NULL,
    Email nvarchar(320) NULL,
    MobileNumber nvarchar(40) NULL,
    ReceivesEtaUpdates bit NOT NULL CONSTRAINT DF_CustomerContacts_ReceivesEtaUpdates DEFAULT (1),
    Active bit NOT NULL CONSTRAINT DF_CustomerContacts_Active DEFAULT (1),
    CONSTRAINT PK_CustomerContacts PRIMARY KEY (Id)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CustomerContacts_CustomerCode_Name' AND object_id = OBJECT_ID(N'dbo.CustomerContacts'))
CREATE UNIQUE INDEX IX_CustomerContacts_CustomerCode_Name ON dbo.CustomerContacts(CustomerCode, Name);

IF OBJECT_ID(N'dbo.Vehicles', N'U') IS NULL
CREATE TABLE dbo.Vehicles (
    Id uniqueidentifier NOT NULL,
    Registration nvarchar(20) NOT NULL,
    FleetNumber nvarchar(40) NULL,
    Abbreviation nvarchar(20) NULL,
    Transmission nvarchar(20) NULL,
    DvsCompliant bit NULL,
    FuelProvider nvarchar(30) NULL,
    FuelPinSecretName nvarchar(120) NULL,
    FuelCardLastFour nvarchar(4) NULL,
    Active bit NOT NULL CONSTRAINT DF_Vehicles_Active DEFAULT (1),
    CONSTRAINT PK_Vehicles PRIMARY KEY (Id)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Vehicles_Registration' AND object_id = OBJECT_ID(N'dbo.Vehicles'))
CREATE UNIQUE INDEX IX_Vehicles_Registration ON dbo.Vehicles(Registration);

IF OBJECT_ID(N'dbo.Drivers', N'U') IS NULL
CREATE TABLE dbo.Drivers (
    Id uniqueidentifier NOT NULL,
    EmployeeNumber nvarchar(40) NOT NULL,
    DisplayName nvarchar(160) NOT NULL,
    TachoName nvarchar(160) NULL,
    MobileNumber nvarchar(40) NULL,
    DriverType nvarchar(80) NULL,
    DriverGroup nvarchar(80) NULL,
    Skills nvarchar(160) NULL,
    Active bit NOT NULL CONSTRAINT DF_Drivers_Active DEFAULT (1),
    CONSTRAINT PK_Drivers PRIMARY KEY (Id)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Drivers_EmployeeNumber' AND object_id = OBJECT_ID(N'dbo.Drivers'))
CREATE UNIQUE INDEX IX_Drivers_EmployeeNumber ON dbo.Drivers(EmployeeNumber);

IF OBJECT_ID(N'dbo.Trailers', N'U') IS NULL
CREATE TABLE dbo.Trailers (
    Id uniqueidentifier NOT NULL,
    TrailerNumber nvarchar(40) NOT NULL,
    Type nvarchar(80) NULL,
    StandardCapacity int NULL,
    EuroCapacity int NULL,
    Active bit NOT NULL CONSTRAINT DF_Trailers_Active DEFAULT (1),
    CONSTRAINT PK_Trailers PRIMARY KEY (Id)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Trailers_TrailerNumber' AND object_id = OBJECT_ID(N'dbo.Trailers'))
CREATE UNIQUE INDEX IX_Trailers_TrailerNumber ON dbo.Trailers(TrailerNumber);

IF OBJECT_ID(N'dbo.Sites', N'U') IS NULL
CREATE TABLE dbo.Sites (
    Id uniqueidentifier NOT NULL,
    ExternalCode nvarchar(40) NOT NULL,
    Name nvarchar(200) NOT NULL,
    DriverTextName nvarchar(200) NULL,
    CollectionAddress nvarchar(500) NULL,
    CollectionInstructions nvarchar(1000) NULL,
    MapLink nvarchar(1000) NULL,
    Active bit NOT NULL CONSTRAINT DF_Sites_Active DEFAULT (1),
    CONSTRAINT PK_Sites PRIMARY KEY (Id)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sites_ExternalCode' AND object_id = OBJECT_ID(N'dbo.Sites'))
CREATE UNIQUE INDEX IX_Sites_ExternalCode ON dbo.Sites(ExternalCode);

IF OBJECT_ID(N'dbo.MarketContacts', N'U') IS NULL
CREATE TABLE dbo.MarketContacts (
    Id uniqueidentifier NOT NULL,
    Market nvarchar(80) NOT NULL,
    Name nvarchar(200) NOT NULL,
    StandOrLocation nvarchar(200) NULL,
    Salesman nvarchar(200) NULL,
    Sender nvarchar(200) NULL,
    Active bit NOT NULL CONSTRAINT DF_MarketContacts_Active DEFAULT (1),
    CONSTRAINT PK_MarketContacts PRIMARY KEY (Id)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MarketContacts_Market_Name' AND object_id = OBJECT_ID(N'dbo.MarketContacts'))
CREATE UNIQUE INDEX IX_MarketContacts_Market_Name ON dbo.MarketContacts(Market, Name);

IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NULL
CREATE TABLE dbo.StagedImports (
    Id uniqueidentifier NOT NULL,
    EntityType nvarchar(80) NOT NULL,
    IdempotencyKey nvarchar(200) NOT NULL,
    PayloadJson nvarchar(max) NOT NULL,
    Status int NOT NULL CONSTRAINT DF_StagedImports_Status DEFAULT (0),
    Source nvarchar(200) NULL,
    ReceivedAtUtc datetimeoffset NOT NULL,
    ReviewedAtUtc datetimeoffset NULL,
    ReviewedBy nvarchar(200) NULL,
    ReviewNote nvarchar(1000) NULL,
    RowVersion rowversion NOT NULL,
    CONSTRAINT PK_StagedImports PRIMARY KEY (Id)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StagedImports_IdempotencyKey' AND object_id = OBJECT_ID(N'dbo.StagedImports'))
CREATE UNIQUE INDEX IX_StagedImports_IdempotencyKey ON dbo.StagedImports(IdempotencyKey);

IF OBJECT_ID(N'dbo.VehicleTrackingEvents', N'U') IS NULL
CREATE TABLE dbo.VehicleTrackingEvents (
    Id uniqueidentifier NOT NULL,
    ProviderName nvarchar(50) NOT NULL,
    ProviderEventId nvarchar(100) NOT NULL,
    VehicleIdentifier nvarchar(100) NOT NULL,
    ReceivedAtUtc datetimeoffset NOT NULL,
    EventTimeUtc datetimeoffset NOT NULL,
    Latitude decimal(9,6) NOT NULL,
    Longitude decimal(9,6) NOT NULL,
    SpeedKph decimal(10,2) NULL,
    IgnitionOn bit NULL,
    IsMoving bit NULL,
    RawPayload nvarchar(max) NOT NULL,
    MatchStatus nvarchar(50) NULL,
    CreatedAtUtc datetimeoffset NOT NULL,
    CONSTRAINT PK_VehicleTrackingEvents PRIMARY KEY (Id)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_VehicleTrackingEvent_ProviderName_ProviderEventId' AND object_id = OBJECT_ID(N'dbo.VehicleTrackingEvents'))
CREATE UNIQUE INDEX IX_VehicleTrackingEvent_ProviderName_ProviderEventId ON dbo.VehicleTrackingEvents(ProviderName, ProviderEventId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_VehicleTrackingEvent_VehicleIdentifier' AND object_id = OBJECT_ID(N'dbo.VehicleTrackingEvents'))
CREATE INDEX IX_VehicleTrackingEvent_VehicleIdentifier ON dbo.VehicleTrackingEvents(VehicleIdentifier);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_VehicleTrackingEvent_EventTimeUtc' AND object_id = OBJECT_ID(N'dbo.VehicleTrackingEvents'))
CREATE INDEX IX_VehicleTrackingEvent_EventTimeUtc ON dbo.VehicleTrackingEvents(EventTimeUtc);

IF OBJECT_ID(N'dbo.VehicleLiveStatuses', N'U') IS NULL
CREATE TABLE dbo.VehicleLiveStatuses (
    Id uniqueidentifier NOT NULL,
    VehicleIdentifier nvarchar(100) NOT NULL,
    LastEventTimeUtc datetimeoffset NOT NULL,
    LastReceivedAtUtc datetimeoffset NOT NULL,
    Latitude decimal(9,6) NOT NULL,
    Longitude decimal(9,6) NOT NULL,
    SpeedKph decimal(10,2) NULL,
    IgnitionOn bit NULL,
    IsMoving bit NULL,
    LastKnownStatus nvarchar(100) NULL,
    UpdatedAtUtc datetimeoffset NOT NULL,
    CONSTRAINT PK_VehicleLiveStatuses PRIMARY KEY (Id)
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_VehicleLiveStatus_VehicleIdentifier' AND object_id = OBJECT_ID(N'dbo.VehicleLiveStatuses'))
CREATE UNIQUE INDEX IX_VehicleLiveStatus_VehicleIdentifier ON dbo.VehicleLiveStatuses(VehicleIdentifier);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_VehicleLiveStatus_LastEventTimeUtc' AND object_id = OBJECT_ID(N'dbo.VehicleLiveStatuses'))
CREATE INDEX IX_VehicleLiveStatus_LastEventTimeUtc ON dbo.VehicleLiveStatuses(LastEventTimeUtc);

COMMIT TRANSACTION;
