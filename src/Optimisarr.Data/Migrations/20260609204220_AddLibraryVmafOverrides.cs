using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryVmafOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MinVmafHarmonicMean",
                table: "Libraries",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MinVmafMin",
                table: "Libraries",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinVmafHarmonicMean",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "MinVmafMin",
                table: "Libraries");
        }
    }
}
