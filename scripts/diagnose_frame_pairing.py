#!/usr/bin/env python3
"""Compare seeking and sequential frame pairing on retained SDR files; never modify media."""
import argparse
from fractions import Fraction
import json
from pathlib import Path
import subprocess


def command(ffmpeg, source, candidate, start, seconds, rate, sequential, log_name):
    args = [ffmpeg, '-nostdin', '-v', 'error', '-threads', '2']
    for file in (candidate, source):
        if not sequential:
            args += ['-ss', str(start)]
        args += ['-threads', '2', '-i', str(file)]
    first = round(start * rate) if sequential else 0
    last = first + round(seconds * rate)
    trim = f'trim=start_frame={first}:end_frame={last},settb=AVTB,setpts=N*{round(1_000_000/rate)},format=yuv420p'
    graph = f'[0:v]{trim}[d];[1:v]{trim}[r];[d][r]libvmaf=n_threads=2:model=version=vmaf_v0.6.1:log_fmt=json:log_path={log_name}:shortest=1:repeatlast=0'
    return args + ['-lavfi', graph, '-an', '-t', str(seconds), '-f', 'null', '-']


def probe(ffprobe, path):
    result = subprocess.run([ffprobe, '-v', 'error', '-select_streams', 'V:0',
                             '-count_packets', '-count_frames', '-show_streams', '-of', 'json', str(path)],
                            capture_output=True, text=True, timeout=1800, check=True)
    return json.loads(result.stdout)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ffmpeg', required=True)
    parser.add_argument('--ffprobe', required=True)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--root', type=Path, required=True, help='New evidence directory; must not exist')
    parser.add_argument('--starts', type=int, nargs='+', required=True)
    parser.add_argument('--seconds', type=int, default=10)
    args = parser.parse_args()
    if args.seconds <= 0 or any(t < 0 for t in args.starts):
        parser.error('Window starts must be nonnegative and duration positive.')
    source, candidate = args.source.resolve(strict=True), args.candidate.resolve(strict=True)
    root = args.root.resolve()
    root.mkdir(parents=True, exist_ok=False)
    probes = {name: probe(args.ffprobe, path) for name, path in [('source', source), ('candidate', candidate)]}
    (root / 'probes.json').write_text(json.dumps(probes, indent=2))
    src, dst = (probes[key]['streams'][0] for key in ['source', 'candidate'])
    if any(s.get('color_transfer') in ['smpte2084', 'arib-std-b67'] for s in (src, dst)):
        parser.error('This diagnostic supports SDR only.')
    if (src['width'], src['height']) != (dst['width'], dst['height']):
        parser.error('This diagnostic requires matching frame dimensions.')
    rate = float(Fraction(src['avg_frame_rate']))
    if rate <= 0:
        parser.error('Source frame rate is unavailable.')
    report = {'source': str(source), 'candidate': str(candidate), 'frameRate': rate,
              'note': 'Diagnostic comparisons only; these do not authorize replacement or override verification gates.',
              'windows': []}
    for start in args.starts:
        for sequential in (False, True):
            mode = 'sequential' if sequential else 'seek'
            name = f'{start}-{mode}'
            argv = command(args.ffmpeg, source, candidate, start, args.seconds, rate, sequential, name + '.json')
            (root / (name + '-command.json')).write_text(json.dumps(argv, indent=2))
            result = subprocess.run(argv, cwd=root, capture_output=True, text=True, timeout=1800)
            (root / (name + '-stderr.log')).write_text(result.stderr)
            item = {'start': start, 'mode': mode, 'exitCode': result.returncode}
            if result.returncode == 0:
                scores = json.loads((root / (name + '.json')).read_text())
                item.update(scores=scores['pooled_metrics']['vmaf'], frames=len(scores['frames']))
            report['windows'].append(item)
            (root / 'report.json').write_text(json.dumps(report, indent=2))
            print(json.dumps(item), flush=True)
    return 1 if any(w['exitCode'] != 0 for w in report['windows']) else 0


if __name__ == '__main__':
    raise SystemExit(main())
