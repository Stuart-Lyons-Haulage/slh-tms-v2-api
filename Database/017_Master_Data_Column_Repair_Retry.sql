/* A final per-column repair pass. Some Azure SQL users expose the tables but
   not enough metadata for COL_LENGTH, so the older all-in-one repair batches
   can stop on a duplicate-column error. Each change is isolated and a
   duplicate is deliberately harmless. */
SET XACT_ABORT OFF;

IF OBJECT_ID(N'dbo.CustomerContacts', N'U') IS NULL
BEGIN
    BEGIN TRY CREATE TABLE dbo.CustomerContacts (Id uniqueidentifier NOT NULL CONSTRAINT PK_Repair_CustomerContacts_017 PRIMARY KEY, CustomerCode nvarchar(40) NULL, Name nvarchar(200) NULL, Email nvarchar(320) NULL, MobileNumber nvarchar(40) NULL, ReceivesEtaUpdates bit NOT NULL CONSTRAINT DF_Repair_CustomerContacts_Eta_017 DEFAULT(1), Active bit NOT NULL CONSTRAINT DF_Repair_CustomerContacts_Active_017 DEFAULT(1)); END TRY BEGIN CATCH END CATCH;
END;
IF OBJECT_ID(N'dbo.MarketContacts', N'U') IS NULL
BEGIN
    BEGIN TRY CREATE TABLE dbo.MarketContacts (Id uniqueidentifier NOT NULL CONSTRAINT PK_Repair_MarketContacts_017 PRIMARY KEY, Market nvarchar(80) NOT NULL, Name nvarchar(200) NOT NULL, StandOrLocation nvarchar(200) NULL, Salesman nvarchar(200) NULL, Sender nvarchar(200) NULL, Active bit NOT NULL CONSTRAINT DF_Repair_MarketContacts_Active_017 DEFAULT(1)); END TRY BEGIN CATCH END CATCH;
END;

DECLARE @changes table (SqlText nvarchar(max) NOT NULL);
INSERT @changes VALUES
(N'ALTER TABLE dbo.CustomerContacts ADD MobileNumber nvarchar(40) NULL'),
(N'ALTER TABLE dbo.CustomerContacts ADD Email nvarchar(320) NULL'),
(N'ALTER TABLE dbo.CustomerContacts ADD CustomerCode nvarchar(40) NULL'),
(N'ALTER TABLE dbo.CustomerContacts ADD Name nvarchar(200) NULL'),
(N'ALTER TABLE dbo.CustomerContacts ADD ReceivesEtaUpdates bit NOT NULL CONSTRAINT DF_Repair_CustomerContacts_Eta_017b DEFAULT(1)'),
(N'ALTER TABLE dbo.CustomerContacts ADD Active bit NOT NULL CONSTRAINT DF_Repair_CustomerContacts_Active_017b DEFAULT(1)'),
(N'ALTER TABLE dbo.Drivers ADD MobileNumber nvarchar(40) NULL'),
(N'ALTER TABLE dbo.Drivers ADD TachoName nvarchar(160) NULL'),
(N'ALTER TABLE dbo.Vehicles ADD BpPlainCard nvarchar(80) NULL'),
(N'ALTER TABLE dbo.Vehicles ADD BpRedCard nvarchar(80) NULL'),
(N'ALTER TABLE dbo.Vehicles ADD CabMobile nvarchar(40) NULL'),
(N'ALTER TABLE dbo.Vehicles ADD FleetioId nvarchar(80) NULL'),
(N'ALTER TABLE dbo.Vehicles ADD FleetioName nvarchar(160) NULL'),
(N'ALTER TABLE dbo.Vehicles ADD FleetioStatus nvarchar(80) NULL'),
(N'ALTER TABLE dbo.Vehicles ADD FuelPin nvarchar(80) NULL'),
(N'ALTER TABLE dbo.Vehicles ADD Notes nvarchar(500) NULL'),
(N'ALTER TABLE dbo.Vehicles ADD ShellCard nvarchar(80) NULL'),
(N'ALTER TABLE dbo.MarketContacts ADD Salesman nvarchar(200) NULL'),
(N'ALTER TABLE dbo.MarketContacts ADD Sender nvarchar(200) NULL');

DECLARE @sql nvarchar(max);
DECLARE repair_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT SqlText FROM @changes;
OPEN repair_cursor;
FETCH NEXT FROM repair_cursor INTO @sql;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY EXEC sys.sp_executesql @sql; END TRY BEGIN CATCH
        /* 2705 = column already exists; 4902 = table absent. The remaining
           columns continue so one legacy shape cannot block the whole import. */
        IF ERROR_NUMBER() NOT IN (2705, 4902) PRINT ERROR_MESSAGE();
    END CATCH;
    FETCH NEXT FROM repair_cursor INTO @sql;
END;
CLOSE repair_cursor;
DEALLOCATE repair_cursor;
