-- Adds Fleetio alignment fields to vehicles without replacing existing master data.
IF COL_LENGTH('dbo.Vehicles', 'FleetioId') IS NULL ALTER TABLE dbo.Vehicles ADD FleetioId nvarchar(80) NULL;
IF COL_LENGTH('dbo.Vehicles', 'FleetioName') IS NULL ALTER TABLE dbo.Vehicles ADD FleetioName nvarchar(160) NULL;
IF COL_LENGTH('dbo.Vehicles', 'FleetioStatus') IS NULL ALTER TABLE dbo.Vehicles ADD FleetioStatus nvarchar(80) NULL;
