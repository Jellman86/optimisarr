using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowVerifiedCrossFilesystemReplacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The previous check could not detect separate mounts, so a saved "False" never refused
            // anything. Allowing is what these installs were already doing, now with a verified copy.
            migrationBuilder.Sql(
                "UPDATE AppSettings SET Value = 'True' WHERE Key = 'replacement.allowCrossFilesystem';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the earlier value was never effective, and the setting stays valid either way.
        }
    }
}
