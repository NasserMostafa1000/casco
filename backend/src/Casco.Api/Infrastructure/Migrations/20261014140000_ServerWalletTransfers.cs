using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casco.Api.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261014140000_ServerWalletTransfers")]
public class ServerWalletTransfers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Purpose",
            table: "WalletTransfers",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "pro");

        migrationBuilder.AddColumn<Guid>(
            name: "ProjectId",
            table: "WalletTransfers",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "HostingTier",
            table: "WalletTransfers",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Interval",
            table: "WalletTransfers",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Purpose", table: "WalletTransfers");
        migrationBuilder.DropColumn(name: "ProjectId", table: "WalletTransfers");
        migrationBuilder.DropColumn(name: "HostingTier", table: "WalletTransfers");
        migrationBuilder.DropColumn(name: "Interval", table: "WalletTransfers");
    }
}
