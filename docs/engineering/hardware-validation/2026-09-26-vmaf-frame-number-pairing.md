# VMAF frame-number pairing on drifting candidates — 2026-09-26

## Scope and safety

This closes the fourth and last known cause of #269: whole-file VMAF windows scoring near zero on
candidates that were visually and structurally sound. It used two retained failed candidates copied
read-only from the fleet together with their sources, Optimisarr's own `QualityScoreService`
driven by a small scratch harness, and the supported retry endpoint. No verification gate was
weakened and no database row, container or deployment setting was edited by hand. Both retried jobs
went on to replace their originals through the normal pipeline once they passed.

## Environment

| Item | Value |
| --- | --- |
| Control plane | Riker, Optimisarr 0.2.15 during diagnosis, 0.2.16 for the live retries |
| Worker | MacBook Air, macOS sidecar, `hevc_videotoolbox` |
| Measurement | libvmaf 3.2.1, `vmaf_v0.6.1`, three 40-second windows (early, middle, late) |
| Tools | the installed sidecar's bundled `ffmpeg`/`ffprobe` |

## What was observed

Two SpongeBob episodes (jobs 5692 and 5861) failed final VMAF with the signature #269 is about,
a harmonic mean in the teens or twenties with individual frames at zero, after the three earlier
causes (#274, #275, #281) were already deployed.

For job 5692:

- source and candidate each hold **34,046** video packets, so the encode dropped and added nothing;
- sorted presentation timestamps show the candidate **drifting by exactly one frame in
  stretches** against the source: in step at frames 2,925, 5,000, 20,000 and 30,165, one frame
  early (−41.3 ms at 23.976 fps) at frames 1,000, 16,545 and 34,000;
- the server's measurement scored the three windows at **16.20 / 24.71 / 39.03**;
- trimming both files to the same frame numbers and pairing by index scored
  **93.83 / 93.68 / 92.38**.

The start-of-window alignment from #274 cannot fix this. The drift changes *inside* a window, so no
single shift is right for all of it, and at a drift boundary the candidate's timestamps skip a
cadence slot, so shifting its timeline by a frame's worth of time selects the same first picture as
not shifting it.

## Change (#287)

When source and candidate hold the same number of video packets, each windowed measurement:

1. cuts the window by time, as before, with a two-frame margin either side;
2. renumbers both streams on a **whole-microsecond** step, `setpts=N*K`. A fractional step
   (41,708.33 µs) rounds differently once one side is shifted, and libvmaf pairs each frame with the
   other stream's latest frame at or before it, so a microsecond short met a third of the frames
   with their predecessors;
3. applies the alignment probe's offset in **whole frames after numbering**,
   `setpts=(N-round(shift*fps))*K`;
4. trims both sides to the same frame numbers, half a step clear of any frame.

Unequal counts, a missing frame rate, full-file measurement, cut-clip samples and decimated
references keep timestamp pairing. Mispairing can only lower a VMAF score, so this choice can fail a
good candidate but never pass a bad one.

The server decides from the packet counts its timestamp-integrity scans already read
(`TimestampCheckResult.PacketCount`). Workers are sent `framePairedCommands` beside `commands`,
count both files with `ffprobe -count_packets`, and choose; older workers ignore the field and older
servers never send it.

## Evidence

Offline, server code, three windows each:

| job | frames (source / candidate) | by timestamp | by frame number |
| --- | --- | --- | --- |
| 5692 | 34,046 / 34,046 | 16.20 / 24.71 / 39.03 | 93.83 / 93.68 / 92.38 |
| 5861 | 32,605 / 32,605 | 16.53 / 27.53 / 94.21 | 94.90 / 90.90 / 94.20 |
| x264 control of 5692's source | 34,046 / 34,046 | 93.34 / 94.37 / 93.95 | 93.34 / 94.37 / 93.95 |

The 5861 late window never drifted and scores identically either way, as does the regular x264
control, so the change moves nothing that was already right.

Live, 0.2.16 server and Mac sidecar, jobs retried through the API:

- the Mac logged `34046 candidate frames, 34046 source frames; pairing frames by number`, chose a
  different start offset per window (−1, 0 and +1 frame), and measured 93.83 / 93.68 / 92.38;
- both jobs passed every gate: 5692 at harmonic mean 93.29, fifth percentile 90.85, lowest frame
  87.39; 5861 at 93.30, 87.21 and 82.18, against gates of 90, 75 and a catastrophic floor of 45.

## Still open

- Frame pairing needs equal counts. A candidate that genuinely loses frames (see the 2026-09-15
  record) is still measured by timestamp, which is right for it but leaves any in-window drift on
  such a file unhandled.
- Outputs about 1% longer than their source failing the duration gate are a separate problem (#289).
