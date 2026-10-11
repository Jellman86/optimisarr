# Exact-copy journal and claim foundation

D3a of [issue #397](https://github.com/Jellman86/optimisarr/issues/397), 11 October 2026.
This is the first safety foundation for D3. Manual exact-copy quarantine and restore are still
unavailable. There is no dedupe mutation endpoint, confirmation button, automatic cleanup or
new filesystem move implementation in this slice.

## Durable intent

An additive migration introduces `DedupeActions`, `DedupeActionFiles` and `DedupeFileClaims`.
These are independent of encode replacement records and their automatic retention policy.
Every preparation freezes the keeper and copy paths, library roots, physical identities,
expected full-file hashes, sizes, modification observations and copy restore destinations.
The complete plan has a versioned digest and an explicit `UntilRestored` retention contract.
Quarantine does not imply freed space.

The journal store is mechanical persistence, not an action preflight. Its expected hash can come
from a review snapshot; that hash alone never authorizes a move. The store currently accepts
independent physical-file snapshots only. Linked or overlapping references require the complete
alias adapter before actions can be offered. Valid absolute paths and matching snapshot hashes
are necessary record checks, not proof of storage, permissions, content freshness or ownership.

Preparation and all reservations commit in one SQLite transaction. Repeating an identical
preparation preserves its timestamp and rows. Reusing an action ID with a different plan fails.
Retries cannot reactivate a cancelled action or silently accept missing reservations. Conflicting
paths, identities, destinations, active snapshot jobs and process claims reject preparation.
History has no cascading foreign key to a library or inventory row; deleting either cannot lose
the recorded restore paths. Claims and snapshots prevent deletion of their owning journal.

Only an untouched `Prepared` action can be cancelled. Cancellation records the terminal state
and releases its claims in the same transaction. An action with move intent cannot be dismissed
as an untouched preparation. This store has no completion method that could assert a successful
move or restore without a separately qualified file adapter.

## Shared replacement guards

`FileOperationClaims` acquires the complete key set atomically in a deterministic order. It uses
inventory IDs, canonical paths and known filesystem identities. Two inventory IDs or hard-link
names cannot bypass the physical claim. Failed acquisition claims nothing; disposal is
idempotent. Case folding is conservative on Windows and macOS and can exclude independent
case-sensitive names rather than permit an overlap.

Replacement, rollback and interrupted replacement recovery now use this shared coordinator and
check persisted dedupe reservations. Claims include the source, work output and expected final
path for replacement, and all three recorded paths for rollback/recovery. Existing finalization
capacity and cancellation semantics remain. An unresolved dedupe reservation defers replacement
or rollback without moving either file.

These guards coordinate this server's replacement paths. They do not exclude another process,
an external importer or every queue/worker operation. Completing that wider contract is a
prerequisite for enabling D3 actions, not something this foundation claims to have delivered.

## Restart behaviour

Journal recovery starts after database migration and before queue/background workers.
`Prepared`, `Quarantining` and `Restoring` intents become `NeedsAttention` on restart. They retain
all reservations. A stable `Quarantined` state also retains its reservations. No state transition
is treated as filesystem evidence, and startup never guesses that a file was moved or restored.

Missing reservations are rebuilt from the frozen plan. Unsupported contracts or retention,
changed rollback evidence and conflicting unresolved reservations fail startup instead of
allowing file operations with uncertain protection. Recovery itself creates no media directory,
never moves a file and never releases an unresolved claim. Repeating it is idempotent.

## Tests and limits

Tests own disposable SQLite databases and synthetic files. They cover atomic and reversed-order
claims, a real hard-link alias, duplicate disposal, immutable plan signatures, retries,
conflicts, prepared cancellation, interrupted states, missing-reservation repair, corrupted
contracts/paths, migration reapplication and history surviving inventory/library deletion.
Replacement regressions prove process and durable claims leave originals and outputs intact.

The full Release solution builds with warnings treated as errors and zero warnings on all
three available development hosts. Native backend results:

| Platform | SDK | Result |
| --- | --- | --- |
| Mac, Arm64 | 10.0.302 | 2,981 passed |
| Quark, Linux | 10.0.300 | 2,981 passed |
| Picard, native Windows | 10.0.401 | 2,973 passed, eight existing platform skips |

All 755 backend source/fixture hashes match the tested source archive on Quark and Picard.
The real hard-link claim test passes on all three hosts, including Windows. This proves claim
identity agreement, not Windows link-count or destructive-action qualification. The final source
adds 35 backend cases over D2. Python checks pass 105 tests; documentation links, generated
OpenAPI and Unraid metadata checks pass. Riker's active migration backup and production services
remain untouched. No installed worker, host security policy or media library changed.

These are SSD-backed synthetic fixtures. They do not qualify USB/SMR storage, atomic dedupe moves, permissions, Windows link
counts, fresh action hashes, external-manager reconciliation or a destructive action workflow.
The UI and documentation screenshots are unchanged in D3a.

## Remaining D3 work, in dependency order

1. Integrate durable reservations with local and remote job claims, enqueue/preview/calibration,
   inventory reconciliation and import/manager ownership. Capture every physical alias and all
   affected references without silently widening the selected scope. Add races between each
   operation and dedupe preparation. No action can become eligible until these tests pass.
2. Add an action preflight using current scope/generations, permissions, supported independent
   assets, qualified writable storage and fresh full reads of all selected keepers and copies.
   Block connected-manager paths, unknown/read-only storage and unsupported bundles. Qualify
   Windows identity and link probing before enabling Windows actions.
3. Record per-file move/restore intent and progress before implementing a no-overwrite atomic
   same-filesystem move. Refuse cross-filesystem fallback. Recheck identity and content around
   each move. Test cancellation, retries, path swaps, keeper loss and process exit at every
   journal boundary; reconcile ambiguous files without discarding their journal or claims.
4. Add concrete media-rich preview and separate confirmation to the current library review:
   keepers, selected copies, destinations, retained bytes and restore rules. Add conflict-safe
   restore and journal attention states. Exercise keyboard, both themes, mobile/wide layouts,
   enlarged text, failed artwork and stale evidence, then run native hardware and container CI.

D3 remains incomplete until these action and recovery gates pass. Broader image, audio and video
matching remains D4 through D6, with the research and keeper strategy requirements in issue #397.
