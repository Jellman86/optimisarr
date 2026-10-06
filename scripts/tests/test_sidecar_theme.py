"""The Mac and Windows sidecars say what a state means in the web interface's colours.

Each sidecar is otherwise drawn by its platform (native macOS controls, Windows' Fluent theme).
Their status tones write each colour exactly as `web/src/app.css` does and name the token in a
trailing comment. These tests fail when a sidecar drifts from the stylesheet, or when the two
sidecars stop covering the same tokens, so the three faces of the product cannot quietly
diverge.
"""

from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
STYLESHEET = ROOT / "web" / "src" / "app.css"
MAC_PALETTE = ROOT / "sidecars" / "macos" / "Sources" / "OptimisarrSidecar" / "Instrument.swift"
WINDOWS_PALETTE = ROOT / "sidecars" / "windows" / "src" / "Optimisarr.Sidecar.Tray" / "SidecarTheme.cs"

TOKEN_LINE = re.compile(r'(?i:light):\s*"([^"]+)"\s*,\s*(?i:dark):\s*"([^"]+)".*//\s*(--[a-z0-9-]+)\s*$')
DECLARATION = re.compile(r"(--[a-z0-9-]+)\s*:\s*([^;]+);")


def normalise(colour: str) -> str:
    return re.sub(r"\s+", "", colour.strip().lower())


def block(css: str, selector: str) -> dict[str, str]:
    start = css.index(selector)
    body = css[css.index("{", start) + 1 : css.index("}", start)]
    return {name: normalise(value) for name, value in DECLARATION.findall(body)}


def stylesheet_tokens() -> tuple[dict[str, str], dict[str, str]]:
    css = STYLESHEET.read_text(encoding="utf-8")
    light = block(css, ":root {")
    dark = {**light, **block(css, "html.dark,\n.scheme-dark {")}
    return light, dark


def palette(path: Path) -> dict[str, tuple[str, str]]:
    tokens: dict[str, tuple[str, str]] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        match = TOKEN_LINE.search(line)
        if match:
            light, dark, token = match.groups()
            if token in tokens:
                raise AssertionError(f"{path.name} names {token} twice")
            tokens[token] = (normalise(light), normalise(dark))
    return tokens


class SidecarThemeTests(unittest.TestCase):
    def test_each_sidecar_palette_matches_the_stylesheet_in_both_schemes(self) -> None:
        light, dark = stylesheet_tokens()
        for path in (MAC_PALETTE, WINDOWS_PALETTE):
            tokens = palette(path)
            self.assertGreaterEqual(len(tokens), 12, f"{path.name} declares too few theme tokens")
            for token, (light_value, dark_value) in tokens.items():
                with self.subTest(file=path.name, token=token):
                    self.assertIn(token, light, f"{token} is not a stylesheet token")
                    self.assertEqual(light[token], light_value, f"{path.name} light {token}")
                    self.assertEqual(dark[token], dark_value, f"{path.name} dark {token}")

    def test_mac_and_windows_cover_the_same_tokens(self) -> None:
        self.assertEqual(sorted(palette(MAC_PALETTE)), sorted(palette(WINDOWS_PALETTE)))

    def test_the_parser_reads_a_token_line_and_rejects_a_drifted_value(self) -> None:
        light, _ = stylesheet_tokens()
        match = TOKEN_LINE.search('static let ground = dynamic(light: "#E7E9EE", dark: "#161618") // --ground')
        self.assertIsNotNone(match)
        self.assertEqual(light["--ground"], normalise(match.group(1)))
        drifted = TOKEN_LINE.search('new("Ground", light: "#101A2C", dark: "#161618"), // --ground')
        self.assertNotEqual(light["--ground"], normalise(drifted.group(1)))


if __name__ == "__main__":
    unittest.main()
