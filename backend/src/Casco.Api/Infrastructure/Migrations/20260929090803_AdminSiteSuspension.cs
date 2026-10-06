using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casco.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AdminSiteSuspension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AdminSuspendedAt",
                table: "Projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AdminSuspendedBy",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdminSuspensionReason",
                table: "Projects",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAdminSuspended",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminSuspendedAt",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AdminSuspendedBy",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AdminSuspensionReason",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "IsAdminSuspended",
                table: "Projects");
        }
    }
}
