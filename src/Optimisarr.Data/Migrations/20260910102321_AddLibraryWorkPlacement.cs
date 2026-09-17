using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryWorkPlacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkPlacement",
                table: "Libraries",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                // "Anywhere" rather than EF's generated "", which is not a member and would fail to
                // materialise. Every existing library upgrades to "here or on a worker", which is
                // exactly how its jobs were placed before the choice existed.
                defaultValue: "Anywhere");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkPlacement",
                table: "Libraries");
        }
    }
}
