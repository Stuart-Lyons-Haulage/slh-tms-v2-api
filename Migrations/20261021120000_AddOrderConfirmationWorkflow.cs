using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slh.Tms.Api.Migrations;

/// <summary>
/// Add order confirmation workflow: ConfirmationStatus, ConfirmedAtUtc, ConfirmedBy
/// Orders transition from AwaitingReview to Confirmed before planning.
/// </summary>
public partial class AddOrderConfirmationWorkflow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
IF COL_LENGTH(N'dbo.TransportOrders', N'ConfirmationStatus') IS NULL
    ALTER TABLE [dbo].[TransportOrders] ADD [ConfirmationStatus] int NOT NULL CONSTRAINT [DF_TransportOrders_ConfirmationStatus] DEFAULT (0);
IF COL_LENGTH(N'dbo.TransportOrders', N'ConfirmedAtUtc') IS NULL
    ALTER TABLE [dbo].[TransportOrders] ADD [ConfirmedAtUtc] datetimeoffset NULL;
IF COL_LENGTH(N'dbo.TransportOrders', N'ConfirmedBy') IS NULL
    ALTER TABLE [dbo].[TransportOrders] ADD [ConfirmedBy] nvarchar(160) NULL;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_TransportOrders_ConfirmationStatus'
      AND object_id = OBJECT_ID(N'[dbo].[TransportOrders]')
)
BEGIN
    CREATE INDEX [IX_TransportOrders_ConfirmationStatus]
    ON [dbo].[TransportOrders] ([ConfirmationStatus])
    WHERE [ConfirmationStatus] = 0;
END
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_TransportOrders_ConfirmationStatus'
      AND object_id = OBJECT_ID(N'[dbo].[TransportOrders]')
)
BEGIN
    DROP INDEX [IX_TransportOrders_ConfirmationStatus] ON [dbo].[TransportOrders];
END

IF COL_LENGTH(N'dbo.TransportOrders', N'ConfirmedBy') IS NOT NULL
    ALTER TABLE [dbo].[TransportOrders] DROP COLUMN [ConfirmedBy];
IF COL_LENGTH(N'dbo.TransportOrders', N'ConfirmedAtUtc') IS NOT NULL
    ALTER TABLE [dbo].[TransportOrders] DROP COLUMN [ConfirmedAtUtc];
IF COL_LENGTH(N'dbo.TransportOrders', N'ConfirmationStatus') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'[dbo].[DF_TransportOrders_ConfirmationStatus]', N'D') IS NOT NULL
        ALTER TABLE [dbo].[TransportOrders] DROP CONSTRAINT [DF_TransportOrders_ConfirmationStatus];
    ALTER TABLE [dbo].[TransportOrders] DROP COLUMN [ConfirmationStatus];
END
""");
    }
}
