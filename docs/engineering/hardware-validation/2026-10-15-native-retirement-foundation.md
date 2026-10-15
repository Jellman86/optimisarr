# Native resource-retirement foundation, 15 October 2026

Development evidence for [draft #415](https://github.com/Jellman86/optimisarr/pull/415).
This is the shared helper's retirement contract, not a completed fix for Swift resource
accounting. Protocol 10 remains advertised and automatic protocol 11 remains disabled.

## Expected ownership and positive retirement

The private helper protocol has an explicit version 2 for accounted runs. Before launching
one, its parent prepares the chosen root's physical root, coordinator and lock identities.
Run, retirement and consumption requests must carry that scope and the parent-selected
owner nonce, job and lease. Scope validation uses the same opened handles and coordinator
lock as the operation; missing infrastructure is never recreated by a scoped run.

An accounted run creates its owner with `CreateNew` before early cancellation or capacity
returns. A reused nonce cannot adopt existing work or claim its retirement. No media tool
starts before the complete active record is flushed. A cancelled invocation may create
and retire these small ownership records without downloading or encoding media.

Cleanup acquires the inherited owner guard exclusively, validates the existing physical
identities and bounded tree, records deletion intent, then flushes a durable `retired`
receipt on the same guard inode. A surviving child vetoes this transition. Missing,
changed or malformed evidence remains unconfirmed; an empty general recovery result is
never positive retirement evidence. General sweeps retain externally accounted records.

The parent consumes a receipt only after durably recording retirement and accounting
reconciliation outside scratch. Consumption distinguishes removal, already-absent receipt
and unconfirmed results, allowing a lost successful response to be replayed. Already-absent
consumption requires the pinned scope and does not establish writer retirement. Parent
ledger phases and their RAM-volume ordering are still to be implemented in Swift.

## Verification and limits

Test-first cases cover busy and abandoned owners, durable receipt replay, consumption replay,
wrong job/lease/nonce, duplicate-nonce early exits, missing/replaced infrastructure, changed
aliases pointing to another valid scope with the same run identity, corruption, cancellation
and interrupted deletion before the retired record is flushed. Audio/image runner fixtures
also distinguish delivered media from independently confirmed cleanup.

On Mac, Quark and native Picard, a real managed child survives its parent's forced exit,
keeps targeted retirement busy, then exits naturally when the fixture releases its barrier.
Targeted recovery subsequently retires the exact owner and consumes the replayable receipt.
These are actual process/handle tests, not full FFmpeg/metric or Swift app crash workflows.

The same source snapshot passed zero-warning .NET builds on all three hosts. Backend suites
passed 3,526 on Mac/Quark and 3,503 plus 23 documented platform skips on Windows. Shared worker
suites passed 547 plus three Windows-only skips on Mac/Quark and all 550 on Windows. Linux
worker suites passed 31 on Mac/Quark. Swift passed 370 core plus nine UI tests; Python passed
154 and browser tests passed 346. Two Swift session tests timed out during a concurrent
local run; the complete unchanged suite passed on the quiet rerun without longer deadlines.

The self-contained Mac helper separately passed actual version-2 scope, retirement and
consumption framing, changed-scope refusal and version-1 downgrade refusal. This does not
qualify an installed or signed app package. Private source hashes, logs and receipts retain
the exact test evidence; no originals, installed pairing or protection settings changed.

The remaining P1 fix requires Swift's persistent grant ledger, exactly-once reconciliation,
old actual-root discovery, and original mount identity before delayed RAM ejection. Explicit
full-encode child reaping is also open. See the [parity matrix](../../development/adaptive-audio-image-parity.md)
and [interruption review](2026-10-15-native-interruption.md) before activation.
