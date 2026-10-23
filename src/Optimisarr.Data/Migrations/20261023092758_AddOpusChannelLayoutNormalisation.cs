using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOpusChannelLayoutNormalisation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NormalizeOpusChannelLayout",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                // Existing libraries keep the recommended behaviour when the column appears.
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "AppliedAudioFilter",
                table: "Jobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NormalizeOpusChannelLayout",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "AppliedAudioFilter",
                table: "Jobs");
        }
    }
}
