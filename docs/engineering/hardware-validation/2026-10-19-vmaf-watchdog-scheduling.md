# VMAF watchdog scheduling investigation

Investigated **19 October 2026**, in [draft #415](https://github.com/Jellman86/optimisarr/pull/415).

## Failure and reproduction

At `ecdec120`, the [backend CI job](https://github.com/Jellman86/optimisarr/actions/runs/39314640595/job/118272824814)
failed `Advancing_decode_counters_survive_a_startup_longer_than_the_idle_deadline`:
3,572 tests passed and one failed. The fixture advances every 200 ms for three seconds,
against a two-second idle deadline. Failure reported 700/700 decoded reference/candidate
frames and no VMAF output.

The measurement's pipe readers and process/cleanup awaits captured the caller's
`SynchronizationContext`. If that context was busy, pipe continuations could not observe
advancing counters before the independent idle timer expired. The timeout handler itself
also waited for the same context. Reported counters are read after cleanup drains the pipes;
700/700 therefore does not establish when those counters were observed relative to expiry.

Two controlled POSIX process regressions fail before the fix:

- Holding caller continuations until the advancing fixture writes its score log reproduces
  the exact two-second timeout with 700/700 counters, in three seconds.
- Holding caller continuations while a genuinely stalled fixture runs prevents child exit
  before an independent ten-second test bound. The finally block resumes the context and
  cancels/joins the measurement so the regression cannot leave a decoder behind.

This establishes a scheduling defect and reproduces the CI symptom. The original CI run
did not capture continuation scheduling, so it does not independently prove which context
delay occurred on that runner.

## Change

Measurement pipe reads, process-exit/drain waits and stop/reap finalization now use
`ConfigureAwait(false)`. They can observe progress and enforce cancellation without waiting
for caller-context availability. Public result delivery retains its existing caller context.
The production idle budget, short test budget, qualifying counter rules, scoring progress,
required log evidence and cleanup policy are unchanged.

The new test context queues continuations until explicitly resumed. Fixture completion is
signalled after score-log creation, rather than inferred from a sleep. The stalled case
requires actual child exit before the caller is resumed. Both regressions fail before the
change and pass after it; all nine watchdog process tests pass together.

## Verification scope

The native Mac source archive is SHA-256
`691bff7490f14fa84232cd5f5182adacde959d61c95484e47a8b70f751c00790`,
containing base `ecdec120` plus the service/test changes before this documentation.
Logs and archives remain outside Git. These managed process fixtures do not require installed
FFmpeg and are explicitly skipped on Windows because their executable uses a POSIX shebang.
They qualify process scheduling and safety checks, not a new GPU or media-format path.

The full backend suite passes **3,575 tests** on both Quark and the native Mac, with
zero-warning solution builds. Documentation, release metadata and OpenAPI checks pass.
Quark also passes **817 shared-worker tests** (23 platform skips), **31 Linux-sidecar
tests** and all **347 browser tests** with one worker.
