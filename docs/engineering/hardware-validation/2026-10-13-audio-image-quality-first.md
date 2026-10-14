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

Native testing exposed AAC sample loss at zero and fractional demuxer seeks. Standalone audio
on the server and Mac worker now decodes a bounded lead-in before trimming the exact window.
Container CI also caught the different demuxer-relative origin of video soundtracks; those
retain their existing picture-origin seek rather than adopting the standalone rule. Tests retain the
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


## 14 October worker foundation and colour-profile follow-up

The local workflow remains distinct from unfinished worker parity. Protocol 11 is reserved;
advertised protocol 10 is unchanged and production workers still refuse the new search.
The branch adds bounded, additive worker-capability and lease-search storage, a frozen
four-probe state machine with replay/source/tool fencing, and strict version-4 image evidence.
The reporting endpoint, native search loops, Swift image verification, soundtrack selection,
preview/calibration integration and exact-package qualification remain outstanding.

The complete backend suite passes **3,374 tests** on both the Mac and native Quark snapshot
`source-05`. Its **901 C# file hashes** match the Mac tree. Warning-as-error rebuild passes
without warnings. The Mac also passes **329 shared .NET worker tests** (three Windows-only
skips), **31 Linux worker tests**, **95 frontend unit tests**, the locale/type checks,
**333 browser tests** and **115 Python tests**. Swift's previously recorded 293 tests apply
to the unchanged Swift implementation; they do not establish new image-worker functionality.

Direct invocation of the shared .NET native image verifier passes on the Mac and Quark.
A deliberately poor JPEG returns a complete negative score and low SSIM as measurements;
the server's separate tests prove rejection without any installed server media tools. A
Mac generated gradient JPEG returns SSIMULACRA2 **89.428720304** and SSIM **0.922160**.
Those numbers describe that fixture, not a general target recommendation.

A real ICC/EXIF fixture exposed the pre-existing copy bug: `ICC_Profile:all` selects
individual read-only fields and can exit successfully without writing the binary profile.
The corrected `ICC_Profile` command preserves the exact complete profile bytes, plus the
EXIF Artist, while keeping orientation/previews/dimension exclusions. See the
[official copy example](https://exiftool.org/exiftool_pod2.html).
The Mac and Quark metadata fixtures then pass local quality-first selection, every enabled
final check, replacement and exact rollback. Quark passes all **nine quality-first cases**
and **four WebP/artwork cases**, with preflight/settings restoration separate. The harness
independently compares ICC bytes and reads the generated EXIF Artist. These cases are now
included in final-container CI. Private red/green evidence is retained under `worker-image-01`,
`mac-metadata-02`, Quark `probe-image-03`, `native-07` and `native-webp-04`.

Quark's isolated ExifTool copy lacked Compress::Zlib, which could hide a compressed profile
from field-only inspection; its installed app already has the module. Grouped JSON now reads
the binary profile tag, prevents tag-value spoofing and rejects warning/incomplete results.
A missing-module run stops before sample/full encoding, retains the original, spends **zero
media failure counts** and starts no quality retry. The source warning is retained for diagnosis.
The same immutable source passes after signed Fedora modules are extracted into the isolated
test directory and added only to that process's Perl search path. No host packages were installed.
The complete-tool verifier preserves all **3,144 ICC bytes** (SHA-256
`2b3aa1645779a9e634744faf9b01e9102b0c9b88fd6deced7934df86b949af7e`) and EXIF.
The container build now declares the compression dependency and asserts it can load.

ExifTool processes now have bounded capture, deadlines, UTF-8 standard input where needed,
and reap children before returning cancellation. Separate native image scorer instances
share one process-wide lane. These lifetime changes have process and concurrency tests.

Picard remains unreachable through both its Tailscale SSH address and the Quark LAN jump;
no new physical Windows qualification is claimed. No installed service or personal media
was modified by these runs.
