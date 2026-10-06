using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Casco.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AiUsageUnitEconomics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CostUsd",
                table: "AiUsages",
                newName: "EstimatedCostUsd");

            migrationBuilder.AddColumn<decimal>(
                name: "ActualCostUsd",
                table: "AiUsages",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Plan",
                table: "AiUsages",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "AiUsages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "AiUsages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AiProviderInvoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    Month = table.Column<string>(type: "text", nullable: false),
                    AmountUsd = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    EstimatedUsd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProviderInvoices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsages_ProjectId",
                table: "AiUsages",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsages_TaskId",
                table: "AiUsages",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_AiProviderInvoices_Provider_Month",
                table: "AiProviderInvoices",
                columns: new[] { "Provider", "Month" },
                unique: true);

            migrationBuilder.Sql("""
                UPDATE "AiUsages" a SET "ProjectId" = t."ProjectId" FROM "AgentTasks" t WHERE a."TaskId" = t."Id";
                UPDATE "AiUsages" SET "RetryCount" = GREATEST(split_part("Purpose", '#', 2)::int - 1, 0), "Purpose" = split_part("Purpose", '#', 1)
                WHERE "Purpose" ~ '#[0-9]+$';
                UPDATE "AiUsages" SET "ActualCostUsd" = 0 WHERE "ResponseCacheHit";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiProviderInvoices");

            migrationBuilder.DropIndex(
                name: "IX_AiUsages_ProjectId",
                table: "AiUsages");

            migrationBuilder.DropIndex(
                name: "IX_AiUsages_TaskId",
                table: "AiUsages");

            migrationBuilder.DropColumn(
                name: "ActualCostUsd",
                table: "AiUsages");

            migrationBuilder.DropColumn(
                name: "Plan",
                table: "AiUsages");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "AiUsages");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "AiUsages");

            migrationBuilder.RenameColumn(
                name: "EstimatedCostUsd",
                table: "AiUsages",
                newName: "CostUsd");
        }
    }
}
