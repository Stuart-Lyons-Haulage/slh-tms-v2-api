/* Completes the master-data shape on databases that already have older tables. */
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Customers', N'Code') IS NULL ALTER TABLE dbo.Customers ADD Code nvarchar(40) NULL;
    IF COL_LENGTH(N'dbo.Customers', N'Name') IS NULL ALTER TABLE dbo.Customers ADD Name nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.Customers', N'Active') IS NULL ALTER TABLE dbo.Customers ADD Active bit NOT NULL CONSTRAINT DF_Repair_Customers_Active_016 DEFAULT(1);
END;

IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.CustomerContacts', N'CustomerCode') IS NULL ALTER TABLE dbo.CustomerContacts ADD CustomerCode nvarchar(40) NULL;
    IF COL_LENGTH(N'dbo.CustomerContacts', N'Name') IS NULL ALTER TABLE dbo.CustomerContacts ADD Name nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.CustomerContacts', N'Email') IS NULL ALTER TABLE dbo.CustomerContacts ADD Email nvarchar(320) NULL;
    IF COL_LENGTH(N'dbo.CustomerContacts', N'MobileNumber') IS NULL ALTER TABLE dbo.CustomerContacts ADD MobileNumber nvarchar(40) NULL;
    IF COL_LENGTH(N'dbo.CustomerContacts', N'ReceivesEtaUpdates') IS NULL ALTER TABLE dbo.CustomerContacts ADD ReceivesEtaUpdates bit NOT NULL CONSTRAINT DF_Repair_CustomerContacts_ReceivesEta_016 DEFAULT(1);
    IF COL_LENGTH(N'dbo.CustomerContacts', N'Active') IS NULL ALTER TABLE dbo.CustomerContacts ADD Active bit NOT NULL CONSTRAINT DF_Repair_CustomerContacts_Active_016 DEFAULT(1);
END;

IF OBJECT_ID(N'dbo.Vehicles', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Vehicles', N'FleetNumber') IS NULL ALTER TABLE dbo.Vehicles ADD FleetNumber nvarchar(40) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'Abbreviation') IS NULL ALTER TABLE dbo.Vehicles ADD Abbreviation nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'Transmission') IS NULL ALTER TABLE dbo.Vehicles ADD Transmission nvarchar(20) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'DvsCompliant') IS NULL ALTER TABLE dbo.Vehicles ADD DvsCompliant bit NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'FuelProvider') IS NULL ALTER TABLE dbo.Vehicles ADD FuelProvider nvarchar(30) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'CabMobile') IS NULL ALTER TABLE dbo.Vehicles ADD CabMobile nvarchar(40) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'FuelPin') IS NULL ALTER TABLE dbo.Vehicles ADD FuelPin nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'ShellCard') IS NULL ALTER TABLE dbo.Vehicles ADD ShellCard nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'BpRedCard') IS NULL ALTER TABLE dbo.Vehicles ADD BpRedCard nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'BpPlainCard') IS NULL ALTER TABLE dbo.Vehicles ADD BpPlainCard nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'Notes') IS NULL ALTER TABLE dbo.Vehicles ADD Notes nvarchar(500) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'FuelPinSecretName') IS NULL ALTER TABLE dbo.Vehicles ADD FuelPinSecretName nvarchar(120) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'FuelCardLastFour') IS NULL ALTER TABLE dbo.Vehicles ADD FuelCardLastFour nvarchar(4) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'FleetioId') IS NULL ALTER TABLE dbo.Vehicles ADD FleetioId nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'FleetioName') IS NULL ALTER TABLE dbo.Vehicles ADD FleetioName nvarchar(160) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'FleetioStatus') IS NULL ALTER TABLE dbo.Vehicles ADD FleetioStatus nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'Active') IS NULL ALTER TABLE dbo.Vehicles ADD Active bit NOT NULL CONSTRAINT DF_Repair_Vehicles_Active_016 DEFAULT(1);
END;

IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Drivers', N'EmployeeNumber') IS NULL ALTER TABLE dbo.Drivers ADD EmployeeNumber nvarchar(40) NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'DisplayName') IS NULL ALTER TABLE dbo.Drivers ADD DisplayName nvarchar(160) NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'TachoName') IS NULL ALTER TABLE dbo.Drivers ADD TachoName nvarchar(160) NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'MobileNumber') IS NULL ALTER TABLE dbo.Drivers ADD MobileNumber nvarchar(40) NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'DriverType') IS NULL ALTER TABLE dbo.Drivers ADD DriverType nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'DriverGroup') IS NULL ALTER TABLE dbo.Drivers ADD DriverGroup nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'Skills') IS NULL ALTER TABLE dbo.Drivers ADD Skills nvarchar(160) NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'Active') IS NULL ALTER TABLE dbo.Drivers ADD Active bit NOT NULL CONSTRAINT DF_Repair_Drivers_Active_016 DEFAULT(1);
END;

IF OBJECT_ID(N'dbo.Trailers', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Trailers', N'Type') IS NULL ALTER TABLE dbo.Trailers ADD Type nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Trailers', N'StandardCapacity') IS NULL ALTER TABLE dbo.Trailers ADD StandardCapacity int NULL;
    IF COL_LENGTH(N'dbo.Trailers', N'EuroCapacity') IS NULL ALTER TABLE dbo.Trailers ADD EuroCapacity int NULL;
    IF COL_LENGTH(N'dbo.Trailers', N'Active') IS NULL ALTER TABLE dbo.Trailers ADD Active bit NOT NULL CONSTRAINT DF_Repair_Trailers_Active_016 DEFAULT(1);
END;

IF OBJECT_ID(N'dbo.Sites', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Sites', N'DriverTextName') IS NULL ALTER TABLE dbo.Sites ADD DriverTextName nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.Sites', N'CollectionAddress') IS NULL ALTER TABLE dbo.Sites ADD CollectionAddress nvarchar(500) NULL;
    IF COL_LENGTH(N'dbo.Sites', N'CollectionInstructions') IS NULL ALTER TABLE dbo.Sites ADD CollectionInstructions nvarchar(1000) NULL;
    IF COL_LENGTH(N'dbo.Sites', N'MapLink') IS NULL ALTER TABLE dbo.Sites ADD MapLink nvarchar(1000) NULL;
    IF COL_LENGTH(N'dbo.Sites', N'Active') IS NULL ALTER TABLE dbo.Sites ADD Active bit NOT NULL CONSTRAINT DF_Repair_Sites_Active_016 DEFAULT(1);
END;

IF OBJECT_ID(N'dbo.MarketContacts', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.MarketContacts', N'StandOrLocation') IS NULL ALTER TABLE dbo.MarketContacts ADD StandOrLocation nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.MarketContacts', N'Salesman') IS NULL ALTER TABLE dbo.MarketContacts ADD Salesman nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.MarketContacts', N'Sender') IS NULL ALTER TABLE dbo.MarketContacts ADD Sender nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.MarketContacts', N'Active') IS NULL ALTER TABLE dbo.MarketContacts ADD Active bit NOT NULL CONSTRAINT DF_Repair_MarketContacts_Active_016 DEFAULT(1);
END;

