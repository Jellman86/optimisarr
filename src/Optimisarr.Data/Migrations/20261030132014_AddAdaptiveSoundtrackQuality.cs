using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdaptiveSoundtrackQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdaptiveSoundtrackQuality",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SoundtrackSearchJson",
                table: "Jobs",
                type: "TEXT",
                maxLength: 1048576,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdaptiveSoundtrackQuality",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "SoundtrackSearchJson",
                table: "Jobs");
        }
    }
}
