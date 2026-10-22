-- Repair the Tacho operational state columns required by the Driver Master model.
-- This is idempotent because older production databases may have recorded the
-- corresponding EF migration without applying its additive SQL.
IF COL_LENGTH(N'dbo.Drivers', N'TachoCardNumber') IS NULL ALTER TABLE dbo.Drivers ADD TachoCardNumber nvarchar(80) NULL;
IF COL_LENGTH(N'dbo.Drivers', N'LastTachoVehicleCode') IS NULL ALTER TABLE dbo.Drivers ADD LastTachoVehicleCode nvarchar(80) NULL;
IF COL_LENGTH(N'dbo.Drivers', N'LastTachoCardInsertedUtc') IS NULL ALTER TABLE dbo.Drivers ADD LastTachoCardInsertedUtc datetimeoffset NULL;
IF COL_LENGTH(N'dbo.Drivers', N'LastTachoDutyEndUtc') IS NULL ALTER TABLE dbo.Drivers ADD LastTachoDutyEndUtc datetimeoffset NULL;
IF COL_LENGTH(N'dbo.Drivers', N'LastTachoDutyOpen') IS NULL ALTER TABLE dbo.Drivers ADD LastTachoDutyOpen bit NOT NULL CONSTRAINT DF_Drivers_LastTachoDutyOpen_089 DEFAULT (0);
IF COL_LENGTH(N'dbo.Drivers', N'TachoWorkTodayMinutes') IS NULL ALTER TABLE dbo.Drivers ADD TachoWorkTodayMinutes int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoDriveTodayMinutes') IS NULL ALTER TABLE dbo.Drivers ADD TachoDriveTodayMinutes int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoAvailableTodayMinutes') IS NULL ALTER TABLE dbo.Drivers ADD TachoAvailableTodayMinutes int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoRestTodayMinutes') IS NULL ALTER TABLE dbo.Drivers ADD TachoRestTodayMinutes int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoBreakCount') IS NULL ALTER TABLE dbo.Drivers ADD TachoBreakCount int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoBreakMinutes') IS NULL ALTER TABLE dbo.Drivers ADD TachoBreakMinutes int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoDriveAvailableTodayMinutes') IS NULL ALTER TABLE dbo.Drivers ADD TachoDriveAvailableTodayMinutes int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoDriveAvailableWeekMinutes') IS NULL ALTER TABLE dbo.Drivers ADD TachoDriveAvailableWeekMinutes int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoWorkAvailableWeekMinutes') IS NULL ALTER TABLE dbo.Drivers ADD TachoWorkAvailableWeekMinutes int NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Drivers_TachoCardNumber' AND object_id = OBJECT_ID(N'dbo.Drivers'))
    CREATE INDEX IX_Drivers_TachoCardNumber ON dbo.Drivers (TachoCardNumber) WHERE TachoCardNumber IS NOT NULL;
