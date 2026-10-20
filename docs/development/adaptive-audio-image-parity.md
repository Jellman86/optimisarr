# Audio and image quality-first parity

Operator priority: **13 October 2026**. Finish quality-first workflows for audio and
images before continuing perceptual dedupe or activating exact-copy file actions.
Tracking context: [#332](https://github.com/Jellman86/optimisarr/issues/332).

## Active checkpoint, 16 October 2026

The operator resumed full parity work after the 14 October pause. [Draft PR #415](https://github.com/Jellman86/optimisarr/pull/415)
remains unmerged and undeployed. Protocol 10 remains advertised; automatic protocol-11 placement
stays disabled. No quality target has been calibrated as a universal default.

The 20 October [Linux scratch-open follow-up](../engineering/hardware-validation/2026-10-20-linux-scratch-open-flags.md)
extends the verified ABI mapping from journal storage to scratch directory/file opens. Pure
contracts and native Quark tests pass, and an ARM64 syscall probe demonstrates the corrected
no-follow behavior under emulation. Physical Linux ARM64 execution remains unqualified;
both hosts were reachable again at the later 20 October check. Fresh exact-source shared
suites pass 765 tests on Mac (49 platform skips) and 882 on Picard (26 Linux-only skips),
with the Mac backend also passing 3,575 tests. These source tests supersede the earlier
availability blocker without qualifying installed packages or a Linux ARM64 host.

The Mac package now contains the self-contained shared helper, exact runtime notices and pinned
ExifTool source/modules using system Perl. Every nested native library is signed before the app;
Developer ID builds grant JIT only to the managed helper. Development ad-hoc builds retain their
existing non-hardened signing because ad-hoc signatures cannot establish a shared team identity
for runtime library validation. The minimum-macOS check now traverses nested runtime libraries
and checks the highest requirement across universal-binary slices.

Physical Mac acceptance passed from a relocated Developer ID signed app with no Homebrew or
installed .NET on its execution path: managed startup, JPEG/WebP encoding with compressed ICC/EXIF
preservation and marker readback, both pinned metrics, scratch cleanup and all 20 UI fixture
states. This is package qualification for that tool set, not notarisation, automatic placement,
full job/restart acceptance or complete parity. The pinned Mac FFmpeg now includes WebP. Its lossy/lossless checks require exact alpha, and lossless colour must match the independent RGBA fixture byte for byte. Those WebP checks pass on Mac, native Quark and native Picard. The relocated signed Mac bundle now proves both JPEG and WebP with metadata preservation.

Native scratch now uses persistent, inherited ownership guards across .NET and Swift work.
Recovery retains files while a surviving tool holds its guard, validates physical identities,
preflights bounded trees and records deletion intent for interrupted cleanup. Startup and chosen
folder recovery run before native admission. Actual FFmpeg parent-death trials pass on Mac,
Quark and Picard; the signed Mac helper also passes Swift recovery and busy-volume retention on
a real RAM disk. Unknown filenames cannot starve owner pages. This proves specific process
restart behaviour, not complete packaged-job or machine-reboot/remount recovery. See the
[ownership qualification and limits](../engineering/hardware-validation/2026-10-15-native-scratch-recovery.md).

Native helper interruption now has a separate acknowledged end reason. Parent exit or early
cancellation returns the lease without a media refusal or capacity cooldown. Actual Quark and
Windows trials survive three parent crashes and then complete the same job with the restored
worker identity, retained source assessments, independent final verification and exact rollback.
Fresh Swift pairing and packaged Mac AAC/Opus/MP3/RAM workflows passed on the pre-fix bundle;
the rebuilt bundle's full crash/image workflows remain constrained by current memory pressure.
See the [interruption qualification and exact limits](../engineering/hardware-validation/2026-10-15-native-interruption.md).

Independent Astra review identified Swift pre-run cancellation and helper failure using ordinary
release. The Swift follow-up now acknowledges those interruptions independently of task
cancellation, with regressions for requirements, recovery, RAM preparation and running cleanup.
The connected runtime now retains native peak/disk reservations when an inherited writer outlives
the helper. Signed release-library trials cover busy delivery and a killed Swift parent followed
by restored accounting and same-identity replay. Missing ownership evidence cannot count as
retirement, and delayed RAM ejection validates mount identity. Shared encode/probe execution
now retains direct-child ownership through cancellation and reader failures, bounds captured
text and refuses incomplete probe evidence. Managed process and inherited-guard tests pass
on Mac and Quark; the current shared suite also passes fresh native Picard execution on
19 October. Standalone durable reservation integration remains open. See the
[process ownership evidence and limits](../engineering/hardware-validation/2026-10-15-native-process-ownership.md).

Accounted capability workspaces now use the same pinned ownership and retained retirement
receipts as jobs. Component tests pass actual parent death with surviving probe tools on Mac
and Quark. Standalone startup, capability admission and durable charge reconciliation remain
unconnected; this prerequisite does not establish standalone restart accounting.

The shared .NET [accounting format and checkpoint rules](native-work-accounting.md) now freeze
immutable obligations and forward transitions, and accept only independently recorded old/new
snapshots after an interrupted save. A fixed physical-ID pool now prevents mixed path/ID
accounting namespaces. The shared transaction boundary now serializes preparation, actual work
and finalization, fences uncertain commits and retains ownership through shutdown. Its injected
storage tests do not establish physical durability. Physical identity/capacity capture, durable OS
storage, first-start bootstrap and coordinator effect ordering remain required before standalone integration.
Linux durable storage has since landed on this branch. Independent design reviews on 19 October 2026
rejected the first Windows storage design (shared-workspace foundation, directory `FlushFileBuffers`,
path-based replace) and then allowed a revised NTFS-only handle-scoped design only as an
unconnected experimental candidate. That candidate is now implemented with Windows-gated tests
(private creation-time ACLs, pinned 128-bit identities, exclusive coordinator, cross-process
forced-death lock release, lost-acknowledgement phases). It claims no directory synchronization
or power-loss durability; physical power-cut qualification and standalone wiring remain open.
Fresh Picard execution on 19 October also passes the
[49-case process-death matrix](../engineering/hardware-validation/2026-10-19-windows-journal-process-death.md),
including provisioning, commits and interrupted pending old/new recovery. Mac and Picard are
reachable again; this supersedes their earlier offline status without qualifying power loss.
An unconnected Linux-only bootstrap/restoration module now separates explicit operator
provisioning from normal opening and restores exact physical charges into an unpublished pool;
it has no service callers and does not provide startup admission or reporting integration.
A checksum-verified Fedora 44 cloud guest also boots under KVM on Quark, but no storage
power-cut trial has run in it. See the [accounting record](native-work-accounting.md).

The shared helper now has a version-2, parent-bound retirement contract with pinned physical
scope, inherited-guard exclusion, durable retirement receipts and replayable consumption.
Its source/process tests pass on Mac, Quark and Picard. Swift now has a checksummed, atomically
replaced obligation journal, restart debt restoration and a tested reconciliation actor.
Every running owner still needs targeted positive retirement, including a duplicate invocation
that reports no workspace. Durable receipt consumption precedes RAM ejection, and accounting
release is replayable with either fresh or retained in-process budgets. The actual Swift bridge
passes busy-owner, retirement and consumption replay checks against the self-contained helper.
The RAM component now captures the original boot, media and backing-driver IDs, volume UUID
and root identity. Six physical Mac tests pass busy-writer retention, changed/redirected identities,
actual device-name reuse, replay after unmount and rejection of a matching file-backed image. Removal uses retained handles and never forces
ejection; missing registry matches need a completed valid scan before counting as absence.
An independent Keychain checkpoint now binds the journal hash/store/path, with pending old/new
transitions and signed-process hard-exit tests. Unreadable items and unrelated older ledger snapshots
remain blocked. Owner-bound partial allocation receipts now cover interrupted creation before
a filesystem exists; a mounted identity must match that original allocation. Partial retirement
cannot unmount or format. Three additional physical cases cover unformatted allocation, actual
BSD-name reuse and mounted-volume refusal. Journal close explicitly releases its acquired lock
even during transient descriptor inheritance. A registry-backed RAM factory now persists partial
allocation before formatting and the actual mounted root before helper scope preparation. It
retains the original device pin through actual command exit, including caller cancellation.
Signed-process SIGKILL trials pass at the pre-attach, partial-allocation and mounted-root durable
boundaries; unknown pre-attach debt stays held. The app/session/runner now share anchored startup
recovery and registry-backed v2 execution. Incomplete recovery drains new claims, and late leases
after disconnect are acknowledged independently. Final connected-runtime and packaged lifecycle
qualification remains open; these results do not certify every installed-app lifetime boundary. See the
[runtime integration and current limits](../engineering/hardware-validation/2026-10-15-native-work-runtime.md),
[helper foundation](../engineering/hardware-validation/2026-10-15-native-retirement-foundation.md),
[Swift accounting evidence](../engineering/hardware-validation/2026-10-15-swift-durable-native-accounting.md)
[independent checkpoint evidence](../engineering/hardware-validation/2026-10-15-native-accounting-checkpoint.md)
and [RAM retirement qualification and limits](../engineering/hardware-validation/2026-10-15-native-ram-retirement.md).
The [partial-allocation record](../engineering/hardware-validation/2026-10-15-native-ram-allocation.md)
records the partial receipt. The [factory qualification](../engineering/hardware-validation/2026-10-15-native-ram-factory.md)
records creation, physical tests and signed crash-recovery limits.

Latest source verification: 3,526 backend tests on Mac/Quark; 3,503 plus 23 platform skips on native
Windows; 554 shared worker tests plus three Windows-only skips on Mac/Quark; 557 native Windows
shared tests; 31 Linux tests, 489 registered Swift core plus nine UI, 154 Python and 346 browser tests.
Final Swift and browser gates pass serially with unchanged assertions and deadlines; parallel
host-load failures remain retained in the runtime qualification record.
Live/paired Swift gates remain opt-in; twelve identity/partial/factory physical RAM cases pass explicitly.
Check the PR for exact-head CI. Native trial protocol elevation remains fixture-only.

Continue with exact-package media lifecycles and broader restart/reconnect qualification.
Fixed-image worker coverage, adaptive selection for
every retained re-encoded soundtrack, preview/calibration parity, broader source, reconnect/retry
and input-format qualification remain required. Positive full-image execution under live Mac
admission is still unproved: earlier available memory was below the 3.5 GiB budget, and the job
returned safely before downloading. The current Mac also reports warning memory pressure, which
correctly prevents a live capacity grant; tool-fixture success does not override admission. The matrix below remains the completion contract. Perceptual
dedupe and exact-copy file actions remain behind this work in priority.

## Completion contract

Parity means the workflow already offered by adaptive VMAF: an explicit quality target,
a bounded search on the eventual encoding host, the smallest measured passing candidate,
clear progress and retained evidence, conservative fallback, independent final verification,
safe retries, cancellation/restart recovery and qualified packages on supported hosts.
Different metrics retain their own units. Neither a search result nor a worker assertion
authorises replacement. Existing installations retain their saved settings.

Use SSIMULACRA2 for images and pinned Zimtohrli for audio. Existing image SSIM,
transparency, metadata, decode, timing, channel, loudness/clipping and size gates remain
independent. No universal image floor or audio-distance cutoff is assumed. The current
supported image domain and mono/stereo audio domain remain explicit; wider coverage needs
its own qualification and must not be inferred from successful search orchestration.

## Implementation and evidence

The current branch contains a local workflow under qualification, not completed parity:

See the [13 October local qualification record](../engineering/hardware-validation/2026-10-13-audio-image-quality-first.md)
for available-device media tests, discovered regressions and explicit limits.

| Area | Current branch | Required before parity is complete |
| --- | --- | --- |
| Search | Shared four-candidate search, measured-byte selection, codec bitrate ladders, deduplicated JPEG quantizers, deterministic fallback | Independent acceptance of selection and final commands on each host |
| Images | Whole-image probes, copied colour metadata, independent alpha and saved SSIM target; shared native proof, conditional Swift collection, capacity-aware claim and real-server lifecycle tests | Positive full-image execution under live Mac admission, packaged Swift advertisement/placement and full job/restart qualification |
| Standalone audio | Up to three disjoint windows, at most 90 seconds; .NET runner and real server pass AAC/Opus/MP3 lifecycle and lost-response replay with lossless 48 kHz references; ordinary Swift claims now pass all three codecs under live memory admission | Automatic placement parity, longer-track and difficult-passage qualification |
| Soundtracks | Existing separate reporting and gate | Adaptive bitrate selection over every retained re-encoded track, frozen stream mapping, copied tracks excluded |
| Settings | Independent opt-ins, explicit enabled target required, additive migration, config round-trip | Full settings and unsupported-combination acceptance; no hidden fixed-quality worker fallback |
| Calibration and previews | Existing disposable comparisons keep their fixed settings and cannot replace originals | WebP lossless-source calibration intent, target guidance and adaptive preview parity must be qualified explicitly |
| Evidence | Candidate settings, bytes, raw metric reports, source hash, sampling, selected value and reason; completed local probes checkpointed and shown before final verification; lease-bound server exchange rejects replay conflicts and source/tool changes | Packaged Swift capability/placement qualification, broader reconnect acceptance, interrupted-attempt history and export qualification |
| Lifetime | App-shared anchored Mac accounting, scoped v2 execution, inherited native guards, partial cleanup replay and positive writer/RAM retirement; connected busy-delivery and parent-kill/same-identity restart trials; source and attempt fences | Exact packaged restart/reconnect, full reboot/remount and resource-exhaustion acceptance |
| Final verification | Fresh final encodes; one retry only for a sole measured perceptual failure, with retained rejection history. Local and remote retries run a fresh search above the prior minimum | Native worker retry/restart and exact-package qualification |
| Release | Nothing deployed by this work | Green full CI, exact-package acceptance and Git-backed deployment when qualified |

Audio probe cuts use whole seconds to match the transcode contract. Fractional tails are
explicitly outside search coverage and remain in final verification's coverage. A passing
sample cannot certify unassessed passages. Image measurements remain sequential per server;
the qualified 16-megapixel comparison used approximately 2.3 GiB of resident memory.

Local search results are regenerated for each fresh execution rather than reused across a
changed source, encoder or policy. The source is hashed before/after the search and again
by final verification. Candidate paths are unique owned scratch leaves and refuse overwrite.
Unavailable evidence falls back to the saved setting; the required final metric still blocks
replacement when it cannot be obtained. Advertised protocol 10 prevents automatic placement of
the new native search contract; unsupported clients refuse it rather than bypassing selection.

## Worker foundation in progress

The next contract is reserved as protocol 11; the advertised protocol remains 10. Current
workers do not automatically claim quality-first audio/image jobs. The persisted worker capabilities and
lease search state are additive, bounded JSON fields with idempotent migration coverage.
Pairing and check-in can record fresh optional native-tool proof; omission clears earlier
proof so a downgraded or failed installation cannot inherit it. Shared .NET collection now
runs those probes; the ordinary Swift collector conditionally uses the bundled shared helper.
Packaged capability/placement qualification remains required before placement is enabled.

The shared search coordinator freezes the source and encoder/metric binaries, validates
raw measurements against the frozen policy, caps work at four candidates and handles repeated
identical reports without spending another probe. Conflicting, stale, out-of-order or changed
source/tool reports cannot advance it. Source refusal cannot authorize a baseline encode;
an unavailable sample can select the saved baseline, which still requires final verification.
The reporting endpoint and shared .NET runner now use these rules. Automatic placement remains
disabled until capabilities, resource admission on all clients and packages are qualified.

Strict image verification has a separate version-4 evidence contract. Source and candidate
native assessment, SSIM, metadata, probes and decode are separate measurements. The server
recomputes the final verdict without installing media tools, and rejects missing required
evidence without local fallback. Shared .NET native verification qualifies both images with
the bounded decoder before FFmpeg, rechecks file/tool hashes and shares the process-wide
image metric lane. It is tested directly on the Mac and Quark, with native Windows source/tool
acceptance and conditional Swift routing also implemented; automatic worker assignment and
exact-package qualification remain outstanding. ExifTool operations now have bounded capture,
a deadline and child reaping before cancellation returns, including UTF-8 argument input. Source metadata is inspected before sample encoding when
required. Grouped JSON and explicit binary-profile presence prevent tag-value spoofing and
missing compression support being mistaken for an image with no profile. Incomplete source
inspection holds the job without spending media failure counts; the runtime declares and
checks its compression dependency.

A real metadata fixture exposed an existing ICC-copy bug: the group-field copy could report
success while dropping the profile. Copying the complete binary ICC tag follows the
[ExifTool application documentation](https://exiftool.org/exiftool_pod2.html). The quality-first
acceptance harness now independently compares complete ICC bytes and the generated EXIF Artist
on JPEG and WebP outputs, then exercises replacement and exact rollback. The final-container
CI matrix includes these workflows and the audio/image searches.

## Qualification matrix

Extend the existing isolated acceptance harness; do not use the installed service or replace
personal media as a test fixture. `--regression quality-first` exercises JPEG and AAC/Opus/MP3
selection, deliberate impossible targets, original hashes, actual final encoder settings,
replacement rejection and exact rollback. Its deliberately permissive success limits and
zero-distance rejection limits are fixtures, not recommended policies.

Before declaring parity, prove:

- Native Mac and Quark runs on the same sources, with native Picard and all sidecar packages
  tested independently. CI is separate from physical host acceptance.
- Source/candidate/tool changes, incomplete windows, malformed output and old workers refuse
  required evidence; sampling and alpha/channel aggregation cannot hide damage.
- Final-score disagreement triggers only a bounded permitted retry, preserves the source and
  records both attempts. Size, decode, timing or metadata failures cannot be retried away.
- Queued/running cancellation reaps children before scratch cleanup. Reservation/ownership
  conflicts preserve evidence, do not spend retry budget and do not exclude the source.
- Restart, reconnect, repeated reports and stale attempts neither adopt another encoder's
  selection nor mutate a newer candidate. Required worker verification stays on the worker.
- Phone/desktop settings and reports remain usable in both themes, including fallback,
  unsupported input, missing tool, waiting worker and final rejection states.

Record exact commits, tool hashes, commands, wall time, memory and tested coverage. Keep raw
private evidence outside Git. Update this table as each implementation and qualification lands;
do not mark the larger #332 capability complete merely because the local path passes.

## Server exchange (14 October)

The server can now freeze an audio/image search when claiming a job for a protocol-11 worker.
`POST /api/workers/leases/{leaseId}/perceptual-probe` accepts at most 48 KiB of canonical
measurement evidence within a 64 KiB request limit. Only the current holder, live lease and
execution attempt may advance it. Identical reports replay their response; conflicting,
out-of-order, expired and downgraded-worker reports cannot advance. Source rejection ends
the lease without a full encode, quality retry or media failure count. Original metadata,
source hash and native tool identities cannot change between settings.

The selected encode changes exactly one control in the saved command. It keeps artwork,
metadata and marker intent and never reloads changed library settings. Output suffixes and
input arguments are validated. Delivery and verification remain blocked while the search
is incomplete or its encoding snapshot is corrupt. Image final verification checks the
frozen codec too. A retry contract retains its source hash and searches only at or above
the required fidelity, including JPEG's one-setting maximum-quality case.

Claim admission includes bounded samples, lossless references, native PCM scratch and
metadata rewrites. The required workspace is sent with the assignment for native admission.
.NET worker and local-server native memory/workspace reservations are implemented; Swift admission is implemented and independently qualified for audio; positive image admission remains pending. The global worker
protocol remains **10**: declaring protocol-11 server contracts does not activate native work.
Packaged advertisement, exact-package media lifecycles and remaining workflow qualification are still required.

The shared native probe executor now validates every supplied command against the canonical
original-source plan before starting tools. It qualifies the source, prepares references,
copies image metadata, retains per-window native/SSIM/loudness evidence and removes its owned
scratch after child processes stop. Changed retry sources, changed tools and unavailable
workspace preparation block encoding. Unsupported bounded candidate evidence cannot fall
through to ordinary SSIM decoding. Probe errors retain completed evidence and cannot become
a partial quality pass. The .NET report transport has byte/deadline limits, rejects ambiguous
directions and marks interrupted responses as replayable. A shared native search session now
replays the exact report after a lost committed response, checks source/raw evidence and the
unchanged contract, and compares the selected final command with the claim while permitting
only its measured quality-control change. Replays are bounded, cancellation propagates, and
the server cannot override a required source refusal. These components do not activate protocol 11.

## Shared .NET runner (14 October)

`JobRunner` now runs this session during its renewing measurement stage. Native claims require
matching search/final contracts, an original SHA-256 and a peak scratch budget. Downloads are
bounded to the assigned source size, including chunked and resumed transfers. Unique per-lease
workspaces cannot adopt a previous attempt. A refused original never starts a full encode or
preview decoder. Source and encoder identities are checked before full encoding, afterward and
after independent final verification. Image metadata and the saved optimisation marker are
prepared before final size checks, hashing, measurement and upload.

Native work reserves peak disk and memory across concurrent native jobs until cleanup finishes.
The conservative image lane requires 3.5 GiB because untrusted inventory dimensions cannot lower
the source decoder's initial maximum; audio requires 512 MiB. RAM-backed scratch additionally
reserves its byte budget in memory. Ancestor links are resolved before identifying the volume.
Unavailable capacity stops before fetching and does not consume media failure counts.

Linux admission uses current available memory and cgroup-v1/v2 limits through visible ancestors,
including hybrid hosts and v2 pressure limits;
Windows uses live physical availability and conservative job-object bounds that include native
children. These follow the [Linux controller documentation](https://www.kernel.org/doc/html/latest/admin-guide/cgroup-v2.html)
and [Windows job limits](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information).
The .NET provider resolves controller membership through mountinfo, including mounted subtrees
and namespace roots, and withholds capacity when the mapping or readings are unresolved. Legacy
and hybrid layouts have deterministic parser coverage; live Quark proof is cgroup v2. macOS
availability is still unknown in .NET and holds work. The shipped macOS client is Swift and needs its own implementation.

Native runner tests pass JPEG/AAC/Opus/MP3 on the Mac with an explicitly provisioned test memory
lane, and JPEG/WebP/AAC/Opus/MP3 on Quark using its live memory provider. They exercise the actual
runner, native processes, report transport, transfer and final server verdict, with an in-process
simulated control plane. They do not yet prove a real server's persisted claim/upload/replacement
lifecycle. A separate real 256 MiB cgroup trial refuses before fetch or encode and leaves no
scratch. Broader restart/fault acceptance, Swift support and complete package qualification remain required before advertising protocol 11.

A subsequent isolated real-server harness passes the persisted claim, source download, native
search, sealed upload, strict final verdict, replacement and exact-byte rollback for JPEG,
AAC, Opus and MP3 on the Mac, plus WebP on Quark. Audio runs and all five Quark cases discard
one successful probe response after the server commits it: replay recovers without a fifth
measurement or changed selection. Repeating replacement creates one rollback record. These
fixtures use manually seeded protocol-11 identities while automatic negotiation remains 10;
they do not qualify capability discovery or shipped native packages. Server encoding/quality
tools are absent; audio planning still uses the server's ffprobe to read source metadata.
Recorded checks validate source hashes, smallest measured passing selection, actual full
commands and empty worker scratch. Reviewing those records also exposed inconsistent nested
image location labels; executor and receiver now identify those measurements as worker evidence.

## Fresh retry selection

A retained retry supplies a minimum fidelity, rather than a selection proven on a previous
encoder or host. Local and remote planning share this rule. The current encoder measures new
samples at or above that minimum; an unavailable search uses the higher retry baseline and
still requires independent final verification. Missing, exhausted or conflicting retry intent,
changed sources, codecs or relevant targets cannot restart an unconstrained search. Local
preparation refusal does not spend media failure/exclusion counts. The rejected attempt remains
in history and no third automatic attempt is permitted.

Reports separately label a pending retry's previous selection, its minimum setting and the
current attempt's measured selection. Completed retry evidence records `minimumValue`;
`retryValue` continues to mean a pending retry. Phone/desktop reports in both themes and all
nine locales retain the existing layout and independent final-verification message.

Real local tests on the Mac and Quark exercise a naturally disagreeing 95.5-second audio
fixture: sampled silence passes, its fractional noise tail fails final verification, and a
fresh higher-quality retry also fails the unchanged zero-distance gate. The original and first
rejection remain. Seeded earlier-host image intent separately exercises fresh JPEG selection
on both hosts and WebP on Quark, followed by verification, replacement and exact rollback.
Missing-intent fixtures stop before measurement with zero media failures or exclusions.

## Native .NET capability collection and placement

Startup qualification now runs actual JPEG/WebP encodes, probes, full decode and native
comparisons on a synthetic 32×32 fixture. It proves compressed PNG metadata inspection, copies
complete ICC/EXIF into a stripped PNG and each encoded format, compares all profile bytes, and
round-trips the portable marker. Separate image and stereo-audio self-assessments must match
this release's metric revisions, file identities and complete coverage. A failed or missing
module withholds its proof; it cannot inherit an earlier installation's capability. Fixtures
are embedded in the worker assembly and removed after stopped children have been reaped.

Pairing and heartbeats carry that proof. Volatile available memory and physical working-volume
facts refresh without rerunning encodes. Native reservations stay deducted until cleanup, and
RAM-backed scratch shares the memory budget. Placement and runtime use the same conservative
3.5 GiB image/512 MiB audio allowances. Unknown memory/storage facts or insufficient capacity
hold native work without changing media failure counts. Existing fixed video/audio work keeps
its established requirements. A runtime resource refusal adds a 30-second monotonic claim
cooldown while heartbeats continue, bounding stale-capacity reclaims.

Mac tool qualification passes JPEG and both metrics; its .NET memory provider remains unknown
and the shipped Swift client still needs implementation. Quark proves JPEG/WebP, both metrics
and metadata. Its real 256 MiB scope reports limited capacity despite host memory; the server
refuses a claim before source transfer. With missing compressed-metadata support, metric proof
remains available but image encoding is withheld. Real pairing/heartbeat and qualified
JPEG/WebP claim/search/upload/verification/replacement/rollback pass on an isolated server.
Fixtures elevate only their stored protocol to 11 after normal negotiation at 10. This does
not enable production assignment or complete package/Swift qualification.

## Linux memory-controller admission

The shared Core provider resolves the process's memory-controller membership against its actual
mount table. A legacy memory controller takes priority on hybrid hosts; it cannot borrow an
unrelated unified hierarchy's host-memory reading. All visible ancestors constrain admission.
Mounted subtrees, namespace roots and escaped mount paths are covered. Missing or ambiguous
mappings, incomplete limit pairs, malformed counters and overflow report unknown capacity.
V2 `memory.high` also bounds admission so native work does not knowingly enter a configured
pressure-throttled region. Swap is not counted as usable native RAM. The deprecated v1 soft
limit is not used.

On Quark, the actual provider reports host capacity, less than 256 MiB inside a real 256 MiB
scope, and less than 128 MiB when that scope also has a 128 MiB pressure limit. These processes
end normally and do not modify an installed service. V1/hybrid proof is deterministic fixture
coverage; Quark's live hierarchy is v2. Local-server memory/workspace admission is implemented below.

## Interrupted file identity

Changed, removed or unreadable source/candidate bytes are attempt interruptions rather than
evidence of damaged media. Local search, initial source capture, delivered-file checks and
independent final verification use this neutral path. Saved search/rejection evidence and
owned candidates remain available; no automatic quality retry or media failure/exclusion
count is spent. Cancellation remains cancellation, even when identity is missing. Expected
source lookup is fenced to the execution attempt. Replacement still requires a fresh full
verification of the exact bytes.

Actual isolated Mac and Quark server runs change only their copied synthetic source after a sample
encode. Each stops after one retained probe, removes sample scratch, creates no replacement,
and leaves both failure/exclusion counts at zero and the prior retry count unchanged.

## Local-server memory and workspace admission

Normal adaptive audio/image work reserves native peak memory before source assessment, then
reserves its bounded workspace before creating references or samples. RAM-backed scratch is
charged to memory too. The reservation ledger and physical-volume/link resolution are shared
with the .NET worker. A local source stays in the library and is not charged as a downloaded
copy. Reservations remain held through independent final verification and sample cleanup.

Unavailable capacity re-queues the job without media failures, auto-exclusion, an additional
quality retry or a spent crash attempt. Its execution generation remains unique. Saved retry
intent is restored when workspace admission refuses, so a later attempt cannot lose its higher
minimum. The same generation has a 30-second monotonic local backoff; eligible workers remain
free to claim it. A restart re-evaluates live capacity. The live queue exposes an explicit
resource-wait flag and readable phone/desktop details in nine locales. This is transient state
and requires no database migration. The job-feed OpenAPI response now declares its DTO.

The server provider is Linux. A .NET macOS server reports unknown memory and holds this work;
the shipped Swift worker still needs its own provider and native implementation. No protocol
version is activated by this change.

The first longer audio acceptance run exposed a memory-only reservation cleanup error that
stranded the local slot after queuing a final-quality retry. A deterministic reverse-release
regression reproduces it. Memory-only reservations now stay separate from the disk ledger;
release order cannot remove another reservation's memory accounting. Original bytes remained
unchanged during that failed test. The final Quark publication passes an explicit idle-slot check, complete final-quality audio
retry and JPEG low-memory/restart/replacement/rollback. This supersedes the interrupted trial.

## Native-client helper foundation

The native macOS app will retain pairing, claims, live capacity and its UI while a bundled
self-contained helper runs the shared .NET audio/image engine. This keeps command validation,
source/tool binding, search replay, transfers and final gates in one implementation. Existing
Swift video/VMAF execution is unchanged. The helper neither claims nor advertises capacity:
its private request carries an allocation already held by its parent. Unknown live capacity
must still prevent native claims. The Swift bridge, shared RAM/native allocation policy,
runtime notices, architecture and hardened-runtime signing remain separate qualification work.

Private IPC version 1 bounds requests, rejects duplicate/case-aliased fields, keeps credentials
out of arguments/environment and bounds output. Only a final event together with process exit
can confirm the helper outcome; the server verdict still authorises replacement. Parent EOF,
SIGTERM and blocked output cancel owned work. A real Mac regression caught console reads
ignoring cancellation after native children and scratch had stopped; explicit bounded waits
now allow the helper to exit with the control pipe still open.

The self-contained ARM64 executable runs with no system .NET on its search path. Actual JPEG
and AAC claims pass search, lost committed-response replay, sealed upload, independent final
verification, repeated replacement and exact rollback against an isolated server without
encoding/quality tools. This fixture grants 8 GiB explicitly; it is not live Swift memory
admission or signed-app acceptance. The installed Mac FFmpeg still lacks WebP. Protocol 10
remains advertised and protocol 11 remains reserved.

## Swift bridge and live memory foundation

The Swift client now has a bounded private-pipe bridge to the shared engine. Requests keep
credentials out of arguments and environment; nonblocking writes suppress SIGPIPE locally.
Replies must match their job, retain complete newline framing and agree with process exit.
A terminal event cannot authorise an incompletely sent request or release process ownership
early. Cancellation stops a blocked writer too. Killing the real Swift fixture process causes
EOF in its native helper, which stops its tool and grandchild and clears scratch.

The bridge passes real JPEG and audio component workflows through isolated acceptance processes.
The current signed release-library route passes AAC/Opus/MP3 and actual RAM work with live parent
resource admission, committed-response replay, sealed upload, final verification and exact rollback.
The server offer capacity and protocol elevation remain explicit test fixtures. App/headless sessions
and the runner now share anchored recovery; installed-app and complete package qualification
remain separate. See the [current runtime evidence](../engineering/hardware-validation/2026-10-15-native-work-runtime.md).

Swift's memory ledger can atomically account for native peaks and complete RAM-volume
allocations. RAM-storage preferences constrain RAM allocations, not native work on disk.
Native obligations now remain charged until registry-backed writer and RAM retirement are
positively established. The existing Swift RAM-job path also checks live capacity before creating a
volume and falls back to disk when it is insufficient or unknown.

The Mac admission estimate uses Mach free/inactive counters, excludes swap/compressed pages,
leaves at least 1 GiB or 10% of physical RAM and refuses unknown/non-normal kernel memory
pressure. Inactive pages are an estimate of reclaimable memory, not a guarantee. Speculative
pages are already counted in free pages; no extra speculative or purgeable allowance is added.
The implementation follows Apple's [VM counter definitions](https://github.com/apple-oss-distributions/xnu/blob/main/osfmk/mach/vm_statistics.h)
and [kernel counter collection](https://github.com/apple-oss-distributions/xnu/blob/main/osfmk/kern/host.c).
A live healthy Mac provider and real RAM-volume creation, encode success/failure/cancellation,
ejection and stray-volume cleanup pass separately from deterministic capacity/refusal tests.

The bridge can also obtain a validated assignment's native peak and required scratch budget
from the helper without credentials, media tools, filesystem writes or network traffic.
This keeps the budget policy in the shared engine. The reply is not a live capacity reading
or a grant: the parent still has to atomically account for native peak plus full RAM-volume
allocation before starting work. CI exposed a Swift compiler difference in synthesized
initializer access; the frame parser now declares its initializer explicitly.

## Physical Windows qualification, 14 October

The shared .NET worker now passes real JPEG, WebP, AAC, Opus and MP3 searches on
physical Windows, including lost committed-response replay, sealed upload, strict final
verification, idempotent replacement and exact rollback. Image runs independently compare
the complete ICC profile. This qualifies the shared runner and tools, rather than an installed
service or release package. Automatic protocol-11 negotiation remains disabled.

Actual nested Windows jobs exposed a capacity overestimate: current-job and app-memory
queries missed a stricter outer limit. Native work now holds when the process belongs to
any job, or membership cannot be established. It uses physical available memory only when
explicitly outside jobs. This is deliberately conservative, including an apparently unbounded
job; product code never escapes a job or changes its limits. The physical happy-path matrix
was repeated in an independently verified standalone process after the guard. Installed Swift app
qualification, exact packages and the other completion-matrix obligations remain outstanding.

## Mac execution route, 14 October

The ordinary Swift client preserves an opaque native assignment, and its normal job runner
selects the shared engine when available. Version-4 image evidence remains in that engine;
it cannot enter legacy verification. Unknown fields, number precision and nonidentity duplicates
survive the private request for authoritative validation. Ambiguous lease/job identities are
refused at the client boundary; an ambiguous lease cannot be reported as safely handed back.

Requirements are validated before reserving the shared native/RAM budget. Disk helpers share
planned workspace by volume/container; owned RAM is charged for its full allocation and remains
charged after a failed ejection. A fresh memory check follows workspace creation. Lease checks
bracket preparation before the helper begins downloading. Confirmed capacity refusal pauses
claims for 30 monotonic seconds while heartbeats continue. Delivery with an unknown byte count
is displayed without substituting zero. Image cards use the photo icon and native stages carry
no invented transfer counts.

The real Mac route passes AAC/Opus/MP3, committed-response replay, final verification,
idempotent replacement and exact rollback with live memory admission. RAM storage/ejection
and custom RAM aliases are qualified separately. A live image claim holds below its 3.5 GiB
budget before any source download. This is conditional execution support, not automatic placement:
Tool-proof advertisement and connected accounting/restart are now implemented and component
qualified. Positive live image acceptance and complete packaged lifecycle qualification remain
required before protocol 11 is enabled. The soundtrack, preview/calibration and broader coverage obligations
in the matrix are unchanged.

Capacity returns now have a separate, authenticated, replay-safe lease operation and durable
end reason. They retain the same per-job/worker pause as ordinary hand-backs, but do not count
toward the three-worker refusal threshold. Genuine refusals still do. Three actual Swift runtime
capacity returns leave a fourth worker eligible, without downloads, media failures or exclusions.
The original release operation and its .NET public method signature remain compatible. This
adds no column or migration; the reason fits the existing nullable enum-string field.

## Conditional Swift capability collection

The ordinary Mac collector now calls the bundled shared helper on owned synthetic inputs,
validates bounded native tool proof and carries it in pairing and heartbeat requests. Current
memory and the chosen physical disk fallback are sampled separately, subtracting app native/RAM
and planned native disk reservations. Unknown resources clear capacity; custom RAM aliases
cannot stand in for a physical disk. A RAM preference advertises its conservative disk fallback;
the actual per-job RAM allocation is still admitted independently before creation.

Duplicate, escaped-alias or case-alias fields in helper events are refused before dictionary
conversion can erase their ambiguity. Missing tools, failed proof, malformed revisions and
helper-supplied resource claims cannot advertise native support. Probe ownership survives async
suspension: unpair, sleep and quit cancel native qualification, obsolete connection attempts
cannot restore a pairing, and disk classification runs away from the menu's main actor.

The ordinary collector and actual Swift pair/heartbeat transport pass against an isolated real
server with its encoding/quality tools absent. JPEG, colour metadata and both pinned metrics are
proved on the Mac; unavailable WebP is omitted. The server stores the fresh heartbeat proof with
normal protocol-10 negotiation, no row elevation or explicit memory grant, no job leases and
empty scratch. The observed memory remains below full-image admission, so this is tool and
transport evidence, not positive live image execution. Those initial collector tests passed 352
core tests and nine UI tests. Current runtime qualification passes 489 core plus nine UI tests.
Native-helper bundling is now implemented; installed-package qualification and protocol-11
activation remain outstanding, along with the other parity requirements above.

## Audio assessment input boundaries

Native original-audio qualification and shared audio quality measurements now admit only local
media protocols and explicit audio containers. Soundtrack measurements additionally admit the
supported video containers. The restrictions precede both ffprobe input opening and PCM decoding;
DASH/HLS/concat and other indirect formats cannot qualify by reading secondary media whose bytes
are absent from the transferred original's hash. Candidate/reference duration, channel, sample,
quality and source/tool identity checks remain independent.

A physical Windows DASH fixture without a filename extension previously passed an ordinary audio
probe while reading segments outside its source folder. The same frozen shape now fails native
original qualification before encoder preparation, sample creation or scratch allocation. Mac
AAC/Opus/MP3 claim/search/replay/final verification/repeated replacement/exact rollback pass again;
Windows WAV/AAC/Opus/MP3 measurements pass and clear scratch. This slice bounds audio qualification
and assessment. Generic inventory probes and other full-encode/verification input paths still
need their own format audit before protocol-11 activation; it is not a general process sandbox.

## Mac WebP and corresponding sources, 15 October

FFmpeg uses the same immutable WebP pin as the native image decoder. Build provenance records
its actual commit; corresponding-source packaging retains the declared immutable Git object
identity while archiving its peeled commit. This matters for annotated tag hashes, which differ
from their commit hashes. Moving tag names are refused. A dirty checkout cannot change archive
bytes. Completed build provenance is published only after media-tool qualification passes.

The regenerated bundle passes Developer ID signing, relocated JPEG/WebP ICC/EXIF/marker proof,
both pinned metrics, empty scratch, nested minimum-OS checks and all 20 UI fixtures. Physical Mac
checks also prove VideoToolbox encode/decode, AAC/Opus/MP3 and software AV1 after the FFmpeg rebuild.
Platform-neutral source/validation tests pass on Mac, Quark and native Windows; independent
64×64 RGBA WebP fixtures pass lossy and lossless checks on all three hosts. These small fixtures
qualify codec/alpha behaviour, not representative quality thresholds or full native-job lifecycles.

The updated packaged helper also passes ordinary Swift claim/admission trials for JPEG and WebP
while the physical Mac reports warning memory pressure. Both return a neutral acknowledged
capacity hold before any source download or perceptual report, with zero media failures,
unchanged source hashes, no scratch and no residual resource reservations. These trials use
explicit server-side placement fixtures and fixture protocol elevation; the parent uses actual
live pressure/capacity without a grant override. They prove safe refusal, not positive image
execution or automatic placement.
