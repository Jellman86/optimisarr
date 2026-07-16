using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackCleanupSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubtitleLanguages",
                table: "MediaFiles",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeepSubtitleLanguages",
                table: "Libraries",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnqueueReason",
                table: "Jobs",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubtitleLanguages",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "KeepSubtitleLanguages",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "EnqueueReason",
                table: "Jobs");
        }
    }
}
