# Sampled VMAF frame-number pairing: 26 September 2026

This is a **historical record of 0.2.15 diagnosis and 0.2.16 retries**, recovered from
[#296](https://github.com/Jellman86/optimisarr/pull/296). Current code subsequently added
mandatory decoded picture counts and full-file repeated-timestamp handling. Use the
[current handoff](../../development/sidecar-hardening-handoff.md) before applying a fix.

## Environment and observations

Retained sources and failed candidates were copied read-only from the Mac worker.
The scratch runner called Optimisarr's `QualityScoreService` using the installed
FFmpeg/ffprobe tools, libvmaf 3.2.1, the v0.6.1 model and three 40-second windows.
Production retries used the supported API. No gate was lowered or database row edited.

Jobs 5692 and 5861 showed very low sampled scores after earlier alignment fixes.
In job 5692, source and candidate each contained 34,046 video packets. Presentation
timestamps differed by about one cadence slot in some stretches, including around
packet positions 1,000, 16,545 and 34,000, while other positions aligned. Equal packet
counts did **not** prove decoded picture retention, identity or order.

| Job/control | Video packets: source / candidate | Timestamp-window harmonic scores | Frame-number-window harmonic scores |
| --- | --- | --- | --- |
| 5692 | 34,046 / 34,046 | 16.20 / 24.71 / 39.03 | 93.83 / 93.68 / 92.38 |
| 5861 | 32,605 / 32,605 | 16.53 / 27.53 / 94.21 | 94.90 / 90.90 / 94.20 |
| x264 control of the first source | 34,046 / 34,046 | 93.34 / 94.37 / 93.95 | 93.34 / 94.37 / 93.95 |

## Change tested then

[#287](https://github.com/Jellman86/optimisarr/pull/287) used equal packet totals to select
an alternate sampled comparison. It retained a small time-window margin, renumbered
pictures using whole-microsecond cadence steps, applied the measured shift as a whole
number of pictures, and trimmed matching frame-number ranges. Fractional timestamp
rounding had previously selected neighboring pictures in part of a window.

Those packet totals were the implementation's selection signal at the time. They must
not be described as decoded frame totals. The worker log called them frames, but it
used `ffprobe -count_packets`. Current production uses decoded counts where required.
A good score is supporting quality evidence, not a guarantee against missing pictures,
misalignment or all visible damage; independent structural and timing checks still apply.

On the live 0.2.16 retries, both jobs passed their configured gates. Recorded aggregate
results were harmonic 93.29, fifth percentile 90.85 and minimum 87.39 for the first;
93.30, 87.21 and 82.18 for the second. The configured limits were 90, 75 and 45.
Both replacements used the normal verified pipeline. These values establish what
happened on those fixtures, not a calibrated quality threshold.

## Limits and later work

This change addressed sampled window pairing. It did not establish full-file behavior,
fix every source with unequal counts or prove picture identity from packet statistics.
The later duration investigation was separate. VMAF v1 deployment and all-platform
acceptance are recorded in the [1 October report](2026-10-01-vmaf-v1.md); decoded-count
and repeated-timestamp follow-up remains tracked in
[issue #353](https://github.com/Jellman86/optimisarr/issues/353).
