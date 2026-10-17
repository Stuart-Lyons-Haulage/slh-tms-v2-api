IF OBJECT_ID(N'dbo.master_drivers', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.master_drivers', N'EmployeeNumber') IS NULL ALTER TABLE dbo.master_drivers ADD EmployeeNumber nvarchar(40) NULL;
    IF COL_LENGTH(N'dbo.master_drivers', N'CardLastRead') IS NULL ALTER TABLE dbo.master_drivers ADD CardLastRead date NULL;
    IF COL_LENGTH(N'dbo.master_drivers', N'Email') IS NULL ALTER TABLE dbo.master_drivers ADD Email nvarchar(320) NULL;
    IF COL_LENGTH(N'dbo.master_drivers', N'SourceSite') IS NULL ALTER TABLE dbo.master_drivers ADD SourceSite nvarchar(160) NULL;
    IF COL_LENGTH(N'dbo.master_drivers', N'StartedOn') IS NULL ALTER TABLE dbo.master_drivers ADD StartedOn date NULL;
    IF COL_LENGTH(N'dbo.master_drivers', N'LicencePassDate') IS NULL ALTER TABLE dbo.master_drivers ADD LicencePassDate date NULL;
    IF COL_LENGTH(N'dbo.master_drivers', N'LicenceCheckDue') IS NULL ALTER TABLE dbo.master_drivers ADD LicenceCheckDue date NULL;
    IF COL_LENGTH(N'dbo.master_drivers', N'LicencePhotoExpiry') IS NULL ALTER TABLE dbo.master_drivers ADD LicencePhotoExpiry date NULL;
    IF COL_LENGTH(N'dbo.master_drivers', N'DqcExpiry') IS NULL ALTER TABLE dbo.master_drivers ADD DqcExpiry date NULL;
END;
