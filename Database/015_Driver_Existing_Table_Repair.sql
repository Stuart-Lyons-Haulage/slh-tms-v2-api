/* Repairs driver columns on an existing production table. Safe to rerun. */
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Drivers', N'TachoName') IS NULL ALTER TABLE dbo.Drivers ADD TachoName nvarchar(160) NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'MobileNumber') IS NULL ALTER TABLE dbo.Drivers ADD MobileNumber nvarchar(40) NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'DriverType') IS NULL ALTER TABLE dbo.Drivers ADD DriverType nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'DriverGroup') IS NULL ALTER TABLE dbo.Drivers ADD DriverGroup nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'Skills') IS NULL ALTER TABLE dbo.Drivers ADD Skills nvarchar(160) NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'Active') IS NULL
        ALTER TABLE dbo.Drivers ADD Active bit NOT NULL CONSTRAINT DF_Drivers_Active_Repair DEFAULT(1);
END;
