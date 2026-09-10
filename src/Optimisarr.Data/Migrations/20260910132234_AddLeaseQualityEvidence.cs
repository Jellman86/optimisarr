using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLeaseQualityEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeliveredSha256",
                table: "JobLeases",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualityCandidateSha256",
                table: "JobLeases",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualityContractJson",
                table: "JobLeases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualityScoresJson",
                table: "JobLeases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualitySourceSha256",
                table: "JobLeases",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveredSha256",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "QualityCandidateSha256",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "QualityContractJson",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "QualityScoresJson",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "QualitySourceSha256",
                table: "JobLeases");
        }
    }
}
