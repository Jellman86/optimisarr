# VMAF sample correspondence, 12 October 2026

Assessment and isolated acceptance for [#406](https://github.com/Jellman86/optimisarr/issues/406),
based on development `7beab4cc` and the changes delivered with this record. Private media,
operational exports and raw logs remain outside Git. No production jobs were retried or
originals replaced; media work used disposable copies. Installed workers were not updated.

## Finding and implementation

The old sampled “frames paired by number” graph first chose pictures by timestamps after two
independent input seeks. It only numbered the pictures **after** that selection. Equal decoded
counts and equal initial timestamps therefore did not establish matching sample origins.
The existing first-five-second alignment probe could also miss motion after a static opening.

Complete encodes with equal decoded counts now select the same absolute decoded indices on
both inputs, for every codec. They decode from the beginning and apply no visual-score offset
search. This generalises the already qualified VC-1 path. Equal counts alone still do not prove
picture identity: a deliberate duplicated-first/dropped-last candidate remains a quality failure.

The timestamp path for unequal/unavailable counts probes the bounded window, up to 40 seconds,
with sparse metric sampling. Five-second blocks must support one consistent correction; opposite
scene preferences, incomplete coverage or different scored frame identities retain zero. A missing
baseline cannot authorise a correction. Final sampling, models, quality floors and all other
verification gates remain unchanged. Workers use the command's frozen cadence rather than a
potentially different nominal probe rate. Software fallback reassesses its own decoder's alignment.

## Generated regression and negative controls

The generated source is 55 seconds, 320×180, 50 fps, FFV1: 20 seconds black followed by 35 seconds
of moving `testsrc2`. The timestamp-only candidate keeps all 2,750 decoded pictures in exactly the
same order, but moves presentation timestamps by 20 ms from picture 250 onwards. Raw decoded
SHA-256, obtained with passthrough output cadence, matches:
`6defb1008581043e845aa4d11c14b39bf02e0ccbae864211473324c9c4cbe82a`.

The window starts at 10 seconds and lasts 40 seconds, so the old probe sees only the static part.
With the old graph/automatic alignment it scored harmonic **67.17**, minimum **0**. Forcing one
frame scored **98.61**, showing a false rejection without any changed picture content.
The new automatic absolute-index comparison scores **98.61** without a correction.

The server service and the shared C# worker execute commands from the current Core builder.
The native Swift worker independently validates and executes the same commands. Live controls:

| Case | Pairing | Chosen shift | Harmonic VMAF | Result |
| --- | --- | --- | --- | --- |
| Same decoded pictures, timestamp step | Absolute indices | 0 | 98.61 | Pass |
| Duplicated first picture, dropped last picture; equal count | Absolute indices | 0 | 67.17 | Refuse damaged correspondence |
| Heavy blur | Absolute indices | 0 | 0.33 | Refuse poor quality |
| Timestamp step, timestamp comparison | Timestamp | +20 ms | 98.61 | Consistent correction |
| Opposing offsets within the window | Timestamp | 0 | 82.76 | Refuse scene-dependent correction |
| Clean identity control | Absolute indices | 0 | 98.61 | Pass |

The Mac live test covers all six cases. The generated source/candidate and negative controls also
run through native Windows using its installed FFmpeg. Identical raw scores are expected only
when model and FFmpeg builds match; acceptance checks keep a margin around the quality floors.

An unavailable QSV device forced a real hardware-to-software fallback on the timestamp-step
case. Software reassessment selected +20 ms and scored 98.61. Cancelling a late sequential server
measurement returned in 0.87 seconds with no new FFmpeg child remaining.

## Retained operator media and physical NVIDIA encode

A retained 1,840.96-second 1080p source and its failed HEVC QSV candidate each decode to 44,138
pictures at 24000/1001 fps. Three 40-second windows use VMAF v1, full metric sampling:

| Start | New harmonic | New wall time, seconds | Old wall time, seconds |
| --- | --- | --- | --- |
| 164 s | 89.53 | 69.95 | 34.57 |
| 900 s | 83.66 | 132.24 | 37.96 |
| 1,636 s | 90.23 | 171.64 | 39.09 |

The combined harmonic score is **87.70**, versus the retained report's **87.71**, and still fails
the configured floor of 90. The low fifth percentile (78.94) and minimum (72.82) also remain.
This is a genuine quality miss preserved by the new comparison. The old development service
was rerun from an isolated source archive: it scores 87.71 again. New and old timings total
373.84 and 111.61 seconds respectively on the same Mac. Other test workloads overlapped the new
run, so this is an indicative cost observation, not a controlled throughput benchmark.

Native Windows, physical RTX 4070, `av1_nvenc`, VBR/CQ 29:

| Disposable input | Source/output decoded counts | Server and C# worker harmonic |
| --- | --- | --- |
| Generated 50 fps control | 2,750 / 2,750 | 97.87 |
| Operator-media excerpt scaled to 640×360 and explicitly converted to 50 fps | 2,249 / 2,249 | 95.39 |

Both server and worker commands agree without a shift. The second fixture is **derived** 50 fps
media, not a claim that the private library or issue reporter's original is native 50 fps.

## Verification and limits

- Backend build: zero warnings/errors; all 3,126 tests pass.
- Browser end-to-end suite: all 325 tests pass.
- Shared C# worker suite: 328 pass plus three Windows-only skips on Mac; **331 pass, no skips**, on native Windows.
- Linux worker build: zero warnings/errors; all 31 tests pass.
- Mac sidecar: all 289 normal tests pass and release build succeeds; six live manifest cases pass.
- Acceptance-script tests: all 105 pass; Unraid/container metadata validation passes.

A full Swift run combined with concurrent live media and backend workloads hit an existing
five-second session-test deadline. The live suite passed, and a subsequent normal full Swift run
passed all tests. No lifecycle timeout or assertion was relaxed.

Sequential decoding adds CPU, disk reads and latency, especially for late windows; only the
selected pictures are scored. There are no extra alignment probes on this deterministic path.
The existing server progress timeout remains conservative: a sufficiently slow long predecode
can stop without approving or replacing anything. Long 4K inputs and the reporter's exact files
remain unqualified. This change makes no claim to infer picture identity from matching counts,
repair actual missing pictures, or validate playback/A/V timing through VMAF.

Quark was unavailable over LAN and Tailscale at final live-test time. Its production container
was not restarted or modified; native Linux hardware acceptance is not claimed. The shared
C# worker logic is tested locally and on Windows. Release/deployment qualification remains
separate from these isolated media and source tests.

The opt-in Swift regression accepts `OPTIMISARR_WINDOW_ALIGNMENT_MANIFEST` and
`OPTIMISARR_FFMPEG`. The JSON object has keys `timestamp-only`, `content-shift`, `degraded`,
`timestamp-search`, `mixed-offset` and `clean`. Each entry supplies local `source`, `candidate`,
Core-built placeholder `arguments`, `frameSeconds`, `expectedShift`, and optional
`minimumHarmonic`/`maximumHarmonic`. Run `swift test --filter LiveWindowAlignmentTests` in the
Mac sidecar directory. Keep private fixture manifests outside Git.
