# Audio and image quality-first parity

Operator priority: **13 October 2026**. Finish quality-first workflows for audio and
images before continuing perceptual dedupe or activating exact-copy file actions.
Tracking context: [#332](https://github.com/Jellman86/optimisarr/issues/332).

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
| Images | Whole-image probes, copied colour metadata, independent alpha and saved SSIM target; .NET native proof, capacity-aware claim and real-server lifecycle tests | Swift verification/resource proof and packaging |
| Standalone audio | Up to three disjoint windows, at most 90 seconds; .NET runner and real server pass AAC/Opus/MP3 lifecycle and lost-response replay with lossless 48 kHz references | Placement parity, longer-track and difficult-passage qualification |
| Soundtracks | Existing separate reporting and gate | Adaptive bitrate selection over every retained re-encoded track, frozen stream mapping, copied tracks excluded |
| Settings | Independent opt-ins, explicit enabled target required, additive migration, config round-trip | Full settings and unsupported-combination acceptance; no hidden fixed-quality worker fallback |
| Calibration and previews | Existing disposable comparisons keep their fixed settings and cannot replace originals | WebP lossless-source calibration intent, target guidance and adaptive preview parity must be qualified explicitly |
| Evidence | Candidate settings, bytes, raw metric reports, source hash, sampling, selected value and reason; completed local probes checkpointed and shown before final verification; lease-bound server exchange rejects replay conflicts and source/tool changes | Native search integration, reconnect acceptance, interrupted-attempt history and export qualification |
| Lifetime | Owned local scratch, stopped-process cleanup, cancellation propagation, source revalidation, attempt-fenced result persistence | All native worker implementations, resource exhaustion and restart/reconnect acceptance |
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
replacement when it cannot be obtained. Current workers refuse the new search contract until
implemented, preserving strict worker placement rather than silently bypassing selection.

## Worker foundation in progress

The next contract is reserved as protocol 11; the advertised protocol remains 10. Current
workers still refuse quality-first audio/image jobs. The persisted worker capabilities and
lease search state are additive, bounded JSON fields with idempotent migration coverage.
Pairing and check-in can record fresh optional native-tool proof; omission clears earlier
proof so a downgraded or failed installation cannot inherit it. Shared .NET collection now
runs those probes; Swift and exact-package qualification remain required before placement is enabled.

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
image metric lane. It is tested directly on the Mac and Quark; automatic worker assignment, Swift support and
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
metadata rewrites. The required workspace is sent with the assignment for native admission;
native available-memory checks and reservations still need implementation. The global worker
protocol remains **10**: declaring protocol-11 server contracts does not activate native work.
Native capability discovery, execution and exact-package acceptance remain required.

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
scratch. Broader restart/fault acceptance, capacity-aware placement/backoff, capability collection, Swift
support and complete package qualification remain required before advertising protocol 11.

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
coverage; Quark's live hierarchy is v2. Local-server memory reservation remains outstanding.

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
