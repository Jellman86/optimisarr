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
