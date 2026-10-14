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

## 14 October: server search exchange

The server exchange adds tests for claim-time image intent, measurement workspace refusal,
frozen settings, concurrent and replayed probes, stale leases/attempts, source metadata changes,
protocol downgrade, source rejection without failure counts and premature delivery. Frozen
commands round-trip through persisted JSON for JPEG and WebP; additional inputs and mismatched
or traversing output suffixes are refused. A wrong image codec fails the same final evaluator
used for local and worker evidence. Image evidence remains usable when unrelated audio gates
are enabled. Retry-floor tests cover custom audio rates and the highest JPEG quantizer.

The Mac rebuild has **zero warnings** and all **3,414 backend tests pass**. Frontend checks,
all **95 unit tests** and all **333 browser tests pass**. Protocol 11 remains disabled while
native execution and resource admission are implemented. These results qualify the server
exchange; they do not claim end-to-end native worker parity.

Quark independently passes the same **3,414 tests** and all **nine quality-first workflows**
on an immutable snapshot with **910 matching C# hashes**. All checks for the pushed server
stage are tracked separately from subsequent native-executor changes.

## Native probe executor qualification

The executor adds **15 backend tests**, bringing the warning-free Mac and Quark backend
runs to **3,429 passing tests**. Tests cover canonical-command refusal, image source and
metadata qualification before FFmpeg, raw audio windows and auxiliary gates, partial failure,
changed retry sources/native tools, unavailable workspace, oversize output and cancellation
cleanup. The shared .NET transport has **345 passing tests on the Mac**, with three explicit
Windows-only skips. Its request/response budgets count UTF-8 bytes, malformed/ambiguous
directions are terminal, and interrupted response bodies permit replaying the same evidence.

Isolated native execution passes **JPEG, AAC, Opus and MP3 on the Mac**, and **JPEG, WebP,
AAC, Opus and MP3 on Quark**. Each case performs four probes, runs a fresh final encode,
collects full native verification and evaluates it with the server's media tools deliberately
unavailable. Source and final candidate hashes remain unchanged during verification; probe
scratch is empty afterward. These runs do not replace their fixtures. The image cases include
ICC/EXIF metadata. Permissive thresholds in this harness are test inputs, not recommendations.
An additional licensed mono speech source at **44.1 kHz** passes on Quark: two disjoint
30-second reference windows are measured, and fresh final verification covers its fractional
tail. Independent container parsing confirms all **3,144 ICC bytes** survive JPEG on the Mac
and both JPEG/WebP on Quark, with the same profile SHA-256 recorded above.
Quark's immutable executor snapshot has **912 matching C# hashes**; later .NET transport
additions are unit-tested separately and await their own package CI. Private raw evidence is
retained under `native-probe-01` and `quark-native-probe-01-evidence`.

This qualifies probe execution and independent verification in isolation. Actual worker-loop
renewal/reconnect, capability proof, memory admission, Swift support, package dependencies,
soundtrack selection and preview/calibration parity remain outstanding. Protocol 11 is still off.

The subsequent native search-session stage passes **3,430 backend tests** and **359 shared
.NET tests** on the Mac (three Windows-only skips), with zero-warning rebuilds. Its fourteen
session cases cover a committed report whose response was lost, bounded persistent outages,
cancellation during replay, frozen contract/sample questions, unsafe sample commands,
premature selection, changed final commands and source-refusal overrides. The worker checks
final arguments using the same one-control patch as the server. These are session tests;
the session is not yet wired into automatic job execution or capability advertisement.

CI exposed a scheduling race in the existing noisy-decoder fixture: it intentionally emits
no progress while filling both pipes, so its two-second idle deadline could win under load.
That drain case now has a separate 20-second cancellation bound and a 30-second test-only
idle deadline. Dedicated stall tests retain two seconds. All seven process tests and the
full **3,430-test Release suite** pass locally with a zero-warning rebuild; CI must requalify
the revised fixture. Production watchdog deadlines are unchanged.

## Shared .NET runner and resource admission

The prior session/watchdog commit `04caa23aa91083194731c8f409d1bf40b3e2d3f1` passed all applicable
CI checks; publication was skipped as intended for a pull request. Subsequent runner changes
have **413 passing shared .NET tests on the Mac**, with three Windows-only skips and a
zero-warning rebuild. Coverage includes lease-loss cancellation, source-refusal preview safety,
image preparation before final hashing, changed source/tool identities, incomplete contracts,
peak reservations, linked working folders and eight exact-length/resumable source-transfer cases.

