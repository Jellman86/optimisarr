# Perceptual quality for audio and still images

Researched **1 October 2026**, extended **2 October 2026** with pipeline, cost and dedupe plans.
Status: **audio qualification tool implemented on a development branch; library gates,
worker integration and dedupe remain planned**.
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
Qualify Zimtohrli's native packaging, coverage and thresholds before enforcing a gate;
ViSQOL remains an optional offline comparison tool, not a prerequisite for choosing the
production metric. This direction is not a claim that one metric always predicts human
perception best. No new listening-panel programme is a prerequisite.

| Candidate | Fit for Optimisarr | Decision and limits |
| --- | --- | --- |
| [SSIMULACRA2](https://github.com/cloudinary/ssimulacra2) | Native image-compression metric; explicitly measures blur and ringing/blocking. Its author's held-out CID22 results favour it over plain SSIM and Butteraugli. Available in libjxl tools. | Preferred for SDR stills. The author's benchmark is useful evidence, not an independent Optimisarr evaluation. Colour, alpha and metadata safety need separate tests. |
| [Butteraugli](https://github.com/google/butteraugli) | Native perceptual distance with a spatial error map. Its original documentation emphasises barely noticeable differences. | Useful comparison baseline; SSIMULACRA2 better matches a user-facing compression-quality score. Reconsider if local near-lossless evidence favours it. |
| [LPIPS](https://github.com/richzhang/PerceptualSimilarity) | Learned perceptual patch distance using PyTorch and pretrained networks. | Research comparator if needed; the additional runtime/weights are not justified for the first native worker implementation. This is a packaging judgement, not an accuracy claim. |
| [ViSQOL](https://github.com/google/visqol) | Established full-reference general-audio mode, with Linux/Mac build instructions and experimental Windows instructions. | Optional offline comparator only. Audio mode uses 48 kHz and downmixes to mono; one score cannot certify surround preservation. It is not a required shipped dependency or runtime fallback. |
| [Zimtohrli](https://github.com/google/zimtohrli) | Newer native psychoacoustic metric aimed at high-quality audio compression and just-noticeable differences. | Selected audio implementation. Upstream documents Debian-like testing; native Windows/Mac packaging and performance must still be proved before distribution or gating. |
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

Research starting points, not committed production tool pins:

- ViSQOL revision `38d0b0163e441047d4429bf07ad09e5b9031d02c`.
- Zimtohrli revision `f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3`.
- libjxl `v0.12.0` contains the SSIMULACRA2 tool; freeze and record the metric revision too.

## Implementation sequence

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
The qualification workflow targets an older Ubuntu baseline and includes Mac/Windows native
builds. Test those exact artefacts on the actual hosts before claiming fleet qualification.
Native tools are not bundled into the production server or installers in this slice.

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

Status: **roadmap research; no dedupe implementation is shipped**. Cover image, audio,
video and mixed libraries. Quality measurements compare a known source with its encode;
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
