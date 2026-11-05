using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScopeAdaptiveVideoQualityToEncoder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdaptiveVideoQualityEncoder",
                table: "Jobs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AdaptiveVideoQualityRetry",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdaptiveVideoQualityEncoder",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AdaptiveVideoQualityRetry",
                table: "Jobs");
        }
    }
}
