using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSoundtrackQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MaximumSoundtrackQualityDistance",
                table: "Libraries",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SoundtrackQualityGateEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SoundtrackQualityReportingEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaximumSoundtrackQualityDistance",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "SoundtrackQualityGateEnabled",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "SoundtrackQualityReportingEnabled",
                table: "Libraries");
        }
    }
}
