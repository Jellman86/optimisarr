from pathlib import Path
import shutil
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from check_unraid_metadata import validate

ROOT = Path(__file__).resolve().parents[2]


class UnraidMetadataTests(unittest.TestCase):
    def test_repository_template_matches_the_container_contract(self):
        self.assertEqual([], validate(ROOT))

    def test_detects_development_image_in_the_release_listing(self):
        self.check_mutation('Repository', 'ghcr.io/jellman86/optimisarr:dev', 'release image')

    def test_detects_profile_and_application_icon_drift(self):
        self.check_mutation('Icon', 'https://example.org/old.png', 'icon')

    def test_detects_port_mapping_that_breaks_the_webui(self):
        self.check_mutation("Config[@Type='Port']", None, 'port', attribute=('Target', '8788'))

    def test_detects_template_drifting_from_the_worker_default(self):
        self.check_mutation("Config[@Target='OPTIMISARR_EXPERIMENTAL_REMOTE_WORKERS']", 'false', 'disable control')

    def test_detects_work_directory_outside_the_storage_mapping(self):
        self.check_mutation("Config[@Target='OPTIMISARR_WORK_DIR']", '/work', 'storage root')

    def check_mutation(self, tag, value, expected, attribute=None):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ['unraid', 'web/public', 'docs/images', 'docs/setup']:
                (root / name).mkdir(parents=True, exist_ok=True)
            for name in ['unraid/optimisarr.xml', 'ca_profile.xml', 'web/public/favicon-192.png',
                         'docs/images/optimisarr-dashboard-dark.png', 'docs/images/optimisarr-queue-dark.png',
                         'docs/setup/unraid.md', 'LICENSE', 'Dockerfile']:
                shutil.copyfile(ROOT / name, root / name)
            tree = ET.parse(root / 'unraid/optimisarr.xml')
            node = tree.find(tag)
            self.assertIsNotNone(node, tag)
            if attribute:
                node.set(*attribute)
            else:
                node.text = value
            tree.write(root / 'unraid/optimisarr.xml')
            self.assertTrue(any(expected in error for error in validate(root)), validate(root))
