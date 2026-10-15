using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdaptiveAudioImageQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdaptiveAudioQuality",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AdaptiveImageQuality",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AdaptiveAudioBitrateKbps",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdaptiveImageQuality",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PerceptualSearchJson",
                table: "Jobs",
                type: "TEXT",
                maxLength: 262144,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Jobs_PerceptualSearch",
                table: "Jobs",
                sql: "(AdaptiveAudioBitrateKbps IS NULL OR AdaptiveAudioBitrateKbps BETWEEN 8 AND 512)\nAND (AdaptiveImageQuality IS NULL OR AdaptiveImageQuality BETWEEN 0 AND 100)\nAND (PerceptualSearchJson IS NULL OR length(PerceptualSearchJson) <= 262144)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Jobs_PerceptualSearch",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AdaptiveAudioQuality",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "AdaptiveImageQuality",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "AdaptiveAudioBitrateKbps",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AdaptiveImageQuality",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PerceptualSearchJson",
                table: "Jobs");
        }
    }
}
