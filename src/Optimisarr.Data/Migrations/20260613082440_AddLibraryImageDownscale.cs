using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryImageDownscale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageDownscaleMode",
                table: "Libraries",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                // Existing libraries default to no downscale; the string must parse back to the enum.
                defaultValue: "None");

            migrationBuilder.AddColumn<int>(
                name: "ImageDownscaleValue",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageDownscaleMode",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "ImageDownscaleValue",
                table: "Libraries");
        }
    }
}
