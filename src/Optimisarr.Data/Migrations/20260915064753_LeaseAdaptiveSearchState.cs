using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class LeaseAdaptiveSearchState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AdaptiveAskedQuality",
                table: "JobLeases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdaptiveContractJson",
                table: "JobLeases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdaptiveProbesJson",
                table: "JobLeases",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdaptiveAskedQuality",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "AdaptiveContractJson",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "AdaptiveProbesJson",
                table: "JobLeases");
        }
    }
}
