IF OBJECT_ID(N'dbo.Vehicles', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Vehicles', N'VIN') IS NULL ALTER TABLE dbo.Vehicles ADD VIN nvarchar(40) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'OwnerType') IS NULL ALTER TABLE dbo.Vehicles ADD OwnerType nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'VehicleSite') IS NULL ALTER TABLE dbo.Vehicles ADD VehicleSite nvarchar(160) NULL;
END;
