using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casco.Api.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261023040000_OwnerEarningsMarketplace")]
public class OwnerEarningsMarketplace : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AdminNote",
            table: "Users",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "OrderId",
            table: "Payments",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Payments_OrderId",
            table: "Payments",
            column: "OrderId");

        migrationBuilder.AddColumn<string>(
            name: "PaymentStatus",
            table: "Orders",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "paid");

        migrationBuilder.AddColumn<string>(
            name: "ProviderPaymentId",
            table: "Orders",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "OwnerEarningEntries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                PaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                GrossMinor = table.Column<int>(type: "integer", nullable: false),
                FeeMinor = table.Column<int>(type: "integer", nullable: false),
                NetMinor = table.Column<int>(type: "integer", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_OwnerEarningEntries", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_OwnerEarningEntries_UserId",
            table: "OwnerEarningEntries",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_OwnerEarningEntries_PaymentId",
            table: "OwnerEarningEntries",
            column: "PaymentId");

        migrationBuilder.CreateIndex(
            name: "IX_OwnerEarningEntries_CreatedAt",
            table: "OwnerEarningEntries",
            column: "CreatedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "OwnerEarningEntries");
        migrationBuilder.DropIndex(name: "IX_Payments_OrderId", table: "Payments");
        migrationBuilder.DropColumn(name: "OrderId", table: "Payments");
        migrationBuilder.DropColumn(name: "AdminNote", table: "Users");
        migrationBuilder.DropColumn(name: "PaymentStatus", table: "Orders");
        migrationBuilder.DropColumn(name: "ProviderPaymentId", table: "Orders");
    }
}
