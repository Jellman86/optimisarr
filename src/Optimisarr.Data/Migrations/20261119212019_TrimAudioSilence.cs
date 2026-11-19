using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrimAudioSilence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing libraries get the same defaults a new one has; trimming itself stays off.
            migrationBuilder.AddColumn<double>(
                name: "AudioSilenceMinimumSeconds",
                table: "Libraries",
                type: "REAL",
                nullable: false,
                defaultValue: 2.0);

            migrationBuilder.AddColumn<double>(
                name: "AudioSilenceThresholdDb",
                table: "Libraries",
                type: "REAL",
                nullable: false,
                defaultValue: -60.0);

            migrationBuilder.AddColumn<bool>(
                name: "TrimAudioSilence",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DetectedSilenceTrim",
                table: "Jobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioSilenceMinimumSeconds",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "AudioSilenceThresholdDb",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "TrimAudioSilence",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "DetectedSilenceTrim",
                table: "Jobs");
        }
    }
}
