using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Optimisarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveVerificationGatesToLibraries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AudioClippingGateEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AudioLoudnessGateEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "DurationTolerancePercent",
                table: "Libraries",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<bool>(
                name: "ImageMetadataGateEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ImageQualityGateEnabled",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<double>(
                name: "MaxLoudnessDriftLufs",
                table: "Libraries",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<double>(
                name: "MaxTruePeakDbtp",
                table: "Libraries",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "MinimumImageSsim",
                table: "Libraries",
                type: "REAL",
                nullable: false,
                defaultValue: 0.95);

            migrationBuilder.AddColumn<bool>(
                name: "RequireAudioRetained",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireSizeReduction",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireSubtitlesRetained",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Verification policy used to be one global AppSettings bundle. Materialise the
            // effective legacy values onto every existing library before deleting those keys:
            // an upgrade must never silently weaken, tighten, enable, or disable a safety gate.
            // New/malformed values fall back to the same conservative defaults used by Core.
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE "__LegacyVerificationPolicy" AS
                SELECT
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.durationTolerancePercent' THEN "Value" END), '1') AS "DurationRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.requireAudioRetained' THEN "Value" END), 'True') AS "AudioRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.requireSubtitlesRetained' THEN "Value" END), 'False') AS "SubtitlesRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.requireSizeReduction' THEN "Value" END), 'True') AS "SizeRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.audioLoudnessGateEnabled' THEN "Value" END), 'False') AS "LoudnessEnabledRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.maxLoudnessDriftLufs' THEN "Value" END), '1') AS "LoudnessDriftRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.audioClippingGateEnabled' THEN "Value" END), 'False') AS "ClippingEnabledRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.maxTruePeakDbtp' THEN "Value" END), '0') AS "TruePeakRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.imageQualityGateEnabled' THEN "Value" END), 'True') AS "ImageQualityRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.minimumImageSsim' THEN "Value" END), '0.95') AS "ImageSsimRaw",
                    COALESCE(MAX(CASE WHEN "Key" = 'verification.imageMetadataGateEnabled' THEN "Value" END), 'True') AS "ImageMetadataRaw"
                FROM "AppSettings";

                UPDATE "Libraries"
                SET
                    "DurationTolerancePercent" = (
                        SELECT CASE
                            WHEN trim("DurationRaw") GLOB '*[0-9]*'
                             AND lower(trim("DurationRaw")) NOT GLOB '*[^0-9e+.-]*'
                             AND CAST("DurationRaw" AS REAL) >= 0
                            THEN CAST("DurationRaw" AS REAL) ELSE 1 END
                        FROM "__LegacyVerificationPolicy"),
                    "RequireAudioRetained" = (
                        SELECT CASE lower(trim("AudioRaw")) WHEN 'true' THEN 1 WHEN 'false' THEN 0 ELSE 1 END
                        FROM "__LegacyVerificationPolicy"),
                    "RequireSubtitlesRetained" = (
                        SELECT CASE lower(trim("SubtitlesRaw")) WHEN 'true' THEN 1 ELSE 0 END
                        FROM "__LegacyVerificationPolicy"),
                    "RequireSizeReduction" = (
                        SELECT CASE lower(trim("SizeRaw")) WHEN 'false' THEN 0 ELSE 1 END
                        FROM "__LegacyVerificationPolicy"),
                    "AudioLoudnessGateEnabled" = (
                        SELECT CASE lower(trim("LoudnessEnabledRaw")) WHEN 'true' THEN 1 ELSE 0 END
                        FROM "__LegacyVerificationPolicy"),
                    "MaxLoudnessDriftLufs" = (
                        SELECT CASE
                            WHEN trim("LoudnessDriftRaw") GLOB '*[0-9]*'
                             AND lower(trim("LoudnessDriftRaw")) NOT GLOB '*[^0-9e+.-]*'
                             AND CAST("LoudnessDriftRaw" AS REAL) >= 0
                            THEN CAST("LoudnessDriftRaw" AS REAL) ELSE 1 END
                        FROM "__LegacyVerificationPolicy"),
                    "AudioClippingGateEnabled" = (
                        SELECT CASE lower(trim("ClippingEnabledRaw")) WHEN 'true' THEN 1 ELSE 0 END
                        FROM "__LegacyVerificationPolicy"),
                    "MaxTruePeakDbtp" = (
                        SELECT CASE
                            WHEN trim("TruePeakRaw") GLOB '*[0-9]*'
                             AND lower(trim("TruePeakRaw")) NOT GLOB '*[^0-9e+.-]*'
                            THEN CAST("TruePeakRaw" AS REAL) ELSE 0 END
                        FROM "__LegacyVerificationPolicy"),
                    "ImageQualityGateEnabled" = (
                        SELECT CASE lower(trim("ImageQualityRaw")) WHEN 'false' THEN 0 ELSE 1 END
                        FROM "__LegacyVerificationPolicy"),
                    "MinimumImageSsim" = (
                        SELECT CASE
                            WHEN trim("ImageSsimRaw") GLOB '*[0-9]*'
                             AND lower(trim("ImageSsimRaw")) NOT GLOB '*[^0-9e+.-]*'
                             AND CAST("ImageSsimRaw" AS REAL) BETWEEN 0 AND 1
                            THEN CAST("ImageSsimRaw" AS REAL) ELSE 0.95 END
                        FROM "__LegacyVerificationPolicy"),
                    "ImageMetadataGateEnabled" = (
                        SELECT CASE lower(trim("ImageMetadataRaw")) WHEN 'false' THEN 0 ELSE 1 END
                        FROM "__LegacyVerificationPolicy");

                DELETE FROM "AppSettings"
                WHERE "Key" IN (
                    'verification.durationTolerancePercent',
                    'verification.requireAudioRetained',
                    'verification.requireSubtitlesRetained',
                    'verification.requireSizeReduction',
                    'verification.audioLoudnessGateEnabled',
                    'verification.maxLoudnessDriftLufs',
                    'verification.audioClippingGateEnabled',
                    'verification.maxTruePeakDbtp',
                    'verification.imageQualityGateEnabled',
                    'verification.minimumImageSsim',
                    'verification.imageMetadataGateEnabled'
                );

                DROP TABLE "__LegacyVerificationPolicy";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioClippingGateEnabled",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "AudioLoudnessGateEnabled",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "DurationTolerancePercent",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "ImageMetadataGateEnabled",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "ImageQualityGateEnabled",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "MaxLoudnessDriftLufs",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "MaxTruePeakDbtp",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "MinimumImageSsim",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "RequireAudioRetained",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "RequireSizeReduction",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "RequireSubtitlesRetained",
                table: "Libraries");
        }
    }
}
