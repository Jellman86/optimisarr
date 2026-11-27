using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordTrackSizesForTrackCleanupMinimum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioTrackBytes",
                table: "MediaFiles",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubtitleTrackBytes",
                table: "MediaFiles",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MinimumTrackRemovalSavingBytes",
                table: "Libraries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MinimumTrackRemovalSavingPercent",
                table: "Libraries",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioTrackBytes",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "SubtitleTrackBytes",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "MinimumTrackRemovalSavingBytes",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "MinimumTrackRemovalSavingPercent",
                table: "Libraries");
        }
    }
}