The actual `JobRunner`, native probe executor, report client, full encoder, metadata writer,
independent final verification and upload pass **JPEG/AAC/Opus/MP3 on the Mac** and
**JPEG/WebP/AAC/Opus/MP3 on Quark**. A second published Linux harness verifies all **38 artifact
hashes**, records **921 C# source hashes**, and passes all five encoders with exact transfer
bounds. This published snapshot precedes the final linked-volume/incomplete-claim refinements;
those refinements have separate regression tests. Source hashes remain unchanged, delivered
bytes match final evidence and scratch is empty. The Mac trial explicitly provisions a 4 GiB
test lane; it does not qualify the unimplemented Swift admission path.

The control plane in these runs is an in-process HTTP simulation using the real search
coordinator; the final verdict uses the actual server evaluator with server media tools absent.
These results do not yet prove the real ASP.NET persistent lease, staged upload, replacement
and rollback lifecycle. Test policy thresholds remain fixture inputs, not calibrated defaults.
Independent Mac extraction confirms the delivered JPEG retains all **3,144 ICC bytes** with
the recorded profile hash and the frozen optimisation marker.

A separate Quark run under a real transient **256 MiB cgroup memory limit** refuses before
source fetch, probes or full encoding, despite ample host memory. The original remains unchanged
and scratch is empty. The isolated scope exits with the helper; installed services and personal
media are unchanged. The earlier immutable Quark source snapshot passes **402 shared tests**
with three Windows-only skips; later byte-bound and linked-volume changes are distinct evidence.

Resource-aware placement/backoff, cgroup-v1 support, native capability proof, Swift support,
soundtrack selection, preview/calibration parity and exact-package acceptance remain open.
Protocol 11 remains disabled. Picard was unreachable during this stage, so Windows CI is
separate from unresolved physical Windows acceptance.

## Real server lifecycle and committed-response loss

On runner commit `7c15ffc0a5cfded15b38d55812ee786f829a3507`, isolated ASP.NET processes with
fresh migrated SQLite databases pass **JPEG/AAC/Opus/MP3 on the Mac** and
**JPEG/WebP/AAC/Opus/MP3 on Quark**. The Linux publication verifies **41 artifact hashes**
and records **921 C# source hashes**. These runs use the real authenticated claim, source,
probe, verification, staged upload and completion routes, followed by the actual queue's
final verdict and supported replacement/rollback controls. No simulated control plane is used.
Original hashes match after exact rollback, repeated replace requests create one recorded
replacement, and worker scratch is empty. Independent checks confirm four native measurements,
smallest measured passing selection and the actual JPEG/WebP encoder control in the full command.

AAC, Opus and MP3 on the Mac and all five Quark cases deliberately discard the first successful
probe response after the real server commits it. The worker replays the report and completes
without running a fifth probe. Image server encoding, probing and quality tools are absent.
Audio retains server ffprobe for claim-time source metadata planning; server encoding and final
quality tools remain absent. This is strict final-verification evidence, not a claim that the
server performs no metadata reads. Memory admission on the Mac uses the explicit test lane.

The fixture seeds protocol-11 worker identities through EF after startup while negotiated
protocol remains 10. It does not prove automatic capability discovery or final packages.
Early harness runs correctly stopped for minimum file size and missing lossy-conversion opt-in;
another reached verified delivery before a harness report-URL error. Fresh complete runs use
eligible fixtures with explicit conversion intent and finish replacement/rollback.

Evidence review found nested image assessments still inherited a server label from the generic
metric service. The executor now stamps worker provenance and the receiver normalises both
source and sample reports independently of raw labels. Failing-then-passing regression tests
cover this correction; safety thresholds and metric values are unchanged. Raw private results
are retained under `native-master-01` and `native-master-quark-01-evidence`.
The corrected branch rebuilds with zero warnings and passes **3,431 backend tests** and
**413 shared .NET tests** on the Mac, with three Windows-only skips.

## Fresh higher-fidelity retry qualification

