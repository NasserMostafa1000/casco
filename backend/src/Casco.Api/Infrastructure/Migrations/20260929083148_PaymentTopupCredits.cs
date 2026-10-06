using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casco.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PaymentTopupCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TopupCredits",
                table: "Payments",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TopupCredits",
                table: "Payments");
        }
    }
}
