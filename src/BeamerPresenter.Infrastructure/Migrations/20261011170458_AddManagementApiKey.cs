using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeamerPresenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddManagementApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApiKeyHash",
                table: "Settings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiKeySalt",
                table: "Settings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApiKeyHash",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ApiKeySalt",
                table: "Settings");
        }
    }
}
