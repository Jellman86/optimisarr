using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPerceptualWorkerState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PerceptualCapabilitiesJson",
                table: "Workers",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PerceptualSearchStateJson",
                table: "JobLeases",
                type: "TEXT",
                maxLength: 262144,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Workers_PerceptualCapabilities",
                table: "Workers",
                sql: "\"PerceptualCapabilitiesJson\" IS NULL OR\n(length(\"PerceptualCapabilitiesJson\") <= 4096 AND json_valid(\"PerceptualCapabilitiesJson\"))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobLeases_PerceptualSearch",
                table: "JobLeases",
                sql: "\"PerceptualSearchStateJson\" IS NULL OR\n(length(\"PerceptualSearchStateJson\") <= 262144 AND json_valid(\"PerceptualSearchStateJson\"))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Workers_PerceptualCapabilities",
                table: "Workers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JobLeases_PerceptualSearch",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "PerceptualCapabilitiesJson",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "PerceptualSearchStateJson",
                table: "JobLeases");
        }
    }
}
