#!/usr/bin/env python3
"""Exercise the relocated Mac bundle's native encoders, metadata and both pinned metrics."""
import argparse
import json
import os
from pathlib import Path
import selectors
import subprocess
import tempfile
import time


def validate(event):
    proof = event.get('capabilities') or {}
    if (event.get('version') != 1 or event.get('type') != 'capabilities'
            or proof.get('imageMetadataSupported') is not True
            or not {'mjpeg', 'libwebp'} <= set(proof.get('imageEncoders') or [])
            or proof.get('imageMetricRevision') != 'a7a9c787341cf703dede03c2009fa460cae5e5df'
            or proof.get('audioMetricRevision') != 'f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3'):
        raise ValueError('Packaged native tools did not prove JPEG/WebP, metadata and both pinned metrics')


def check(app):
    resources = app.resolve() / 'Contents/Resources'
    with tempfile.TemporaryDirectory(prefix='optimisarr-native-tools-') as scratch:
        request = {'version': 1, 'mode': 'capabilities', 'scratchRoot': scratch,
                   'ffmpegPath': str(resources / 'ffmpeg'), 'imageMetric': str(resources / 'optimisarr-image-quality'),
                   'audioMetric': str(resources / 'optimisarr-audio-quality'),
                   'metadataTool': str(resources / 'metadata/exiftool')}
        with subprocess.Popen([str(resources / 'native-quality/Optimisarr.Sidecar.Native')],
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                              cwd=scratch, env={'PATH': '/usr/bin:/bin'}) as process:
            try:
                # Keep the private input pipe alive until the helper finishes; EOF revokes work.
                process.stdin.write(json.dumps(request).encode() + b'\n')
                process.stdin.flush()
                output = bytearray()
                deadline = time.monotonic() + 90
                with selectors.DefaultSelector() as selector:
                    selector.register(process.stdout, selectors.EVENT_READ)
                    while selector.get_map() or process.poll() is None:
                        if time.monotonic() >= deadline:
                            raise TimeoutError('Packaged native tool qualification timed out')
                        for key, _ in selector.select(0.1):
                            data = os.read(key.fd, 16384)
                            if data:
                                output.extend(data)
                            else:
                                selector.unregister(key.fileobj)
                        if len(output) > 16 * 1024:
                            raise ValueError('Packaged native qualification exceeded its frame budget')
                events = [json.loads(line) for line in output.splitlines()]
                if process.wait(timeout=10) != 0 or len(events) != 1:
                    raise ValueError('Packaged native qualification did not complete')
                validate(events[0])
                if list(Path(scratch).iterdir()):
                    raise ValueError('Packaged native qualification left scratch behind')
                print(json.dumps(events[0]['capabilities'], sort_keys=True))
            finally:
                process.stdin.close()
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=30)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=10)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('bundle', type=Path)
    args = parser.parse_args()
    check(args.bundle)
