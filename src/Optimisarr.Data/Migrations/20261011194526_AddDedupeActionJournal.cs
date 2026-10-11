using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDedupeActionJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DedupeActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Contract = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Retention = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    ReviewKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ScopeKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PlanHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AttentionReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DedupeActions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DedupeActionFiles",
                columns: table => new
                {
                    ActionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaFileId = table.Column<int>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Root = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    QuarantinePath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    Identity = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    IsKeeper = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DedupeActionFiles", x => new { x.ActionId, x.MediaFileId });
                    table.CheckConstraint("CK_DedupeActionFiles_RollbackPath", "(IsKeeper = 1 AND QuarantinePath IS NULL) OR (IsKeeper = 0 AND QuarantinePath IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_DedupeActionFiles_DedupeActions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "DedupeActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DedupeFileClaims",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ActionId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DedupeFileClaims", x => x.Key);
                    table.ForeignKey(
                        name: "FK_DedupeFileClaims_DedupeActions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "DedupeActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DedupeActionFiles_MediaFileId",
                table: "DedupeActionFiles",
                column: "MediaFileId");

            migrationBuilder.CreateIndex(
                name: "IX_DedupeActionFiles_QuarantinePath",
                table: "DedupeActionFiles",
                column: "QuarantinePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DedupeActions_LibraryId_State",
                table: "DedupeActions",
                columns: new[] { "LibraryId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_DedupeFileClaims_ActionId",
                table: "DedupeFileClaims",
                column: "ActionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DedupeActionFiles");

            migrationBuilder.DropTable(
                name: "DedupeFileClaims");

            migrationBuilder.DropTable(
                name: "DedupeActions");
        }
    }
}
