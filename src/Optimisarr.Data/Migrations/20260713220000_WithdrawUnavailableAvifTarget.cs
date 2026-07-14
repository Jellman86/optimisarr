using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations;

/// <summary>
/// Moves persisted AVIF overrides to WebP because the FFmpeg build shipped for production
/// transcoding does not contain an AVIF encoder. WebP is the closest efficient, modern target and
/// is already proven by the final-container smoke suite.
/// </summary>
[DbContext(typeof(OptimisarrDbContext))]
[Migration("20260713220000_WithdrawUnavailableAvifTarget")]
public sealed class WithdrawUnavailableAvifTarget : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "UPDATE Libraries SET TargetImageFormat = 'webp' " +
            "WHERE lower(TargetImageFormat) = 'avif';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The original intent cannot be reconstructed safely after an operator saves the migrated
        // library, so this data correction is deliberately irreversible.
    }
}
