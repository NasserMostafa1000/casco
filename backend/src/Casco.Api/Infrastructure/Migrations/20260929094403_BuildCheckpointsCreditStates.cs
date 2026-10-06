using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casco.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BuildCheckpointsCreditStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CreditReservations_UserId",
                table: "CreditReservations");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                table: "ProjectVersions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FilesChanged",
                table: "ProjectVersions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "ProjectVersions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Parts",
                table: "ProjectVersions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Prompt",
                table: "ProjectVersions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloseReason",
                table: "CreditReservations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "CreditReservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Consumed",
                table: "CreditReservations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "CreditReservations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "reserved");

            migrationBuilder.AddColumn<int>(
                name: "Part",
                table: "AiUsages",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "CheckpointJson",
                table: "AgentTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContinuesTaskId",
                table: "AgentTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PagesBefore",
                table: "AgentTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Parts",
                table: "AgentTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Remaining",
                table: "AgentTasks",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditReservations_Status",
                table: "CreditReservations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CreditReservations_UserId_Status",
                table: "CreditReservations",
                columns: new[] { "UserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CreditReservations_Status",
                table: "CreditReservations");

            migrationBuilder.DropIndex(
                name: "IX_CreditReservations_UserId_Status",
                table: "CreditReservations");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "ProjectVersions");

            migrationBuilder.DropColumn(
                name: "FilesChanged",
                table: "ProjectVersions");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "ProjectVersions");

            migrationBuilder.DropColumn(
                name: "Parts",
                table: "ProjectVersions");

            migrationBuilder.DropColumn(
                name: "Prompt",
                table: "ProjectVersions");

            migrationBuilder.DropColumn(
                name: "CloseReason",
                table: "CreditReservations");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "CreditReservations");

            migrationBuilder.DropColumn(
                name: "Consumed",
                table: "CreditReservations");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "CreditReservations");

            migrationBuilder.DropColumn(
                name: "Part",
                table: "AiUsages");

            migrationBuilder.DropColumn(
                name: "CheckpointJson",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "ContinuesTaskId",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "PagesBefore",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "Parts",
                table: "AgentTasks");

            migrationBuilder.DropColumn(
                name: "Remaining",
                table: "AgentTasks");

            migrationBuilder.CreateIndex(
                name: "IX_CreditReservations_UserId",
                table: "CreditReservations",
                column: "UserId");
        }
    }
}
