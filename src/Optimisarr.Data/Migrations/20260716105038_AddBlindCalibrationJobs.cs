using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBlindCalibrationJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CalibrationClipSeconds",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CalibrationClipStartSeconds",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CalibrationSessionId",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CalibrationSessionId",
                table: "Jobs",
                column: "CalibrationSessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_CalibrationSessionId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "CalibrationClipSeconds",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "CalibrationClipStartSeconds",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "CalibrationSessionId",
                table: "Jobs");
        }
    }
}
