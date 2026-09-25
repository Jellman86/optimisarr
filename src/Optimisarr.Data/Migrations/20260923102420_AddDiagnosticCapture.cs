using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDiagnosticCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiagnosticCaptureSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    StoppedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ScopedJobId = table.Column<int>(type: "INTEGER", nullable: true),
                    IncludePaths = table.Column<bool>(type: "INTEGER", nullable: false),
                    EventsStored = table.Column<int>(type: "INTEGER", nullable: false),
                    EventLimitReached = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticCaptureSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WorkerId = table.Column<int>(type: "INTEGER", nullable: true),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PreviousStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CurrentStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticEvents_DiagnosticCaptureSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "DiagnosticCaptureSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticCaptureSessions_StartedAt",
                table: "DiagnosticCaptureSessions",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticEvents_SessionId_JobId_Id",
                table: "DiagnosticEvents",
                columns: new[] { "SessionId", "JobId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiagnosticEvents");

            migrationBuilder.DropTable(
                name: "DiagnosticCaptureSessions");
        }
    }
}
