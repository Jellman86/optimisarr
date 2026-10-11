using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableExactCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExactDuplicateScans",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Root = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    ActiveSlot = table.Column<int>(type: "INTEGER", nullable: true),
                    ScopeSignature = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ForceRefresh = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Checked = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    Total = table.Column<int>(type: "INTEGER", nullable: false),
                    Reused = table.Column<int>(type: "INTEGER", nullable: false),
                    BytesRead = table.Column<long>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExactDuplicateScans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExactDuplicateScans_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExactFileFingerprints",
                columns: table => new
                {
                    MediaFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Root = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Identity = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Contract = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExactFileFingerprints", x => x.MediaFileId);
                    table.ForeignKey(
                        name: "FK_ExactFileFingerprints_MediaFiles_MediaFileId",
                        column: x => x.MediaFileId,
                        principalTable: "MediaFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExactDuplicateObservations",
                columns: table => new
                {
                    ScanId = table.Column<long>(type: "INTEGER", nullable: false),
                    MediaFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    HardLinkCount = table.Column<int>(type: "INTEGER", nullable: true),
                    CheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Reused = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExactDuplicateObservations", x => new { x.ScanId, x.MediaFileId });
                    table.ForeignKey(
                        name: "FK_ExactDuplicateObservations_ExactDuplicateScans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "ExactDuplicateScans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExactDuplicateObservations_ScanId_SizeBytes_Sha256",
                table: "ExactDuplicateObservations",
                columns: new[] { "ScanId", "SizeBytes", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_ExactDuplicateScans_ActiveSlot",
                table: "ExactDuplicateScans",
                column: "ActiveSlot",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExactDuplicateScans_LibraryId",
                table: "ExactDuplicateScans",
                column: "LibraryId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExactDuplicateObservations");

            migrationBuilder.DropTable(
                name: "ExactFileFingerprints");

            migrationBuilder.DropTable(
                name: "ExactDuplicateScans");
        }
    }
}
