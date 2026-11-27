using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArrImportWebhook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LocalPathPrefix",
                table: "ArrConnections",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemotePathPrefix",
                table: "ArrConnections",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookLastDetail",
                table: "ArrConnections",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookLastOutcome",
                table: "ArrConnections",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WebhookLastReceivedAt",
                table: "ArrConnections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookSecretFingerprint",
                table: "ArrConnections",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocalPathPrefix",
                table: "ArrConnections");

            migrationBuilder.DropColumn(
                name: "RemotePathPrefix",
                table: "ArrConnections");

            migrationBuilder.DropColumn(
                name: "WebhookLastDetail",
                table: "ArrConnections");

            migrationBuilder.DropColumn(
                name: "WebhookLastOutcome",
                table: "ArrConnections");

            migrationBuilder.DropColumn(
                name: "WebhookLastReceivedAt",
                table: "ArrConnections");

            migrationBuilder.DropColumn(
                name: "WebhookSecretFingerprint",
                table: "ArrConnections");
        }
    }
}
