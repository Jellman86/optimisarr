using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class BindVerifiedFileIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OriginalSha256",
                table: "Replacements",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputSha256",
                table: "Replacements",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedOutputSha256",
                table: "Jobs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedSourceSha256",
                table: "Jobs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginalSha256",
                table: "Replacements");

            migrationBuilder.DropColumn(
                name: "OutputSha256",
                table: "Replacements");

            migrationBuilder.DropColumn(
                name: "VerifiedOutputSha256",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "VerifiedSourceSha256",
                table: "Jobs");
        }
    }
}
