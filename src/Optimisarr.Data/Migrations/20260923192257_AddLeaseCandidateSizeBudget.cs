using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseCandidateSizeBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MaxCandidateBytes",
                table: "JobLeases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SizeBudgetExceededAtBytes",
                table: "JobLeases",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxCandidateBytes",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "SizeBudgetExceededAtBytes",
                table: "JobLeases");
        }
    }
}
