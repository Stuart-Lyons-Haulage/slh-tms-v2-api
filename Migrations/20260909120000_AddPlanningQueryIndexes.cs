using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slh.Tms.Api.Migrations;

public partial class AddPlanningQueryIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_TransportOrders_CollectionDate_Status",
            table: "TransportOrders",
            columns: new[] { "CollectionDate", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_Loads_PlanningDate_Status",
            table: "Loads",
            columns: new[] { "PlanningDate", "Status" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Loads_PlanningDate_Status",
            table: "Loads");

        migrationBuilder.DropIndex(
            name: "IX_TransportOrders_CollectionDate_Status",
            table: "TransportOrders");
    }
}
