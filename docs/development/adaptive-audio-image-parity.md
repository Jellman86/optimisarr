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
| Images | Whole-image probes, copied colour metadata, independent alpha and saved SSIM target; strict image evidence and shared .NET native verification tested in isolation | Worker image encoding, capability collection, Swift verification and packaging |
| Standalone audio | Up to three disjoint windows, at most 90 seconds; original-source encoding and lossless 48 kHz references without downmix/gain matching | Native worker search and placement parity, longer-track and difficult-passage qualification |
| Soundtracks | Existing separate reporting and gate | Adaptive bitrate selection over every retained re-encoded track, frozen stream mapping, copied tracks excluded |
| Settings | Independent opt-ins, explicit enabled target required, additive migration, config round-trip | Full settings and unsupported-combination acceptance; no hidden fixed-quality worker fallback |
| Calibration and previews | Existing disposable comparisons keep their fixed settings and cannot replace originals | WebP lossless-source calibration intent, target guidance and adaptive preview parity must be qualified explicitly |
| Evidence | Candidate settings, bytes, raw metric reports, source hash, sampling, selected value and reason; completed local probes checkpointed and shown before final verification; lease-bound search model rejects replay conflicts and source/tool changes | Remote endpoint and native search integration, reconnect acceptance, interrupted-attempt history and export qualification |
| Lifetime | Owned local scratch, stopped-process cleanup, cancellation propagation, source revalidation, attempt-fenced result persistence | All native worker implementations, resource exhaustion and restart/reconnect acceptance |
| Final verification | Existing full verification runs on a fresh final encode; one higher-fidelity retry only for a sole measured perceptual failure, with rejected attempt history | Same safety on worker delivery and native image retry qualification |
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
proof so a downgraded or failed installation cannot inherit it. Capability collection on the
native clients is still required before any new placement is enabled.

The shared search coordinator freezes the source and encoder/metric binaries, validates
raw measurements against the frozen policy, caps work at four candidates and handles repeated
identical reports without spending another probe. Conflicting, stale, out-of-order or changed
source/tool reports cannot advance it. Source refusal cannot authorize a baseline encode;
an unavailable sample can select the saved baseline, which still requires final verification.
These are tested domain rules; the reporting endpoint and native search loops are not wired yet.

Strict image verification has a separate version-4 evidence contract. Source and candidate
native assessment, SSIM, metadata, probes and decode are separate measurements. The server
recomputes the final verdict without installing media tools, and rejects missing required
evidence without local fallback. Shared .NET native verification qualifies both images with
the bounded decoder before FFmpeg, rechecks file/tool hashes and shares the process-wide
image metric lane. It is tested directly on the Mac and Quark; worker assignment, Swift support and
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
