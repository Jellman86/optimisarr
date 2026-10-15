# Shared worker process ownership

Development qualification on **15 October 2026**, in [draft #415](https://github.com/Jellman86/optimisarr/pull/415).
This component strengthens audio/image execution and existing video encodes. It does not
complete quality-first parity or enable automatic protocol-11 placement.

## Behaviour

Encode, media-probe and capability-command callers check cancellation before launch. Once
launched, the direct child stays owned until its exit is positively confirmed. Cancellation,
reader failure and other post-launch failures all pass through the same independent stop/reap
finalizer. Failed tree termination also attempts direct-child termination. A failed exit wait
or an elapsed deadline cannot authorize resource release; unconfirmed exit keeps the operation
and its job slot held.

Readers have an independent lifetime and are supervised separately. One reader's failure stops
the producer without waiting for the opposite pipe to reach EOF. After direct exit, inherited
pipes receive a bounded drain grace, then cancellation and closure. Individual reader tasks,
the aggregate drain and the original exit wait all have exception observers.

Probe and capability success requires both captured streams to finish successfully. A zero
process exit with incomplete output returns unavailable evidence. Probe/capability text is bounded
to 1 MiB per stream. Encode diagnostics retain twelve distinct lines of at most 4,096 characters
each, with an explicit truncation label; oversized progress lines are discarded. A throwing
progress observer is disabled while the pipe continues draining. Synchronous observers must
return promptly; callbacks queued inside an external `IProgress` implementation retain that
implementation's own lifetime.

Direct exit and scratch retirement remain distinct. As documented by
[Microsoft](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0),
waiting after tree termination does not prove every descendant has exited. The inherited scratch
guard remains the retirement authority. Standalone .NET admission and durable reservation
retirement still need integration; this process component cannot establish those properties.

## Test evidence

Test-first OS regressions reproduced six encode/probe failures and two capability failures.
Independent review then identified incomplete-evidence success; all three added regressions
failed before the follow-up fix. The final slice adds 25 shared-worker cases, including:

- pre-cancelled work never launching a process;
- cancellation retaining ownership until controlled actual exit;
- failed tree kill and failed exit wait retaining ownership;
- reader failure while the opposite pipe stays open;
- a throwing observer followed by output larger than the OS pipe buffer;
- bounded probe evidence and unterminated stderr lines;
- incomplete streams and a fault raised during closure refusing probe success;
- real managed encode/probe cancellation and confirmed direct-child exit;
- real parent exit while its child retains both pipes and the actual scratch guard: bounded
  runner completion, no later direct progress reports, busy recovery until child release,
  then positive workspace removal.

The exact source archive was SHA-256 verified before native Quark execution. Mac and Quark
both pass zero-warning .NET builds, all **3,526 backend tests**, and **579 shared-worker tests**
with three Windows-only event-log skips. Quark also passes all **31 Linux-sidecar tests**.
Mac passes all **489 Swift core tests and nine UI tests**; its two existing legacy Keychain
deprecation warnings remain. Python validation passes **154 tests**, and documentation
and Unraid checks pass. All **346 browser tests** pass with one worker.

The rebuilt self-contained Mac helper also passes connected AAC, Opus and MP3 disk workflows
and an AAC workflow on an actual RAM volume. The normal signed Swift release-library driver
uses live worker memory admission. Each workflow loses one committed probe response, replays
the search, seals upload, passes independent final verification, repeats replacement without
duplication and restores the exact original through rollback. Scratch is clean, the RAM volume
is positively retired and removed, and each private Keychain anchor is removed only after its
independently anchored ledger is confirmed empty. These are isolated fixture-server trials with
explicit protocol-11 elevation and a server-side capacity offer; they do not prove automatic
placement or the full installed package lifecycle.

Fresh Picard execution is pending: Tailscale SSH timed out, the Mac had no direct LAN route,
and Quark's forwarded LAN connection reported no route to the host. Earlier Windows evidence
belongs to earlier source and does not certify this slice. No host protection, installed
worker, pairing or original media was changed.

Independent Astra review found the incomplete-evidence and aggregate-observation issues; its
follow-up source review found no further actionable defect in this process component. Review
is separate from executed tests. Raw logs, source hashes, private fixtures and copied media
remain outside Git.

## CI race follow-up

The first CI run exposed two fixture races. Linux observed the new PID-readiness file after
creation but before its asynchronous contents were written. The managed fixture now writes
and closes a sibling temporary file before publishing it through a same-directory move.
The Mac test assumed a callback had started before its 30 ms deadline; under concurrent load,
the real implementation correctly refused to start an operation whose permission had expired.

Tests for an already-running retirement now expire a controlled deadline only after both the
operation and its timeout have started. A separate real-deadline test still proves that queued
or running OS work cannot veto caller completion while its release barrier remains held. Failed
assertions also release fixture barriers. The production timeout and dispatch behaviour are
unchanged; no existing deadline or ownership assertion is weakened.

The repaired full parallel Swift run passes **490 core and nine UI tests**. Its separate live
RAM gate completes **17 registered cases**, including an explicitly skipped legacy FFmpeg-dependent
case when that tool variable is absent. Release builds succeed with the same two existing
Keychain API warnings. The repeated Mac .NET suites pass 3,526 backend and 579 shared-worker
tests plus three platform skips. The failed CI logs and the intermediate new timer-test wall-clock
failure remain in private evidence; the latter was replaced by an explicit completion-before-OS-release
handshake rather than measuring scheduler delay as retirement latency. The repeated Quark .NET
and Linux suites also pass at the repaired fixture source.

The newly linked signed driver correctly refuses an AAC RAM trial when live capacity is
insufficient: no source download or probe occurs, the hand-back is acknowledged, failure count
stays zero and its scratch/ledger are positively empty. This refusal is not a new positive RAM
workflow result; the earlier successful RAM workflow above belongs to the pre-race-fix driver.
The same newly linked driver passes a fresh AAC disk workflow with strict final verification,
lost-response replay, idempotent replacement, exact rollback and positive empty-ledger cleanup.
