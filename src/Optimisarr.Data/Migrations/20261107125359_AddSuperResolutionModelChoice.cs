using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSuperResolutionModelChoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SuperResolutionEngine",
                table: "Libraries",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuperResolutionModel",
                table: "Libraries",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SuperResolutionScale",
                table: "Libraries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SuperResolutionRun",
                table: "Jobs",
                type: "TEXT",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SuperResolutionEngine",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "SuperResolutionModel",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "SuperResolutionScale",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "SuperResolutionRun",
                table: "Jobs");
        }
    }
}
