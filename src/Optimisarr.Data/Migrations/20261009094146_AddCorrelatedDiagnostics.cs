using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCorrelatedDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExecutionAttempt",
                table: "JobLeases",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DetailsJson",
                table: "DiagnosticEvents",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InstanceId",
                table: "DiagnosticEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReceivedAt",
                table: "DiagnosticEvents",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "DiagnosticEvents",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Server");

            migrationBuilder.AddColumn<long>(
                name: "SourceSequence",
                table: "DiagnosticEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BytesStored",
                table: "DiagnosticCaptureSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "FailureRetentionDays",
                table: "DiagnosticCaptureSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<bool>(
                name: "HasFailure",
                table: "DiagnosticCaptureSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "MaximumBytes",
                table: "DiagnosticCaptureSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 4194304L);

            migrationBuilder.AddColumn<bool>(
                name: "PersistAcrossRestart",
                table: "DiagnosticCaptureSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Pinned",
                table: "DiagnosticCaptureSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RetentionDays",
                table: "DiagnosticCaptureSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.Sql("UPDATE DiagnosticEvents SET ReceivedAt = OccurredAt;");
            migrationBuilder.Sql("UPDATE DiagnosticCaptureSessions SET BytesStored = (SELECT COUNT(*) * 512 FROM DiagnosticEvents e WHERE e.SessionId = DiagnosticCaptureSessions.Id), HasFailure = EXISTS(SELECT 1 FROM DiagnosticEvents e WHERE e.SessionId = DiagnosticCaptureSessions.Id AND (e.CurrentStatus = 'Failed' OR e.ReasonCode LIKE 'Failure.%'));");

            migrationBuilder.CreateTable(
                name: "DiagnosticWorkerReceipts",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkerId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastUploadAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinalUploadAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DroppedEvents = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticWorkerReceipts", x => new { x.SessionId, x.WorkerId });
                    table.ForeignKey(
                        name: "FK_DiagnosticWorkerReceipts_DiagnosticCaptureSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "DiagnosticCaptureSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticEvents_SessionId_WorkerId_InstanceId_SourceSequence",
                table: "DiagnosticEvents",
                columns: new[] { "SessionId", "WorkerId", "InstanceId", "SourceSequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiagnosticWorkerReceipts");

            migrationBuilder.DropIndex(
                name: "IX_DiagnosticEvents_SessionId_WorkerId_InstanceId_SourceSequence",
                table: "DiagnosticEvents");

            migrationBuilder.DropColumn(
                name: "ExecutionAttempt",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "DetailsJson",
                table: "DiagnosticEvents");

            migrationBuilder.DropColumn(
                name: "InstanceId",
                table: "DiagnosticEvents");

            migrationBuilder.DropColumn(
                name: "ReceivedAt",
                table: "DiagnosticEvents");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "DiagnosticEvents");

            migrationBuilder.DropColumn(
                name: "SourceSequence",
                table: "DiagnosticEvents");

            migrationBuilder.DropColumn(
                name: "BytesStored",
                table: "DiagnosticCaptureSessions");

            migrationBuilder.DropColumn(
                name: "FailureRetentionDays",
                table: "DiagnosticCaptureSessions");

            migrationBuilder.DropColumn(
                name: "HasFailure",
                table: "DiagnosticCaptureSessions");

            migrationBuilder.DropColumn(
                name: "MaximumBytes",
                table: "DiagnosticCaptureSessions");

            migrationBuilder.DropColumn(
                name: "PersistAcrossRestart",
                table: "DiagnosticCaptureSessions");

            migrationBuilder.DropColumn(
                name: "Pinned",
                table: "DiagnosticCaptureSessions");

            migrationBuilder.DropColumn(
                name: "RetentionDays",
                table: "DiagnosticCaptureSessions");
        }
    }
}
