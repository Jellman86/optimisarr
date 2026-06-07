using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryRuleOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExcludePaths",
                table: "Libraries",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HdrHandling",
                table: "Libraries",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxHeight",
                table: "Libraries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MinFileSizeBytes",
                table: "Libraries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TargetContainer",
                table: "Libraries",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetVideoCodec",
                table: "Libraries",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExcludePaths",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "HdrHandling",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "MaxHeight",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "MinFileSizeBytes",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "TargetContainer",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "TargetVideoCodec",
                table: "Libraries");
        }
    }
}
