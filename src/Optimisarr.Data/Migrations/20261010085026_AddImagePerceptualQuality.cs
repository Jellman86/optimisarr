using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImagePerceptualQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ImagePerceptualGateEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ImagePerceptualReportingEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "MinimumImagePerceptualScore",
                table: "Libraries",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImagePerceptualGateEnabled",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "ImagePerceptualReportingEnabled",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "MinimumImagePerceptualScore",
                table: "Libraries");
        }
    }
}
