#!/usr/bin/env python3
"""Qualify a published helper in a relocated signed app without pairing or personal media."""
from pathlib import Path
import os
import plistlib
import shutil
import subprocess
import tempfile

from check_macos_minimum import check_bundle
from macos_native_package import publish, sign_bundle, smoke_runtime


def check():
    with tempfile.TemporaryDirectory(prefix='optimisarr-native-package-') as scratch:
        root = Path(scratch)
        app = root / 'original/NativePackageTest.app'
        executable = app / 'Contents/MacOS/NativePackageTest'
        executable.parent.mkdir(parents=True)
        with (app / 'Contents/Info.plist').open('wb') as stream:
            plistlib.dump({'CFBundleExecutable': executable.name, 'CFBundleIdentifier':
                          'uk.optimisarr.native-package-test', 'CFBundlePackageType': 'APPL',
                          'LSMinimumSystemVersion': '14.0'}, stream)
        source = root / 'main.c'
        source.write_text('int main(void) { return 0; }\n')
        subprocess.run(['clang', '-mmacosx-version-min=14.0', str(source), '-o', str(executable)], check=True)
        publish(app / 'Contents/Resources/native-quality')
        sign_bundle(app, os.environ.get('SIGNING_IDENTITY', '-'))
        relocated = root / 'relocated/NativePackageTest.app'
        relocated.parent.mkdir()
        shutil.move(app, relocated)
        subprocess.run(['codesign', '--verify', '--deep', '--strict', str(relocated)], check=True)
        problems = check_bundle(relocated)
        if problems:
            raise ValueError('; '.join(problems))
        smoke_runtime(relocated / 'Contents/Resources/native-quality')
        print('Relocated signed native package: exact runtime notices, nested minimum and managed startup passed.')


if __name__ == '__main__':
    check()
