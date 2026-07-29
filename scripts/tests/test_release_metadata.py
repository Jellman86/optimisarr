from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from check_release_metadata import ReleaseContext, validate


class ReleaseMetadataTests(unittest.TestCase):
    def write_repository(
        self,
        root: Path,
        *,
        version: str = "0.2.9",
        openapi_version: str = "0.2.9.0",
        changelog_heading: str = "Unreleased",
        released_version: str | None = "0.2.9",
    ) -> None:
        (root / "docs").mkdir()
        (root / "Directory.Build.props").write_text(
            f"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>",
            encoding="utf-8",
        )
        (root / "docs" / "openapi.json").write_text(
            f'{{"info": {{"version": "{openapi_version}"}}}}',
            encoding="utf-8",
        )
        released_heading = (
            f"\n\n## {released_version} — 2026-07-29\n\n### Fixed\n\n- Previous release."
            if released_version
            else ""
        )
        (root / "CHANGELOG.md").write_text(
            f"# Changelog\n\n## {changelog_heading}\n\n### Changed\n\n- Current work."
            f"{released_heading}\n",
            encoding="utf-8",
        )

    def validate_fixture(self, context: ReleaseContext, **fixture: object) -> list[str]:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_repository(root, **fixture)
            return validate(root, context)

    def test_development_accepts_unreleased_section_at_latest_release_version(self) -> None:
        errors = self.validate_fixture(
            ReleaseContext.development(latest_tag="v0.2.9"),
        )

        self.assertEqual([], errors)

    def test_development_rejects_missing_unreleased_section(self) -> None:
        errors = self.validate_fixture(
            ReleaseContext.development(latest_tag="v0.2.9"),
            changelog_heading="0.2.9 — 2026-07-29",
            released_version=None,
        )

        self.assertIn("development changelog must begin with '## Unreleased'", errors)

    def test_development_rejects_version_older_than_latest_release_tag(self) -> None:
        errors = self.validate_fixture(
            ReleaseContext.development(latest_tag="v0.2.9"),
            version="0.2.8",
            openapi_version="0.2.8.0",
            released_version="0.2.8",
        )

        self.assertIn(
            "development version 0.2.8 must match the latest release tag v0.2.9",
            errors,
        )

    def test_release_accepts_matching_version_heading_and_tag(self) -> None:
        errors = self.validate_fixture(
            ReleaseContext.release(tag_name="v0.2.9"),
            changelog_heading="0.2.9 — 2026-07-29",
            released_version=None,
        )

        self.assertEqual([], errors)

    def test_release_rejects_tag_that_does_not_match_application_version(self) -> None:
        errors = self.validate_fixture(
            ReleaseContext.release(tag_name="v0.2.8"),
            changelog_heading="0.2.9 — 2026-07-29",
            released_version=None,
        )

        self.assertIn(
            "release tag v0.2.8 must match application version 0.2.9",
            errors,
        )

    def test_all_refs_reject_openapi_version_drift(self) -> None:
        errors = self.validate_fixture(
            ReleaseContext.development(latest_tag="v0.2.9"),
            openapi_version="0.2.8.0",
        )

        self.assertIn(
            "OpenAPI version 0.2.8.0 must match application version 0.2.9.0",
            errors,
        )


if __name__ == "__main__":
    unittest.main()
