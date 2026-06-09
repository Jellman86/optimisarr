using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWatcherRefreshOnReplace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RefreshOnReplace",
                table: "ActivityWatchers",
                type: "INTEGER",
                nullable: false,
                // Opt-out feature: existing watchers refresh by default too, honouring
                // "configure each server once". A library re-scan is non-destructive.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RefreshOnReplace",
                table: "ActivityWatchers");
        }
    }
}
