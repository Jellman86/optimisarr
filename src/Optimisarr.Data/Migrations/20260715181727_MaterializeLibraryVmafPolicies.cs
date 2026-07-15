using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class MaterializeLibraryVmafPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // VMAF is now exclusively a per-library policy. Materialise the effective legacy
            // global values onto every library before removing the obsolete AppSettings rows, so
            // an upgrade neither enables nor disables quality scoring unexpectedly.
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE "__LegacyVmafPolicy" AS
                SELECT
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.qualityGateEnabled' THEN "Value" END), 'False') AS "EnabledRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.minimumVmafHarmonicMean' THEN "Value" END), '93') AS "HarmonicRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.minimumVmafMin' THEN "Value" END), '80') AS "FifthRaw",
                    MAX(CASE WHEN "Key" = 'verification.minimumVmafCatastrophicMin' THEN "Value" END) AS "CatastrophicRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.clipVmafEnabled' THEN "Value" END), 'False') AS "ClipRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.vmafFrameSubsample' THEN "Value" END), '1') AS "FrameRaw"
                FROM "AppSettings";

                ALTER TABLE "__LegacyVmafPolicy" ADD COLUMN "Enabled" INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE "__LegacyVmafPolicy" ADD COLUMN "Harmonic" REAL NOT NULL DEFAULT 93;
                ALTER TABLE "__LegacyVmafPolicy" ADD COLUMN "Fifth" REAL NOT NULL DEFAULT 80;
                ALTER TABLE "__LegacyVmafPolicy" ADD COLUMN "Catastrophic" REAL NOT NULL DEFAULT 50;
                ALTER TABLE "__LegacyVmafPolicy" ADD COLUMN "Clip" INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE "__LegacyVmafPolicy" ADD COLUMN "Frame" INTEGER NOT NULL DEFAULT 1;

                UPDATE "__LegacyVmafPolicy"
                SET
                    "Enabled" = CASE lower(trim("EnabledRaw")) WHEN 'true' THEN 1 ELSE 0 END,
                    "Harmonic" = CASE
                        WHEN trim("HarmonicRaw") <> ''
                         AND trim("HarmonicRaw") NOT GLOB '*[^0-9.]*'
                         AND CAST("HarmonicRaw" AS REAL) BETWEEN 0 AND 100
                        THEN CAST("HarmonicRaw" AS REAL) ELSE 93 END,
                    "Fifth" = CASE
                        WHEN trim("FifthRaw") <> ''
                         AND trim("FifthRaw") NOT GLOB '*[^0-9.]*'
                         AND CAST("FifthRaw" AS REAL) BETWEEN 0 AND 100
                        THEN CAST("FifthRaw" AS REAL) ELSE 80 END,
                    "Clip" = CASE lower(trim("ClipRaw")) WHEN 'true' THEN 1 ELSE 0 END,
                    "Frame" = CASE
                        WHEN trim("FrameRaw") <> ''
                         AND trim("FrameRaw") NOT GLOB '*[^0-9]*'
                         AND CAST("FrameRaw" AS INTEGER) BETWEEN 1 AND 10
                        THEN CAST("FrameRaw" AS INTEGER) ELSE 1 END;

                UPDATE "__LegacyVmafPolicy"
                SET "Catastrophic" = CASE
                    WHEN "CatastrophicRaw" IS NOT NULL
                     AND trim("CatastrophicRaw") <> ''
                     AND trim("CatastrophicRaw") NOT GLOB '*[^0-9.]*'
                     AND CAST("CatastrophicRaw" AS REAL) BETWEEN 0 AND 100
                    THEN CAST("CatastrophicRaw" AS REAL)
                    ELSE max(0, "Fifth" - 30) END;

                UPDATE "Libraries"
                SET
                    "VmafQualityGateEnabled" = COALESCE("VmafQualityGateEnabled", (SELECT "Enabled" FROM "__LegacyVmafPolicy")),
                    "MinVmafHarmonicMean" = COALESCE("MinVmafHarmonicMean", (SELECT "Harmonic" FROM "__LegacyVmafPolicy")),
                    "MinVmafMin" = COALESCE("MinVmafMin", (SELECT "Fifth" FROM "__LegacyVmafPolicy")),
                    "MinVmafCatastrophicMin" = COALESCE("MinVmafCatastrophicMin", (SELECT "Catastrophic" FROM "__LegacyVmafPolicy")),
                    "ClipVmafEnabled" = COALESCE("ClipVmafEnabled", (SELECT "Clip" FROM "__LegacyVmafPolicy")),
                    "VmafFrameSubsample" = COALESCE("VmafFrameSubsample", (SELECT "Frame" FROM "__LegacyVmafPolicy"));

                DROP TABLE "__LegacyVmafPolicy";

                DELETE FROM "AppSettings"
                WHERE "Key" IN (
                    'verification.qualityGateEnabled',
                    'verification.minimumVmafHarmonicMean',
                    'verification.minimumVmafMin',
                    'verification.minimumVmafCatastrophicMin',
                    'verification.clipVmafEnabled',
                    'verification.vmafFrameSubsample');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A single global policy cannot be reconstructed from independently edited libraries.
            // Restore the former defaults so an older binary has a valid baseline after rollback.
            migrationBuilder.Sql(
                """
                INSERT OR IGNORE INTO "AppSettings" ("Key", "Value", "UpdatedAt") VALUES
                    ('verification.qualityGateEnabled', 'False', strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now')),
                    ('verification.minimumVmafHarmonicMean', '93', strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now')),
                    ('verification.minimumVmafMin', '80', strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now')),
                    ('verification.minimumVmafCatastrophicMin', '50', strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now')),
                    ('verification.clipVmafEnabled', 'False', strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now')),
                    ('verification.vmafFrameSubsample', '1', strftime('%Y-%m-%dT%H:%M:%f+00:00', 'now'));
                """);
        }
    }
}
