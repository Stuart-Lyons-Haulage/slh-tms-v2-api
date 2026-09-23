using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Slh.Tms.Api.Data;

#nullable disable

namespace Slh.Tms.Api.Migrations;

[DbContext(typeof(TmsDbContext))]
[Migration("20260831214900_CanonicalRelationalPlanning")]
public partial class CanonicalRelationalPlanning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Runs",
            columns: table => new
            {
                RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PlanningDate = table.Column<DateOnly>(type: "date", nullable: false),
                RunReference = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Runs", x => x.RunId);
            });

        migrationBuilder.CreateTable(
            name: "RunOrderAllocations",
            columns: table => new
            {
                AllocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Pallets = table.Column<int>(type: "int", nullable: false),
                Trolleys = table.Column<int>(type: "int", nullable: false),
                Trays = table.Column<int>(type: "int", nullable: false),
                CapacityUnits = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                SourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RunOrderAllocations", x => x.AllocationId);
                table.ForeignKey(
                    name: "FK_RunOrderAllocations_OrderRevisions_SourceRevisionId",
                    column: x => x.SourceRevisionId,
                    principalTable: "OrderRevisions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_RunOrderAllocations_Runs_RunId",
                    column: x => x.RunId,
                    principalTable: "Runs",
                    principalColumn: "RunId",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_RunOrderAllocations_TransportOrders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "TransportOrders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "RunResourceAllocations",
            columns: table => new
            {
                ResourceAllocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DriverId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TrailerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AllocatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                AllocatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RunResourceAllocations", x => x.ResourceAllocationId);
                table.ForeignKey(
                    name: "FK_RunResourceAllocations_Drivers_DriverId",
                    column: x => x.DriverId,
                    principalTable: "Drivers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_RunResourceAllocations_Runs_RunId",
                    column: x => x.RunId,
                    principalTable: "Runs",
                    principalColumn: "RunId",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_RunResourceAllocations_Trailers_TrailerId",
                    column: x => x.TrailerId,
                    principalTable: "Trailers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_RunResourceAllocations_Vehicles_VehicleId",
                    column: x => x.VehicleId,
                    principalTable: "Vehicles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "RunStatusHistory",
            columns: table => new
            {
                HistoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                ChangedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RunStatusHistory", x => x.HistoryId);
                table.ForeignKey(
                    name: "FK_RunStatusHistory_Runs_RunId",
                    column: x => x.RunId,
                    principalTable: "Runs",
                    principalColumn: "RunId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RunStops",
            columns: table => new
            {
                RunStopId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sequence = table.Column<int>(type: "int", nullable: false),
                SiteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PlannedArrival = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                PlannedDeparture = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                ActualArrival = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                ActualDeparture = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                GeofenceVisitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RunStops", x => x.RunStopId);
                table.ForeignKey(
                    name: "FK_RunStops_GeofenceVisits_GeofenceVisitId",
                    column: x => x.GeofenceVisitId,
                    principalTable: "GeofenceVisits",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_RunStops_Runs_RunId",
                    column: x => x.RunId,
                    principalTable: "Runs",
                    principalColumn: "RunId",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_RunStops_Sites_SiteId",
                    column: x => x.SiteId,
                    principalTable: "Sites",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "RunTrackingStates",
            columns: table => new
            {
                RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                LastLatitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                LastLongitude = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                LastUpdated = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                ETAMinutes = table.Column<int>(type: "int", nullable: true),
                TrackingSource = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RunTrackingStates", x => x.RunId);
                table.ForeignKey(
                    name: "FK_RunTrackingStates_Runs_RunId",
                    column: x => x.RunId,
                    principalTable: "Runs",
                    principalColumn: "RunId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Runs_Status",
            table: "Runs",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "UX_Runs_PlanningDate_RunReference",
            table: "Runs",
            columns: new[] { "PlanningDate", "RunReference" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RunOrderAllocations_OrderId",
            table: "RunOrderAllocations",
            column: "OrderId");

        migrationBuilder.CreateIndex(
            name: "IX_RunOrderAllocations_SourceRevisionId",
            table: "RunOrderAllocations",
            column: "SourceRevisionId");

        migrationBuilder.CreateIndex(
            name: "UX_RunOrderAllocations_RunId_OrderId",
            table: "RunOrderAllocations",
            columns: new[] { "RunId", "OrderId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RunResourceAllocations_DriverId",
            table: "RunResourceAllocations",
            column: "DriverId");

        migrationBuilder.CreateIndex(
            name: "IX_RunResourceAllocations_TrailerId",
            table: "RunResourceAllocations",
            column: "TrailerId");

        migrationBuilder.CreateIndex(
            name: "UX_RunResourceAllocations_RunId",
            table: "RunResourceAllocations",
            column: "RunId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RunResourceAllocations_VehicleId",
            table: "RunResourceAllocations",
            column: "VehicleId");

        migrationBuilder.CreateIndex(
            name: "IX_RunStatusHistory_RunId_ChangedAt",
            table: "RunStatusHistory",
            columns: new[] { "RunId", "ChangedAt" });

        migrationBuilder.CreateIndex(
            name: "UX_RunStops_GeofenceVisitId",
            table: "RunStops",
            column: "GeofenceVisitId",
            unique: true,
            filter: "[GeofenceVisitId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "UX_RunStops_RunId_Sequence",
            table: "RunStops",
            columns: new[] { "RunId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RunStops_SiteId",
            table: "RunStops",
            column: "SiteId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "RunOrderAllocations");
        migrationBuilder.DropTable(name: "RunResourceAllocations");
        migrationBuilder.DropTable(name: "RunStatusHistory");
        migrationBuilder.DropTable(name: "RunStops");
        migrationBuilder.DropTable(name: "RunTrackingStates");
        migrationBuilder.DropTable(name: "Runs");
    }
}
