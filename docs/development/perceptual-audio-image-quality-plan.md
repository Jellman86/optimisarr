# Perceptual quality for audio and still images

Researched **1 October 2026**, extended **2 October 2026** with pipeline, cost and dedupe plans. Delivery status reconciled **10 October 2026**.
Status: **opt-in standalone audio and video soundtrack reports, separate operator-selected gates and strict worker measurement implemented in dev; experimental server-local SDR still-image reports and explicit SSIMULACRA2 gates implemented in the current development slice; adaptive audio quality selection, broader image coverage and surround support remain planned**.
Implementation tracking: [issue #332](https://github.com/Jellman86/optimisarr/issues/332).
This complements [VMAF v1](vmaf-v1-and-nvidia-plan.md) and the existing
[personal blind comparisons](../usage/personal-quality-check.md).

The outcome is to detect audible or visible compression damage before replacement, with
the same evidence on the container and every supported worker. Existing decode, timing,
channel, loudness/clipping, image SSIM and metadata checks remain applicable. An original
is not replaced until every configured gate passes; quarantine provides rollback until
approval or retention purge. Quarantine is not a backup.

## Candidate decision

**Use SSIMULACRA2 for the image implementation and Zimtohrli for the audio implementation.**
Following the research, the operator selected the newer audio approach on 1 October 2026.
The implemented mono/stereo audio and soundtrack slices have pinned native tools,
explicit sampled coverage and operator-selected gate limits. Broader coverage and any
calibrated default still need evidence. ViSQOL remains an optional offline comparison tool, not a prerequisite for choosing the
production metric. This direction is not a claim that one metric always predicts human
perception best. No new listening-panel programme is a prerequisite.

| Candidate | Fit for Optimisarr | Decision and limits |
| --- | --- | --- |
| [SSIMULACRA2](https://github.com/cloudinary/ssimulacra2) | Native image-compression metric; explicitly measures blur and ringing/blocking. Its author's held-out CID22 results favour it over plain SSIM and Butteraugli. Available in libjxl tools. | Preferred for SDR stills. The author's benchmark is useful evidence, not an independent Optimisarr evaluation. Colour, alpha and metadata safety need separate tests. |
| [Butteraugli](https://github.com/google/butteraugli) | Native perceptual distance with a spatial error map. Its original documentation emphasises barely noticeable differences. | Useful comparison baseline; SSIMULACRA2 better matches a user-facing compression-quality score. Reconsider if local near-lossless evidence favours it. |
| [LPIPS](https://github.com/richzhang/PerceptualSimilarity) | Learned perceptual patch distance using PyTorch and pretrained networks. | Research comparator if needed; the additional runtime/weights are not justified for the first native worker implementation. This is a packaging judgement, not an accuracy claim. |
| [ViSQOL](https://github.com/google/visqol) | Established full-reference general-audio mode, with Linux/Mac build instructions and experimental Windows instructions. | Optional offline comparator only. Audio mode uses 48 kHz and downmixes to mono; one score cannot certify surround preservation. It is not a required shipped dependency or runtime fallback. |
| [Zimtohrli](https://github.com/google/zimtohrli) | Newer native psychoacoustic metric aimed at high-quality audio compression and just-noticeable differences. | Selected audio implementation. Upstream documents Debian-like testing. Current development bundles include qualified native tools for the implemented mono/stereo coverage; surround support and calibrated defaults remain planned. |
| [PEAQ / GstPEAQ](https://github.com/HSU-ANT/gstpeaq) | Established reference-audio approach. The available plugin explicitly says it does not meet the test tolerances of BS.1387-1. [Current ITU recommendation](https://www.itu.int/rec/R-REC-BS.1387-2-202305-I/en) is revision 2. | Optional laboratory comparator, not the initial production dependency. Do not describe this plugin as a conforming current-standard implementation. |

Zimtohrli's [published comparison](https://github.com/google/zimtohrli/blob/main/CORRELATION.md)
favours it over ViSQOL on several datasets, including ODAQ, but ViSQOL leads on SiSEC08.
These are upstream results, not results on our libraries. Zimtohrli's temporal warping also
means a good perceptual score must never override our independent timing/tail checks.

## Packaging and licensing

ViSQOL and Zimtohrli use Apache-2.0 source licences; SSIMULACRA2 uses BSD-3-Clause.
Primary licence files: [ViSQOL](https://github.com/google/visqol/blob/master/LICENSE),
[Zimtohrli](https://github.com/google/zimtohrli/blob/main/LICENSE),
[SSIMULACRA2](https://github.com/cloudinary/ssimulacra2/blob/main/LICENSE).
These make the shortlist practical for an open-source project. Before distribution,
audit the exact dependency/model artefacts, retain notices and matching source where
required, pin revisions and hashes, and build without runtime downloads. This is not
certification of bundles that have not been built.

The source/build manifests are authoritative for shipped tools. The original research starting points were:

- ViSQOL revision `38d0b0163e441047d4429bf07ad09e5b9031d02c`.
- Zimtohrli revision `f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3`.
- libjxl `v0.12.0` contains the SSIMULACRA2 tool; freeze and record the metric revision too.

## Delivered slices

- [#343](https://github.com/Jellman86/optimisarr/pull/343): opt-in sampled audio reports,
  native tools and strict worker measurement.
- [#345](https://github.com/Jellman86/optimisarr/pull/345): separate explicit maximum-distance
  gate; unavailable or incomplete required evidence blocks replacement.
- [#346](https://github.com/Jellman86/optimisarr/pull/346): codec-aware bitrate presets,
  independent of the metric threshold.
- [#347](https://github.com/Jellman86/optimisarr/pull/347): sampled assessment of retained,
  re-encoded video soundtracks, with frozen track mapping and separate controls.
- [#344](https://github.com/Jellman86/optimisarr/pull/344): manual read-only exact-copy
  review for every library type; broader matching and cleanup remain planned.

- [#395](https://github.com/Jellman86/optimisarr/pull/395): experimental bounded SDR still-image
  reports and explicit SSIMULACRA2 gates, with [qualification and limits](../engineering/hardware-validation/2026-10-10-image-perceptual-quality.md).

These slices are implemented in development. They do not complete broader image coverage,
multichannel audio qualification, automatic bitrate search or perceptual dedupe. The
[configuration guide](../setup/configuration.md#audio-quality-reports-and-gates-development)
is the maintained source for user-facing coverage, cost and limits.

## Implementation sequence

This sequence records the original plan and the qualification still required for new
coverage. Implemented mono/stereo audio reporting and explicit gates are listed above;
do not repeat them as undelivered work.

1. **Qualify the selected tools.** Build isolated Zimtohrli and SSIMULACRA2 tools on this Mac, PICARD and Quark,
   and run the published container on the authorised disposable host. Exercise Riker's
   supported CPU path through its documented deployment/test route. Validate Zimtohrli
   on a small attributed free-media set: speech, music, mixed soundtracks, silence and
   stereo/5.1 channel fixtures, with Opus/AAC/MP3 at several bitrates. Compare image metrics
   on JPEG/WebP, text/edges, photographs, gradients, colour and alpha fixtures. Include
   identities and deliberate degradation. Record disagreement, runtime, peak memory and
   platform parity. ViSQOL may help explain disagreements in offline research, but choosing
   between two production metrics is no longer a prerequisite. A packaging or coverage
   failure is an explicit blocker to fix, not permission to silently substitute an older metric.
2. **Add measurement-only providers using TDD.** Pure command builders, bounded parsers and
   policy evaluation; cancellable native execution with captured logs. Return metric name,
   version/model hash, raw units, window/channel coverage and failure reason. Persist
   evidence through additive, idempotent migrations. No VMAF-to-audio or SSIM-to-SSIMULACRA2
   numeric conversion: their scales mean different things.
3. **Preserve the actual signal.** Audio compares corresponding retained tracks/channels
   over several active windows. Decode both sides consistently to the required sample
   rate for measurement; keep original channel/sample-rate checks. Bound codec-delay
   alignment and retain independent timing, silence/dropout, loudness and clipping checks.
   A mono mix cannot pass for surround evidence. Validate channel swaps, polarity changes,
   LFE-only content, padding and missing channels; unsupported coverage is explicit, not
   a fabricated quality score. Lossless PCM identity remains stronger evidence where applicable.
   Images use one tested colour-managed reference/candidate preparation path at the intended
   size. Check ICC handling, orientation, alpha over light/dark backgrounds and transparent
   pixels separately. HDR, animation and unsupported depth stay outside an unproved SDR gate.
4. **Run identically on workers.** Version the protocol and negotiate the exact metric,
   model and supported coverage using real capability probes. Bind evidence to the lease,
   source/candidate hashes and assigned policy. Strict sidecar-only mode refuses a worker
   that cannot complete a required check; it never silently adds server measurement.
   Enforce scratch/RAM budgets, concurrency, timeouts, cancellation and cleanup.
5. **Expose optional reporting, then optional gates.** Start with an explicit per-library
   measurement-only setting, off by default. Show metric/version, coverage, quality result
   and limitations with explanatory tooltips in the established settings/queue style.
   Keep saved SSIM choices and existing safety checks. Review scores against representative
   encodes and deliberate damage before offering a named preset or custom threshold.
   No universal “transparent” promise or invented default floor; diagnose unmeasured versus
   measured-and-failed separately. A configured gate fails closed if evidence is missing.

## Evidence needed to call it complete

- Tests fail first for wrong policy/hash/model, malformed/non-finite output, partial coverage,
  unsupported channels/formats and unsafe reference preparation; automatic retries retain policy.
- Actual good/bad candidates, cancellation, reconnect, resource exhaustion, exact rollback and
  concurrent isolation pass on all available machines and final server/worker images. Older
  workers remain compatible with jobs that do not require the new metrics.
- Windows installer and Mac/Linux bundles include pinned tools, offline models, provenance and
  required notices; capabilities prove real measurements, not executable/filter presence alone.
- A second-order review demonstrates that sampling, alignment, colour conversion or mono
  aggregation cannot hide dropped media, clipping, channel damage or transparency loss.
- Documentation and UI distinguish reporting from an enforced gate. Existing installations
  retain their settings; no roadmap entry claims this capability is already available.

## Pipeline, settings and cost

Measure in the existing verification stage after encoding and before a candidate becomes
ready for replacement. Run the existing structural/decode checks first. Cover standalone
audio and re-encoded audio tracks inside video; identify each reference/output track explicitly.
Proved decoded-PCM identity can avoid a perceptual calculation for unchanged audio, while
timing, channel and stream checks still apply. Reuse preparation only when its hash, track,
window, decoder and preparation policy match. Keep bounded scratch data within the job.

Workers prepare and measure their local source/candidate pair before delivery, returning
hash-bound evidence. The server validates that evidence and owns the replacement decision.
Confirm coverage for each media kind in every implementation: an existing audio/video
capability does not prove image measurement. CPU measurement may follow GPU encoding;
show this clearly in the queue and sidecar monitor. Use the existing non-video/evidence
lane budgets and library placement rules. Benchmark overlap before increasing concurrency.

| Setting level | Proposed control | Behaviour |
| --- | --- | --- |
| Library, ordinary controls | Perceptual quality: Off / Report / Require | Off initially. Report records scores and measurement errors without introducing a new blocking gate; existing gates still decide replacement. Require is offered only after qualification and refuses missing evidence. |
| Library, advanced controls | Metric-specific limit and coverage | Audio exposes a maximum distance; images expose a minimum score. Record covered channels/windows and any omissions. Sampled evidence must be labelled sampled. Preserve saved SSIM settings. |
| Server, work settings | Placement, measurement concurrency, RAM/scratch/time budgets | One policy governs container and workers. Strict worker-only placement never falls back to server measurement. Capacity exhaustion waits; a failed required measurement holds/fails the job with its reason. |
| Worker monitor | Capability, progress and resource availability | Report supported metrics/coverage and the current stage. Worker UI cannot weaken the assigned library policy. |

Zimtohrli works at 48 kHz; its documented distance runs from zero for identity towards one
for strong differences. Upstream reports about 70 seconds of audio compared per second on
one 2.5 GHz core. This is an upstream performance claim, not a measurement on our machines.
Decoding, channel count, preparation and scratch I/O add cost. [Zimtohrli documentation](https://github.com/google/zimtohrli).

Memory estimates from raw buffer sizes, excluding tool features and decoder overhead:

- A 30-second float32 stereo buffer at 48 kHz is 11.52 MB; a reference/candidate pair is
  23.04 MB. Six channels need 69.12 MB for the pair. Full-track buffers would grow with duration.
- A 24-megapixel float32 RGB image is 288 MB; two are 576 MB before multiscale features,
  alpha, decoder buffers and colour conversion. Bound dimensions and concurrent images.
- SSIMULACRA2 uses six image scales and returns scores up to 100, with negative scores
  possible. Keep its raw units and reject malformed/non-finite output; never clamp it to
  VMAF's range. [SSIMULACRA2 documentation](https://github.com/cloudinary/ssimulacra2).

These are sizing calculations, not measured peak RAM. Record wall time, CPU time, peak RAM,
scratch bytes and extra transfer bytes separately on Mac, PICARD, Quark and Riker/container.
Compare encoding with and without measurement under idle and concurrent load. Start with
audio provider qualification, then image qualification using the same evidence contract.
Neither metric requires a hosted service or per-measurement fee; local compute, packaging
and storage costs remain. Defer automatic quality-search retries until that cost is known.

### First development slice: audio qualification tool

The [AudioStudy CLI](../../tools/Optimisarr.AudioStudy/Optimisarr.AudioStudy.csproj) now calls a
reusable [assessment provider](../../src/Optimisarr.Core/Verification/AudioQualityService.cs).
A [small native wrapper](../../tools/audio-quality-native/CMakeLists.txt) builds the pinned
Zimtohrli core and its exact Highway submodule revision, without the upstream ViSQOL adapter
or a Python/ML production runtime. The native wrapper uses the core's default 78.3 dB
reference setting, rather than the upstream compare CLI's different default setting.

```bash
cmake -S tools/audio-quality-native -B /tmp/optimisarr-audio-native -DCMAKE_BUILD_TYPE=Release
cmake --build /tmp/optimisarr-audio-native --config Release --parallel 2
ctest --test-dir /tmp/optimisarr-audio-native -C Release --output-on-failure
dotnet run --project tools/Optimisarr.AudioStudy -c Release -- \
  --reference ./reference.wav --candidate ./candidate.opus \
  --ffmpeg /path/to/ffmpeg --ffprobe /path/to/ffprobe \
  --metric /tmp/optimisarr-audio-native/optimisarr-audio-quality \
  --report ./new-audio-report.json
```

On a multi-configuration Windows build, the executable is under `Release` and ends in
`.exe`. Pass absolute executable paths. The report path must be new; originals, tools and
existing evidence are never overwritten. Build dependencies need network access once;
measurement itself is offline. `cmake --install` retains the upstream tool notices.

The first provider supports a single mono/stereo audio track, including audio with attached
cover art. Video, multiple audio tracks, surround and unknown channel layouts are explicit
unsupported cases for this slice. Resample both files to 48 kHz float PCM without downmixing
or gain matching. Measure up to three disjoint windows, each at most 30 seconds: cover tracks
up to 90 seconds, and sample beginning/middle/end for longer tracks. Report covered seconds,
duration drift, raw per-channel distances, worst channel distance, metric/preparation revisions,
input/tool hashes and elapsed time. A 100 ms duration tolerance allows qualification of normal
codec padding; it is not a new production duration policy or permission to accept lost audio.
No manual delay correction or calibrated quality verdict is provided yet.

Each native call verifies PCM shape, equal frame counts, finite samples in the metric's
documented range and complete per-channel output. Preparation has byte limits, process
deadlines and cancellation; concurrent runs own separate scratch directories. Changed inputs
or tools invalidate results. Reports explicitly say no quality gate was evaluated and no
replacement was authorized. The tool never reads a production database or creates a job.

Initial real Mac qualification covers freely usable music encoded with AAC/MP3 at 128 kbps,
Opus at 160/16 kbps, music/speech identities, a stereo swap and deliberate truncation. Identities
return zero; low-rate Opus has greater distance than high-rate Opus; truncation is refused.
The 19-second music assessments took roughly 0.8 to 0.9 seconds each in this small local run,
including preparation and hashes. These timings and relative scores are fixture observations,
not a throughput guarantee or calibrated quality threshold. Quark's native tests pass; the same
decoded Opus/swap fixtures match Mac distances to within about `6e-8`.

A Quark-built binary initially failed on Riker because its newer glibc/libstdc++ dependencies
were unavailable there. This demonstrates why a build on one Linux host is insufficient.
The qualification workflow now passes on Ubuntu 22.04, Mac and Windows. Its Linux artefact
runs on Riker's Debian host and inside the live Riker server and Quark sidecar containers.
The Windows artefact and CLI run on PICARD with its installed media tools. Music identities,
AAC/MP3 at 128 kbps, Opus at 160/16 kbps, stereo swaps and rejected truncation pass these
small runtime checks across all four machines. Digests and full reports are retained privately.
This qualifies the tested reporting cases; it does not establish surround, full-length library
coverage, gating thresholds or worker-protocol support. Windows warm runs were about 1.4 seconds
for the music cases, with the first identity run taking about 14.7 seconds; cold and warm costs
must be distinguished in later benchmarks. Test fixture areas inside the containers were removed
after collecting reports. Native tools are not bundled into production packages in this slice.

### Fixture preparation recorded on 2 October 2026

Downloaded [Samplelib WAV fixtures](https://samplelib.com/sample-wav.html) under its
[free-use licence](https://samplelib.com/license.html), retaining the source/license pages,
SHA-256 hashes and FFprobe results privately. Actual probed durations:

- `sample-15s.wav`: 19.174 seconds, stereo music.
- `sample-speech-1m.wav`: 60 seconds, mono speech.
- `sample-3s-stereo.wav`: 3 seconds, distinct left/right tones.

Also copied eight JPEGs from the operator's Immich library on Riker into private local
test storage. Source files were read only; private paths/photos stay outside Git and public
reports. This is a starting set, not representative coverage or a completed metric benchmark.
Add lawful longer music/mixed recordings and generated silence/channel controls, plus
public synthetic gradients/text/alpha and colour-managed fixtures. Produce known-good and
deliberately degraded candidates, including swaps, truncation, clipping and low bitrates.

## Duplicate detection for all library types

Status: **manual read-only exact-copy review is implemented in development; persistent
indexing, broader matching and cleanup remain planned**. The implemented review covers
image, audio, video and mixed libraries. Quality measurements compare a known source with its encode;
duplicate detection first needs to establish whether two independently discovered files
contain matching media. A quality score alone cannot establish that relationship.

### Delivery sequence and researched candidates

1. **Exact copies across every type.** Filter by file size and calculate full-file SHA-256
   where needed; reuse existing hashes only while their file identities remain valid.
   Cache incrementally with algorithm/version, file identity and scan time, through additive
   migrations. Recheck both files before a cleanup action. Treat symlinks, hard links and
   overlapping library paths as references to investigate; they may offer no recoverable space.
2. **Image candidates.** Qualify [PDQ](https://github.com/facebook/ThreatExchange/blob/main/pdq/README.md)
   using one consistent decoder, orientation and colour preparation. It includes a feature
   quality indicator, useful for rejecting unreliable blank/low-detail comparisons. Test its
   rotation variants rather than assuming a canonical hash is rotation invariant. Treat
   upstream distance examples as research starting points, never deletion thresholds.
   For harder resized/cropped/edited candidates, evaluate optional local
   [SigLIP 2 image features](https://huggingface.co/google/siglip2-base-patch16-224), followed
   by correspondence checks and human review. Its retrieval capability is established;
   its fitness for our dedupe cases still needs measurement. Related scenes and photo bursts
   remain distinct choices. Protect RAW/JPEG, Live Photo companions, alpha, animation,
   ICC/EXIF and edits. A larger file is insufficient evidence that it is the best version.
3. **Audio candidates.** Qualify [Chromaprint](https://github.com/acoustid/chromaprint)
   locally for near-identical recordings across encodes. It is designed for this narrower
   task and trades precision/robustness for speed. Confirm duration, aligned content,
   channels/tracks and tags; protect remasters, live recordings, alternate mixes and album
   versions. Short speech, silence and sound effects need explicit qualification. Use full
   content coverage before any cleanup recommendation; a shared excerpt cannot prove identity.
4. **Video candidates.** Evaluate [TMK+PDQF and vPDQ](https://github.com/facebook/ThreatExchange/blob/main/vpdq/README.md).
   TMK targets same-length content; vPDQ can retrieve clips/subsequences. Its reference
   comparison treats frames as an unordered collection, so add ordered timeline checks
   before suggesting whole-file duplicates. Compare audio tracks, languages, subtitles,
   duration, cuts and HDR/SDR characteristics. Shared intros, reordered scenes, trailers,
   matching artwork and partial clips must not approve removal of a complete edition.
5. **Review, then safe actions.** Use previews, side-by-side metadata/stream differences,
   paths, confidence reasons and estimated recoverable space. Support keeping several
   versions and remembering ignored pairs. Check every proposed removal against its chosen
   retained file: similarity is not transitive, so an A/B match and B/C match cannot prove A/C.
   Validate the retained copy, record rollback before quarantine and recheck identities
   under the replacement/action lock. Read-only libraries allow detection only. Connected
   Immich/media-manager references, albums, tags and sidecars need supported reconciliation
   before removal; block the action when it cannot preserve those relationships. Never
   overwrite a manager's database directly. Automatic deletion and hard-link conversion
   are outside the first delivery.

[Immich's duplicate review](https://docs.immich.app/features/duplicates-utility/) is a useful
UI reference: it finds visually similar assets, lets users keep several, and preserves
selected metadata during resolution. Any Optimisarr integration needs its own tested API
contract. Detection in an external library does not grant permission to delete its files.

### Settings, costs and qualification

Default to off. Offer per-library **Exact copies** and later **Possible matches**, plus an
explicit cross-library scope, scan schedule and review screen. Keep sensitivity and resource
limits in Advanced with clear tooltips. Detection runs as low-priority cancellable background
work with existing placement/lane limits; it never holds an encoding job waiting for a match.
Strict worker placement applies to expensive fingerprint/feature generation too. Content
and features stay local; do not require cloud uploads or a new database service.

Exact hashing is principally an I/O cost. A first scan reads the selected bytes; incremental
scans reuse validated identities. PDQ stores compact hashes, while learned features add model
download/bundle size, inference and index costs. Compare a native hash-only baseline with the
optional image encoder on CPU and available GPU hardware before selecting its export/runtime.
For scale planning, 100,000 vectors of 768 float32 values occupy 307.2 MB before any index or
database overhead; this is an example sizing assumption. Avoid all-pairs comparisons as a
default. Video decoding and fingerprint storage grow with duration and sampling density;
candidate filtering must expose its coverage limits. Measure end-to-end wall time, disk reads,
peak RAM, index size, candidate recall and false positives on labelled tests, including large
incremental rescans. Do not copy performance numbers from another product into our defaults.

Audit exact packaged artefacts before shipping: PDQ/vPDQ's project has a
[BSD licence](https://github.com/facebook/ThreatExchange/blob/main/LICENSE), SigLIP 2's model
card lists Apache-2.0, and [Chromaprint's licence](https://github.com/acoustid/chromaprint/blob/master/LICENSE.md)
includes LGPL-2.1 considerations for incorporated FFmpeg code and external FFT libraries.
Pin revisions, model hashes, preparation versions and notices; invalidate derived indexes
when any relevant preparation/model changes.

Test first for changed files, collision candidates, low-detail images, bursts, edited crops,
track/channel loss, partial clips, reordered scenes, similarity chains, Unicode paths,
hard links, symlinks, read-only mounts, duplicate library paths and concurrent encode/replace.
Prove ignored-pair persistence, cancellation, disconnect/retry, restart recovery,
cross-filesystem quarantine and exact rollback on available platforms. Private Immich copies
can qualify image behaviour; public CI uses attributed free or synthetic fixtures. Publish
aggregate results and synthetic examples. No private media or paths enter documentation.

## Integrated report controls in development

Enable **Audio quality report** under **Libraries → Configure → Verify** for a music library
(or a mixed library containing standalone audio). It defaults off. The advanced verification
view shares the same choice; breadcrumb navigation retains the draft. Queue job details and
Quarantine show the same report panel, with the largest distance per channel across samples,
covered seconds and whether the measurement ran on the server or worker.

Report-only mode is experimental. When the explicit audio gate is off, reports do not enforce replacement,
change retries or override decode, duration, channel, metadata or size checks. Originals still
require all configured gates before replacement, and replacement retains the quarantine rollback
path. Missing tools, incompatible input and incomplete worker evidence are displayed as
unavailable; these observations never become a pass verdict.

Strict sidecar verification requests the observation in the frozen audio contract. Both worker
implementations measure before delivery using the same pin and 48 kHz preparation. The server
validates hashes, profiles, sample positions, complete channel counts and finite distances, then
stores the observation separately from verification checks. Missing or incompatible reports
never trigger server media reads. Older workers can complete their existing verification and
return an unavailable report until upgraded. In server verification mode, the server measures.

The opt-in cost includes hashing both complete media files before and after assessment,
resampling two streams and scoring at most three serial windows covering up to 90 seconds.
Each command has a 90-second deadline (probes use 30 seconds in the shared provider). Scratch
is at most two 11.52 MB PCM files per assessment and is removed on success, failure and
cancellation. Concurrent jobs share the existing verification/worker slots. Turning reporting
off adds no audio assessment processes, hashes or scratch files.

Packages build the pinned metric core without Python at runtime. Python, CMake 3.27 or newer,
Git and a C++17 compiler are build dependencies. Mac packaging now signs the metric alongside
FFmpeg and ffprobe. Windows packaging includes it in the MSI; Docker builds include it in both
runtime targets. Sidecar corresponding-source archives include Zimtohrli and Highway.
`OPTIMISARR_AUDIO_QUALITY` can name an explicit native executable. It is an operator override,
not a UI setting; unproved metric revisions are refused.

Real pipeline acceptance passed with freely reusable Samplelib music on this Mac, PICARD,
Quark's running sidecar and Riker's running container. Evidence was consumed with an injected
server fallback that would throw if invoked. All scratch was removed and both containers
remained healthy with zero restarts. The native Swift path also passed using the installed
Mac media tools and the new metric. These were isolated test executables, not a production
upgrade or a claim that the deployed 0.2.19 jobs already record these reports.


### Explicit audio gate in development

The report-only integration now also supports an opt-in, per-library Zimtohrli gate. It starts
off and requires an explicit finite maximum distance between 0 and 1; there is no preset or
calibrated threshold. Every validated channel/sample distance must meet the inclusive limit.
Missing or unsupported measurements fail the gate, and strict worker jobs do not fall back to
server assessment. Gate-enabled jobs request measurement independently of the report toggle.
Historical report-only jobs keep their original verdict. Coverage remains limited to one
mono/stereo track and up to 90 assessed seconds. See the
[controls and limits](../setup/configuration.md#audio-quality-reports-and-gates-development).
A universal threshold, full-duration assessment, surround support and audio quality search
remain future work. This operator-selected gate does not claim an inaudibility guarantee.

### Audio preset design review, 2 October 2026

The horizontal gate control is implemented. The prototype that named its numeric limits as
quality tiers was rejected during design review. The replacement slice uses codec-aware encoding
presets that change actual bitrate, with the experimental difference limit in Advanced
verification. Changing a verification limit alone does not change the encoded output.

The freely licensed speech acceptance cases provide a concrete counterexample to simply naming
the existing numeric points: deliberately silent mono output and a stereo output with its right
channel silenced both measured approximately `0.0645978451` on Mac, Windows, Linux sidecar and
server-container tools. Their other configured checks passed. A maximum of `0.1` would approve
those damaged candidates. It must not be presented as an endorsed quality preset. The tested
gate limit `0.01` blocked them; this fixture result does not establish a universal threshold.
The [gate acceptance in PR #345](https://github.com/Jellman86/optimisarr/pull/345)
records those good/bad, worker/server and cleanup checks. The separate
[encoding preset evidence](../reviews/evidence/2026-10-02-audio-encoding-presets.json)
records each proposed codec/bitrate/channel combination using installed tools; those encode,
probe and decode checks do not establish a perceptual-quality threshold.

Design and remaining pipeline work:

1. Keep output codec/container selection as a playback-compatibility choice.
2. Give quality presets encoder-appropriate starting settings. An optional bounded sample
   search can choose a bitrate that meets the selected target before the full encode.
3. Retain the final quality gate and the existing decode, duration, channel, metadata and
   replacement checks. Reports expose the actual selected limit and sample coverage.
4. Keep exact distance limits in advanced controls, with no automatic reinterpretation of
   existing saved policies.

The metric compares decoded audio, so AAC, Opus and MP3 can use the same assessment preparation;
their settings and the bitrate needed to meet a target differ. A future sample search must
measure actual candidates rather than assume a bitrate produces the same quality in each codec.

The encoding control offers Space saver, Balanced, High and Very high, plus Default and Custom.
The [configuration reference](../setup/configuration.md#audio-quality-reports-and-gates-development)
records each codec's bitrate. These are explicit starting points, without cross-codec quality
equivalence or a claimed listening grade. Existing saved bitrates and gate limits stay unchanged.
An explicitly chosen tier follows codec changes during the current edit; after saving, the
stored policy remains codec and bitrate. Automatic sample search is future work.

### Video soundtrack reports and gate in development

The shared metric now supports explicitly selected audio streams in video containers. The
standalone parser still rejects moving video and multiple audio tracks; the video path uses
its own planner and separate reporting, gate and maximum-distance settings. Both switches
start off. The gate requires an explicit finite limit from 0 to 1 and requests measurement
independently of reporting. Configuration exports use format 3; older imports and update
requests preserve omitted soundtrack choices. See the
[operator controls](../setup/configuration.md#assess-re-encoded-video-soundtracks).

The frozen encode's removed source audio indexes determine the mapping. Each retained source
track maps to the next output audio position, and every retained track must be present. The
planner checks canonical language, exact title and commentary disposition, compatible duration
and channel layout; it refuses reordered, missing or extra tracks, invalid removals and more
than eight retained tracks. Mono/stereo tracks are supported. Surround, including an intentional
surround downmix, remains unavailable rather than being compared with an untransformed reference.

Start-time differences over 50 ms and duration differences over 100 ms are refused. PCM
preparation uses the picture/container lead (bounded to 0–100 ms) and the versioned
`audio-f32le-48k-video-timeline-v1` preparation. This accounts for bounded container priming;
it is not perceptual alignment that may erase a timing error, and does not establish lip-sync
quality. Existing independent video timing, retention, decode, metadata, loudness/clipping,
size and VMAF checks still apply. Copied audio, remuxes and previews skip soundtrack observation
and its gate. No automatic bitrate search or quality retry is added.

Each retained track gets its own channel measurements and up to 90 seconds of coverage. Tracks
up to 90 seconds use the complete duration in at most three windows; longer tracks use 30 seconds
at the start, middle and end. The server recomputes the largest distance from every validated
channel/window; all tracks must meet the inclusive selected limit. Missing tools, unsupported
profiles, invalid mapping, changed files or incomplete evidence cannot pass a configured gate.
Report-only mode shows these limits without changing replacement decisions. This is sampled
experimental evidence, without a calibrated listening grade or quality guarantee.

Strict worker jobs require protocol 8 and full-verification contract 3, which freezes the removal
mapping. Mac and shared Windows/Linux workers measure before delivery. The server validates the
contract, file/tool hashes, metric pin and preparation, exact track mapping/profiles/indexes,
complete windows/channel counts and finite distances. It computes the verdict against the frozen
policy rather than trusting a worker verdict. Older workers cannot claim a soundtrack-assessment
contract, and missing evidence never triggers server media analysis. Server verification mode
measures on the server.

Cost scales with retained tracks: up to 24 serial metric windows across eight tracks, two bounded
PCM files at a time (at most 11.52 MB each), and full source/candidate and tool hashes before and
after each track's measurement. Shared-provider probes have 30-second deadlines and decode/metric
commands have 90-second deadlines; these are command limits, not a total-job runtime guarantee.
Scratch is removed on success, failure and cancellation. Turning both controls off or copying
audio adds no soundtrack assessment processes, hashes or PCM scratch. Existing verification
slots bound concurrency. These implementation and unit-test checks do not replace final package,
real-media or cross-platform acceptance evidence.

Every configured gate must pass before replacement. A failure leaves the original untouched;
a successful replacement records rollback and quarantines the original before installing the
verified output. Quarantine is not a backup, and purge removes rollback. Dry-run permits
assessment while blocking replacement and purge.

## Experimental image integration, 10 October 2026

The native wrapper pins libjxl v0.12.0 at `a7a9c787341cf703dede03c2009fa460cae5e5df`
and WebP v1.6.0 at `b7e29b9d75bd31422b00c2a446d49d7af06c328d`. Exact selected
submodule revisions and licences ship alongside the helper and in sidecar source archives.
See [build and qualification](../../tools/image-quality-native/README.md) and
[operator coverage](../setup/configuration.md#perceptual-image-quality-reports-and-gates-development).

This is a bounded first slice, without a calibrated default or a claim that its scores prove
visually lossless output. Synthetic fixtures test identity, damage, JPEG/WebP decoding,
embedded sRGB, alpha backgrounds and independent alpha comparison, malformed/incomplete ICC,
orientation, unsupported depth/animation and mismatched dimensions. Server integration keeps
existing gates, records file/tool hashes, serializes full-resolution measurement and fails
closed. Image sidecar placement is not implemented by this work.
