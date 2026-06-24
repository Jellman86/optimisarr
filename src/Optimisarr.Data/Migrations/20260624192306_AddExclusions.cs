using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExclusions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Exclusions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: true),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exclusions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Exclusions_LibraryId",
                table: "Exclusions",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_Exclusions_Path",
                table: "Exclusions",
                column: "Path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Exclusions");
        }
    }
}
