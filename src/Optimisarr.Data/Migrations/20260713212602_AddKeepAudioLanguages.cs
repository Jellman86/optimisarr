using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKeepAudioLanguages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioLanguages",
                table: "MediaFiles",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeepAudioLanguages",
                table: "Libraries",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            // Rows probed by an older build have no per-track language data. Queue only affected
            // videos for the existing background probe worker; otherwise an already-clean remux is
            // declared ineligible before the dispatcher's job-time fallback can ever run.
            migrationBuilder.Sql(
                """
                UPDATE MediaFiles
                SET Status = 'Discovered', ProbedAt = NULL
                WHERE Status = 'Probed'
                  AND MediaKind = 'Video'
                  AND COALESCE(AudioTrackCount, 0) > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioLanguages",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "KeepAudioLanguages",
                table: "Libraries");
        }
    }
}
