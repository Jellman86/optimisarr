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

## Resource reconciliation foundation

The pure shared pool now supports owner-keyed admission, restoration and release. Identical
restoration is idempotent; a conflicting volume or charge is refused without changing existing
debt. Restored obligations are retained even when current capacity is smaller than their charge.
Overflow is checked before mutation, and ordinary disposable reservations share the same
accounting. Owner IDs must remain unique to each obligation and cannot be reused after release;
the future coordinator must enforce that lifetime.

Three test-first regressions also exposed an existing path-key defect: a trailing separator
could hide an ordinary or restored disk charge when capacity was queried without that separator.
All reserve, restore and capacity queries now use the same normalized key. The new foundation
adds **19 pure tests**, including conflicting replay, overflow, reduced capacity, changed volumes,
concurrent restoration/release and distinct owners competing for limited capacity. All **28
resource policy tests** pass. Repeated zero-warning Mac and Quark .NET builds and backend suites
pass **3,545 tests**; shared suites pass 579 plus three platform skips, and Quark Linux passes 31.
All 346 browser tests pass again. Independent Astra source review found no actionable defect;
its additional distinct-owner concurrency cases are included in the passing tests.

The rebuilt helper and signed release-library driver pass fresh AAC/Opus/MP3 disk and actual
AAC RAM workflows with live admission, committed-response replay, final verification, idempotent
replacement, exact rollback and positive cleanup. An initial capacity refusal during concurrent
host testing occurs before transfer, with zero media failures. The attempted fresh busy-writer
and crash trials also meet a live capacity refusal before injection; they do not count as new
fault qualification. Their empty ledgers are positively reconciled and their private anchors
removed. Earlier successful busy/crash evidence remains dated to its previous source.

This pool performs no filesystem access and does not persist obligations, authenticate retirement
or connect standalone startup, capability probing and execution. The durable coordinator, its
independent checkpoint, previous-root recovery and positive receipt consumption remain required
before standalone restart accounting can be qualified. Fresh Picard execution is still pending.

## Accounted capability ownership

Capability probes now have explicit v2 workspace creation, targeted retirement and receipt
consumption APIs. They require a parent-selected owner and a pinned physical root/control/gate
scope. Probe identities carry no job or lease, and neither kind of receipt can satisfy the other.
Generic recovery preserves both active and retired accounted probe records for their coordinator.
Cleanup closes this parent's inherited guard before attempting retirement and retains the positive
receipt until explicit consumption. Missing records never establish retirement.

The test-first API contract and ownership cases cover live guards, replay, purpose confusion,
legacy unaccounted probes, replaced root/control/gate/owner/marker identities, missing scopes,
nonce conflicts, incomplete deletion and pre-cancelled retirement. The managed parent-death
fixture now exercises both jobs and capability probes: a surviving child vetoes targeted cleanup
until its actual exit, then retirement and receipt consumption succeed. These are workspace
component tests; the standalone capability prober still needs durable resource admission and
startup reconciliation before using this API.

The expanded component gate passes **35 cases**, including both real parent-death variants,
on Mac and native Quark. Each host also passes zero-warning .NET builds, **3,545 backend tests**
and **596 shared-worker tests** plus three Windows-only event-log skips; Quark passes **31 Linux
tests**. The source archive was hash-verified before remote execution. Independent Astra review
found no actionable defect in this prerequisite. Fresh Picard SSH still times out, so these
results do not certify physical Windows execution of the new capability API.

All **346 browser tests**, **154 Python tests**, documentation and Unraid checks pass again.
The live RAM gate initially refuses a partial allocation's immediate post-unmount retirement
and retains its recovery intent. A later exact-owner capture through the normal API positively
retires and replays that owned fixture; independent enumeration confirms it absent. The original
failure and recovery evidence remain private. This establishes immediate refusal followed by
successful owned retirement, without identifying which original OS guard refused the attempt.

The fixture now polls retirement within a bounded interval using the **same original receipt**
after successful handle-bound unmount and handle closure. It still requires positive retirement
and preserves the mounted-volume, redirected-owner and sentinel assertions. Unconfirmed cleanup
now records the full physical allocation receipt. No production retirement logic or deadline
changes. The repeated live gate passes **17 registered cases**, with the same explicit legacy
FFmpeg-only skip. Astra's read-only review found no actionable production defect and accepted
this bounded same-receipt qualification.

## Shared ledger and checkpoint foundation

The new pure shared format records immutable owner/purpose/job/lease, actual root, pinned scope,
physical volume identity and separate peak/scratch requirements. Checked transition validation
refuses rewritten obligations, phase regression, skipped retirement, premature compaction and
revision overflow. Charges remain through Retired until durable Released. Immutable collections
close an exposed-array mutation reproduced by a failing regression.

The independent-checkpoint rules bind a store to its journal path, physical directory and lock.
Interrupted saves admit only their exact old/new hashes. Earlier empty snapshots, changed bindings,
missing authority and nested pending transitions are refused. Canonical bounded JSON and golden
byte fixtures freeze property order, nulls, GUIDs, enums, escaping and the full-document digest.

All **90 pure cases** pass locally, including null/malformed data, UTF-16 and text limits,
duplicate owners, aggregate charge overflow, mutable collection exposure and rollback rules.
A further failing regression proves valid escaped path text cannot make a generated checkpoint
exceed its serialized byte limit; initialization, pending and resolved results are all bounded.
Astra's initial and follow-up read-only reviews find no actionable defect in this foundation.
This is not persistence or a crash-boundary qualification: OS storage, independent checkpoint
durability, physical-volume pool keys and coordinator wiring remain required. See the
[accounting contract and integration requirements](../../development/native-work-accounting.md).

The final frozen C# source passes zero-warning builds and all **3,545 backend / 686 shared-worker
tests** on Mac and Quark, with the same three Windows-only skips. Quark also passes all **31
Linux tests** after verifying the source archive hash. Swift, browser and Python sources are
unchanged from the preceding capability checkpoint's passing 490 core/nine UI, 346 browser
and 154 Python gates. Documentation checks pass again. Picard remains unreachable, so fresh
physical Windows qualification is still pending.

## Physical-volume pool prerequisite

The shared pool now has a fixed physical-ID mode alongside the unchanged default path mode.
Opaque IDs preserve case and path-like characters on every platform. Wrong-mode APIs fail
before normalization or mutation and cannot create a second accounting namespace. Null and
uninitialized ID wrappers, invalid UTF-16, unknown capacity and invalid charges are refused.
All volumes still share one owner table and memory charge; replay, overflow, exactly-once release
and concurrent admission retain the existing policy.

All **20 new pure cases** and **48 targeted resource cases** pass locally. Astra's design and
source reviews find no actionable defect in this prerequisite. This does not prove that an OS
adapter captures physical identity and capacity from one pinned root or that standalone callers
share the new pool; those integration requirements remain open.

The frozen source passes zero-warning .NET builds, **3,565 backend tests** and **686 shared-worker
tests** on Mac and native Quark, plus three Windows-only skips. Quark passes **31 Linux tests**
after verifying the source archive hash. All **346 browser tests** pass again. Swift and Python
sources retain the preceding passing checkpoints. Picard's SSH connection still times out;
the new physical-ID policy has no fresh physical Windows qualification yet.
