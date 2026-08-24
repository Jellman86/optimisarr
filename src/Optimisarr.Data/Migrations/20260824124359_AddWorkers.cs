using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Workers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    OperatingSystem = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Architecture = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    VideoEncoders = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    HardwareDecoders = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Vmaf = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FreeScratchBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxConcurrency = table.Column<int>(type: "INTEGER", nullable: false),
                    CredentialFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PairedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workers_CredentialFingerprint",
                table: "Workers",
                column: "CredentialFingerprint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Workers");
        }
    }
}
