using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEncoderTuningOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentTune",
                table: "Libraries",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                // "None" rather than EF's generated "", which is not a member and would fail to
                // materialise. Every existing library upgrades to "no content tune", which is the
                // behaviour it already had.
                defaultValue: "None");

            migrationBuilder.AddColumn<int>(
                name: "MaxBitrateKbps",
                table: "Libraries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StrongerAdaptiveQuantisation",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentTune",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "MaxBitrateKbps",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "StrongerAdaptiveQuantisation",
                table: "Libraries");
        }
    }
}
