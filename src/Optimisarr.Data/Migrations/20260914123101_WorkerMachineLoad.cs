using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkerMachineLoad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CpuBusyFraction",
                table: "Workers",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GpuBusyFraction",
                table: "Workers",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LoadReportedAt",
                table: "Workers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CpuBusyFraction",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "GpuBusyFraction",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "LoadReportedAt",
                table: "Workers");
        }
    }
}
