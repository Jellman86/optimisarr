using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJobType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing jobs predate previews, so they are Normal. A non-"Normal" default would
            // store a value the JobType converter cannot read back.
            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Jobs",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Normal");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Type",
                table: "Jobs");
        }
    }
}
