using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casco.Api.Infrastructure.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261020170000_ChatSuggestions")]
    public partial class ChatSuggestions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SuggestionsJson",
                table: "ChatMessages",
                type: "text",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "SuggestionsJson", table: "ChatMessages");
        }
    }
}
