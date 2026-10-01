# Decision: collect VMAF v1 observations before changing replacement gates

Accepted: 27 September 2026. Related: [model study](vmaf-model-study.md), [PR #114](https://github.com/Jellman86/optimisarr/pull/114).

The [1 October practical migration and NVIDIA plan](vmaf-v1-and-nvidia-plan.md) proposes starting
with existing numerical floors, automated validation and targeted anomaly review. It does not
require a subjective calibration study or legacy-verdict agreement, and does not yet change
production behaviour or enable research. The original conditions below record the September
decision; implementation planning now follows the simplified migration plan.

## Decision

Keep VMAF v0.6.1 as the authority for adaptive encoding, quality retries and replacement
verification. Collect paired v0/v1 measurements with an explicitly enabled server research mode.
V1 has no production pass/fail threshold yet. Research scores never enter verification inputs,
and a research failure cannot promote or reject a replacement.

The first study completed 84 paired windows from eight sources across seven independent titles
and four encoders. Changes depended on the material: animation rose around four to five points,
one HD live-action source fell around six, and one UHD source fell around fifteen. Reusing the
existing gates changed nine window verdicts. Fitted HD thresholds also accepted legacy failures
when tested on titles excluded from fitting. UHD had only one independent title.

This rejects a blanket threshold conversion. It does not establish that v1 is less accurate:
agreement with v0 is a regression check, not perceptual ground truth. A separate same-clip CPU
check on macOS, Windows and Linux agreed within 0.000004 points, so the evidence does not point
to an endpoint-specific scoring defect.

## Operation and cost

Set **`OPTIMISARR_VMAF_SHADOW_SERVER=1` on the main container** to opt in. Any other value, or
an absent variable, disables research. Remove it and redeploy to stop collecting new observations.
Previously recorded observations remain in job history. Public releases leave this disabled.

The server evaluates normal verification first, then performs optional research while the source
and delivered candidate still exist. There is no second encode and no additional retained media
copy. Up to three ten-second windows are scored with both pinned models, on CPU, every frame.
The candidate model uses 10-bit SDR and the actual candidate's dimensions and bit depth for CAMBI.
Both models use the baseline's alignment; matching positive frame counts and finite metrics are
required for a complete pair. These short research windows are separate from the authoritative
verification sampling and must not be presented as full-file quality results.

One research operation runs at a time per server. A busy observer records a skip instead of
queuing. Each operation has a two-minute wall-clock budget, including alignment probes and both
models; this can add up to two minutes to that job's finalisation. Child processes are cancelled
when that budget expires or the job is cancelled. Missing models, unreadable files, mismatched
frames and incomplete pairs are recorded explicitly. Completed pairs survive a later timeout.

**This opt-in adds server CPU decoding even when “Verify entirely on the sidecar” is enabled.**
That setting still governs all authoritative verification: missing worker evidence fails as before,
and research is never its fallback. The research location is recorded separately as `Server`.
No sidecar protocol or installer upgrade is needed for this collection mode. Keep the variable
unset when the server must not perform these additional research measurements.

The initial scope is re-encoded SDR video with a measured authoritative VMAF result, known source
frame rate below 45 fps and an 8/10-bit candidate. HDR, tone mapping, frame-rate conversion,
disposable previews, missing probes and failed candidate decodes are skipped. Reference-native
display dimensions are preserved; changing SD/720p display upscaling is a separate experiment.

## Retained evidence

Each job's existing `verificationReportJson` gains a separate `shadowVmaf` object. There is no
database migration. It records status (`Measured`, `Skipped`, `Unavailable` or `TimedOut`), the
reason, model identities, time, server placement, measurement policy, source/candidate preparation,
window positions, alignment and per-model scores/frame counts. The existing `vmaf`, `checks` and
`context` keep their current meaning. Research is exposed in the report JSON, not as another
green/red verification badge or a user-facing v1 quality setting.

Save the authenticated `GET /api/jobs?pageSize=0` response (all matching jobs), then export it
before normal job-history pruning:

```sh
python3 scripts/export_vmaf_shadow.py jobs.json shadow-observations.jsonl
```

The export retains skipped and incomplete observations and refuses to overwrite an existing file.
It omits the normal job path/name fields, but measurement errors can still contain local paths;
review exports before sharing them. It is an observational dataset, not the reproducible media
archive produced by the dedicated study tool. Source IDs help group observations within one
installation; repeated encodes and episodes of one title are not independent calibration titles.

## What must happen before switching models

1. Collect broader independent HD/UHD titles and enough near-boundary encodes. Record skipped
   coverage; a small set of successful measurements is not a complete fleet validation.
2. Compare explicitly chosen display-scaling policies, especially for SD/720p.
3. Investigate disagreements in alignment, preprocessing, banding and actual picture quality.
4. Validate proposed model-specific gates on titles excluded from fitting, and document the
   quality/false-rejection tradeoff. Do not silently fit each title to reproduce its old verdict.
5. Approve a separate, staged SDR rollout with rollback. HDR, HFR and accelerated v1 scoring
   require their own evidence. There is no automatic cutover or threshold update in this feature.

## Release position

The final-container and paired Linux RAM acceptance gates run with `--vmaf-shadow`. They require
complete, matching research pairs on their sequential video cases, including deliberately rejected
encodes, while independently checking that the baseline still controls verification and rollback.
The normal harness remains usable with research disabled; partial research coverage fails an
explicit shadow acceptance run even though it cannot fail a production replacement gate.

The sidecar hardening and Linux worker can be released independently of v1 calibration. Describe
this addition as optional research collection; do not claim that production has migrated to v1.
The next coordinated release must still pass the [release checklist](releasing.md): exact-tag CI
and images, notarised Mac assets, Windows installation/paired-upgrade evidence, and matching
media-tool source packages. Windows remains an unsigned preview unless its signing status changes.
