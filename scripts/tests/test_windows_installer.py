import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('payload', ROOT / 'sidecars/windows/installer/generate_payload.py')
payload = importlib.util.module_from_spec(spec)
spec.loader.exec_module(payload)
NS = {'w': payload.NS}


class InstallerPayloadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in ['runtime/dotnet.exe', 'runtime/shared/10/runtime.dll', 'Optimisarr.Sidecar.Service.dll', 'Optimisarr.Sidecar.Tray.exe', 'Optimisarr.Sidecar.Tray.dll', 'ffmpeg.exe', 'ffprobe.exe']:
            self.add(name)

    def add(self, name):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text('fixture')

    def components(self):
        root = ET.fromstring(payload.generate(self.root))
        return {c.find('w:File', NS).attrib['Source']: c.attrib['Guid'] for c in root.findall('.//w:Component', NS)}

    def test_adding_files_preserves_existing_component_identities(self):
        before = self.components()
        self.add('runtime/new.dll')
        after = self.components()
        self.assertEqual(before, {k: after[k] for k in before})

    def test_service_host_is_authored_once_and_media_tools_are_required(self):
        self.assertFalse(any(k.endswith('dotnet.exe') for k in self.components()))
        (self.root / 'ffprobe.exe').unlink()
        with self.assertRaisesRegex(ValueError, 'Incomplete'):
            payload.generate(self.root)

    def test_symlinks_cannot_smuggle_external_files_into_the_package(self):
        link = self.root / 'external.dll'
        try:
            link.symlink_to(__file__)
        except OSError:
            self.skipTest('Host cannot create symlinks')
        with self.assertRaisesRegex(ValueError, 'symlinks'):
            payload.generate(self.root)

    def test_installer_refuses_to_overwrite_a_manually_registered_service(self):
        root = ET.parse(ROOT / 'sidecars/windows/installer/Package.wxs')
        conditions = [item.attrib['Condition'] for item in root.findall('.//w:Launch', NS)]
        self.assertIn('Installed OR WIX_UPGRADE_DETECTED OR NOT EXISTINGSIDECAR', conditions)

    def test_installer_preserves_pairing_and_never_deletes_working_media(self):
        root = ET.parse(ROOT / 'sidecars/windows/installer/Package.wxs')
        directory = root.find('.//w:Component[@Id="PairingDirectory"]', NS)
        self.assertEqual('yes', directory.attrib['Permanent'])
        self.assertIn('D:P', directory.find('.//w:PermissionEx', NS).attrib['Sddl'])
        self.assertEqual([], root.findall('.//w:RemoveFile', NS))
        service = root.find('.//w:ServiceControl', NS)
        self.assertEqual('both', service.attrib['Stop'])
        self.assertNotIn('Start', service.attrib)


if __name__ == '__main__':
    unittest.main()
