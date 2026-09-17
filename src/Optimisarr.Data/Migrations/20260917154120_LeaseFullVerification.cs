using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class LeaseFullVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VerificationContractJson",
                table: "JobLeases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationEvidenceJson",
                table: "JobLeases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationWorkJson",
                table: "JobLeases",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerificationContractJson",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "VerificationEvidenceJson",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "VerificationWorkJson",
                table: "JobLeases");
        }
    }
}
