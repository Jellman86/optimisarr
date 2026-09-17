"""Generate stable WiX component identities for a sealed, offline installer payload."""
from pathlib import Path
import argparse
import hashlib
import uuid
import xml.etree.ElementTree as ET

NS = 'http://wixtoolset.org/schemas/v4/wxs'
IDENTITY = uuid.UUID('f13e0c18-610c-4595-bb64-64e1815bd584')
ET.register_namespace('', NS)


def element(parent, name, **attributes):
    return ET.SubElement(parent, '{' + NS + '}' + name, attributes)


def identifier(prefix, path):
    return prefix + hashlib.sha256(path.lower().encode()).hexdigest()[:28]


def generate(payload):
    payload = Path(payload).resolve()
    root = ET.Element('{' + NS + '}Wix')
    fragment = element(root, 'Fragment')
    group = element(fragment, 'ComponentGroup', Id='PayloadFiles')
    directories = {'': 'INSTALLFOLDER', 'runtime': 'RUNTIMEFOLDER'}
    seen = set()
    paths = sorted(payload.rglob('*'), key=lambda p: p.relative_to(payload).as_posix().lower())
    for file in paths:
        if file.is_symlink():
            raise ValueError('Installer payload must not contain symlinks')
        if not file.is_file():
            continue
        relative = file.relative_to(payload).as_posix()
        if relative.lower() in seen:
            raise ValueError('Duplicate Windows path: ' + relative)
        seen.add(relative.lower())
        if relative == 'runtime/dotnet.exe':
            continue  # The service host has explicit authoring in Package.wxs.
        parent = ''
        for part in file.relative_to(payload).parts[:-1]:
            current = parent + '/' + part if parent else part
            if current not in directories:
                directories[current] = identifier('D', current)
                ref = element(fragment, 'DirectoryRef', Id=directories[parent])
                element(ref, 'Directory', Id=directories[current], Name=part)
            parent = current
        component = element(group, 'Component', Id=identifier('C', relative),
                            Guid=str(uuid.uuid5(IDENTITY, relative.lower())),
                            Directory=directories[parent], Bitness='always64')
        element(component, 'File', Id=identifier('F', relative),
                Source='$(var.Payload)\\' + relative.replace('/', '\\'), KeyPath='yes')
    required = {'runtime/dotnet.exe', 'optimisarr.sidecar.service.dll', 'optimisarr.sidecar.tray.exe', 'optimisarr.sidecar.tray.dll', 'ffmpeg.exe', 'ffprobe.exe'}
    if not required.issubset(seen):
        raise ValueError('Incomplete installer payload: ' + ', '.join(sorted(required - seen)))
    ET.indent(root)
    return ET.tostring(root, encoding='unicode', xml_declaration=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('payload', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    args.output.write_text(generate(args.payload), encoding='utf-8')
