using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryAutoEnqueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoEnqueueEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "AutoEnqueueWindowEnd",
                table: "Libraries",
                type: "TEXT",
                nullable: false,
                defaultValue: new TimeOnly(0, 0, 0));

            migrationBuilder.AddColumn<TimeOnly>(
                name: "AutoEnqueueWindowStart",
                table: "Libraries",
                type: "TEXT",
                nullable: false,
                defaultValue: new TimeOnly(0, 0, 0));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAutoEnqueueAt",
                table: "Libraries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoEnqueueEnabled",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "AutoEnqueueWindowEnd",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "AutoEnqueueWindowStart",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "LastAutoEnqueueAt",
                table: "Libraries");
        }
    }
}
