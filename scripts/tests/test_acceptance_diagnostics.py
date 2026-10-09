import copy
from pathlib import Path
import sys
import unittest
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from acceptance.diagnostics import validate_bundle


class DiagnosticAcceptanceTests(unittest.TestCase):
    def bundle(self):
        return {'manifest': {'schemaVersion': 4, 'pathsIncluded': False, 'manifestId': 'test', 'participants': [{'state': 'Collected', 'operatingSystem': 'linux'}]},
                'jobs': [{'job': {'path': None}}],
                'events': [{'source': 'Server', 'reasonCode': 'Replacement.Replaced', 'details': {'report': {'passed': True}}},
                           {'source': 'Sidecar', 'reasonCode': 'Worker.ToolsIdentified', 'details': {'ffmpegSha256': 'a' * 64}, 'leaseId': 'lease', 'workerId': 1, 'attempt': 1, 'instanceId': 'instance', 'sourceSequence': 1},
                           {'source': 'Sidecar', 'reasonCode': 'Worker.TransferAcknowledged', 'leaseId': 'lease', 'workerId': 1, 'attempt': 1, 'instanceId': 'instance', 'sourceSequence': 2}]}

    def test_valid_correlated_collection_is_accepted(self):
        self.assertEqual(2, validate_bundle(self.bundle(), require_sidecar=True)['sidecarEvents'])

    def test_absent_sidecar_replay_unacknowledged_collection_or_path_is_a_failure(self):
        original = self.bundle()
        mutations = [lambda b: b['events'].__delitem__(slice(1, None)),
                     lambda b: b['events'].append(copy.deepcopy(b['events'][1])),
                     lambda b: b['manifest']['participants'][0].update(state='OfflineLocalEvidenceUnavailable'),
                     lambda b: b['jobs'][0]['job'].update(path='/private/media.mkv')]
        for mutate in mutations:
            bundle = copy.deepcopy(original); mutate(bundle)
            with self.assertRaises(AssertionError): validate_bundle(bundle, require_sidecar=True)

if __name__ == '__main__': unittest.main()
