import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('sidecar_release', ROOT / 'scripts/sidecar_release_version.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class SidecarReleaseVersionTests(unittest.TestCase):
    def test_coordinated_tag_matches_shared_version(self):
        self.assertEqual('0.2.13', release.resolve('tag', 'v0.2.13', '0.2.13'))

    def test_legacy_sidecar_tag_is_supported(self):
        self.assertEqual('0.2.13', release.resolve('tag', 'sidecar-v0.2.13', '0.2.13', '0.2.13'))

    def test_branch_dispatch_cannot_publish_a_release(self):
        with self.assertRaises(ValueError):
            release.resolve('branch', 'dev', '0.2.13', '0.2.13')

    def test_mismatched_or_malformed_versions_fail_before_signing(self):
        for tag, requested in [('v0.2.12', ''), ('v0.2.13', '0.2.12'), ('v1;echo bad', ''), ('unrelated', '')]:
            with self.subTest(tag=tag, requested=requested), self.assertRaises(ValueError):
                release.resolve('tag', tag, '0.2.13', requested)
