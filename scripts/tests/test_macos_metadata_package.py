import hashlib
import importlib.util
import io
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))
spec = importlib.util.spec_from_file_location('metadata_package', SCRIPTS / 'macos_metadata_package.py')
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


class MetadataPackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'metadata'

    def archive(self, extra=None, omit=None):
        files = {'exiftool': b'#!/usr/bin/env perl\nuse Image::ExifTool;\n',
                 'lib/Image/ExifTool.pm': b'package Image::ExifTool; 1;\n',
                 'LICENSE': b'fixture license', 'README': b'fixture readme',
                 't/test.jpg': b'unwanted test media'}
        files.update(extra or {})
        if omit:
            del files[omit]
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode='w:gz') as archive:
            for name, data in files.items():
                entry = tarfile.TarInfo(f'exiftool-{package.REVISION}/{name}')
                if data is None:
                    entry.type = tarfile.SYMTYPE
                    entry.linkname = '../../outside'
                    archive.addfile(entry)
                else:
                    entry.size = len(data)
                    archive.addfile(entry, io.BytesIO(data))
        return output.getvalue()

    def extract(self, data):
        package.extract(data, self.root, expected_hash=hashlib.sha256(data).hexdigest())

    def test_packages_source_modules_and_notices_with_the_system_perl_interpreter(self):
        self.extract(self.archive())
        self.assertTrue((self.root / 'exiftool').read_bytes().startswith(b'#!/usr/bin/perl\n'))
        self.assertEqual(0o755, (self.root / 'exiftool').stat().st_mode & 0o777)
        self.assertTrue((self.root / 'lib/Image/ExifTool.pm').is_file())
        self.assertTrue((self.root / 'LICENSE').is_file())
        self.assertFalse((self.root / 't').exists())

    def test_wrong_source_checksum_is_refused_before_creating_the_destination(self):
        with self.assertRaisesRegex(ValueError, 'checksum'):
            package.extract(self.archive(), self.root)
        self.assertFalse(self.root.exists())

    def test_traversal_is_refused_before_extraction(self):
        for name in ['../outside', 'lib/../../outside']:
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, 'path'):
                self.extract(self.archive(extra={name: b'bad'}))
        self.assertFalse(self.root.exists())

    def test_duplicate_canonical_paths_cannot_replace_a_source_module(self):
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            self.extract(self.archive(extra={'lib//Image/ExifTool.pm': b'replacement'}))
        self.assertFalse(self.root.exists())

    def test_archive_links_cannot_supply_modules_outside_the_package(self):
        with self.assertRaisesRegex(ValueError, 'link'):
            self.extract(self.archive(extra={'lib/escape.pm': None}))
        self.assertFalse(self.root.exists())

    def test_incomplete_source_does_not_make_an_apparently_executable_tool(self):
        with self.assertRaisesRegex(ValueError, 'incomplete'):
            self.extract(self.archive(omit='lib/Image/ExifTool.pm'))
        self.assertFalse(self.root.exists())

    def test_packaging_refuses_to_overwrite_an_existing_metadata_directory(self):
        self.root.mkdir()
        (self.root / 'sentinel').write_text('keep')
        with self.assertRaises(FileExistsError):
            self.extract(self.archive())
        self.assertEqual('keep', (self.root / 'sentinel').read_text())
