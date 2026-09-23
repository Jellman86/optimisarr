# Real-media acceptance harness

The harness drives a **new disposable Optimisarr instance** through the public API. It scans real
files, probes, enqueues, waits for verification, independently decodes and measures the candidate,
replaces only its own fixture copy, and proves rollback restores the original SHA-256. It never
connects to an arbitrary existing application URL or modifies an installed sidecar's pairing.

The normal unit, endpoint, browser and sidecar suites remain required. This suite adds evidence
from actual FFmpeg processes, files, HTTP transfers and worker implementations.
The [initial execution record](../engineering/hardware-validation/2026-09-17-media-acceptance.md)
lists measured results, defects found, and outstanding physical-host validation.

## Container CI gate

After building an image:

```bash
python3 scripts/media_acceptance.py --image optimisarr:test \
  --root /tmp/optimisarr-acceptance-001 --tier smoke
```

The root must not exist. The runner creates private config, data, work and quarantine directories,
starts the exact image with a random admin credential, and stops/removes only that container on
exit. Test data and evidence remain under the run root. No production media mounts are used.
Run against a local Docker daemon: bind mounts must refer to this machine's filesystem.

CI runs this after the existing final-image smoke and before image publication. A failed or blocked
acceptance case fails the job. CI retains reports, raw VMAF logs, probes and server logs for 14 days.
The small CI corpus is generated locally and requires no third-party media download.

## Native development

```bash
dotnet build Optimisarr.slnx --configuration Release -warnaserror
python3 scripts/media_acceptance.py \
  --native src/Optimisarr.Api/bin/Release/net10.0/Optimisarr.Api.dll \
  --ffmpeg /absolute/path/to/ffmpeg --ffprobe /absolute/path/to/ffprobe \
  --vmaf /absolute/path/to/ffmpeg-with-libvmaf \
  --root /tmp/optimisarr-acceptance-002
```

Native orchestration is for macOS/Linux. FFmpeg must support the tested codecs, their decoders,
`libvmaf`, `ssim`, and `ebur128`. An encoder appearing in its listing does not prove the resulting
file can be decoded or that a required bit depth is supported; these are acceptance failures.
For image metadata coverage, use the application's normal ExifTool installation.

## Openly licensed corpus

```bash
python3 scripts/acceptance_corpus.py --root /tmp/optimisarr-corpus \
  --ffmpeg /absolute/path/to/ffmpeg --ffprobe /absolute/path/to/ffprobe
```

This downloads pinned Big Buck Bunny and Tears of Steel releases (about 437 MB total), checks
their exact lengths and SHA-256 digests, and creates three 12-second excerpts from each.
`--source bunny` or `--source tears` selects one film. `--seconds` allows longer excerpts up to
600 seconds. Downloads use a cache; changed upstream bytes are rejected rather than silently
becoming a new baseline. ZIP extraction selects one named, size-bounded member.

Each `corpus.json` records source licence and attribution, source/archive and excerpt hashes,
tool versions, extraction start and duration, and decoded stream information. An `ATTRIBUTION.txt`
accompanies the corpus. Prepared clips are copied into the isolated run root after checking their
hashes; sources outside the manifest directory and escaping symlinks are rejected.

