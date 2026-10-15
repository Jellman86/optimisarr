import importlib.util
from pathlib import Path
import unittest
import tempfile
import os

SPEC = importlib.util.spec_from_file_location('quality_bundle', Path(__file__).resolve().parents[1] / 'check_native_quality_bundle.py')
bundle = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bundle)


class NativeQualityBundleTests(unittest.TestCase):
    def proof(self):
        return {'version': 1, 'type': 'capabilities', 'capabilities': {
            'imageEncoders': ['mjpeg', 'libwebp'], 'imageMetadataSupported': True,
            'imageMetricRevision': 'a7a9c787341cf703dede03c2009fa460cae5e5df',
            'audioMetricRevision': 'f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3'}}

    def test_packaged_metrics_and_jpeg_metadata_proof_pass(self):
        bundle.validate(self.proof())

    def test_missing_metadata_or_either_pinned_metric_blocks_package_acceptance(self):
        for key, value in [('imageMetadataSupported', False), ('imageEncoders', []),
                           ('imageMetricRevision', None), ('audioMetricRevision', 'a' * 40)]:
            proof = self.proof()
            proof['capabilities'][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                bundle.validate(proof)

    def test_refused_helper_or_old_wire_version_cannot_qualify_a_package(self):
        for key, value in [('version', 0), ('type', 'refused')]:
            proof = self.proof()
            proof[key] = value
            with self.assertRaises(ValueError):
                bundle.validate(proof)

    def test_a_jpeg_only_package_cannot_claim_complete_image_tool_parity(self):
        proof = self.proof()
        proof['capabilities']['imageEncoders'] = ['mjpeg']
        with self.assertRaises(ValueError):
            bundle.validate(proof)


class NativeScratchBundleTests(unittest.TestCase):
    def test_completed_qualification_allows_only_the_empty_stable_coordinator(self):
        with tempfile.TemporaryDirectory() as root:
            control = Path(root) / '.optimisarr-native'
            control.mkdir()
            (control / 'root.lock').touch()
            bundle.validate_scratch(Path(root))

    def test_unknown_or_owned_files_are_never_hidden_by_the_control_directory_exception(self):
        for relative in ['source', 'native-capability-old/source.png', '.optimisarr-native/owner-dead.json',
                         '.optimisarr-native/unknown', '.optimisarr-native/root.lock/child']:
            with self.subTest(relative=relative), tempfile.TemporaryDirectory() as root:
                leaf = Path(root) / relative
                leaf.parent.mkdir(parents=True, exist_ok=True)
                leaf.touch()
                if not (Path(root) / '.optimisarr-native/root.lock').exists():
                    (Path(root) / '.optimisarr-native').mkdir(exist_ok=True)
                    (Path(root) / '.optimisarr-native/root.lock').touch()
                with self.assertRaises(ValueError):
                    bundle.validate_scratch(Path(root))

    def test_reparse_links_and_shared_or_nonempty_gate_files_do_not_qualify(self):
        for kind in ['control-link', 'gate-link', 'gate-hardlink', 'gate-content', 'missing-gate', 'missing-control']:
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as root, tempfile.TemporaryDirectory() as outside:
                control = Path(root) / '.optimisarr-native'
                gate = control / 'root.lock'
                target = Path(outside) / 'kept'
                target.touch()
                if kind == 'control-link':
                    control.symlink_to(outside, target_is_directory=True)
                elif kind != 'missing-control':
                    control.mkdir()
                    if kind == 'gate-link': gate.symlink_to(target)
                    elif kind == 'gate-hardlink': os.link(target, gate)
                    elif kind == 'gate-content': gate.write_text('unexpected')
                    elif kind != 'missing-gate': gate.touch()
                with self.assertRaises(ValueError):
                    bundle.validate_scratch(Path(root))
                self.assertTrue(target.exists())

    def test_failed_proof_retains_owned_work_even_after_the_helper_exits(self):
        with tempfile.TemporaryDirectory() as root:
            work = Path(root) / 'native-capability-owned'
            work.mkdir()
            copy = work / 'source.png'
            copy.write_text('still owned by a surviving encoder')
            self.assertFalse(bundle.retire_scratch(Path(root)))
            self.assertEqual(copy.read_text(), 'still owned by a surviving encoder')

    def test_successful_proof_retires_only_its_empty_control_directory(self):
        with tempfile.TemporaryDirectory() as parent:
            root = Path(parent) / 'proof'
            control = root / '.optimisarr-native'
            control.mkdir(parents=True)
            (control / 'root.lock').touch()
            self.assertTrue(bundle.retire_scratch(root))
            self.assertFalse(root.exists())
