using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slh.Tms.Api.Migrations;

/// <summary>
/// Keeps StagedImports safe on SQL Server installations that have audit triggers and adds
/// the operational queue index used by Order Review, TachoMaster sync status and recovery rows.
/// </summary>
public partial class StagedImportsTriggerSafeAndOperationalIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_StagedImports_Entity_Status_ReceivedAtUtc'
      AND object_id = OBJECT_ID(N'[dbo].[StagedImports]')
)
BEGIN
    CREATE INDEX [IX_StagedImports_Entity_Status_ReceivedAtUtc]
    ON [dbo].[StagedImports] ([EntityType], [Status], [ReceivedAtUtc] DESC);
END
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
IF EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_StagedImports_Entity_Status_ReceivedAtUtc'
      AND object_id = OBJECT_ID(N'[dbo].[StagedImports]')
)
BEGIN
    DROP INDEX [IX_StagedImports_Entity_Status_ReceivedAtUtc] ON [dbo].[StagedImports];
END
""");
    }
}