IF OBJECT_ID(N'dbo.FuelPrices', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.FuelPrices', N'WeekCommencing') IS NULL ALTER TABLE dbo.FuelPrices ADD WeekCommencing date NULL;
    IF COL_LENGTH(N'dbo.FuelPrices', N'Provider') IS NULL ALTER TABLE dbo.FuelPrices ADD Provider nvarchar(120) NULL;
    IF COL_LENGTH(N'dbo.FuelPrices', N'PricePencePerLitre') IS NULL ALTER TABLE dbo.FuelPrices ADD PricePencePerLitre decimal(10,2) NULL;
    IF COL_LENGTH(N'dbo.FuelPrices', N'IsPricingMaximum') IS NULL ALTER TABLE dbo.FuelPrices ADD IsPricingMaximum bit NOT NULL CONSTRAINT DF_Repair_FuelPrices_Max_016 DEFAULT(0);
    IF COL_LENGTH(N'dbo.FuelPrices', N'Source') IS NULL ALTER TABLE dbo.FuelPrices ADD Source nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.FuelPrices', N'Notes') IS NULL ALTER TABLE dbo.FuelPrices ADD Notes nvarchar(500) NULL;
    IF COL_LENGTH(N'dbo.FuelPrices', N'CreatedAtUtc') IS NULL ALTER TABLE dbo.FuelPrices ADD CreatedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_Repair_FuelPrices_Created_016 DEFAULT(SYSUTCDATETIME());
END;

IF OBJECT_ID(N'dbo.StagedImports', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.StagedImports', N'EntityType') IS NULL ALTER TABLE dbo.StagedImports ADD EntityType nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.StagedImports', N'IdempotencyKey') IS NULL ALTER TABLE dbo.StagedImports ADD IdempotencyKey nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.StagedImports', N'PayloadJson') IS NULL ALTER TABLE dbo.StagedImports ADD PayloadJson nvarchar(max) NULL;
    IF COL_LENGTH(N'dbo.StagedImports', N'Status') IS NULL ALTER TABLE dbo.StagedImports ADD Status int NOT NULL CONSTRAINT DF_Repair_StagedImports_Status_016 DEFAULT(0);
    IF COL_LENGTH(N'dbo.StagedImports', N'Source') IS NULL ALTER TABLE dbo.StagedImports ADD Source nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.StagedImports', N'ReceivedAtUtc') IS NULL ALTER TABLE dbo.StagedImports ADD ReceivedAtUtc datetimeoffset NOT NULL CONSTRAINT DF_Repair_StagedImports_Received_016 DEFAULT(SYSUTCDATETIME());
    IF COL_LENGTH(N'dbo.StagedImports', N'ReviewedAtUtc') IS NULL ALTER TABLE dbo.StagedImports ADD ReviewedAtUtc datetimeoffset NULL;
    IF COL_LENGTH(N'dbo.StagedImports', N'ReviewedBy') IS NULL ALTER TABLE dbo.StagedImports ADD ReviewedBy nvarchar(200) NULL;
    IF COL_LENGTH(N'dbo.StagedImports', N'ReviewNote') IS NULL ALTER TABLE dbo.StagedImports ADD ReviewNote nvarchar(1000) NULL;
    IF COL_LENGTH(N'dbo.StagedImports', N'RowVersion') IS NULL ALTER TABLE dbo.StagedImports ADD RowVersion rowversion NOT NULL;
END;
