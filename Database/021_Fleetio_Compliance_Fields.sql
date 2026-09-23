IF COL_LENGTH('dbo.Vehicles','FleetioVor') IS NULL ALTER TABLE dbo.Vehicles ADD FleetioVor bit NULL;
IF COL_LENGTH('dbo.Vehicles','FleetioPmiDueUtc') IS NULL ALTER TABLE dbo.Vehicles ADD FleetioPmiDueUtc datetimeoffset NULL;
IF COL_LENGTH('dbo.Vehicles','FleetioMotDueUtc') IS NULL ALTER TABLE dbo.Vehicles ADD FleetioMotDueUtc datetimeoffset NULL;
IF COL_LENGTH('dbo.Vehicles','FleetioServiceStatus') IS NULL ALTER TABLE dbo.Vehicles ADD FleetioServiceStatus nvarchar(160) NULL;
IF COL_LENGTH('dbo.Vehicles','FleetioLastSyncedUtc') IS NULL ALTER TABLE dbo.Vehicles ADD FleetioLastSyncedUtc datetimeoffset NULL;
