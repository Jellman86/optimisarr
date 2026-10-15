import importlib.util
from pathlib import Path
import unittest

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
