#!/usr/bin/env python3
"""Validate the shipped Unraid template against the release/container contract, offline."""
from pathlib import Path
import struct
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
RAW = 'https://raw.githubusercontent.com/Jellman86/optimisarr/main/'
PROJECT = 'https://github.com/Jellman86/optimisarr'


def validate(root: Path) -> list[str]:
    errors = []
    try:
        template = ET.parse(root / 'unraid/optimisarr.xml').getroot()
        profile = ET.parse(root / 'ca_profile.xml').getroot()
    except (OSError, ET.ParseError) as exc:
        return [f'Unraid XML cannot be read: {exc}']

    def expect(condition, message):
        if not condition:
            errors.append(message)

    expect(template.tag == 'Container' and template.get('version') == '2', 'Unraid template must use Container version 2')
    expect(profile.tag == 'CommunityApplications' and bool((profile.findtext('Profile') or '').strip()), 'Community Apps profile must be non-empty')
    expect(template.findtext('Repository') == 'ghcr.io/jellman86/optimisarr:latest', 'Unraid must use the stable release image')
    for tag, value in {'Name': 'Optimisarr', 'Project': PROJECT, 'Support': PROJECT+'/issues',
                       'TemplateURL': RAW+'unraid/optimisarr.xml', 'WebUI': 'http://[IP]:[PORT:8787]/',
                       'Network': 'bridge', 'Privileged': 'false', 'ReadMe': PROJECT+'/blob/main/docs/setup/unraid.md',
                       'License': PROJECT+'/blob/main/LICENSE'}.items():
        expect(template.findtext(tag) == value, f'Unraid {tag} does not match the supported contract')
    for xml in [template, profile]:
        expect(xml.findtext('Icon') == RAW+'web/public/favicon-192.png', 'Unraid icon must match the current application icon URL')
    expect(profile.findtext('WebPage') == PROJECT, 'Profile homepage must point to the project')
    icon = root / 'web/public/favicon-192.png'
    raw = icon.read_bytes() if icon.exists() else b''
    expect(len(raw) >= 24 and raw[:8] == b'\x89PNG\r\n\x1a\n' and struct.unpack('>II', raw[16:24]) == (192, 192),
           'Unraid icon must resolve to the 192-pixel application PNG')
    expect(bool((template.findtext('Overview') or '').strip()), 'Unraid overview must be non-empty')
    expect((root / 'LICENSE').exists(), 'Repository license must exist')
    for name in ['optimisarr-dashboard-dark.png', 'optimisarr-queue-dark.png']:
        expect(RAW+'docs/images/'+name in [node.text for node in template.findall('Screenshot')], 'Unraid screenshot metadata is missing: '+name)
        expect((root/'docs/images'/name).exists(), 'Unraid screenshot is missing: '+name)
    configs = template.findall('Config')
    keys = [(c.get('Type'), c.get('Target')) for c in configs]
    expect(len(keys) == len(set(keys)), 'Unraid config targets must not be duplicated')
    port = next((c for c in configs if c.get('Type') == 'Port'), None)
    expect(port is not None and port.get('Target') == '8787' and port.get('Mode') == 'tcp', 'Unraid port must map the container WebUI on TCP 8787')
    expect('EXPOSE 8787' in (root/'Dockerfile').read_text(), 'Container no longer exposes the template port')
    paths = {c.get('Target'): c for c in configs if c.get('Type') == 'Path'}
    expect(set(paths) == {'/config', '/data'} and all(c.get('Mode') == 'rw' for c in paths.values()), 'Use only config and a common read-write storage root')
    variables = {c.get('Target'): c for c in configs if c.get('Type') == 'Variable'}
    for target, value in {'OPTIMISARR_WORK_DIR': '/data/.optimisarr/work', 'OPTIMISARR_TRASH_DIR': '/data/.optimisarr/trash'}.items():
        node = variables.get(target)
        expect(node is not None and node.text == value and node.get('Default') == value, target+' must remain inside the storage root')
    preview = variables.get('OPTIMISARR_EXPERIMENTAL_REMOTE_WORKERS')
    expect(preview is not None and preview.text == 'true' and preview.get('Default') == 'true', 'Remote workers must default on with an explicit disable control')
    token = variables.get('OPTIMISARR_ADMIN_TOKEN')
    expect(token is not None and token.get('Mask') == 'true' and not (token.text or '').strip(), 'Admin token must be masked and contain no shipped credential')
    for target, value in {'PUID': '99', 'PGID': '100', 'UMASK': '002'}.items():
        expect(target in variables and variables[target].text == value, 'Unraid permission defaults changed: '+target)
    return errors


if __name__ == '__main__':
    failures = validate(ROOT)
    if failures:
        print('Unraid metadata errors:', *failures, sep='\n  ')
        sys.exit(1)
    print('Unraid profile, assets and container contract are consistent.')
