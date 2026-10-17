/* Ensures the core register tables exist when an older production database was only partially provisioned. */
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.Customers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Customers (Id uniqueidentifier NOT NULL, Code nvarchar(40) NOT NULL, Name nvarchar(200) NOT NULL, Active bit NOT NULL CONSTRAINT DF_Repair_Customers_Active DEFAULT(1), CONSTRAINT PK_Repair_Customers PRIMARY KEY(Id));
    CREATE UNIQUE INDEX IX_Repair_Customers_Code ON dbo.Customers(Code);
END;

IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerContacts (Id uniqueidentifier NOT NULL, CustomerCode nvarchar(40) NOT NULL, Name nvarchar(200) NOT NULL, Email nvarchar(320) NULL, MobileNumber nvarchar(40) NULL, ReceivesEtaUpdates bit NOT NULL CONSTRAINT DF_Repair_CustomerContacts_ReceivesEta DEFAULT(1), Active bit NOT NULL CONSTRAINT DF_Repair_CustomerContacts_Active DEFAULT(1), CONSTRAINT PK_Repair_CustomerContacts PRIMARY KEY(Id));
    CREATE UNIQUE INDEX IX_Repair_CustomerContacts_Key ON dbo.CustomerContacts(CustomerCode, Name);
END;

IF OBJECT_ID(N'dbo.Vehicles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Vehicles (Id uniqueidentifier NOT NULL, Registration nvarchar(20) NOT NULL, FleetNumber nvarchar(40) NULL, Abbreviation nvarchar(20) NULL, Transmission nvarchar(20) NULL, DvsCompliant bit NULL, FuelProvider nvarchar(30) NULL, CabMobile nvarchar(40) NULL, FuelPin nvarchar(80) NULL, ShellCard nvarchar(80) NULL, BpRedCard nvarchar(80) NULL, BpPlainCard nvarchar(80) NULL, Notes nvarchar(500) NULL, FuelPinSecretName nvarchar(120) NULL, FuelCardLastFour nvarchar(4) NULL, FleetioId nvarchar(80) NULL, FleetioName nvarchar(160) NULL, FleetioStatus nvarchar(80) NULL, Active bit NOT NULL CONSTRAINT DF_Repair_Vehicles_Active DEFAULT(1), CONSTRAINT PK_Repair_Vehicles PRIMARY KEY(Id));
    CREATE UNIQUE INDEX IX_Repair_Vehicles_Registration ON dbo.Vehicles(Registration);
END;

IF OBJECT_ID(N'dbo.Drivers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Drivers (Id uniqueidentifier NOT NULL, EmployeeNumber nvarchar(40) NOT NULL, DisplayName nvarchar(160) NOT NULL, TachoName nvarchar(160) NULL, MobileNumber nvarchar(40) NULL, DriverType nvarchar(80) NULL, DriverGroup nvarchar(80) NULL, Skills nvarchar(160) NULL, Active bit NOT NULL CONSTRAINT DF_Repair_Drivers_Active DEFAULT(1), CONSTRAINT PK_Repair_Drivers PRIMARY KEY(Id));
    CREATE UNIQUE INDEX IX_Repair_Drivers_EmployeeNumber ON dbo.Drivers(EmployeeNumber);
END;

IF OBJECT_ID(N'dbo.Trailers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Trailers (Id uniqueidentifier NOT NULL, TrailerNumber nvarchar(40) NOT NULL, Type nvarchar(80) NULL, StandardCapacity int NULL, EuroCapacity int NULL, Active bit NOT NULL CONSTRAINT DF_Repair_Trailers_Active DEFAULT(1), CONSTRAINT PK_Repair_Trailers PRIMARY KEY(Id));
    CREATE UNIQUE INDEX IX_Repair_Trailers_Number ON dbo.Trailers(TrailerNumber);
END;

IF OBJECT_ID(N'dbo.Sites', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Sites (Id uniqueidentifier NOT NULL, ExternalCode nvarchar(40) NOT NULL, Name nvarchar(200) NOT NULL, DriverTextName nvarchar(200) NULL, CollectionAddress nvarchar(500) NULL, CollectionInstructions nvarchar(1000) NULL, MapLink nvarchar(1000) NULL, Active bit NOT NULL CONSTRAINT DF_Repair_Sites_Active DEFAULT(1), CONSTRAINT PK_Repair_Sites PRIMARY KEY(Id));
    CREATE UNIQUE INDEX IX_Repair_Sites_ExternalCode ON dbo.Sites(ExternalCode);
END;

IF OBJECT_ID(N'dbo.MarketContacts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MarketContacts (Id uniqueidentifier NOT NULL, Market nvarchar(80) NOT NULL, Name nvarchar(200) NOT NULL, StandOrLocation nvarchar(200) NULL, Salesman nvarchar(200) NULL, Sender nvarchar(200) NULL, Active bit NOT NULL CONSTRAINT DF_Repair_MarketContacts_Active DEFAULT(1), CONSTRAINT PK_Repair_MarketContacts PRIMARY KEY(Id));
    CREATE UNIQUE INDEX IX_Repair_MarketContacts_Key ON dbo.MarketContacts(Market, Name);
END;

IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StagedImports (Id uniqueidentifier NOT NULL, EntityType nvarchar(80) NOT NULL, IdempotencyKey nvarchar(200) NOT NULL, PayloadJson nvarchar(max) NOT NULL, Status int NOT NULL CONSTRAINT DF_Repair_StagedImports_Status DEFAULT(0), Source nvarchar(200) NULL, ReceivedAtUtc datetimeoffset NOT NULL, ReviewedAtUtc datetimeoffset NULL, ReviewedBy nvarchar(200) NULL, ReviewNote nvarchar(1000) NULL, RowVersion rowversion NOT NULL, CONSTRAINT PK_Repair_StagedImports PRIMARY KEY(Id));
    CREATE UNIQUE INDEX IX_Repair_StagedImports_Key ON dbo.StagedImports(IdempotencyKey);
END;
