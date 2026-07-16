using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryVmafPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClipVmafEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MinVmafCatastrophicMin",
                table: "Libraries",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VmafFrameSubsample",
                table: "Libraries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VmafQualityGateEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClipVmafEnabled",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "MinVmafCatastrophicMin",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "VmafFrameSubsample",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "VmafQualityGateEnabled",
                table: "Libraries");
        }
    }
}
