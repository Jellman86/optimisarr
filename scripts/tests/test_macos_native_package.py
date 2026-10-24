import importlib.util
import json
import io
from pathlib import Path
import plistlib
import sys
import tempfile
import unittest
import zipfile

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
spec = importlib.util.spec_from_file_location('native_package', SCRIPTS / 'macos_native_package.py')
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)
MACH_O = b'\xcf\xfa\xed\xfe' + bytes(28)


class NativePackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.app = self.root / 'Test.app'
        self.resources = self.app / 'Contents/Resources'
        self.native = self.resources / 'native-quality'
        self.native.mkdir(parents=True)
        self.main = self.app / 'Contents/MacOS/Test'
        self.main.parent.mkdir()
        self.main.write_bytes(MACH_O)
        with (self.app / 'Contents/Info.plist').open('wb') as stream:
            plistlib.dump({'CFBundleExecutable': 'Test'}, stream)
        for name in ['Optimisarr.Sidecar.Native', 'libcoreclr.dylib', 'libhostfxr.dylib']:
            (self.native / name).write_bytes(MACH_O)
            (self.native / name).chmod(0o755)
        (self.native / 'Optimisarr.Sidecar.Native.dll').write_bytes(b'managed')
        (self.native / 'Optimisarr.Sidecar.Native.deps.json').write_text('{}')
        self.config = self.native / 'Optimisarr.Sidecar.Native.runtimeconfig.json'
        self.config.write_text(json.dumps({'runtimeOptions': {'includedFrameworks': [
            {'name': 'Microsoft.NETCore.App', 'version': '10.0.10'}]}}))
        self.notices = self.native / 'licenses/Microsoft.NETCore.App/10.0.10'
        self.notices.mkdir(parents=True)
        for name in ['LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT']:
            (self.notices / name).write_text('fixture notice')

    def test_complete_self_contained_runtime_and_its_exact_notices_pass(self):
        package.validate_runtime(self.native)

    def test_framework_dependent_output_is_refused_even_with_managed_code(self):
        self.config.write_text(json.dumps({'runtimeOptions': {'framework': {
            'name': 'Microsoft.NETCore.App', 'version': '10.0.10'}}}))
        with self.assertRaisesRegex(ValueError, 'self-contained'):
            package.validate_runtime(self.native)

    def test_missing_or_previous_runtime_notices_block_packaging(self):
        for change in ['remove', 'version']:
            with self.subTest(change=change):
                target = self.notices / 'THIRD-PARTY-NOTICES.TXT'
                if change == 'remove':
                    target.unlink()
                else:
                    self.config.write_text(json.dumps({'runtimeOptions': {'includedFrameworks': [
                        {'name': 'Microsoft.NETCore.App', 'version': '10.0.11'}]}}))
                with self.assertRaisesRegex(ValueError, 'notice'):
                    package.validate_runtime(self.native)

    def test_missing_native_runtime_library_blocks_packaging(self):
        (self.native / 'libcoreclr.dylib').unlink()
        with self.assertRaisesRegex(ValueError, 'libcoreclr'):
            package.validate_runtime(self.native)

    def test_stripped_sdk_notices_are_taken_from_the_exact_official_runtime_package(self):
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, 'w') as bundle:
            bundle.writestr('LICENSE.TXT', 'exact license')
            bundle.writestr('THIRD-PARTY-NOTICES.TXT', 'exact notices')
        urls = []
        def download(url):
            urls.append(url)
            return archive.getvalue()
        pack = {'PackageDirectory': str(self.root / 'stripped'), 'NuGetPackageId':
                'Microsoft.NETCore.App.Runtime.osx-arm64', 'NuGetPackageVersion': '10.0.10'}
        destination = self.root / 'downloaded-notices'
        package.copy_runtime_notices(pack, destination, download=download)
        self.assertEqual(['https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.osx-arm64/10.0.10/'
                          'microsoft.netcore.app.runtime.osx-arm64.10.0.10.nupkg'], urls)
        self.assertEqual('exact notices', (destination / 'THIRD-PARTY-NOTICES.TXT').read_text())
        provenance = json.loads((destination / 'provenance.json').read_text())
        self.assertEqual(urls[0], provenance['url'])
        self.assertEqual(128, len(provenance['sha512']))

    def test_downloaded_runtime_package_without_complete_notices_is_refused(self):
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, 'w') as bundle:
            bundle.writestr('LICENSE.TXT', 'exact license')
        pack = {'PackageDirectory': str(self.root / 'stripped'), 'NuGetPackageId':
                'Microsoft.NETCore.App.Runtime.osx-arm64', 'NuGetPackageVersion': '10.0.10'}
        with self.assertRaisesRegex(ValueError, 'notice'):
            package.copy_runtime_notices(pack, self.root / 'notices', download=lambda _: archive.getvalue())

    def test_signs_nested_mach_o_before_sealing_app_and_limits_jit_to_helper(self):
        (self.resources / 'ffmpeg').write_bytes(MACH_O)
        calls = []
        package.sign_bundle(self.app, 'Developer ID Application: Fixture', run=lambda args, **kwargs: calls.append(args))
        signed = [args for args in calls if '--sign' in args]
        self.assertEqual(self.app, Path(signed[-1][-1]))
        self.assertEqual({self.app, self.resources / 'ffmpeg', self.native / 'Optimisarr.Sidecar.Native',
                          self.native / 'libcoreclr.dylib', self.native / 'libhostfxr.dylib'},
                         {Path(args[-1]) for args in signed})
        entitled = [args for args in signed if '--entitlements' in args]
        self.assertEqual(1, len(entitled))
        self.assertEqual(self.native / 'Optimisarr.Sidecar.Native', Path(entitled[0][-1]))
        with Path(entitled[0][entitled[0].index('--entitlements') + 1]).open('rb') as stream:
            self.assertEqual({'com.apple.security.cs.allow-jit': True}, plistlib.load(stream))
        self.assertTrue(all('--options' in args and 'runtime' in args for args in signed))
        self.assertTrue(all('--timestamp' in args for args in signed))

    def test_explicit_keychain_is_used_for_every_signature_without_global_preferences(self):
        calls = []
        keychain = self.root / 'signing.keychain-db'
        package.sign_bundle(self.app, 'Developer ID Application: Fixture', keychain=keychain,
                            run=lambda args, **kwargs: calls.append(args))
        signed = [args for args in calls if '--sign' in args]
        self.assertTrue(signed)
        self.assertTrue(all(args[args.index('--keychain') + 1] == str(keychain) for args in signed))
        self.assertFalse(any(args[0] == 'security' for args in calls))

    def test_ad_hoc_development_signing_does_not_enable_team_based_library_validation(self):
        calls = []
        package.sign_bundle(self.app, '-', run=lambda args, **kwargs: calls.append(args))
        signed = [args for args in calls if '--sign' in args]
        self.assertTrue(signed)
        self.assertFalse(any('--options' in args or '--timestamp' in args or '--entitlements' in args for args in signed))

    def test_developer_identity_adds_secure_timestamps_to_all_native_signatures(self):
        calls = []
        package.sign_bundle(self.app, 'Developer ID Application: Fixture', run=lambda args, **kwargs: calls.append(args))
        self.assertTrue(all('--timestamp' in args for args in calls if '--sign' in args))

    def test_additional_macos_executable_is_signed_before_the_app(self):
        tool = self.main.parent / 'another-tool'
        tool.write_bytes(MACH_O)
        calls = []
        package.sign_bundle(self.app, '-', run=lambda args, **kwargs: calls.append(args))
        signed = [Path(args[-1]) for args in calls if '--sign' in args]
        self.assertIn(tool, signed[:-1])

    def test_foreign_symlinks_are_refused_before_signing_any_code(self):
        (self.resources / 'foreign').symlink_to(self.main)
        calls = []
        with self.assertRaisesRegex(ValueError, 'link'):
            package.sign_bundle(self.app, '-', run=lambda args, **kwargs: calls.append(args))
        self.assertEqual([], calls)
