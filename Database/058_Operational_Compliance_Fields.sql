IF OBJECT_ID(N'dbo.Drivers', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Drivers', N'CPCExpiry') IS NULL ALTER TABLE dbo.Drivers ADD CPCExpiry date NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'DigitalTachoCardExpiry') IS NULL ALTER TABLE dbo.Drivers ADD DigitalTachoCardExpiry date NULL;
    IF COL_LENGTH(N'dbo.Drivers', N'MedicalExpiry') IS NULL ALTER TABLE dbo.Drivers ADD MedicalExpiry date NULL;
END;

IF OBJECT_ID(N'dbo.Vehicles', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Vehicles', N'MOTExpiry') IS NULL ALTER TABLE dbo.Vehicles ADD MOTExpiry date NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'TachoCalibrationExpiry') IS NULL ALTER TABLE dbo.Vehicles ADD TachoCalibrationExpiry date NULL;
    IF COL_LENGTH(N'dbo.Vehicles', N'VehicleTestExpiry') IS NULL ALTER TABLE dbo.Vehicles ADD VehicleTestExpiry date NULL;
END;

IF OBJECT_ID(N'dbo.Sites', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Sites', N'OperationalRegion') IS NULL
    ALTER TABLE dbo.Sites ADD OperationalRegion nvarchar(80) NULL;
