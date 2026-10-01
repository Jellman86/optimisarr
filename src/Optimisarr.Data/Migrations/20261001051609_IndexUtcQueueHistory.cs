using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class IndexUtcQueueHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "QueueEffectiveUtcTicks",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                computedColumnSql: "(CAST(strftime('%s', substr(COALESCE(FinishedAt, EnqueuedAt), 1, 19) || substr(COALESCE(FinishedAt, EnqueuedAt), -6)) AS INTEGER) * 10000000 + 621355968000000000 + CASE WHEN substr(COALESCE(FinishedAt, EnqueuedAt), 20, 1) = '.' THEN CAST(substr(substr(COALESCE(FinishedAt, EnqueuedAt), 21, length(COALESCE(FinishedAt, EnqueuedAt)) - 26) || '0000000', 1, 7) AS INTEGER) ELSE 0 END)");

            migrationBuilder.AddColumn<long>(
                name: "QueueEnqueuedUtcTicks",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                computedColumnSql: "(CAST(strftime('%s', substr(EnqueuedAt, 1, 19) || substr(EnqueuedAt, -6)) AS INTEGER) * 10000000 + 621355968000000000 + CASE WHEN substr(EnqueuedAt, 20, 1) = '.' THEN CAST(substr(substr(EnqueuedAt, 21, length(EnqueuedAt) - 26) || '0000000', 1, 7) AS INTEGER) ELSE 0 END)");

            migrationBuilder.AddColumn<long>(
                name: "AcquiredUtcTicks",
                table: "JobLeases",
                type: "INTEGER",
                nullable: false,
                computedColumnSql: "(CAST(strftime('%s', substr(AcquiredAt, 1, 19) || substr(AcquiredAt, -6)) AS INTEGER) * 10000000 + 621355968000000000 + CASE WHEN substr(AcquiredAt, 20, 1) = '.' THEN CAST(substr(substr(AcquiredAt, 21, length(AcquiredAt) - 26) || '0000000', 1, 7) AS INTEGER) ELSE 0 END)");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Type_Priority_QueueEnqueuedUtcTicks_Id",
                table: "Jobs",
                columns: new[] { "Type", "Priority", "QueueEnqueuedUtcTicks", "Id" },
                descending: new[] { false, true, false, false });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Type_QueueEffectiveUtcTicks",
                table: "Jobs",
                columns: new[] { "Type", "QueueEffectiveUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_JobLeases_JobId_AcquiredUtcTicks_Id",
                table: "JobLeases",
                columns: new[] { "JobId", "AcquiredUtcTicks", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Jobs_Type_Priority_QueueEnqueuedUtcTicks_Id",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_Type_QueueEffectiveUtcTicks",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_JobLeases_JobId_AcquiredUtcTicks_Id",
                table: "JobLeases");

            migrationBuilder.DropColumn(
                name: "QueueEffectiveUtcTicks",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "QueueEnqueuedUtcTicks",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "AcquiredUtcTicks",
                table: "JobLeases");
        }
    }
}