The local retry shortcut previously used the encoder name and retained selection without new
measurement. Local and remote planning now use the same source/target-bound retry minimum;
each execution searches again on its current encoder. Thirteen focused execution-plan cases
cover changed measurement results, minimum-bounded fallback, missing/stale/exhausted intent,
changed sources/codecs/targets and unrelated settings. The current Mac branch rebuilds without
warnings and passes **3,444 backend tests**, **413 shared worker tests** (three Windows-only
skips), **118 Python tests**, frontend checks and **342 browser tests**. OpenAPI is unchanged
and verified. The nine new browser cases include phone/desktop and both themes; saved captures
were visually inspected. The report distinguishes pending previous selection from the fresh
selection and minimum in nine locales.

The real 95.5-second audio fixture passes the dedicated retry regression on the Mac and Quark.
Its first sampled selection is **112 kbps**; the fractional tail fails the enabled zero-distance
final gate. The sole retry freshly measures **128 kbps**, retains **128 kbps** as its minimum
and fails the same unchanged final gate. The source hash is unchanged, the first rejection is
archived and no replacement or third attempt is created. Sampling the whole final file remains
essential; a passing silence window is not a pass for its noise tail.

Separate isolated local image runs seed earlier-host retry intent through EF, rather than claim
an observed natural image disagreement. JPEG freshly measures at a minimum of **91** on both
hosts; WebP on Quark measures **four new settings at or above 90** and selects **100** after
all sample gates. Each finishes independent final verification, repeated replacement and exact
rollback. Missing-intent cases on both hosts stop before native assessment/encoding with zero
media failures/exclusions. The Linux runtime verifies **56 published file hashes** and records
**923 C# source hashes** of the working tree. These are isolated native runtime tests, not
installed-service or final-package acceptance. Private evidence is under `local-retry-mac-01`,
`native-master-01/mac-local-*` and the corresponding Quark task-owned roots.

The full browser run also reproduced an existing fixture error: it attempted to lose an
already retired graphics context. The revised fixture deliberately includes one retired context,
requires deliberate loss of a live renderer and retains fallback, theme/activity and unchanged
creation-count assertions. Four repeated focused runs and the full 342-test suite pass;
production rendering behavior is unchanged.

## Native capability and resource-aware assignment

The current Mac rebuild has zero warnings and passes **3,463 backend tests** and **427 shared
.NET worker tests** (three Windows-only skips); all **31 Linux worker tests** and **118 Python
tests** pass. Focused cases cover unknown/low memory, RAM scratch, overflow, reservation lifetime,
heartbeat refresh, monotonic refusal cooldown, old/mismatched metric evidence, incomplete coverage,
missing modules, cancellation cleanup and system metadata-tool lookup. The additive capability
wire fields are reflected in the generated OpenAPI document; no database schema changes are needed.

The actual collector passes **JPEG, pinned SSIMULACRA2/Zimtohrli and metadata on the Mac** and
**JPEG/WebP, both metrics and metadata on Quark**. Metadata proof compares all **588 ICC bytes**
from the embedded synthetic fixture, inspects EXIF and reads back the portable marker. The Mac's
FFmpeg lacks WebP and the .NET memory provider returns unknown; those facts are not advertised
as ready. All owned probe scratch is removed. The Linux publication verifies **10 artifact
hashes** and records **929 C# working-tree hashes** at that snapshot; later tool-location changes
have their own regression coverage.

A real transient **256 MiB** Quark scope still proves its tools on these tiny fixtures and reports
approximately **234 MiB** available, rather than host memory. A separate missing-Zlib environment
withholds metadata/image-encoder proof while retaining usable metric evidence. Both collected
reports pass through the real server's pairing and heartbeat routes, then prevent a native claim
before source download, with the job queued, no lease and zero media failures. Complete proof
allows JPEG and WebP through native search, committed-response replay, sealed upload, strict
verification, repeated replacement and exact-byte rollback. The server fixtures elevate stored
protocol to 11 after normal protocol-10 negotiation; shipped negotiation is unchanged.

The corresponding server publication verifies **41 artifact hashes** and **929 C# source hashes**.
Raw task-owned evidence is under `native-capabilities-01`, `capability-runtime-linux-01` and
`capability-master-linux-01`. Installed services and personal media remain unchanged. Picard's
SSH endpoint timed out again; physical Windows proof remains unresolved. Cgroup-v1/hybrid
Linux, local-server memory admission, Swift support, package dependencies and broader fault
acceptance remain required before declaring parity.

