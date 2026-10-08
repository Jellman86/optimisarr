using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityWatcherShowViewerNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowViewerNames",
                table: "ActivityWatchers",
                type: "INTEGER",
                nullable: false,
                // Existing watchers keep showing who is watching, the setting's default.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowViewerNames",
                table: "ActivityWatchers");
        }
    }
}
