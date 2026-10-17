using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Slh.Tms.Api.Data;

#nullable disable

namespace Slh.Tms.Api.Migrations;

[DbContext(typeof(TmsDbContext))]
[Migration("20260908173500_AddStagingPerformanceIndex")]
public partial class AddStagingPerformanceIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_StagedImports_Status_EntityType_ReceivedAtUtc",
            table: "StagedImports",
            columns: new[] { "Status", "EntityType", "ReceivedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_StagedImports_Status_EntityType_ReceivedAtUtc",
            table: "StagedImports");
    }
}
