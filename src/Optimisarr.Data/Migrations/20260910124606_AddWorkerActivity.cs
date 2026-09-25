using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastProblem",
                table: "Workers",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastProblemAt",
                table: "Workers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EncodedSeconds",
                table: "JobLeases",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "JobLeases",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastProblem",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "LastProblemAt",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "EncodedSeconds",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "JobLeases");
        }
    }
}
