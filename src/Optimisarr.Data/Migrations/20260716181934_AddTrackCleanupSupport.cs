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

            // Existing probed videos predate SubtitleLanguages. Requeue only rows known to have
            // subtitle streams so the background probe fills their positional tags before a
            // cleanup rule can decide that any stream is removable. Audio-only timed lyrics are
            // outside the video-only cleanup contract and must not be disturbed.
            migrationBuilder.Sql(
                """
                UPDATE MediaFiles
                SET Status = 'Discovered', ProbedAt = NULL
                WHERE Status = 'Probed'
                  AND MediaKind = 'Video'
                  AND COALESCE(SubtitleTrackCount, 0) > 0;
                """);
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