A separate actual Linux executable discovery run leaves the metadata override unset and finds
ExifTool on the system search path. It proves JPEG/WebP, both pinned metrics and metadata,
reports approximately **13.8 GiB** available memory and disk-backed scratch, and preserves
protocol-10 negotiation. Its publication verifies **11 artifact hashes** and records **931 C#
source hashes**. Private evidence is under `native-worker-discovery-linux-01`; this qualifies
the discovery composition, not a new installed package.

## Controller-aware Linux memory provider

Twenty-five deterministic cases qualify actual mount resolution, legacy and hybrid membership,
visible parent limits, mounted subtrees and namespace roots, escaped paths, malformed/ambiguous
readings, missing controllers and v2 pressure limits. Regression tests first reproduced the
unimplemented cases. Shared worker admission now calls this Core provider.

A separate published Core executable on Quark reports **13,194,518,528 bytes** normally,
**261,668,864 bytes** in a real **256 MiB** hard-limited transient scope, and **127,246,336 bytes**
with an additional **128 MiB** pressure limit. The harness independently asserts both limits
and verifies published file hashes; raw evidence is under `linux-memory-results-02`. All scopes
end naturally. This is live cgroup-v2 proof; v1/hybrid coverage uses deterministic fixtures.

The warning-free local builds pass **3,488 backend**, **427 shared worker** (three Windows-only
skips), and **31 Linux worker** tests. Frontend and Python sources are unchanged from the
previous qualified stage. CI on `aabdbdbe` passed the backend, frontend and native metric
checks but the native Windows Event Log concurrent-write check failed after its registration
probe (one unexpected safe availability notice). This is unresolved; CI is not wholly green
and no merge is authorised by these results.

## Neutral source/candidate interruption

The regression suite first reproduces generic failures for changed/deleted bytes, absent
identity and cancellation without a hash. Eleven new file-identity cases pass with the existing
attempt fencing/deferral cases. Warning-free builds pass **3,499 backend** and **427 shared
worker tests** (three Windows-only skips); Linux worker production sources are unchanged
from their passing 31-test controller stage.

The actual isolated Mac server runs JPEG retry selection against copied synthetic media.
A harness wrapper appends bytes to that owned source after the sample encode. The attempt
stops with one checkpointed probe, **zero media failures, exclusions and replacements**, its
existing retry count **1**, and no sample scratch. The changed source remains exactly as
modified by the test. Raw evidence is under `native-master-01/mac-source-change-01`.
The native Windows logging assertion now includes safe availability/error-code state when
concurrent writes fail; its no-failure and exact-event-ID assertions are unchanged.

The same real-server source-change regression passes on Quark, with one retained probe,
zero failures/exclusions/replacements, unchanged retry count and cleared sample scratch.
Its immutable publication verifies all artifact hashes and records the working-tree C#
hashes in `identity-master-linux-01-manifest.json`; evidence is under
`identity-master-linux-01/quark-source-change-01`. All CI checks subsequently passed for
`d2b71e8a`, including native Windows logging, final images and installer. This supersedes
the preceding commit's unresolved logging result without claiming a demonstrated root cause.

## Local-server capacity and recovery

The warning-free builds pass **3,515 backend**, **427 shared .NET worker** (three Windows-only
skips), and **31 Linux worker** tests. Frontend checking includes **95 unit tests** and all
**346 browser tests** pass. Four new capacity-wait cases cover phone/desktop and both themes;
saved screenshots were visually inspected. Every locale explains the wait and preserved retry
intent. The job-feed OpenAPI response now declares its DTO and resource-wait flag. No database
schema changes are needed.

Local source qualification reserves peak native RAM first. Its workspace is admitted separately
and RAM-backed files are charged to memory. A refused job remains queued with a monotonic
30-second local backoff; workers can still claim eligible work. Deterministic cases cover
unknown/low memory, disk/RAM workspace pressure, stale callbacks and both reservation release
orders. Real database tests preserve failure/exclusion counts and the existing quality-retry
minimum. Resource refusal restores the crash-attempt budget while keeping its unique execution
generation. The server's native memory provider is Linux; an unsupported .NET Mac server holds
these jobs rather than guessing host capacity. Swift qualification remains separate.

