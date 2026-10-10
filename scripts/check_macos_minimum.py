#!/usr/bin/env python3
"""Fail when a binary in the Mac sidecar needs a newer macOS than the app declares.

The app says it runs on the macOS in its `LSMinimumSystemVersion`. Every Mach-O it carries —
the app itself and the bundled ffmpeg, ffprobe and native metrics — records its own minimum in its
load commands. A tool built without a deployment target inherits the build machine's macOS, so an
app that installs and pairs on an older Mac can still be unable to run its own media tools there.

    check_macos_minimum.py --bundle OptimisarrSidecar.app
    check_macos_minimum.py --minimum 14.0 vendor/ffmpeg vendor/ffprobe
"""

from __future__ import annotations

import argparse
from pathlib import Path
import plistlib
import re
import subprocess
import sys
from typing import Callable

MACH_O_MAGIC = {b'\xcf\xfa\xed\xfe', b'\xce\xfa\xed\xfe', b'\xca\xfe\xba\xbe', b'\xca\xfe\xba\xbf'}
BUILD_VERSION = re.compile(r'cmd LC_BUILD_VERSION.*?\n\s*minos (\S+)', re.S)
DYLIB = re.compile(r'cmd LC_(?:LOAD|LOAD_WEAK|REEXPORT|LOAD_UPWARD|LAZY_LOAD)_DYLIB\b.*?\n\s*name (.+?) \(offset \d+\)', re.S)
LEGACY_VERSION = re.compile(r'cmd LC_VERSION_MIN_MACOSX.*?\n\s*version (\S+)', re.S)


def declared_minimum(otool_output: str) -> str | None:
    """The minimum macOS a binary's load commands record, or None when it records none."""
    match = BUILD_VERSION.search(otool_output) or LEGACY_VERSION.search(otool_output)
    return match.group(1) if match else None


def newer(version: str, than: str) -> bool:
    def parts(text: str) -> list[int]:
        numbers = [int(part) for part in text.split('.')]
        return numbers + [0] * (3 - len(numbers))
    return parts(version) > parts(than)


def otool(path: Path) -> str:
    return subprocess.run(['otool', '-l', str(path)], check=True, capture_output=True, text=True).stdout


def is_mach_o(path: Path) -> bool:
    with path.open('rb') as stream:
        return stream.read(4) in MACH_O_MAGIC


def check_binaries(binaries: list[Path], minimum: str, read: Callable[[Path], str] = otool,
                   *, system_libraries_only: bool = False) -> list[str]:
    problems = []
    for binary in binaries:
        commands = read(binary)
        found = declared_minimum(commands)
        if found is None:
            problems.append(f'{binary.name} records no minimum macOS')
        elif newer(found, minimum):
            problems.append(f'{binary.name} needs macOS {found}; the app declares {minimum}')
        if system_libraries_only:
            for library in DYLIB.findall(commands):
                if not library.startswith(('/usr/lib/', '/System/Library/')):
                    problems.append(f'{binary.name} has a non-system dependency: {library}')
    return problems


def check_bundle(app: Path, read: Callable[[Path], str] = otool, *, system_libraries_only: bool = False) -> list[str]:
    with (app / 'Contents/Info.plist').open('rb') as stream:
        minimum = plistlib.load(stream)['LSMinimumSystemVersion']
    binaries = sorted(path for folder in ('MacOS', 'Resources')
                      for path in (app / 'Contents' / folder).iterdir()
                      if path.is_file() and is_mach_o(path))
    return check_binaries(binaries, minimum, read, system_libraries_only=system_libraries_only)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--bundle', type=Path)
    parser.add_argument('--minimum')
    parser.add_argument('--system-libraries-only', action='store_true',
                        help='reject dynamic dependencies outside Apple system locations')
    parser.add_argument('binaries', nargs='*', type=Path)
    arguments = parser.parse_args()
    if arguments.bundle:
        problems = check_bundle(arguments.bundle, system_libraries_only=arguments.system_libraries_only)
    elif arguments.minimum and arguments.binaries:
        problems = check_binaries(arguments.binaries, arguments.minimum, system_libraries_only=arguments.system_libraries_only)
    else:
        parser.error('give --bundle, or --minimum with binaries')
    for problem in problems:
        print(f'error: {problem}', file=sys.stderr)
    if problems:
        return 1
    print('every binary runs on the declared minimum macOS')
    return 0


if __name__ == '__main__':
    sys.exit(main())
