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

## D3b first step: transactional job admission

The next concurrency slice checks reservations at the asynchronous persistence boundary for new
active jobs, active status/source/output changes and newly held worker leases. This includes normal,
preview and calibration jobs, retries, local starts and delivered-candidate verification. It opens
SQLite's write transaction before reading reservations and retains it through the job/lease commit.
Preparation uses the same writer. A stale context cannot rely on an old navigation or job source:
the guard checks both pending and current persisted references. Terminal diagnostic updates remain
possible. Reservation and inventory queries are batched rather than repeated for each queued job.

The application injects the live file-identity adapter into its database contexts. Isolated data
consumers without that adapter protect inventory IDs and canonical paths only. The key format is
shared pure Core logic; existing claim digests and the journal contract do not change. Existing
hard-link aliases are covered when their identities are available. This is an application write
guard, not an exclusion mechanism for external writers or arbitrary SQL. New operational paths
must use the guarded asynchronous context and must not bulk-update admission fields.

Enqueue holds the writer across its candidate decision and commit, after fetching network import
activity. Local and delivered-candidate claims hold it across their status check and commit.
Worker claims reuse their existing transaction and skip a reserved candidate. Preview checks
before replacing an earlier preview; calibration checks before source probing and again at job
commit. Candidates show a reservation reason. Interactive admission conflicts return an API conflict
with a fallback explanation; calibration retains its existing unavailable-response contract.

Preparation also checks active job source/output identities, independently held leases and all
paths of pending, rollback-pending or still-retained replacements, including another inventory ID.
This prevents admission against known rollback history; it does not complete cleanup coordination.

New tests use separate contexts sharing a real migrated SQLite database. They race preparation
against new normal/preview/calibration jobs and local/worker/verification claims, check stale
references and worker leases with tracked and untracked jobs, and exercise both commit orders for
real hard links. HTTP tests cover preview, retry, calibration and enqueue refusals. They never
change library content. This slice has no schema, frontend, sidecar or file-action changes.

The final native Release builds have zero warnings: Mac and Quark each pass 3,006 backend tests;
Picard passes 2,998 with the same eight existing Windows platform skips. All 760 backend
source/fixture hashes agree on both remote hosts and the local source. This adds 25 backend cases,
including the pending-inventory and temporary-ID regressions found during review. The Mac final
backend suite uses four managed processors. Browser qualification is recorded separately. No
thresholds or platform skips were changed. Quark passes the complete 325-test Chromium suite,
95 frontend units and zero-warning Svelte checks; all 188 web source/fixture hashes match the
tested archive. Its missing browser libraries were extracted from Fedora RPMs into the disposable
test root using the earlier D2 recipe, without installing system packages.

## Remaining D3 work, in dependency order

1. Complete cleanup, inventory reconciliation and import/manager ownership coordination after
   transactional job admission. Manual/timed quarantine purge and failed-output cleanup still need
   explicit guards and races. Cover aliases of parent directories for absent destinations. Capture every physical alias and all
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
