#!/usr/bin/env python3
"""Keep application, API, changelog, branch, and release-tag versions aligned."""

from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SEMVER = re.compile(r"^\d+\.\d+\.\d+$")
RELEASE_HEADING = re.compile(r"^(?P<version>\d+\.\d+\.\d+) — \d{4}-\d{2}-\d{2}$")


@dataclass(frozen=True)
class ReleaseContext:
    mode: str
    latest_tag: str | None = None
    tag_name: str | None = None

    @classmethod
    def development(cls, *, latest_tag: str | None) -> "ReleaseContext":
        return cls(mode="development", latest_tag=latest_tag)

    @classmethod
    def release(cls, *, tag_name: str | None = None) -> "ReleaseContext":
        return cls(mode="release", tag_name=tag_name)


def read_application_version(root: Path) -> str:
    document = ET.parse(root / "Directory.Build.props")
    element = document.find(".//Version")
    if element is None or not element.text:
        raise ValueError("Directory.Build.props does not contain a Version element")
    return element.text.strip()


def read_openapi_version(root: Path) -> str:
    document = json.loads((root / "docs" / "openapi.json").read_text(encoding="utf-8"))
    version = document.get("info", {}).get("version")
    if not isinstance(version, str) or not version:
        raise ValueError("docs/openapi.json does not contain info.version")
    return version


def read_changelog_headings(root: Path) -> list[str]:
    changelog = (root / "CHANGELOG.md").read_text(encoding="utf-8-sig")
    return re.findall(r"^## (.+)$", changelog, re.MULTILINE)


def validate(root: Path, context: ReleaseContext) -> list[str]:
    application_version = read_application_version(root)
    openapi_version = read_openapi_version(root)
    headings = read_changelog_headings(root)
    errors: list[str] = []

    if not SEMVER.fullmatch(application_version):
        errors.append(
            f"application version {application_version!r} must be a numeric X.Y.Z version"
        )

    expected_openapi_version = f"{application_version}.0"
    if openapi_version != expected_openapi_version:
        errors.append(
            f"OpenAPI version {openapi_version} must match application version "
            f"{expected_openapi_version}"
        )

    if not headings:
        errors.append("CHANGELOG.md must contain a level-two release heading")
        return errors

    if context.mode == "development":
        if headings[0] != "Unreleased":
            errors.append("development changelog must begin with '## Unreleased'")

        if context.latest_tag:
            latest_version = context.latest_tag.removeprefix("v")
            if application_version != latest_version:
                errors.append(
                    f"development version {application_version} must match the latest "
                    f"release tag {context.latest_tag}"
                )
            if not any(
                heading.startswith(f"{latest_version} — ") for heading in headings[1:]
            ):
                errors.append(
                    f"development changelog must retain the {latest_version} release section"
                )
    elif context.mode == "release":
        match = RELEASE_HEADING.fullmatch(headings[0])
        if match is None or match.group("version") != application_version:
            errors.append(
                "release changelog must begin with the dated application version "
                f"'## {application_version} — YYYY-MM-DD'"
            )

        if context.tag_name and context.tag_name != f"v{application_version}":
            errors.append(
                f"release tag {context.tag_name} must match application version "
                f"{application_version}"
            )
    else:
        errors.append(f"unknown release metadata mode {context.mode!r}")

    return errors


def git_output(root: Path, *arguments: str) -> str:
    return subprocess.run(
        ["git", *arguments],
        cwd=root,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def latest_release_tag(root: Path) -> str | None:
    tags = git_output(root, "tag", "--list", "v[0-9]*.[0-9]*.[0-9]*").splitlines()
    valid_tags = [tag for tag in tags if SEMVER.fullmatch(tag.removeprefix("v"))]
    if not valid_tags:
        return None
    return max(
        valid_tags,
        key=lambda tag: tuple(int(part) for part in tag.removeprefix("v").split(".")),
    )


def current_context(root: Path) -> ReleaseContext:
    base_ref = os.environ.get("GITHUB_BASE_REF", "")
    ref_name = os.environ.get("GITHUB_REF_NAME", "")
    ref_type = os.environ.get("GITHUB_REF_TYPE", "")
    branch = ref_name or git_output(root, "branch", "--show-current")

    if ref_type == "tag":
        return ReleaseContext.release(tag_name=ref_name)
    if base_ref == "main" or branch == "main" or branch.startswith("release/"):
        return ReleaseContext.release()
    return ReleaseContext.development(latest_tag=latest_release_tag(root))


def main() -> int:
    context = current_context(ROOT)
    errors = validate(ROOT, context)
    if errors:
        print("Release metadata validation failed:", *errors, sep="\n  ")
        return 1

    application_version = read_application_version(ROOT)
    print(
        f"Release metadata is consistent for {context.mode} "
        f"(application {application_version})."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
