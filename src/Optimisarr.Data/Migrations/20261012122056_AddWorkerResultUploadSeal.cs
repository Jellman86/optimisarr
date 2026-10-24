using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerResultUploadSeal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResultUploadBinding",
                table: "JobLeases",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResultUploadGeneration",
                table: "JobLeases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultUploadPath",
                table: "JobLeases",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ResultUploadSealedBytes",
                table: "JobLeases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_JobLeases_ResultUpload",
                table: "JobLeases",
                sql: "(\"ResultUploadGeneration\" IS NULL AND \"ResultUploadPath\" IS NULL AND \"ResultUploadBinding\" IS NULL AND \"ResultUploadSealedBytes\" IS NULL)\nOR (\"ResultUploadGeneration\" IS NOT NULL AND \"ResultUploadPath\" IS NOT NULL AND \"ResultUploadBinding\" IS NOT NULL\n    AND (\"ResultUploadSealedBytes\" IS NULL OR \"ResultUploadSealedBytes\" >= 0))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_JobLeases_ResultUpload",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "ResultUploadBinding",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "ResultUploadGeneration",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "ResultUploadPath",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "ResultUploadSealedBytes",
                table: "JobLeases");
        }
    }
}
