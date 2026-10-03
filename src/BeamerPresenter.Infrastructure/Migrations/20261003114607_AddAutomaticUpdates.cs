using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeamerPresenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutomaticUpdatesEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutomaticUpdatesEnabled",
                table: "Settings");
        }
    }
}
