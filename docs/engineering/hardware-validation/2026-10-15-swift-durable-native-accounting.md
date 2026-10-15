# Swift durable native accounting foundation, 15 October 2026

Development evidence for [draft #415](https://github.com/Jellman86/optimisarr/pull/415).
This accounting checkpoint was qualified at `02e24887`; the subsequent
[identity-bound RAM component](2026-10-15-native-ram-retirement.md) adds physical retirement
qualification without yet connecting either component to app startup or execution.
The production runner still uses private helper protocol v1. These components are not yet
connected to installed app startup or execution; the native resource-lifetime P1 remains open.
Protocol 10 remains advertised and automatic protocol 11 remains disabled.

## Implemented contract

The Swift private bridge supports explicit v2 scope, run, targeted retirement and receipt
consumption. Run identity must match the frozen job and lease. Ownership-only requests carry
no credentials, grants or media commands; unsupported versions cannot supply retirement proof.
Helper timeout/cancellation also works after a terminal reply followed by stdout EOF. Shutdown
has bounded TERM/KILL grace for the exact helper; its exit never proves that inherited tools
have stopped.

The bounded ledger retains the parent nonce, job/lease, original actual root and physical
scope, boot identity, native peak, disk/RAM obligations and reconciliation phases. Restoration
records existing debt without granting new work, even when current capacity is smaller. Invalid,
corrupt or ambiguous data cannot become an empty ledger. An uncertain attempted RAM allocation
remains charged; the actor may discharge RAM from a completed previous boot without addressing
a potentially reused device name.

The local, owned filesystem journal uses a stable, non-inherited exclusive coordinator lock,
bounded identity-checked files, checksummed payload bytes, temporary-file synchronization and
atomic replacement. Conditional comparison and replacement share one lock, so actors sharing
the journal cannot overwrite each other's obligations. A failed final durability confirmation
closes the journal. Reopening confirms durability again before exposing existing state. Missing
data in a pinned store is retained as an error; it is never recreated as a fresh store.

The reconciliation actor serializes native admission while any prior obligation remains:

1. Persist admission before authorizing RAM creation; capture actual root/scope and RAM identity
   before marking a reservation ready. Persist running before dispatch.
2. Require targeted positive retirement for every running owner. A duplicate helper invocation
   can report no newly created workspace while an earlier invocation still owns live writers.
3. Persist writer retirement before consuming its receipt. Lost consumption responses replay
   against this durable proof; an absent receipt alone never proves writer retirement.
4. Persist consumption before asking the RAM retirement provider to eject. A failed ejection
   retains all charges. The physical provider was pending at this checkpoint; its subsequent
   component evidence is linked above.
5. Persist accounting release before releasing memory/disk reservations. Replay releases the
   same owner idempotently, including recovery with the original in-process budget instances.

## Verification

Test-first regressions include live-owner retention, duplicate no-workspace terminals,
restart debt, lost consumption responses, failed RAM ejection, unknown RAM allocation, old actual
roots, same-process accounting replay, actor reentrancy, shared-journal stale caches, eight-way
conditional commits, corrupt/missing/redirected/hard-linked files and failed durability before
and after replacement. Actual uncooperative helper processes prove bounded timeout both when
silent and after closing stdout following a valid terminal reply.

The release AcceptanceWorker's real Swift bridge passed 11 control cases against the previously
qualified self-contained helper: scope, missing owner, changed scope, v1 retirement refusal,
live-owner busy, positive retirement after its task-owned producer exits, retirement replay,
receipt consumption and replay. Busy retirement preserved the fixture's copy; positive retirement
removed only its recorded workspace. These are control/process checks, not full media jobs or
Swift menu-bar app hard-death acceptance.

The final local suite passed 410 Swift core and nine UI tests. Release build passed with three
existing Keychain API deprecation warnings. The .NET build passed with zero warnings; backend
3,526, shared worker 547 plus three Windows-only skips, Linux 31, Python 154 and browser 346
passed. No C# implementation changed in this slice: the preceding helper source/process tests
on Quark and native Picard remain the [dated cross-host evidence](2026-10-15-native-retirement-foundation.md),
not a new full-package qualification of this Swift revision. Astra reviewed the foundations;
its actionable findings were reproduced and fixed, with no further actionable findings on the
final static pass. The reviewer inspected tests but did not independently execute them.

## Required before app lifetime closure

Connect the journal and registry to production startup, admission, execution and interruption;
persist the store identity independently and recover previous actual roots before granting new
work. Qualify physical RAM identity capture and nonforce retirement, including creation failures,
device reuse, lost responses and reboot/remount behavior. Registry boot identity and retirement
callbacks must come from authoritative OS operations, not caller assumptions. Qualify full native
tool/app hard-death workflows and explicitly reap full-encode processes.

No installed pairing, protection settings, services or original media changed. Private evidence
retains source/binary hashes and test reports. Full image admission on the physical Mac, fixed-image
workers, adaptive retained soundtracks and the rest of the [parity matrix](../../development/adaptive-audio-image-parity.md)
remain required before merge, deployment or protocol-11 activation.