A real Quark server in a transient **512 MiB** scope queues an earlier-host JPEG retry without
calling the native image assessor. Its displayed resource wait persists across multiple polls,
original hash and retry minimum **91** are unchanged, and no crash attempt is spent. After
stopping only that owned test scope and restarting the isolated server with normal capacity,
it freshly measures at the retained minimum, independently verifies, releases the local slot,
replaces idempotently and rolls back exactly. All installed services and personal media are
unchanged. The published snapshot verifies **57 artifacts** and records **941 C# source hashes**.

The first publication's longer audio trial exposed a cleanup defect: a memory-only reservation
looked up a disk entry already removed by workspace cleanup. That stranded the local slot after
queuing the quality retry. A deterministic test reproduced the exact exception before the
ledger fix. The final `local-resource-runtime-linux-02` publication passes the JPEG recovery
with an explicit idle-slot check and the full **95.5-second audio retry** regression. Audio
freshly selects **128 kbps** at its retained **128 kbps** minimum, fails the unchanged final
zero-distance gate, preserves the first rejection and original, and creates no third attempt.
The original remains unchanged in the earlier interrupted test too.

Raw evidence is under `local-resource-runtime-linux-02/quark-local-resource-recovery-02`,
`local-resource-audio-quark-02` and `ui-resources-01`. The earlier failed test is retained for
diagnosis. All CI checks passed for preceding commit `6891f599`; the new local-admission
commit requires its own CI. Protocol negotiation remains 10 and this is not installed-package
or physical Windows acceptance.

## Native-client helper, 14 October

A self-contained osx-arm64 publication of the shared native helper passes actual JPEG and AAC
server claims through private stdin/stdout with PATH limited to system directories and no
system .NET runtime lookup. The server has no encoding or quality tools (audio planning still
uses ffprobe). Each case replays one committed probe whose HTTP response was deliberately
lost, settles selection, performs a new final encode and strict verification, uploads with a
completion seal, repeats replacement without a second record, and restores the exact original.
The two fixture runs explicitly grant 8 GiB; they do not establish live Mac memory admission.
The private publication manifest records 196 artifact hashes.

The first full-flow proxy trials were invalid: the test proxy lacked chunked request bodies,
then PATCH upload forwarding. Those harness errors were corrected; final JPEG/AAC case 03
passes. Separately, a real SIGTERM regression caught the helper waiting indefinitely for Mac
console input even after its native children stopped and owned scratch was removed. Three
new deterministic blocked-pipe regressions fail before explicit cancellation-bounded waits
and pass afterward. The final publication exits in under 0.1 seconds with stdin still open
on SIGTERM, with its sleeping tool and grandchild gone; parent EOF does the same. A signal
while waiting for the initial request also exits without requiring pipe closure.

Warning-free solution and Linux builds pass with 3,515 backend, 459 shared worker and 31 Linux
worker tests (three Windows-only shared skips). The 118 Python acceptance-script tests pass.
Swift application integration, live memory reservation, signed packages, Intel qualification
and Mac WebP remain outstanding; the shipped protocol maximum remains 10.

## Swift bridge and resource foundation, 14 October

The actual Swift AcceptanceWorker now delegates JPEG/AAC claims to the self-contained helper
through its production bridge. Both pass committed-response loss/replay, sealed upload,
independent final verification, repeat-safe replacement and exact rollback. Fixture memory
is explicitly granted; this is not automatic menu-bar placement or signed-package acceptance.
A real killed Swift parent loses its helper, native tool and grandchild in under 0.1 seconds,
with owned scratch empty. The final bridge regression additionally refuses a terminal result
when the child closes stdin before the full request arrives, without a SIGPIPE app crash.

The live Mac memory provider reports about 2.74 GB available for admission on this 16 GiB
machine at capture, rather than substituting installed RAM. That is a transient estimate.
Deterministic tests cover unknown capacity, pressure, overflow, mixed native/RAM reservations,
retained RAM after failed ejection and reduced RAM preferences. The existing Swift runner's
real RAM success/failure/cancellation cases and volume creation/ejection/sweep pass with its
bundled FFmpeg. The complete Swift suite passes 310 tests; the release build succeeds. A full
SDK-27 compile retains the pre-existing legacy-Keychain deprecation warnings; no Keychain
behaviour was changed. The 118 Python tests pass. Native app claims, resource advertisement,
packaging/signing, Intel and physical Windows acceptance remain separate outstanding work.
