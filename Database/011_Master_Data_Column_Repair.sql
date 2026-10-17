-- Safely aligns older Azure SQL databases with the current master-data model.
-- Existing data is preserved; columns are only added when absent.

IF COL_LENGTH('dbo.Drivers', 'TachoName') IS NULL ALTER TABLE dbo.Drivers ADD TachoName nvarchar(160) NULL;
IF COL_LENGTH('dbo.Drivers', 'MobileNumber') IS NULL ALTER TABLE dbo.Drivers ADD MobileNumber nvarchar(40) NULL;
IF COL_LENGTH('dbo.Drivers', 'DriverType') IS NULL ALTER TABLE dbo.Drivers ADD DriverType nvarchar(80) NULL;
IF COL_LENGTH('dbo.Drivers', 'DriverGroup') IS NULL ALTER TABLE dbo.Drivers ADD DriverGroup nvarchar(80) NULL;
IF COL_LENGTH('dbo.Drivers', 'Skills') IS NULL ALTER TABLE dbo.Drivers ADD Skills nvarchar(160) NULL;
IF COL_LENGTH('dbo.Drivers', 'Active') IS NULL ALTER TABLE dbo.Drivers ADD Active bit NOT NULL CONSTRAINT DF_Drivers_Active DEFAULT(1);

IF COL_LENGTH('dbo.Vehicles', 'FleetNumber') IS NULL ALTER TABLE dbo.Vehicles ADD FleetNumber nvarchar(40) NULL;
IF COL_LENGTH('dbo.Vehicles', 'Abbreviation') IS NULL ALTER TABLE dbo.Vehicles ADD Abbreviation nvarchar(20) NULL;
IF COL_LENGTH('dbo.Vehicles', 'Transmission') IS NULL ALTER TABLE dbo.Vehicles ADD Transmission nvarchar(20) NULL;
IF COL_LENGTH('dbo.Vehicles', 'DvsCompliant') IS NULL ALTER TABLE dbo.Vehicles ADD DvsCompliant bit NULL;
IF COL_LENGTH('dbo.Vehicles', 'FuelProvider') IS NULL ALTER TABLE dbo.Vehicles ADD FuelProvider nvarchar(30) NULL;
IF COL_LENGTH('dbo.Vehicles', 'FuelPinSecretName') IS NULL ALTER TABLE dbo.Vehicles ADD FuelPinSecretName nvarchar(120) NULL;
IF COL_LENGTH('dbo.Vehicles', 'FuelCardLastFour') IS NULL ALTER TABLE dbo.Vehicles ADD FuelCardLastFour nvarchar(4) NULL;
IF COL_LENGTH('dbo.Vehicles', 'Active') IS NULL ALTER TABLE dbo.Vehicles ADD Active bit NOT NULL CONSTRAINT DF_Vehicles_Active DEFAULT(1);

IF COL_LENGTH('dbo.Trailers', 'Type') IS NULL ALTER TABLE dbo.Trailers ADD Type nvarchar(80) NULL;
IF COL_LENGTH('dbo.Trailers', 'StandardCapacity') IS NULL ALTER TABLE dbo.Trailers ADD StandardCapacity int NULL;
IF COL_LENGTH('dbo.Trailers', 'EuroCapacity') IS NULL ALTER TABLE dbo.Trailers ADD EuroCapacity int NULL;
IF COL_LENGTH('dbo.Trailers', 'Active') IS NULL ALTER TABLE dbo.Trailers ADD Active bit NOT NULL CONSTRAINT DF_Trailers_Active DEFAULT(1);

IF COL_LENGTH('dbo.Sites', 'DriverTextName') IS NULL ALTER TABLE dbo.Sites ADD DriverTextName nvarchar(200) NULL;
IF COL_LENGTH('dbo.Sites', 'CollectionAddress') IS NULL ALTER TABLE dbo.Sites ADD CollectionAddress nvarchar(500) NULL;
IF COL_LENGTH('dbo.Sites', 'CollectionInstructions') IS NULL ALTER TABLE dbo.Sites ADD CollectionInstructions nvarchar(1000) NULL;
IF COL_LENGTH('dbo.Sites', 'MapLink') IS NULL ALTER TABLE dbo.Sites ADD MapLink nvarchar(1000) NULL;
IF COL_LENGTH('dbo.Sites', 'Active') IS NULL ALTER TABLE dbo.Sites ADD Active bit NOT NULL CONSTRAINT DF_Sites_Active DEFAULT(1);

IF COL_LENGTH('dbo.MarketContacts', 'StandOrLocation') IS NULL ALTER TABLE dbo.MarketContacts ADD StandOrLocation nvarchar(200) NULL;
IF COL_LENGTH('dbo.MarketContacts', 'Salesman') IS NULL ALTER TABLE dbo.MarketContacts ADD Salesman nvarchar(200) NULL;
IF COL_LENGTH('dbo.MarketContacts', 'Sender') IS NULL ALTER TABLE dbo.MarketContacts ADD Sender nvarchar(200) NULL;
IF COL_LENGTH('dbo.MarketContacts', 'Active') IS NULL ALTER TABLE dbo.MarketContacts ADD Active bit NOT NULL CONSTRAINT DF_MarketContacts_Active DEFAULT(1);

IF COL_LENGTH('dbo.CustomerContacts', 'Email') IS NULL ALTER TABLE dbo.CustomerContacts ADD Email nvarchar(320) NULL;
IF COL_LENGTH('dbo.CustomerContacts', 'MobileNumber') IS NULL ALTER TABLE dbo.CustomerContacts ADD MobileNumber nvarchar(40) NULL;
IF COL_LENGTH('dbo.CustomerContacts', 'ReceivesEtaUpdates') IS NULL ALTER TABLE dbo.CustomerContacts ADD ReceivesEtaUpdates bit NOT NULL CONSTRAINT DF_CustomerContacts_ReceivesEtaUpdates DEFAULT(1);
IF COL_LENGTH('dbo.CustomerContacts', 'Active') IS NULL ALTER TABLE dbo.CustomerContacts ADD Active bit NOT NULL CONSTRAINT DF_CustomerContacts_Active DEFAULT(1);
