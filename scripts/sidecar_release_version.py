#!/usr/bin/env python3
"""Refuse to publish native packages from a branch or inconsistent release tag."""

import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET


def resolve(ref_type: str, tag: str, application_version: str, requested: str = '') -> str:
    match = re.fullmatch(r'(?:sidecar-)?v(\d+\.\d+\.\d+)', tag)
    if ref_type != 'tag' or match is None:
        raise ValueError('Dispatch on an existing vX.Y.Z or sidecar-vX.Y.Z tag, never a branch.')
    version = match.group(1)
    if version != application_version or (requested and requested != version):
        raise ValueError('Tag, requested version and Directory.Build.props must agree.')
    return version


if __name__ == '__main__':
    root = Path(__file__).resolve().parents[1]
    application_version = ET.parse(root / 'Directory.Build.props').findtext('.//Version', '')
    version = resolve(os.environ.get('GITHUB_REF_TYPE', ''), os.environ.get('GITHUB_REF_NAME', ''),
                      application_version, os.environ.get('REQUESTED_VERSION', ''))
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
        output.write(f'value={version}\n')
    print(f'Validated release version {version}')
