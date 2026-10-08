using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Casco.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DesktopMachineTrial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DesktopChatMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesktopChatMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DesktopMachines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FreePromptUsed = table.Column<bool>(type: "boolean", nullable: false),
                    ShareMask = table.Column<int>(type: "integer", nullable: false),
                    TrialEndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesktopMachines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DesktopChatMessages_CreatedAt",
                table: "DesktopChatMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DesktopChatMessages_UserId",
                table: "DesktopChatMessages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DesktopMachines_MachineHash",
                table: "DesktopMachines",
                column: "MachineHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DesktopChatMessages");

            migrationBuilder.DropTable(
                name: "DesktopMachines");
        }
    }
}
