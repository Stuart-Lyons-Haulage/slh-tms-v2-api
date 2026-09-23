using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slh.Tms.Api.Migrations;

/// <summary>
/// Persists the latest TachoMaster card insertion, duty and remaining-hours state against Driver Master.
/// Identity must be driven by TachoMaster Member Code first, with persisted DB Tacho card number as the only fallback.
/// </summary>
public partial class AddDriverTachoOperationalState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
IF COL_LENGTH(N'dbo.Drivers', N'TachoCardNumber') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [TachoCardNumber] nvarchar(80) NULL;
IF COL_LENGTH(N'dbo.Drivers', N'LastTachoVehicleCode') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [LastTachoVehicleCode] nvarchar(80) NULL;
IF COL_LENGTH(N'dbo.Drivers', N'LastTachoCardInsertedUtc') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [LastTachoCardInsertedUtc] datetimeoffset NULL;
IF COL_LENGTH(N'dbo.Drivers', N'LastTachoDutyEndUtc') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [LastTachoDutyEndUtc] datetimeoffset NULL;
IF COL_LENGTH(N'dbo.Drivers', N'LastTachoDutyOpen') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [LastTachoDutyOpen] bit NOT NULL CONSTRAINT [DF_Drivers_LastTachoDutyOpen] DEFAULT (0);
IF COL_LENGTH(N'dbo.Drivers', N'TachoWorkTodayMinutes') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [TachoWorkTodayMinutes] int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoDriveTodayMinutes') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [TachoDriveTodayMinutes] int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoAvailableTodayMinutes') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [TachoAvailableTodayMinutes] int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoRestTodayMinutes') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [TachoRestTodayMinutes] int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoBreakCount') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [TachoBreakCount] int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoBreakMinutes') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [TachoBreakMinutes] int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoDriveAvailableTodayMinutes') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [TachoDriveAvailableTodayMinutes] int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoDriveAvailableWeekMinutes') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [TachoDriveAvailableWeekMinutes] int NULL;
IF COL_LENGTH(N'dbo.Drivers', N'TachoWorkAvailableWeekMinutes') IS NULL
    ALTER TABLE [dbo].[Drivers] ADD [TachoWorkAvailableWeekMinutes] int NULL;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_Drivers_TachoCardNumber'
      AND object_id = OBJECT_ID(N'[dbo].[Drivers]')
)
BEGIN
    CREATE INDEX [IX_Drivers_TachoCardNumber]
    ON [dbo].[Drivers] ([TachoCardNumber])
    WHERE [TachoCardNumber] IS NOT NULL;
END
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_Drivers_TachoCardNumber'
      AND object_id = OBJECT_ID(N'[dbo].[Drivers]')
)
BEGIN
    DROP INDEX [IX_Drivers_TachoCardNumber] ON [dbo].[Drivers];
END

IF COL_LENGTH(N'dbo.Drivers', N'TachoWorkAvailableWeekMinutes') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [TachoWorkAvailableWeekMinutes];
IF COL_LENGTH(N'dbo.Drivers', N'TachoDriveAvailableWeekMinutes') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [TachoDriveAvailableWeekMinutes];
IF COL_LENGTH(N'dbo.Drivers', N'TachoDriveAvailableTodayMinutes') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [TachoDriveAvailableTodayMinutes];
IF COL_LENGTH(N'dbo.Drivers', N'TachoBreakMinutes') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [TachoBreakMinutes];
IF COL_LENGTH(N'dbo.Drivers', N'TachoBreakCount') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [TachoBreakCount];
IF COL_LENGTH(N'dbo.Drivers', N'TachoRestTodayMinutes') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [TachoRestTodayMinutes];
IF COL_LENGTH(N'dbo.Drivers', N'TachoAvailableTodayMinutes') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [TachoAvailableTodayMinutes];
IF COL_LENGTH(N'dbo.Drivers', N'TachoDriveTodayMinutes') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [TachoDriveTodayMinutes];
IF COL_LENGTH(N'dbo.Drivers', N'TachoWorkTodayMinutes') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [TachoWorkTodayMinutes];
IF COL_LENGTH(N'dbo.Drivers', N'LastTachoDutyOpen') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'[dbo].[DF_Drivers_LastTachoDutyOpen]', N'D') IS NOT NULL
        ALTER TABLE [dbo].[Drivers] DROP CONSTRAINT [DF_Drivers_LastTachoDutyOpen];
    ALTER TABLE [dbo].[Drivers] DROP COLUMN [LastTachoDutyOpen];
END
IF COL_LENGTH(N'dbo.Drivers', N'LastTachoDutyEndUtc') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [LastTachoDutyEndUtc];
IF COL_LENGTH(N'dbo.Drivers', N'LastTachoCardInsertedUtc') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [LastTachoCardInsertedUtc];
IF COL_LENGTH(N'dbo.Drivers', N'LastTachoVehicleCode') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [LastTachoVehicleCode];
IF COL_LENGTH(N'dbo.Drivers', N'TachoCardNumber') IS NOT NULL ALTER TABLE [dbo].[Drivers] DROP COLUMN [TachoCardNumber];
""");
    }
}
