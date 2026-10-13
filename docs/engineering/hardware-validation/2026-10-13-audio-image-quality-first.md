# Local audio and image quality-first qualification

This development slice adds bounded selection before standalone audio and still-image
encoding. It is not full adaptive-VMAF workflow parity. The [completion matrix](../../development/adaptive-audio-image-parity.md)
tracks worker search, worker image encoding/verification, adaptive soundtracks and package
qualification separately. No production service or personal media was changed by these tests.

## Behaviour under test

The saved setting is measured first. Up to four encoder settings are tested sequentially;
selection uses actual bytes among complete passing measurements. JPEG quantizers are
deduplicated; WebP uses its native quality control; AAC, Opus and MP3 use codec-specific bitrate
ladders. Every candidate starts from the original. Required source assessment runs before
sample encoding. An unsupported source is held with its reason.

Search evidence cannot authorise replacement. A new final encode receives all configured
verification checks. Only a sole measured perceptual failure permits one higher-fidelity retry;
missing evidence, alpha damage or any other failed gate cannot trigger it. Completed probes
are checkpointed; cancelled or stale attempts cannot publish a new selection.
An injected scratch-directory redirect reproduces an ownership refusal. The native regression
proves the original and unrelated evidence remain intact, no encode starts, and failure/exclusion
and retry budgets remain unchanged. It caught and corrected the local dispatcher's error handling.

Enabling quality-first explicitly allows lossy WebP from lossless originals. Fixed WebP
settings retain their previous lossless-source behaviour. The opt-in and consequence are
visible in the library controls; existing libraries start with both search options disabled.

## Native media evidence

The generated matrix uses isolated native server processes, private configuration and owned
media directories on Apple Silicon macOS and Quark's Linux host. It exercises JPEG selection
and rejection, unsupported original refusal, all three audio codecs, impossible audio targets,
final-score disagreement, original hashes, actual final arguments and exact rollback. Quark
additionally exercises WebP selection/rejection and embedded MP3 artwork. Its WebP encoder and
private ExifTool installation are explicit test dependencies. The installed Mac FFmpeg lacks
libwebp, so this record does not claim Mac WebP encoding acceptance.

The portable disagreement fixture contains silence through the integer-second search cuts,
then a seeded pink-noise fractional tail. A deliberately strict zero-distance target rejects
the final file and its one retry while preserving the original and first verdict. A preliminary
pure-tone fixture recovered on Quark but already passed on Mac; it is retained as evidence,
not used to require the same retry decision across distinct encoders and native binaries.

Quark also tests licensed stereo music and mono speech at 44.1 kHz through AAC, Opus and MP3.
Each workflow independently measures the final quality and loudness, checks for introduced
clipping, and restores the exact source bytes. Sources are checksum-locked copies from the
earlier audio qualification; converting their decoded PCM to float WAV for admission does
not make them lossless recording masters. Raw provenance and reports remain private.

Native testing exposed AAC sample loss at zero and fractional demuxer seeks. The server and
Mac worker now decode a bounded lead-in before trimming the exact window. Tests retain the
64-frame rounding bound; no short input is padded. Loudness selection also exposed an old
parser reading the first progress line rather than the final summary. The corrected parser
uses final integrated loudness and true peak, with measured silence distinct from missing data.

## Limits and issue-derived checks

Success fixtures use permissive explicit limits; rejection fixtures use deliberately impossible
limits. These are orchestration tests, not calibrated quality recommendations or a listening study.
The supported 8-bit SDR image and mono/stereo audio domains remain unchanged.

The review of [#240](https://github.com/Jellman86/optimisarr/issues/240) and
[#294](https://github.com/Jellman86/optimisarr/issues/294) requires retained prior attempts,
clear incomplete-evidence failures and no stale worker/encoder result reuse. The tail-loss
history in [#321](https://github.com/Jellman86/optimisarr/issues/321) reinforces exact sample
coverage rather than padding or widening tolerances. Those obligations also apply to the
remaining worker implementation. [#332](https://github.com/Jellman86/optimisarr/issues/332)
remains open; this slice does not complete its broader capability.

Picard was unreachable during this run. Shared worker tests on macOS are not physical Windows
acceptance. CI, exact-container acceptance and installed-package acceptance remain separate
requirements before deployment or a full parity claim.

Local validation passed 3,319 backend tests on both available hosts, 292 Mac worker tests,
328 shared worker tests on macOS (three platform skips), 111 acceptance-script tests and 95
frontend unit tests. The Svelte check and builds report zero warnings. The final browser suite
passes 333 tests. The updated Mac worker also measures the licensed 44.1 kHz music/AAC pair
and the generated fractional-tail fixture with its real pinned tools, cleaning owned scratch.
Exact CI/package results remain required before this slice is merged or deployed.
Quark also passes all 31 native Linux worker tests with a warning-free Release build.
