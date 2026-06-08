using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReplacements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Replacements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    QuarantinePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    FinalPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    OriginalSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    NewSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CrossFilesystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReplacedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RolledBackAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Replacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Replacements_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Replacements_JobId",
                table: "Replacements",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Replacements_Status",
                table: "Replacements",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Replacements");
        }
    }
}
