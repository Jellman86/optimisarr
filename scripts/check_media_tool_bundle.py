#!/usr/bin/env python3
"""Verify a sidecar's AV1 software decode and 10-bit HEVC round trip using owned fixtures."""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile


def check(ffmpeg, ffprobe):
    failures = []
    with tempfile.TemporaryDirectory(prefix='optimisarr-bundle-check-') as scratch:
        root = Path(scratch)
        def run(exe, args):
            return subprocess.run([str(exe), *map(str, args)], capture_output=True, text=True, timeout=120)
        for codec, pixel, decoder in [('libsvtav1', 'yuv420p', 'libdav1d'), ('libx265', 'yuv420p10le', 'hevc')]:
            candidate = root / (codec + '.mkv')
            encoded = run(ffmpeg, ['-nostdin', '-v', 'error', '-f', 'lavfi', '-i',
                'testsrc2=size=64x64:rate=12:duration=1', '-pix_fmt', pixel, '-c:v', codec,
                '-threads', '2', '-y', candidate])
            probe = run(ffprobe, ['-v', 'error', '-show_streams', '-of', 'json', candidate])
            streams = json.loads(probe.stdout).get('streams', []) if probe.returncode == 0 else []
            decode = run(ffmpeg, ['-nostdin', '-v', 'error', '-c:v', decoder, '-i', candidate, '-f', 'null', '-'])
            if encoded.returncode or decode.returncode or not streams or streams[0].get('pix_fmt') != pixel:
                failures.append(f'{codec}: expected {pixel} and successful {decoder} software decode; '
                                f'encode={encoded.returncode}, decode={decode.returncode}, '
                                f'actual={streams[0].get("pix_fmt") if streams else "no probe"}')
        for model in ['vmaf_v1.0.16_3d0h', 'vmaf_v1.0.16_1d5h_2160']:
            log = root / (model + '.json')
            escaped_log = str(log).replace('\\', '/').replace(':', '\\\\:')
            options = f"version={model}\\:cambi.enc_width=320\\:cambi.enc_height=240\\:cambi.enc_bitdepth=10"
            result = run(ffmpeg, ['-nostdin', '-v', 'error', '-f', 'lavfi', '-i',
                'testsrc2=s=320x240:r=24:d=0.25,format=yuv420p10le', '-f', 'lavfi', '-i',
                'testsrc2=s=320x240:r=24:d=0.25,format=yuv420p10le', '-lavfi',
                f"[0:v][1:v]libvmaf=model='{options}':n_threads=1:log_fmt=json:log_path={escaped_log}:shortest=1:repeatlast=0", '-f', 'null', '-'])
            scores = []
            if result.returncode == 0 and log.exists():
                evidence = json.loads(log.read_text())
                scores = [frame.get('metrics', {}).get('vmaf') for frame in evidence.get('frames', [])]
            if len(scores) != 6 or any(not isinstance(score, (int, float)) or not 0 <= score <= 100 for score in scores):
                failures.append(f'{model}: six finite 10-bit frame scores required; exit={result.returncode}; {result.stderr}')
    return failures


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ffmpeg', required=True)
    parser.add_argument('--ffprobe', required=True)
    args = parser.parse_args()
    failures = check(args.ffmpeg, args.ffprobe)
    for failure in failures:
        print(failure)
    print(f'{4 - len(failures)}/4 bundled codec/model checks passed')
    raise SystemExit(bool(failures))
