#!/usr/bin/env python3
"""Publish the Mac's shared quality engine and sign every nested native binary."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import platform
import plistlib
import re
import subprocess
import urllib.request
import zipfile

from check_macos_minimum import is_mach_o

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'sidecars/native/src/Optimisarr.Sidecar.Native'
HOST = 'Optimisarr.Sidecar.Native'
ENTITLEMENTS = ROOT / 'sidecars/macos/Resources/NativeQuality.entitlements'
NOTICE_FILES = ['LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT']


def download_package(url):
    with urllib.request.urlopen(url, timeout=120) as response:
        data = response.read(100 * 1024 * 1024 + 1)
    if len(data) > 100 * 1024 * 1024:
        raise ValueError('Runtime notice package exceeds its download budget')
    return data


def copy_runtime_notices(pack, destination, download=download_package):
    source = Path(pack['PackageDirectory'])
    notices = {}
    provenance = {'package': pack['NuGetPackageId'], 'version': pack['NuGetPackageVersion']}
    if all((source / name).is_file() for name in NOTICE_FILES):
        notices = {name: (source / name).read_bytes() for name in NOTICE_FILES}
    else:
        # Homebrew strips these from its runtime packs. Fetch only the exact official package,
        # retaining its identity/hash, rather than silently substituting the SDK's notices.
        identity = pack['NuGetPackageId'].lower()
        version = pack['NuGetPackageVersion']
        if (not re.fullmatch(r'microsoft\.(?:netcore|aspnetcore)\.app\.runtime\.osx-(?:arm64|x64)', identity)
                or not re.fullmatch(r'\d+\.\d+\.\d+', version)):
            raise ValueError('Unsupported runtime notice package identity')
        url = f'https://api.nuget.org/v3-flatcontainer/{identity}/{version}/{identity}.{version}.nupkg'
        data = download(url)
        provenance.update(url=url, sha512=hashlib.sha512(data).hexdigest())
        with zipfile.ZipFile(io.BytesIO(data)) as bundle:
            for name in NOTICE_FILES:
                entries = [entry for entry in bundle.infolist() if entry.filename == name]
                if len(entries) != 1 or not 0 < entries[0].file_size <= 5 * 1024 * 1024:
                    raise ValueError(f'Missing or invalid runtime notice: {name}')
                notices[name] = bundle.read(entries[0])
    if any(not data for data in notices.values()):
        raise ValueError('Empty runtime notice')
    destination.mkdir(parents=True, exist_ok=False)
    for name, data in notices.items():
        (destination / name).write_bytes(data)
    (destination / 'provenance.json').write_text(json.dumps(provenance, indent=2) + '\n')


def runtime_frameworks(native):
    config = json.loads((native / f'{HOST}.runtimeconfig.json').read_text())['runtimeOptions']
    frameworks = config.get('includedFrameworks', [])
    if config.get('framework') or config.get('frameworks') or not frameworks:
        raise ValueError('The native quality helper must be self-contained')
    result = {}
    for framework in frameworks:
        name, version = framework['name'], framework['version']
        if (not re.fullmatch(r'Microsoft\.(?:NETCore|AspNetCore)\.App', name)
                or not re.fullmatch(r'\d+\.\d+\.\d+', version) or name in result):
            raise ValueError('Unsupported or ambiguous native runtime framework')
        result[name] = version
    if 'Microsoft.NETCore.App' not in result:
        raise ValueError('The native quality helper must include its self-contained .NET runtime')
    return result


def validate_runtime(native):
    for name in [HOST, f'{HOST}.dll', f'{HOST}.deps.json', f'{HOST}.runtimeconfig.json',
                 'libcoreclr.dylib', 'libhostfxr.dylib']:
        path = native / name
        if path.is_symlink() or not path.is_file() or path.stat().st_size == 0:
            raise ValueError(f'Missing native quality runtime file: {name}')
    if not os.access(native / HOST, os.X_OK):
        raise ValueError('The native quality helper must be executable')
    for name, version in runtime_frameworks(native).items():
        for filename in NOTICE_FILES:
            path = native / 'licenses' / name / version / filename
            if not path.is_file() or path.stat().st_size == 0:
                raise ValueError(f'Missing exact native runtime notice: {name}/{version}/{filename}')


def smoke_runtime(native):
    validate_runtime(native)
    # Reaching the parser executes managed code and proves JIT/library loading work in the
    # signed relocated package. This request cannot pair, access media or contact a server.
    refused = subprocess.run([str(native / HOST)], input=b'{}\n', capture_output=True,
                             cwd=native, timeout=30, env={'PATH': '/usr/bin:/bin'})
    events = [json.loads(line) for line in refused.stdout.splitlines()]
    if (refused.returncode != 2 or len(events) != 1 or events[0].get('version') != 1
            or events[0].get('type') != 'refused'):
        raise ValueError(f'Packaged native helper cannot start and refuse an invalid request: '
                         f'exit={refused.returncode}, stderr={refused.stderr[:2048]!r}')


def publish(destination):
    machine = platform.machine()
    rid = {'arm64': 'osx-arm64', 'x86_64': 'osx-x64'}.get(machine)
    if platform.system() != 'Darwin' or rid is None:
        raise ValueError('Publish the native quality helper on its target Mac architecture')
    # A fresh output prevents a previous runtime or architecture surviving an upgrade.
    destination.mkdir(parents=True, exist_ok=False)
    properties = [f'-p:RuntimeIdentifier={rid}', '-p:SelfContained=true']
    subprocess.run(['dotnet', 'publish', str(PROJECT), '-c', 'Release', '-warnaserror',
                    *properties, '-o', str(destination)], check=True)
    resolved = subprocess.run(['dotnet', 'msbuild', str(PROJECT), '-t:ResolveFrameworkReferences',
                               *properties, '-getItem:ResolvedRuntimePack'],
                              check=True, capture_output=True, text=True)
    packs = json.loads(resolved.stdout)['Items']['ResolvedRuntimePack']
    for name, version in runtime_frameworks(destination).items():
        matches = [pack for pack in packs if pack['FrameworkName'] == name
                   and pack['NuGetPackageVersion'] == version and pack['RuntimeIdentifier'] == rid]
        if len(matches) != 1:
            raise ValueError(f'Cannot resolve exact runtime notices: {name}/{version}')
        copy_runtime_notices(matches[0], destination / 'licenses' / name / version)
    validate_runtime(destination)


def sign_bundle(app, identity, run=subprocess.run, keychain=None):
    contents = app / 'Contents'
    native = contents / 'Resources/native-quality'
    validate_runtime(native)
    paths = sorted(contents.rglob('*'))
    if any(path.is_symlink() for path in paths):
        raise ValueError('Unexpected link in the native app bundle')
    with (contents / 'Info.plist').open('rb') as stream:
        main = contents / 'MacOS' / plistlib.load(stream)['CFBundleExecutable']
    # Seal code inside out. Managed assemblies and notices are resources, not Mach-O code.
    binaries = [path for path in paths if path.is_file() and is_mach_o(path)
                and path != main]
    for binary in binaries:
        args = ['codesign', '--force', '--sign', identity, *(['--keychain', str(keychain)] if keychain else [])]
        if identity != '-':
            args.extend(['--options', 'runtime', '--timestamp'])
            if binary == native / HOST:
                args.extend(['--entitlements', str(ENTITLEMENTS)])
        run([*args, str(binary)], check=True)
    args = ['codesign', '--force', '--sign', identity, *(['--keychain', str(keychain)] if keychain else [])]
    if identity != '-':
        args.extend(['--options', 'runtime', '--timestamp'])
    run([*args, str(app)], check=True)
    run(['codesign', '--verify', '--deep', '--strict', str(app)], check=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest='action', required=True)
    publish_parser = subparsers.add_parser('publish')
    publish_parser.add_argument('destination', type=Path)
    sign_parser = subparsers.add_parser('sign')
    sign_parser.add_argument('bundle', type=Path)
    sign_parser.add_argument('--identity', default='-')
    sign_parser.add_argument('--keychain', type=Path)
    check_parser = subparsers.add_parser('check')
    check_parser.add_argument('bundle', type=Path)
    smoke_parser = subparsers.add_parser('smoke')
    smoke_parser.add_argument('bundle', type=Path)
    arguments = parser.parse_args()
    if arguments.action == 'publish':
        publish(arguments.destination.resolve())
    elif arguments.action == 'sign':
        sign_bundle(arguments.bundle.resolve(), arguments.identity, keychain=arguments.keychain)
    elif arguments.action == 'check':
        validate_runtime(arguments.bundle.resolve() / 'Contents/Resources/native-quality')
    else:
        smoke_runtime(arguments.bundle.resolve() / 'Contents/Resources/native-quality')
