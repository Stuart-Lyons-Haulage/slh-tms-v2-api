IF OBJECT_ID(N'dbo.master_vehicles', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.master_vehicles', N'VIN') IS NULL ALTER TABLE dbo.master_vehicles ADD VIN nvarchar(40) NULL;
    IF COL_LENGTH(N'dbo.master_vehicles', N'VehicleSite') IS NULL ALTER TABLE dbo.master_vehicles ADD VehicleSite nvarchar(160) NULL;
    IF COL_LENGTH(N'dbo.master_vehicles', N'OwnerType') IS NULL ALTER TABLE dbo.master_vehicles ADD OwnerType nvarchar(80) NULL;
END;
