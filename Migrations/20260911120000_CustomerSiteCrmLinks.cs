using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Slh.Tms.Api.Migrations;

public partial class CustomerSiteCrmLinks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "TradingName", table: "Customers", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(name: "AccountOwner", table: "Customers", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(name: "ServiceNotes", table: "Customers", maxLength: 1000, nullable: true);
        migrationBuilder.AddColumn<string>(name: "DefaultSiteCode", table: "Customers", maxLength: 80, nullable: true);
        migrationBuilder.AddColumn<string>(name: "CustomerCode", table: "Sites", maxLength: 40, nullable: true);
        migrationBuilder.CreateTable(
            name: "CustomerEmailRoutes",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                CustomerCode = table.Column<string>(maxLength: 40, nullable: false),
                SenderEmail = table.Column<string>(maxLength: 320, nullable: true),
                SenderDomain = table.Column<string>(maxLength: 320, nullable: true),
                SubjectContains = table.Column<string>(maxLength: 200, nullable: true),
                ParserType = table.Column<string>(maxLength: 120, nullable: true),
                DefaultSiteCode = table.Column<string>(maxLength: 80, nullable: true),
                RequiresReview = table.Column<bool>(nullable: false),
                Active = table.Column<bool>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_CustomerEmailRoutes", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_Customers_Code", table: "Customers", column: "Code", unique: false);
        migrationBuilder.CreateIndex(name: "IX_Sites_CustomerCode_ExternalCode", table: "Sites", columns: new[] { "CustomerCode", "ExternalCode" });
        migrationBuilder.CreateIndex(name: "IX_CustomerEmailRoutes_CustomerCode_SenderEmail_SenderDomain", table: "CustomerEmailRoutes", columns: new[] { "CustomerCode", "SenderEmail", "SenderDomain" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CustomerEmailRoutes");
        migrationBuilder.DropIndex(name: "IX_Customers_Code", table: "Customers");
        migrationBuilder.DropIndex(name: "IX_Sites_CustomerCode_ExternalCode", table: "Sites");
        migrationBuilder.DropColumn(name: "TradingName", table: "Customers");
        migrationBuilder.DropColumn(name: "AccountOwner", table: "Customers");
        migrationBuilder.DropColumn(name: "ServiceNotes", table: "Customers");
        migrationBuilder.DropColumn(name: "DefaultSiteCode", table: "Customers");
        migrationBuilder.DropColumn(name: "CustomerCode", table: "Sites");
    }
}
