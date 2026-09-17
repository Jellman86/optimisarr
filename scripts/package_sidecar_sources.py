#!/usr/bin/env python3
"""Retain exact bundled-media sources without resolving moving upstream versions."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
MAC_REPOS = {
    'x264': 'https://code.videolan.org/videolan/x264.git',
    'x265': 'https://bitbucket.org/multicoreware/x265_git.git',
    'svtav1': 'https://gitlab.com/AOMediaCodec/SVT-AV1.git',
    'dav1d': 'https://code.videolan.org/videolan/dav1d.git',
    'vmaf': 'https://github.com/Netflix/vmaf.git',
    'ffmpeg': 'https://github.com/FFmpeg/FFmpeg.git',
}
WINDOWS_RELEASE = 'autobuild-2026-09-14-13-17'
WINDOWS_BINARY_ASSET = 'ffmpeg-n8.1.2-52-g5a03dfa0f6-win64-gpl-8.1.zip'
WINDOWS_BINARY_SHA256 = 'f42dca81bdcce7ccab00bde91ceceb96685beab97c42a77c003c90771b47eb17'
WINDOWS_RUN = '34842508002'
WINDOWS_ARTIFACT = '10346857387'
WINDOWS_CACHE_SHA256 = '7ae4a31b353aeb1dafc738026cd4bc3eafe5ec25d51c538c24272717a5efea73'
WINDOWS_BUILD_SHA = '3e6685eda92f9288c15ac320139622dcedca09a4'
WINDOWS_FFMPEG_SHA = '5a03dfa0f607ee6156a59bdad3987cd3b858ee5d'
PART_LIMIT = 900_000_000


def sha256(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read_mac_revisions(path):
    result = {}
    for line in Path(path).read_text().splitlines():
        if line.startswith('built:') or not line.strip():
            continue
        fields = line.split()
        if len(fields) != 3 or fields[0] not in MAC_REPOS or not re.fullmatch(r'[0-9a-f]{40}', fields[2]):
            raise ValueError(f'Invalid media source revision: {line}')
        if fields[0] in result:
            raise ValueError(f'Duplicate dependency: {fields[0]}')
        result[fields[0]] = fields[2]
    if result.keys() != MAC_REPOS.keys():
        raise ValueError('BUILD-INFO must identify every bundled Mac dependency')
    return result


def validate_windows_toolchain(path):
    script = Path(path).read_text()
    expected = {'Release': WINDOWS_RELEASE, 'Asset': WINDOWS_BINARY_ASSET, 'Sha256': WINDOWS_BINARY_SHA256}
    for key, value in expected.items():
        matches = re.findall(r"^\$" + key + r"\s*=\s*'([^']+)'\s*$", script, re.MULTILINE)
        if len(matches) != 1 or (matches[0].lower() if key == 'Sha256' else matches[0]) != value:
            raise ValueError(f'Windows {key} pin changed; update and validate its corresponding source cache first')
    return {'release': WINDOWS_RELEASE, 'asset': WINDOWS_BINARY_ASSET, 'sha256': WINDOWS_BINARY_SHA256}


def command(*args, cwd=None):
    return subprocess.run(args, cwd=cwd, check=True, capture_output=True).stdout


def git_source(name, url, revision, stage, local=None):
    checkout = local if local and (local / '.git').exists() else stage / f'{name}-checkout'
    if not (checkout / '.git').exists():
        command('git', 'init', str(checkout))
        command('git', 'remote', 'add', 'origin', url, cwd=checkout)
        command('git', 'fetch', '--depth=1', 'origin', revision, cwd=checkout)
    resolved = command('git', 'rev-parse', f'{revision}^{{commit}}', cwd=checkout).decode().strip()
    if not resolved.startswith(revision):
        raise ValueError(f'{name}: source revision mismatch')
    # git archive ignores build outputs and working-tree edits in an existing checkout.
    archive = stage / f'{name}-{resolved}.tar'
    command('git', 'archive', '--format=tar', f'--prefix={name}/', f'--output={archive}', resolved, cwd=checkout)
    return archive, resolved


def copy_licenses(archive, destination):
    count = 0
    with tarfile.open(archive) as source:
        for item in source:
            base = PurePosixPath(item.name).name.upper()
            if not item.isfile() or not (base.startswith(('LICENSE', 'COPYING', 'NOTICE'))):
                continue
            path = PurePosixPath(item.name)
            if path.is_absolute() or '..' in path.parts or item.size > 5_000_000:
                raise ValueError('Invalid licence entry in source archive')
            target = destination.joinpath(*path.parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            with source.extractfile(item) as incoming, target.open('wb') as outgoing:
                shutil.copyfileobj(incoming, outgoing)
            count += 1
    if not count:
        raise ValueError(f'No licence files found in {archive.name}')


def extract_windows_cache(archive, destination, expected_hash=WINDOWS_CACHE_SHA256, aliases=None):
    if sha256(archive) != expected_hash:
        raise ValueError('Dependency-source artifact checksum mismatch')
    # Never extract upstream paths, links or git hooks. Preserve source tarballs as files.
    with zipfile.ZipFile(archive) as bundle:
        if bundle.namelist() != ['cache.tar.gz']:
            raise ValueError('Unexpected dependency artifact contents')
        with bundle.open('cache.tar.gz') as stream, tarfile.open(fileobj=stream, mode='r|gz') as cache:
            names = set()
            source_aliases = {}
            for item in cache:
                path = PurePosixPath(item.name)
                if path.is_absolute() or '..' in path.parts:
                    raise ValueError(f'Unexpected dependency source member: {item.name}')
                if item.isdir():
                    continue
                if item.issym():
                    if (not re.fullmatch(r'[\w.-]+\.tar\.xz', path.name)
                            or not re.fullmatch(r'[\w.-]+_[0-9a-f]{64}\.tar\.xz', item.linkname)
                            or path.name in source_aliases):
                        raise ValueError('Invalid dependency source alias')
                    source_aliases[path.name] = item.linkname
                    continue
                if (not item.isfile() or path.is_absolute() or '..' in path.parts
                        or not re.fullmatch(r'[\w.-]+_[0-9a-f]{64}\.tar\.xz', path.name)
                        or item.size > PART_LIMIT):
                    raise ValueError(f'Unexpected dependency source member: {item.name}')
                if path.name in names:
                    raise ValueError('Duplicate dependency source archive')
                names.add(path.name)
                target = destination / path.name
                with cache.extractfile(item) as incoming, target.open('wb') as outgoing:
                    shutil.copyfileobj(incoming, outgoing)
            if not names:
                raise ValueError('Dependency source cache was empty')
            if any(target not in names for target in source_aliases.values()):
                raise ValueError('Dependency source alias target is missing')
            if aliases is not None:
                aliases.update(source_aliases)
    return sorted(destination.glob('*.tar.xz'))


def write_parts(files, output, prefix, limit=PART_LIMIT):
    parts = []
    current = []
    size = 0
    for path in sorted(files):
        estimate = ((path.stat().st_size + 511) // 512 + 4) * 512
        if estimate + 10240 > limit:
            raise ValueError(f'Source file exceeds archive part limit: {path.name}')
        if current and size + estimate + 10240 > limit:
            parts.append(current)
            current, size = [], 0
        current.append(path)
        size += estimate
    if current:
        parts.append(current)
    outputs = []
    for index, group in enumerate(parts, 1):
        target = output / f'{prefix}-{index:02}.tar'
        with tarfile.open(target, 'w') as archive:
            for path in group:
                archive.add(path, arcname=path.name, recursive=False)
        if target.stat().st_size > limit:
            raise ValueError('Archive part exceeded the publication limit')
        outputs.append(target)
    return outputs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--platform', choices=['macos', 'windows'], required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--mac-build-info', type=Path, default=ROOT / 'sidecars/macos/vendor/BUILD-INFO.txt')
    parser.add_argument('--mac-sources', type=Path)
    parser.add_argument('--windows-cache', type=Path)
    args = parser.parse_args()
    if args.output.exists():
        parser.error('--output must be a new directory')
    args.output.mkdir(parents=True)
    version = ET.parse(ROOT / 'Directory.Build.props').findtext('.//Version')
    if not re.fullmatch(r'\d+\.\d+\.\d+', version or ''):
        parser.error('Invalid application version')
    records = []
    with tempfile.TemporaryDirectory(prefix='optimisarr-sources-') as work:
        stage = Path(work)
        files = []
        if args.platform == 'macos':
            revisions = read_mac_revisions(args.mac_build_info)
            shutil.copy2(args.mac_build_info, args.output / 'BUILD-INFO.txt')
            for name, revision in revisions.items():
                archive, resolved = git_source(name, MAC_REPOS[name], revision, stage,
                    args.mac_sources / name if args.mac_sources else None)
                copy_licenses(archive, args.output / 'licenses')
                files.append(archive)
                records.append({'name': name, 'repository': MAC_REPOS[name], 'revision': resolved, 'sha256': sha256(archive)})
        else:
            binary = validate_windows_toolchain(ROOT / 'sidecars/windows/scripts/fetch-ffmpeg.ps1')
            cache = args.windows_cache or stage / 'dependency-sources.zip'
            if not args.windows_cache:
                with cache.open('wb') as stream:
                    subprocess.run(['gh', 'api', f'repos/BtbN/FFmpeg-Builds/actions/artifacts/{WINDOWS_ARTIFACT}/zip'], stdout=stream, check=True)
            source_dir = stage / 'dependencies'
            source_dir.mkdir()
            aliases = {}
            files.extend(extract_windows_cache(cache, source_dir, aliases=aliases))
            records.append({'name': 'dependency-cache', 'binary': binary, 'upstreamRun': WINDOWS_RUN, 'artifactId': WINDOWS_ARTIFACT,
                            'sha256': WINDOWS_CACHE_SHA256, 'aliases': aliases, 'coverage': 'Complete upstream download cache; includes dependencies beyond the Windows GPL variant', 'archives': [{ 'file': f.name, 'sha256': sha256(f)} for f in files]})
            for name, url, revision in [('ffmpeg-builds', 'https://github.com/BtbN/FFmpeg-Builds.git', WINDOWS_BUILD_SHA),
                                        ('ffmpeg', MAC_REPOS['ffmpeg'], WINDOWS_FFMPEG_SHA)]:
                archive, resolved = git_source(name, url, revision, stage)
                copy_licenses(archive, args.output / 'licenses')
                files.append(archive)
                records.append({'name': name, 'repository': url, 'revision': resolved, 'sha256': sha256(archive)})
        # The exact application tree includes every packaging/build script used for this release.
        app_revision = command('git', 'rev-parse', 'HEAD', cwd=ROOT).decode().strip()
        app_source = stage / f'optimisarr-{app_revision}.tar'
        command('git', 'archive', '--format=tar', '--prefix=optimisarr/', f'--output={app_source}', app_revision, cwd=ROOT)
        files.append(app_source)
        records.append({'name': 'optimisarr', 'revision': app_revision, 'sha256': sha256(app_source)})
        manifest = args.output / 'source-manifest.json'
        manifest.write_text(json.dumps({'platform': args.platform, 'version': version, 'sources': records}, indent=2) + '\n')
        notices = args.output / 'THIRD-PARTY-NOTICES.txt'
        notices.write_text(
        'Optimisarr bundles FFmpeg and codec libraries. Their exact source archives, build scripts,\n'
        'revision manifest and SHA-256 checksums are supplied with this release. Source archives retain\n'
        'their upstream licences. Additional licence copies are provided in the licenses directory.\n'
        'See source-manifest.json for the exact source revisions and download provenance.\n')
        instructions = args.output / 'SOURCE-README.txt'
        instructions.write_text(
            'Extract every source part into one empty directory. Verify each part against its .sha256 file first.\n'
            'The nested tar/tar.xz files are the upstream source trees, including their original licences.\n'
            'source-manifest.json records exact revisions and hashes. Optimisarr source includes packaging scripts.\n'
            'macOS: sidecars/macos/scripts/build-ffmpeg.sh contains all configure/compiler commands. Use the\n'
            'supplied dependency trees at the revisions in BUILD-INFO.txt; do not re-resolve moving branches.\n'
            'Windows: unpack the ffmpeg-builds source tree. Place the dependency tar.xz files in its\n'
            '.cache/downloads directory. Restore each safe alias in the manifest as a symlink to its\n'
            'named hashed archive (or copy that archive to the alias name). This is the COMPLETE cache\n'
            'from the recorded upstream run, including extra variants. Build recipe: win64 gpl 8.1.\n'
            'Use the supplied FFmpeg source in place of build.sh network cloning its moving release branch.\n'
            'The build scripts include configuration, patches and cross-toolchain recipes. Build dependencies\n'
            'and Docker/compiler tooling described by those recipes are still required; no binaries are\n'
            'certified interchangeable solely because a rebuild uses the same source.\n')
        files.extend([manifest, notices, instructions])
        if args.platform == 'macos':
            files.append(args.output / 'BUILD-INFO.txt')
        parts = write_parts(files, args.output, f'OptimisarrSidecar-{version}-{args.platform}-sources')
    for path in parts:
        (args.output / (path.name + '.sha256')).write_text(sha256(path) + '  ' + path.name + '\n')
    print(f'Prepared {len(parts)} source parts in {args.output}')


if __name__ == '__main__':
    main()
