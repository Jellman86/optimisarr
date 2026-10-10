import importlib.util
from pathlib import Path
import plistlib
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('macos_minimum', ROOT / 'scripts/check_macos_minimum.py')
minimum = importlib.util.module_from_spec(spec)
spec.loader.exec_module(minimum)

BUILD_VERSION = """Load command 9
      cmd LC_BUILD_VERSION
  cmdsize 32
 platform 1
    minos {minos}
      sdk 26.2
   ntools 1
"""
LEGACY_VERSION = """Load command 8
      cmd LC_VERSION_MIN_MACOSX
  cmdsize 16
  version {minos}
      sdk 10.15
"""
MACH_O = b'\xcf\xfa\xed\xfe' + bytes(28)


class MacosMinimumTests(unittest.TestCase):
    def test_reads_the_build_version_and_the_older_version_command(self):
        self.assertEqual('15.0', minimum.declared_minimum(BUILD_VERSION.format(minos='15.0')))
        self.assertEqual('10.13', minimum.declared_minimum(LEGACY_VERSION.format(minos='10.13')))
        self.assertIsNone(minimum.declared_minimum('Load command 1\n      cmd LC_SEGMENT_64\n'))

    def test_versions_compare_as_numbers_not_text(self):
        self.assertTrue(minimum.newer('15.0', '14.0'))
        self.assertTrue(minimum.newer('14.10', '14.9'))
        self.assertFalse(minimum.newer('14.0', '14'))
        self.assertFalse(minimum.newer('13.5', '14.0'))

    def test_a_tool_needing_a_newer_macos_than_the_app_declares_is_reported(self):
        with tempfile.TemporaryDirectory() as directory:
            app = self.bundle(Path(directory), {'ffmpeg': '15.0', 'ffprobe': '14.0'})
            problems = minimum.check_bundle(app, self.otool({'ffmpeg': '15.0', 'ffprobe': '14.0'}))
        self.assertEqual(1, len(problems))
        self.assertIn('ffmpeg needs macOS 15.0', problems[0])
        self.assertIn('declares 14.0', problems[0])

    def test_a_bundle_whose_binaries_all_fit_passes_and_non_mach_o_files_are_ignored(self):
        with tempfile.TemporaryDirectory() as directory:
            app = self.bundle(Path(directory), {'ffmpeg': '14.0'})
            (app / 'Contents/Resources/BUILD-INFO.txt').write_text('notes')
            self.assertEqual([], minimum.check_bundle(app, self.otool({'ffmpeg': '14.0'})))

    def test_a_binary_with_no_version_command_is_a_problem_rather_than_a_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            app = self.bundle(Path(directory), {'ffmpeg': None})
            problems = minimum.check_bundle(app, lambda path: 'Load command 1\n      cmd LC_SEGMENT_64\n')
        self.assertIn('no minimum macOS', problems[0])

    def test_system_only_tool_rejects_a_homebrew_library(self):
        output = BUILD_VERSION.format(minos='14.0') + "\ncmd LC_LOAD_DYLIB\nname /opt/homebrew/opt/giflib/lib/libgif.dylib (offset 24)\n"
        problems = minimum.check_binaries([Path('metric')], '14.0', lambda _: output, system_libraries_only=True)
        self.assertIn('/opt/homebrew/opt/giflib/lib/libgif.dylib', problems[0])

    def test_system_only_tool_rejects_relative_and_reexported_libraries(self):
        for command in ['LC_LOAD_DYLIB', 'LC_LOAD_WEAK_DYLIB', 'LC_REEXPORT_DYLIB', 'LC_LOAD_UPWARD_DYLIB', 'LC_LAZY_LOAD_DYLIB']:
            output = BUILD_VERSION.format(minos='14.0') + f"\ncmd {command}\nname @rpath/libcodec.dylib (offset 24)\n"
            self.assertTrue(minimum.check_binaries([Path('metric')], '14.0', lambda _: output, system_libraries_only=True))

    def test_system_only_tool_accepts_system_libraries_without_changing_ordinary_bundle_checks(self):
        output = BUILD_VERSION.format(minos='14.0') + "\ncmd LC_LOAD_DYLIB\nname /usr/lib/libSystem.B.dylib (offset 24)\ncmd LC_LOAD_WEAK_DYLIB\nname /System/Library/Frameworks/CoreFoundation.framework/Versions/A/CoreFoundation (offset 24)\n"
        self.assertEqual([], minimum.check_binaries([Path('metric')], '14.0', lambda _: output, system_libraries_only=True))
        self.assertEqual([], minimum.check_binaries([Path('app')], '14.0', lambda _: output + "\ncmd LC_LOAD_DYLIB\nname @rpath/libswiftCore.dylib (offset 24)\n"))

    def bundle(self, root, tools):
        app = root / 'Test.app'
        (app / 'Contents/MacOS').mkdir(parents=True)
        (app / 'Contents/Resources').mkdir()
        with (app / 'Contents/Info.plist').open('wb') as stream:
            plistlib.dump({'LSMinimumSystemVersion': '14.0'}, stream)
        (app / 'Contents/MacOS/Test').write_bytes(MACH_O)
        for name in tools:
            (app / 'Contents/Resources' / name).write_bytes(MACH_O)
        return app

    def otool(self, versions):
        def run(path):
            return BUILD_VERSION.format(minos=versions.get(Path(path).name, '14.0'))
        return run


if __name__ == '__main__':
    unittest.main()
