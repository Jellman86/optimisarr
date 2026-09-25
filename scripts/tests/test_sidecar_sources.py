import importlib.util
import io
from pathlib import Path
import tarfile
import tempfile
import unittest
import zipfile

SPEC = importlib.util.spec_from_file_location('sidecar_sources', Path(__file__).resolve().parents[1] / 'package_sidecar_sources.py')
source = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(source)


class SidecarSourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def test_missing_dependency_cannot_certify_mac_sources(self):
        info = self.root / 'BUILD-INFO.txt'
        info.write_text('x264 stable ' + 'a' * 40 + '\n')
        with self.assertRaisesRegex(ValueError, 'every bundled'):
            source.read_mac_revisions(info)

    def test_duplicate_dependency_is_rejected(self):
        info = self.root / 'BUILD-INFO.txt'
        info.write_text(('x264 stable ' + 'a' * 40 + '\n') * 2)
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            source.read_mac_revisions(info)

    def test_exact_revision_is_taken_from_build_info_not_tag(self):
        info = self.root / 'BUILD-INFO.txt'
        info.write_text('built: 2026-09-17\n' + ''.join(f'{name} moving-tag {"a" * 40}\n' for name in source.MAC_REPOS))
        self.assertEqual(set(source.read_mac_revisions(info).values()), {'a' * 40})

    def cache(self, name):
        compressed = io.BytesIO()
        with tarfile.open(fileobj=compressed, mode='w:gz') as archive:
            entry = tarfile.TarInfo(name)
            entry.size = 3
            archive.addfile(entry, io.BytesIO(b'abc'))
        target = self.root / 'cache.zip'
        with zipfile.ZipFile(target, 'w') as archive:
            archive.writestr('cache.tar.gz', compressed.getvalue())
        return target

    def test_wrong_artifact_hash_is_rejected_before_extraction(self):
        cache = self.cache('.cache/downloads/x_' + 'a' * 64 + '.tar.xz')
        with self.assertRaisesRegex(ValueError, 'checksum'):
            source.extract_windows_cache(cache, self.root, 'b' * 64)

    def test_path_traversal_is_rejected(self):
        cache = self.cache('../x_' + 'a' * 64 + '.tar.xz')
        with self.assertRaisesRegex(ValueError, 'Unexpected'):
            source.extract_windows_cache(cache, self.root, source.sha256(cache))

    def test_cache_extracts_only_archive_bytes_to_flat_names(self):
        name = '50-x264_' + 'a' * 64 + '.tar.xz'
        cache = self.cache('.cache/downloads/' + name)
        files = source.extract_windows_cache(cache, self.root, source.sha256(cache))
        self.assertEqual(files, [self.root / name])
        self.assertEqual(files[0].read_bytes(), b'abc')

    def test_source_aliases_are_recorded_and_must_resolve(self):
        hashed = '50-x264_' + 'a' * 64 + '.tar.xz'
        compressed = io.BytesIO()
        with tarfile.open(fileobj=compressed, mode='w:gz') as archive:
            entry = tarfile.TarInfo('.cache/downloads/' + hashed)
            entry.size = 3
            archive.addfile(entry, io.BytesIO(b'abc'))
            entry = tarfile.TarInfo('.cache/downloads/50-x264.tar.xz')
            entry.type = tarfile.SYMTYPE
            entry.linkname = hashed
            archive.addfile(entry)
        target = self.root / 'cache.zip'
        with zipfile.ZipFile(target, 'w') as archive:
            archive.writestr('cache.tar.gz', compressed.getvalue())
        aliases = {}
        source.extract_windows_cache(target, self.root, source.sha256(target), aliases)
        self.assertEqual(aliases, {'50-x264.tar.xz': hashed})
        self.assertFalse((self.root / '50-x264.tar.xz').exists())

    def test_updated_binary_cannot_publish_previous_sources(self):
        script = self.root / 'fetch.ps1'
        script.write_text(f"$Release = '{source.WINDOWS_RELEASE}'\n$Asset = '{source.WINDOWS_BINARY_ASSET}'\n$Sha256 = '{'a' * 64}'\n")
        with self.assertRaisesRegex(ValueError, 'pin changed'):
            source.validate_windows_toolchain(script)

    def test_binary_manifest_matches_checked_in_fetch_pins(self):
        result = source.validate_windows_toolchain(source.ROOT / 'sidecars/windows/scripts/fetch-ffmpeg.ps1')
        self.assertEqual(result['sha256'], source.WINDOWS_BINARY_SHA256)

    def test_split_parts_stay_below_upload_limit(self):
        files = []
        for index in range(3):
            path = self.root / f'source-{index}.tar'
            path.write_bytes(b'x' * 15000)
            files.append(path)
        parts = source.write_parts(files, self.root, 'release-sources', limit=40000)
        self.assertEqual(len(parts), 3)
        self.assertTrue(all(p.stat().st_size < 40000 for p in parts))

    def test_git_archive_uses_recorded_commit_not_dirty_checkout(self):
        checkout = self.root / 'repo'
        source.command('git', 'init', str(checkout))
        (checkout / 'LICENSE').write_text('original licence')
        source.command('git', 'add', 'LICENSE', cwd=checkout)
        source.command('git', '-c', 'user.name=Source test', '-c', 'user.email=source@example.invalid',
                       'commit', '-m', 'fixture', cwd=checkout)
        revision = source.command('git', 'rev-parse', 'HEAD', cwd=checkout).decode().strip()
        (checkout / 'LICENSE').write_text('uncommitted change')
        archive, resolved = source.git_source('fixture', 'unused', revision, self.root, checkout)
        with tarfile.open(archive) as contents:
            self.assertEqual(contents.extractfile('fixture/LICENSE').read(), b'original licence')
        self.assertEqual(resolved, revision)

    def test_source_licenses_do_not_extract_symlinks(self):
        archive = self.root / 'source.tar'
        with tarfile.open(archive, 'w') as stream:
            entry = tarfile.TarInfo('library/LICENSE')
            entry.type = tarfile.SYMTYPE
            entry.linkname = '/etc/passwd'
            stream.addfile(entry)
        with self.assertRaisesRegex(ValueError, 'No licence'):
            source.copy_licenses(archive, self.root / 'licenses')


if __name__ == '__main__':
    unittest.main()
