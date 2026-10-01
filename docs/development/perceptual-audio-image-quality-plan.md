# Perceptual quality for audio and still images

Researched **1 October 2026**. Status: **planned; no new metric or gate is shipped**.
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
