using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExactCopyReviewScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Identity",
                table: "ExactDuplicateObservations",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LibraryId",
                table: "ExactDuplicateObservations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Root",
                table: "ExactDuplicateObservations",
                type: "TEXT",
                maxLength: 4096,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ExactDuplicateReviewDecisions",
                columns: table => new
                {
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    ScopeKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ReviewKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Choice = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    KeepIds = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SavedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExactDuplicateReviewDecisions", x => new { x.LibraryId, x.ScopeKey, x.Sha256, x.SizeBytes });
                    table.ForeignKey(
                        name: "FK_ExactDuplicateReviewDecisions_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExactDuplicateScanLibraries",
                columns: table => new
                {
                    ScanId = table.Column<long>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Root = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExactDuplicateScanLibraries", x => new { x.ScanId, x.LibraryId });
                    table.ForeignKey(
                        name: "FK_ExactDuplicateScanLibraries_ExactDuplicateScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "ExactDuplicateScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
            // Preserve existing single-library snapshots. Do not infer an old observation's
            // identity from a newer cached read; an explicit rescan establishes review evidence.
            migrationBuilder.Sql("""
                INSERT INTO ExactDuplicateScanLibraries (ScanId, LibraryId, Root, Name, MediaType)
                SELECT s.Id, s.LibraryId, s.Root, l.Name, l.MediaType
                FROM ExactDuplicateScans s JOIN Libraries l ON l.Id = s.LibraryId;
                UPDATE ExactDuplicateObservations
                SET LibraryId = (SELECT LibraryId FROM ExactDuplicateScans WHERE Id = ScanId),
                    Root = (SELECT Root FROM ExactDuplicateScans WHERE Id = ScanId);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExactDuplicateReviewDecisions");

            migrationBuilder.DropTable(
                name: "ExactDuplicateScanLibraries");

            migrationBuilder.DropColumn(
                name: "Identity",
                table: "ExactDuplicateObservations");

            migrationBuilder.DropColumn(
                name: "LibraryId",
                table: "ExactDuplicateObservations");

            migrationBuilder.DropColumn(
                name: "Root",
                table: "ExactDuplicateObservations");
        }
    }
}
