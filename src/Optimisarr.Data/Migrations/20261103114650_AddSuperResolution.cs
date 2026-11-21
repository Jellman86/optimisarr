using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSuperResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SuperResolutionEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SuperResolutionQualityCheck",
                table: "Libraries",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                // "BrokenFramesOnly" rather than EF's generated "", which is not a member and would
                // fail to materialise. Existing libraries get the recommended check for upscales.
                defaultValue: "BrokenFramesOnly");

            migrationBuilder.AddColumn<bool>(
                name: "SuperResolutionApplied",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SuperResolutionEnabled",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "SuperResolutionQualityCheck",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "SuperResolutionApplied",
                table: "Jobs");
        }
    }
}
