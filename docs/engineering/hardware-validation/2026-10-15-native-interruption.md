# Native interruption and restored-worker qualification, 15 October 2026

Development evidence for [draft #415](https://github.com/Jellman86/optimisarr/pull/415).
The [full parity matrix](../../development/adaptive-audio-image-parity.md) remains open;
protocol 10 is still advertised and automatic protocol 11 is disabled. Each trial uses
isolated configuration, copied/generated media and task-owned processes.

## Defect and fix

An actual Swift-parent hard-kill trial exposed a held lease after the helper stopped its
processes and cleaned scratch. The helper's cancellation escaped without a hand-back, leaving
work waiting for expiry. Merely routing that stop through the ordinary release endpoint would
also turn repeated app crashes into permanent media-refusal opinions.

The shared runner now reports `WorkerInterrupted` through an authenticated, replay-safe
`POST /api/workers/leases/{leaseId}/interrupted`. It uses a separate cancellation-independent
control request after stopped media work unwinds. A failed acknowledgement cannot claim that
the lease was returned. A parsed native run cancelled before execution also reaches this path
without fetching source, starting tools or creating working directories.

Interruption is neither a file refusal nor capacity pressure: it adds no permanent refusal
opinion and no capacity cooldown. A worker restoring its saved identity can reclaim the job
immediately. Genuine refusal and capacity-return pauses remain unchanged. The endpoint
preserves cancelled jobs, source failure/exclusion counts and quality retry intent; replaying
an old acknowledgement cannot requeue a newer lease. Existing string-backed end-reason
storage needs no schema change. The generated OpenAPI document includes the additive route.

## Actual workflow trials

Native Quark and Picard pass both three-distinct-session and three-restored-identity trials:

1. Collect actual tool proof and current resources; pair and heartbeat normally at protocol 10.
2. Elevate only the isolated test worker row to reach the gated native contract.
3. Commit a real original-source assessment, withhold its HTTP response and forcibly terminate
   only the parent app. The inherited helper survives long enough to stop work and acknowledge
   interruption; its committed assessment remains on the ended lease.
4. Repeat three times with zero media failures. Restore the same worker identity for the
   stronger trial, without waiting for a capacity/refusal cooldown.
5. The fourth session finishes the same job: bounded search, independent final verification,
   sealed upload, repeated idempotent replacement and exact rollback. Only empty coordinator
   metadata remains; owner records and working media are retired.

The restored-identity harness protects its test pairing with a per-run encryption key held
only in controller memory and passed through private pipes. Installed pairing stores are not
opened. These trials use the source-built framework host and actual native tools; they do not
qualify a new installed MSI or container. No resource grant overrides the parent's measured
and held reservation.

Picard's SSH process correctly reports unknown capacity under unprovable job ancestry. The
positive Windows workflow ran in an isolated, temporary native service context with real
physical availability. Its service was removed after the trial; the installed worker's status
and pairing were preserved. No job limits or protection settings were changed.

Before this interruption fix, Mac AAC, Opus and MP3 workflows passed using the signed bundle's
helper, encoders, pinned metrics and metadata tool, with fresh Swift pairing, heartbeat,
live admission, lost-response replay, final verification, replacement and exact rollback.
AAC also passed on a real RAM volume, which ejected and released its allocation. JPEG/WebP
trials withheld claims when advertised memory was insufficient. These are the recorded
pre-fix bundle lifecycles, not proof that the rebuilt interruption-fixed bundle completed them.

The rebuilt signed Mac bundle passes relocated tool/startup and rendered-fixture checks.
The subsequent full crash trial was held before claiming when the Mac returned warning memory
pressure. No source download or media failure occurred. Positive full-image admission and the
complete Mac crash workflow remain unqualified; admission was not weakened to obtain a pass.
Packaged ExifTool separately retains the product owner guard across forced parent death and
permits recovery only after it exits.

## Source checks and limits

### Swift interruption follow-up and independent review

An independent Astra review found a second route to ordinary media refusal: Swift cancellation
before the shared helper starts, and an unexpected helper failure while the app remains alive.
Swift now uses the explicit interruption endpoint for both. Its final request runs in a separate,
bounded control task so job cancellation cannot cancel the acknowledgement. A failed response
remains unconfirmed; authoritative lease loss remains distinct.

Test-first regressions reproduced both the wrong endpoint and cancelled-transport failure.
The updated tests cover cancellation before requirements, during requirements and recovery,
during RAM-volume preparation and during an executing helper's cleanup. They also cover helper
failure, acknowledged return, HTTP 503 and authoritative lease loss. These are deterministic
Swift lifecycle tests, not new hardware or packaged-job qualification.

The review caught an additional failed-RAM fallback path before the next cancellation check;
its regression failed before the release helper was made cancellation-aware. Another failing
regression covers cancellation arriving during the capacity HTTP request itself. Requirements and
recovery tests cancel the outer execution task, including its task-group children. Follow-up
source checks pass 370 Swift core and nine UI tests, 3,526 backend tests and 346 browser tests;
the .NET build has zero warnings. Swift release compilation retains the existing SDK warnings.

The review also found an **open resource-accounting defect**: direct helper exit does not prove
that an inherited media child has exited. Swift currently drops native peak and disk reservations
on return even if that child retains its ownership guard; failed RAM ejection keeps only the RAM
allocation. Scratch retention protects files but does not reserve a survivor's future growth.
This must be fixed before activation. Retirement needs positive evidence for the expected owner;
an empty aggregate recovery summary is insufficient when records or roots have disappeared.
Delayed RAM ejection also needs the original mount identity checked before a device name is reused.

Full-encode process cancellation additionally needs focused reaping qualification: the shared
transcoder requests termination but currently does not explicitly await exit in its cancellation
catch. Neither this Swift fix nor the earlier parent-death trials resolves that path.

### Earlier cross-host source qualification

Zero-warning .NET builds and current suites pass **3,526 backend tests** on Mac/Quark,
**3,503 backend tests plus 23 platform skips** on native Windows, **513 shared-worker tests
plus three Windows-only skips** on Mac/Quark and **516 shared-worker tests** on Picard.
Linux worker tests pass **31**; Swift passes **366 core plus nine UI tests**; Python passes
**154** and the browser suite **346**. Swift release compilation retains existing SDK
Keychain deprecation diagnostics. Raw reports, source/tool hashes and cleanup evidence remain
private. Available-host source snapshots are checked against the tested C# files.

This slice qualifies the recorded cancellation, acknowledgement and restored-worker cases.
It does not establish full machine-reboot/remount recovery, all interruption stages, native
image package parity, adaptive retained soundtracks, preview/calibration guidance or broader
source, retry/history/export acceptance. The completion matrix continues to govern activation.
