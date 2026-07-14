using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVmafRecoveryContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EffectiveVideoQuality",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QualityRetryCount",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RequestedVideoQuality",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VideoQualityMode",
                table: "Jobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectiveVideoQuality",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "QualityRetryCount",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "RequestedVideoQuality",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "VideoQualityMode",
                table: "Jobs");
        }
    }
}
