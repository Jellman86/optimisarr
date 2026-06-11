using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaFileMediaKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MediaKind",
                table: "MediaFiles",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                // Existing rows are unclassified until re-probed; a valid enum name keeps
                // them readable (an empty string would fail to map back to MediaKind).
                defaultValue: "Unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MediaKind",
                table: "MediaFiles");
        }
    }
}