Both films are CC BY 3.0: [Big Buck Bunny](https://peach.blender.org/about/) and
[Tears of Steel](https://media.xiph.org/tearsofsteel/README.txt). The pinned distribution copies are
already lossy: Bunny is 320×180 and Tears is 720p. The current excerpts are normalized to 640-pixel
width and lossless FFV1/FLAC **after decoding those copies**. These are reproducible workflow and
quality-regression fixtures, not lossless cinema masters or UHD/HDR certification.

## Fleet matrix and isolated workers

```bash
python3 scripts/media_acceptance.py --image optimisarr:test \
  --root /tmp/optimisarr-fleet-001 --tier fleet \
  --device /dev/dri:/dev/dri \
  --corpus /tmp/optimisarr-corpus/corpus.json \
  --listen 0.0.0.0 --port 18787 --pairing-wait 180 \
  --expected-worker acceptance-picard-hevc_nvenc \
  --expected-worker acceptance-mac-hevc_videotoolbox
```

Use `--gpus all` instead of the device mapping for NVIDIA Docker installations. Only expose the
temporary port to the intended worker hosts. The server prints short-lived pairing PINs during
the pairing interval. Pair the disposable launchers below, **not the installed production workers**.
An explicitly expected but absent worker produces a blocked result and a nonzero exit code.
A fleet run with no paired workers is also blocked. List every required physical machine with
`--expected-worker`; discovery alone cannot distinguish an intentionally absent host from a forgotten one.

The default fleet matrix runs every available local encoder and every recognized advertised
worker encoder against SDR, VFR, nonzero-timestamp and 10-bit fixtures plus imported clips. It also
checks adaptive quality selection locally and on each worker. H.264's documented rejection of
10-bit inputs is tested as an eligibility refusal. Selected local hardware encoders also receive
a compressed H.264 source with hardware decode enabled and an impossible VMAF policy. That case
requires completed software-decode retry evidence and a recorded VMAF rejection, unchanged original,
and no replacement history; a generic FFmpeg failure cannot satisfy it. Other failures remain failures.

Use `--local-encoder libx265` and repeated `--fixture-variant sdr` / `--fixture-variant vfr` to narrow
a diagnostic rerun. The report records the selected matrix; narrowing does not certify omitted
combinations. `--timeout` bounds each job; a timeout requests cancellation and is blocked, never
passed. Each rerun uses a new root so stale jobs and old evidence cannot satisfy new assertions.

For endurance runs, add `--fixture-seconds 600 --soak-cycles 20 --timeout 7200`. This repeats
encode, independent verification, replacement and rollback on the same running instance and each
selected worker, retaining queue/worker/free-space snapshots after each cycle. Fixture copies and
reports intentionally accumulate; provision disk accordingly. This exercises repeated lifecycle
work, but does not yet assert a quantitative memory-leak or GPU-memory threshold.

### macOS launcher

```bash
cd sidecars/macos
swift build --configuration release --product AcceptanceWorker
```

Run the resulting `AcceptanceWorker` executable with these environment variables:

```text
OPTIMISARR_FFMPEG=/path/to/ffmpeg
OPTIMISARR_FFPROBE=/path/to/ffprobe
OPTIMISARR_ACCEPTANCE_SERVER=http://test-server:18787
OPTIMISARR_ACCEPTANCE_PIN=<current test PIN>
OPTIMISARR_ACCEPTANCE_NAME=acceptance-mac-hevc_videotoolbox
OPTIMISARR_ACCEPTANCE_ENCODER=hevc_videotoolbox
OPTIMISARR_ACCEPTANCE_SCRATCH=/new/empty/worker-scratch
```

It uses the actual `SidecarCore` capability prober, protocol client, transfer logic, VMAF measurement,
adaptive search and job runner. Its credential exists only in memory; it never opens the app's
Keychain or preferences. The selected encoder must pass the production capability probe before
pairing. Advertising that single proved encoder makes the ordinary scheduler deterministic.
Start one process per encoder to cover every encoder on a physical host. Use unique names.

For a local Mac, the runner can discover, pair and stop the launchers automatically:

```bash
python3 scripts/media_acceptance.py \
  --native src/Optimisarr.Api/bin/Release/net10.0/Optimisarr.Api.dll \
  --ffmpeg /path/to/ffmpeg --ffprobe /path/to/ffprobe \
  --tier fleet --root /tmp/optimisarr-mac-fleet-001 \
  --worker-command '["/absolute/path/to/AcceptanceWorker"]'
```

`--worker-encoder hevc_videotoolbox` restricts automatic discovery when investigating one path.
The installed Mac sidecar stays paired to its existing server.

### Windows launcher

```powershell
dotnet build sidecars/windows/src/Optimisarr.Sidecar.Acceptance --configuration Release
```

Set the same environment variables (Windows paths, e.g. `C:\acceptance\scratch-nvenc`, and a unique
name such as `acceptance-picard-hevc_nvenc`), then run the built
`Optimisarr.Sidecar.Acceptance.exe`. Keep `ffprobe.exe` beside `ffmpeg.exe`, as required by the
production Windows runner. The launcher uses the real Windows sidecar core without opening the
service's settings/credential store. It refuses to run on another OS; building its code on a Mac
does not constitute Windows hardware evidence. Stop remote launchers after the run.

### Strict sidecar-only verification

Remote verification defaults on for fresh installations. **Verify entirely on the sidecar** in
**Settings → Files & safety → Remote workers** makes the control plane issue full-verification contracts only to
protocol-2 sidecars. The sidecar performs both probes, a complete candidate decode, packet-timestamp
checks and, when the library policy asks for them, loudness/true-peak measurements. Existing worker
quality reports still carry VMAF evidence. Every report is bound to the lease contract, the source
hash and the delivered candidate hash before the server evaluates it.

This mode is deliberately strict: missing evidence, an unavailable media tool,
or evidence for different bytes fails the job. Older sidecars cannot claim these assignments;
upgraded sidecars renegotiate their protocol on heartbeat without needing to pair again.
The server parses returned JSON and applies the verification policy, but does not invoke media
tools during verification of that lease. Choose **Only on workers** placement as well to prevent
local encoding fallback when no compatible worker is available.

The fleet acceptance run exercises this mode by default. Use `--server-verification` with
`--tier fleet` to deliberately test the old server-verification mode; `--sidecar-verification`
remains an accepted explicit spelling for the default. The report
records `strictWorkerVerification`, and each remote result must record
`verificationLocation: Worker`; server fallback cannot satisfy the assertion.
Run once with the default and once with `--server-verification` when validating both configurations.
The independent reference measurement still runs in the harness to check the
worker's result; this is test evidence, not production server verification.

This does not make the server idle. Scanning, initial probing, assignment preparation (including
filter planning), transfers and hashes, database updates, policy evaluation and replacement still
run there. Preview, calibration, audio-only and image jobs retain their existing local paths.
The setting defaults off for compatibility with protocol-1 workers. Preserve an already selected
strict policy during upgrades; unsupported workers cannot claim its assignments.

## What is asserted

| Area | Executable acceptance coverage |
|---|---|
| Lifecycle | Scan, probe, eligibility, real queue dispatch, normal/failing verification, duplicate replace, quarantine, byte-for-byte rollback |
| Quality | Full sequential decode, raw frame VMAF, independent offset harmonic mean/p5/minimum, 93/80/50 gates, impossible 100/100/100 rejection, model/frame-count/aggregate agreement |
| Structure | Actual codec, dimensions, bit depth/pixel format, available colour tags, retained streams/languages/channels, exact decoded audio on copy paths |
| Timing | Decoded frame counts and timestamp cadence checked before VMAF normalization; VFR and nonzero origins |
| Audio/images | FLAC→AAC loudness/peak/metadata checks and rollback; opaque BMP→JPEG SSIM/structure and rollback |
| Disposable work | Short-preview quality, original preservation and cleanup; fleet calibration preparation/playable sample metadata |
| Scheduling | Queued and running cancellation, released encode slots, low-disk-space refusal, overlapping jobs with different inputs and the same basename |
| Recovery | Abrupt restart of the owned smoke server preserves manual pause and queued work; recovered output is measured and rolled back |
| Worker identity | Required machine present, actual job worker matches, actual encoder and output codec match |
| Transfer faults | Wrong credential/source, truncated upload, malformed quality evidence, range resume, duplicate chunks, late result after cancellation, corrupt media with valid transport hashes |
| Verifier self-check | Deliberate black frames, missing pictures and truncated video must be rejected for the expected reason |

The reference implementation does not import production command builders or parsers. It pins the
existing HD/UHD VMAF model family, records the command and recomputes statistics from raw frame
JSON. The two files must first retain the same decoded pictures and timing. For VMAF, both are then
normalized to the reference cadence, matching the declared scoring contract. A 0.25-point aggregate
tolerance permits numerical variation; it never lowers the quality gate thresholds.

This is not a claim of exhaustive media certification. Genuine HDR/Dolby Vision mastering and
tone-map fidelity, ICC/EXIF-bearing image corpus, hardware decode/driver telemetry, sampled-window
versus full-film audits, real worker network partition/process-kill recovery, filesystem exhaustion during
replacement and quantitative resource-leak thresholds remain separate required release evidence. Existing unit and
endpoint tests cover many of those policies, but a green smoke run cannot substitute for hardware
acceptance. The harness deliberately refuses HDR reference scoring until its transform is specified.

## Reports and troubleshooting

Every run writes `report/index.html`, `report/report.json` and `report/junit.xml`, plus per-case
settings, application reports, probe JSON, exact reference measurement commands, raw VMAF frames,
and output hashes. Exit codes: **0** all selected cases passed; **1** failure; **2** missing facility
or timeout; **130** interrupted. Blocked cases are JUnit errors, not skipped successes. Reports HTML-
escape external text. Credentials are passed through the environment and omitted from artifacts.

All fixture copies and candidates remain under the run root for diagnosis. Quarantined copies are
not approved/purged during normal tests. Delete a completed run directory only when its evidence
is no longer needed. Never point cleanup at a production library.

Run harness self-tests with:

```bash
python3 -m unittest discover -s scripts/tests -p 'test_*.py'
```
