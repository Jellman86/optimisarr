using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryAutoDedupe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoDedupeEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAutoDedupeAt",
                table: "Libraries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastAutoDedupeBytes",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "LastAutoDedupeMoved",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastAutoDedupeOutcome",
                table: "Libraries",
                type: "TEXT",
                maxLength: 24,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoDedupeEnabled",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "LastAutoDedupeAt",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "LastAutoDedupeBytes",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "LastAutoDedupeMoved",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "LastAutoDedupeOutcome",
                table: "Libraries");
        }
    }
}
