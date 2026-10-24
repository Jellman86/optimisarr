# VMAF sequential decoding watchdog, 13 October 2026

Follow-up to [#406](https://github.com/Jellman86/optimisarr/issues/406), based on development
`6e652701`. The reporter confirmed that #407 improved adaptive sample alignment, then hit the
server's five-minute idle timeout during final verification. Their exported policy uses three
40-second samples; this is not whole-file VMAF scoring. Their original media is unavailable here,
and the export does not establish which sample timed out.

## Failure and change

Absolute-frame windows decode from the beginning to preserve correspondence. FFmpeg can
therefore decode useful pictures for more than five minutes before output `time=` first advances.
The old watchdog treated that healthy work as a stall. A bounded real-FFmpeg reproduction
confirmed the mechanism without changing the quality policy.

The local service adds metadata-only counters before each input's trim. Every 50th decoded
picture before the sample boundary emits its absolute index. Reference and candidate records
use separate stdout/stderr pipes with direct flushing, including at error log level. Each record
must have a valid header and its matching input marker; only a strictly increasing index inside
that input's predecode range renews the unchanged five-minute idle deadline. The filters do not
select, duplicate, retime or change pixels. They stop emitting when the sample starts, so they
cannot mask stalled scoring. Output time must then advance as before.

Telemetry is streamed and discarded, stdout is drained, and captured stderr is bounded to
64 KiB. Decoding counters never advance the scoring percentage or provide quality evidence.
Cancellation and timeout kill the process tree, wait for exit and drain both pipes before deleting
the owned score log. Cleanup has a bounded grace period; an unconfirmed reap is reported.
Timeout errors identify the sample start, last observed input indices and VMAF output time.
Hardware failure discards its partial log and uses the same tracking in software fallback.

The canonical builder and frozen remote worker graphs, allowlists and protocol remain unchanged.
Full-file, cut-clip and timestamp-pairing commands retain their existing graphs. Models, scored
frame selection, sampling, quality floors, structural gates and replacement authorization are unchanged.

## Linux production-binary reproduction

An isolated native runner on the Intel Linux host used FFmpeg 9.0.2 copied out of the running
server image. A disposable 330-second, 160×96, 50 fps FFV1 fixture was compared with itself.
Input `-readrate 1` deliberately made the sequential startup exceed the real five-minute timeout.
Both runners scored the same absolute 310–320 second window; neither used a shortened timeout.

| Runner | Wall time | Outcome |
| --- | --- | --- |
| Development `6e652701` | 300.25 s | Failed: no VMAF output before the five-minute watchdog |
| Fixed service | 319.74 s | Measured all 500 pictures; harmonic 98.945862 |

This establishes the timeout mechanism and bounded completion on the actual packaged binary;
it does not reproduce the reporter's original H.264/AV1 files. Only selected pictures are scored.
Production containers remained healthy with zero restarts; no installed app or worker was updated.
Media testing used isolated files, and no originals or jobs were changed.

The final service also reproduced a 19.73-second live decoding/scoring run with a two-second
test idle deadline. An unthrottled baseline and final runner both scored the late window at
98.945862 over 500 pictures. Cancelling a throttled late-window run returned in 1.20 seconds.
A window beyond the available source returned unmeasured with no comparable-frame log.

A generated H.264 comparison requested QSV and completed through software fallback at
99.104599 over 100 pictures. A separate device probe established that this packaged static
binary has no enabled QSV child-device type on the native host. This qualifies fallback, not
hardware decoding or CUDA/VAAPI telemetry.

## Score preservation and negative controls

The native Mac runner compared the baseline and fixed service using the same installed FFmpeg,
model and 10–50 second window from the prior 50 fps fixtures. Every pair scored exactly 2,000
pictures. Harmonic scores matched to all six reported decimal places:

| Candidate | Baseline | Fixed |
| --- | --- | --- |
| Identical pictures | 98.608239 | 98.608239 |
| Timestamp step with identical pictures | 98.608239 | 98.608239 |
| Duplicated first/dropped last picture, equal count | 67.168728 | 67.168728 |
| Heavy blur | 0.328833 | 0.328833 |
| Piecewise timestamp offset, identical ordered pictures | 98.608239 | 98.608239 |

The last case uses absolute-index pairing, not timestamp alignment. Actual picture damage and
poor quality remain failures at the existing floors. Raw fixtures and private operational evidence
remain outside Git.

## Retained-media verification

A disposable copy of the previous 1,840.96-second 1080p source and retained HEVC candidate
was measured on the Linux host with VMAF v1, actual 1920×1080/8-bit encode metadata and full
metric sampling. Each 40-second window scored 959 pictures at 24000/1001 fps:

| Start | Harmonic | Wall time |
| --- | --- | --- |
| 164 s | 89.525897 | 30.86 s |
| 900 s | 83.663588 | 112.21 s |
| 1636 s | 90.225631 | 215.93 s |

The combined harmonic score is **87.70**, matching the prior corrected-window acceptance,
and still fails the configured floor of 90. The minimum remains 72.82 and the lowest window's
fifth percentile remains 78.94. Keeping healthy decoding alive did not turn this real quality miss
into a pass. Timings overlapped other isolated controls and are not a controlled benchmark.

## Automated coverage and limits

New tests reproduce slow but advancing startup, repeated/stale counters, a scoring stall after
startup, cancellation, noisy output, missing quality logs and hardware-to-software fallback.
Pure tests check input identity, bounds, malformed counters, fractional cadence, asymmetric
window starts and preservation of non-sequential and frozen canonical commands.

- Backend: zero-warning build; 3,147 tests pass, including 21 new watchdog tests.
- Browser end-to-end: all 325 pass in a complete rerun. The first full run had one unrelated
  soundtrack-control page-load timeout; that case passed on retry. No assertion was relaxed.
- Shared C# worker: 328 pass, with three native Windows event-log tests skipped on Mac.
- Linux worker: zero-warning build; all 31 tests pass.
- Acceptance scripts: all 105 tests pass; Unraid/container metadata checks pass.

Sequential decoding still costs CPU, I/O and latency; this fix does not reduce that cost or add
another decode pass. Counter emission is limited to one record per 50 decoded pictures and stops
at the trim boundary. Extremely slow decoding that advances fewer than 50 pictures within five
minutes can still time out conservatively. Whole-process CPU use is deliberately not evidence
of progress. A hung decoder or scoring phase still fails without authorizing replacement.

The exact reporter media and long 4K cases remain unqualified. Picard was offline during this
follow-up, so new live Windows/NVIDIA acceptance is not claimed. Previous #407 hardware evidence
does not qualify this new telemetry path. Native Windows CI remains a separate platform check.
