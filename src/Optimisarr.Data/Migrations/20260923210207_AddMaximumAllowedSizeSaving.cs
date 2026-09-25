using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMaximumAllowedSizeSaving : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MaximumSizeSavingPercent",
                table: "Libraries",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MinCandidateBytes",
                table: "JobLeases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SizeBudgetUndershotAtBytes",
                table: "JobLeases",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaximumSizeSavingPercent",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "MinCandidateBytes",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "SizeBudgetUndershotAtBytes",
                table: "JobLeases");
        }
    }
}
