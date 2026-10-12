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

## D3b second step: retention and delivery finalization

Manual quarantine approval, timed quarantine purge and failed-output retention cleanup now
reserve the SQLite writer, reload current records and acquire shared file claims before the
bounded filesystem mutation. Durable reservations are checked inside that same writer, which
remains held through the record commit. A reserved or busy file is preserved; manual approval
returns its existing unavailable response and timed cleanup skips it. Cleanup previews exclude durable reservations,
so a reservation added after a browser preview invalidates that confirmation. Retained failed
jobs keep their output reference and diagnostics. Leased jobs, unknown active states and
independently held leases protect failed output paths too.

Worker delivery finalization checks the staging file, final destination and current/snapshot
source references before promotion or acceptance cleanup. It retains staging bytes on a conflict,
including a hash-mismatched upload, and leaves the job and lease unchanged. Existing cancellation,
revocation, expiry, assignment and candidate-hash checks still apply. Network transfer and full
candidate hashing happen before the short writer transaction. This is a finalization fence;
it does not yet fence chunk append, initial staging creation or interrupted-transfer cleanup.
Those paths require their own durable coordination before D3 actions can be enabled.

Tests exercise durable ID/path/physical-identity conflicts, shared process claims, stale tracked
replacement state, changed confirmations and retained diagnostics. Separate SQLite contexts and
independent coordinators prove both writer orderings: a committed reservation prevents deletion,
and preparation cannot enter while cleanup holds the writer for its mutation and commit. The
mechanical preparation can record cached evidence after a file vanished; this is deliberately
not fresh action preflight and never performs a move. Delivery tests cover single-shot and
resumable refusals, hash mismatch, retry after a process claim ends and a reservation committed
during streaming without holding a database writer over the network.

The 12 October Release builds have zero warnings on Mac (SDK 10.0.302) and Quark
(SDK 10.0.300), with 3,028 backend tests passing on each. All 762 backend source/fixture
hashes agree between the tested Mac source and Quark. This adds 22 backend cases over D3b1.
Mac passes all 325 Chromium tests, 95 frontend units and zero-warning Svelte checks. Picard
is unreachable over both LAN and Tailscale during this qualification, so this record makes no
new native Windows hardware claim. The required Windows CI remains a separate gate. No UI,
sidecar, schema, installed worker or production-media change is included.

## D3b third step: interactive disposable cleanup

Preview deletion and blind-calibration deletion now acquire the SQLite writer and an exclusive
process cleanup claim before cancellation, recursive deletion or record/session changes. The
writer and claim remain held through the diagnostic record commit. Existing shared file claims
exclude cleanup, and cleanup excludes new claims. Because opaque reservation keys do not prove
that an entire subtree is independent of an absent destination or aliased parent, any unresolved
duplicate intent or durable claim conservatively defers these recursive operations globally.
Unknown action states, incomplete intents and leftover claims also defer cleanup. This does not
qualify selective subtree cleanup or complete physical-reference capture.

Refused interactive cleanup returns `409 work.cleanupDeferred`, preserving files, job state,
output references and calibration sessions. Preview recreation keeps the previous preview when
its cleanup is deferred. Calibration expiration retains blocked sessions and retries on later
sweeps without stopping the background service. Failed diagnostic rows remain after successful
cleanup, with their output references cleared only when scratch removal succeeds. Missing
previews and sessions still return not found.

Tests cover actual HTTP refusals and retries, nested and absent paths, physical-identity-only and
unrelated claims, failed diagnostics, expiration, exclusive-claim contention and cancellation.
Independent SQLite coordinators prove both reservation/recursive-cleanup writer orderings.
This backend slice changes no schema, sidecar or file-action eligibility. Startup disposable
purge, recovery/cancelled outputs, orphan directories and other queue scratch operations are
separate remaining gates; a delayed whole-root purge must never remove newly created work.

The 12 October native Release builds have zero warnings on Mac (SDK 10.0.302) and Quark
(SDK 10.0.300), with all 3,050 backend tests passing on each, including 22 new cases.
All 764 backend and 188 web source/fixture hashes agree. Both hosts pass all 325 Chromium
tests, 95 frontend units and zero-warning Svelte checks. The Mac browser run uses one worker
while unrelated Xcode work is active; Quark uses four workers and task-local Fedora browser
libraries without system package changes. Picard is unavailable and excluded as requested.
Production services, installed workers and the Riker migration backup are unchanged.

## D3b fourth step: deferred startup recovery

Startup cleanup captures a fixed set of disposable, interrupted and cancelled jobs, plus
ownerless numeric scratch directories already older than seven days. Every retry acquires the
SQLite writer and
exclusive recursive cleanup claim before checking durable reservations, current job/source
snapshots, independent held leases and references. Unresolved intents and leftover claims defer
this pass globally, including unrelated reservations. The writer and claim remain held through
filesystem cleanup and the job/diagnostic commit. Database contention or cleanup failure retries
from the queue loop instead of ending the hosted service.

Only captured preview and calibration job trees are removed. Neither disposable root nor a
whole calibration session is swept, so comparisons created during deferral survive, including
new candidates in the same session. A changed or removed captured job is retired from the plan
without changing its files. Captured jobs remain excluded from local dispatch while pending;
other new work can continue. Failed disposable rows retain diagnostics and lose their output
references only after successful scratch removal. Retained calibration diagnostics with no
session or output are retired from later startup plans without inferring a missing tree.
Repeated startup remains a no-op for those rows. Interrupted local work is requeued or failed
at the existing retry limit only after its output is removed; delivered remote candidates return
to awaiting verification with their bytes intact. Cancelled work keeps its reference until
cleanup succeeds. The queue logs deferral once and completion after retry.

Fresh job outputs, media sources, library roots and replacement paths protect overlapping
cleanup targets. Numeric orphan cleanup also preserves queued/active/unknown jobs without
output references and independent held leases. Unknown job types and lease states also
preserve numeric scratch ownership. Changed directory timestamps retire captured
orphans rather than applying an old cleanup plan to newer work. Outside-root paths, unexpected
file/directory types, symlink/reparse trees and locally reserved scratch directories refuse
cleanup. These checks strengthen this bounded startup pass; they do not qualify arbitrary
parent aliases, external filesystem writers, bind mounts or the full D3 reference model.
Unowned preview/calibration trees without a captured job are retained. Other runtime scratch
helpers and streaming upload ownership remain separate gates.

The startup regressions reproduce 17 unsafe cases against the former algorithm, then pass
29 cases covering preserved bytes and rows, retry, diagnostics, fresh same-session candidates,
changed jobs/sources, superseded work excluded from orphan capture, independent lease state,
failed removal, delivered candidates, repeated restart and both SQLite writer orderings. This slice changes no schema, UI, sidecar or file-action eligibility.

The 12 October native Release builds have zero warnings on Mac (SDK 10.0.302) and Quark
(SDK 10.0.300), with all 3,079 backend tests passing on each, including 29 new startup cases.
All 766 backend and 188 web source/fixture hashes agree. Both hosts pass all 325 Chromium
tests, 95 frontend units and zero-warning Svelte checks. Mac uses one browser worker and
Quark uses four, with Fedora browser libraries extracted only into the task directory.
Python (105), OpenAPI, release metadata and Unraid checks pass. Picard remains unavailable
and excluded as requested. Production, installed workers and the Riker backup are unchanged.

## D3b fifth step: bounded staging and completion seals

Worker single-shot and resumable delivery now acquire the SQLite writer, reload current
credentials/lease/job/source and check shared process/durable file reservations immediately
before creating a directory/file or appending a bounded buffer. Each write is at most 1 MiB;
flush and handle close complete inside the fence. Network reads take place outside it. Offsets
are checked again inside the writer, so independent coordinators cannot replay an append.
Source paths/hashes, media ID, worker holder, job and lease attempts, output container and output reference
must still match. A generation fences a superseded request, including its interrupted cleanup.
Reserved staging and diagnostics are preserved. Interrupted resumable writes report the real
file length on the next offset query, including bytes flushed before an interrupted commit.

An additive migration records each lease's upload generation, source/attempt binding, staging
path and optional sealed byte count. The schema rejects partial ownership records and negative
sealed lengths. Existing leases default to no upload record; legacy partials can be adopted
without truncation. A new single-shot generation replaces only unsealed staging under the same
fence. Interrupted single-shot cleanup uses a separate bounded cancellation token, fresh writer
and generation check; failed or reserved cleanup retains its ownership and bytes for retry.

Completion persists a seal before full-file hashing outside the writer. Every append or
single-shot retry observes that seal across coordinators and restarts. Full hashing counts bytes
actually read; acceptance checks the same generation/binding, sealed length, current assignment,
reservations and physical length before promotion. A competing completion cannot mutate a
newer generation or overwrite a different final file. If promotion succeeds but commit fails,
the seal and final bytes remain, and retry hashes that final candidate to finish the same
assignment. Offset queries report the sealed final bytes after promotion, so the real resumable
worker reaches completion instead of trying to append at zero. A hash mismatch clears ownership only after staging removal succeeds. This contract
coordinates application upload operations; it is not exclusion against an external file editor,
unknown parent aliases, arbitrary SQL or every remaining runtime scratch helper.

### Measured design choice

The disposable [study](../../tools/Optimisarr.UploadStudy/Program.cs) compares the actual append
fence with an optimistic durable-owner prototype, using 256 MiB synthetic transfers, three
passes per variant and a concurrent independent SQLite control writer on each host. The
prototype assumes an owner already excludes every other file writer; it does not implement
reservation integration, owner fencing or crash reconciliation and cannot be shipped as measured.
[Raw results](worker-upload-staging-study.json) include every pass and writer latency/sample count.

| Strategy | Mac MiB/s range | Quark MiB/s range | Observation |
| --- | ---: | ---: | --- |
| Guarded 128 KiB | 110-205 | 35-61 | Repeated transactions dominate and control writers can wait over a second. |
| Guarded 1 MiB, no pause | 633-1,751 | 226-515 | Some passes complete before the other writer gets a turn. |
| Guarded 1 MiB, 1 ms pause | 225-408 | 179-256 | Selected: competing writers make progress during every pass. |
| Guarded 1 MiB, 5 ms pause | 68-93 | 100-107 | Extra delay costs throughput without consistently improving maximum wait. |
| Ideal durable owner | 5,367-11,925 | 2,758-3,333 | Faster upper bound with substantially more ownership/recovery work still required. |

The selected pause runs outside transactions/claims and keeps bounded memory and I/O while
avoiding exclusive ownership over a network wait. It is a measured implementation choice,
not a hard fairness or latency guarantee: maximum competing-writer waits were about 304 ms
on Mac and 902 ms on Quark. These are SSD/page-cache fixtures without fsync or real network
traffic. They do not qualify USB/SMR storage, production latency or external-writer exclusion.
SQLite serializes writers across connections, which is the authority used by both preparation
and staging; see [SQLite transactions](https://www.sqlite.org/lang_transaction.html).
File buffering must be flushed/closed within that authority; see
[.NET stream I/O](https://learn.microsoft.com/en-us/dotnet/standard/io/).

The final native Release builds on 12 October have zero warnings. Mac and Quark each pass
3,109 backend tests, including 30 new cases covering reservation/body-read races, independent
coordinator offsets and both writer orderings, cancellation/revocation/reassignment/expiry,
persistent source/holder binding, older attempts, interrupted cleanup/resume, completion seals,
changed lengths, no-overwrite final paths, promotion-before-commit retry and migration
preservation/idempotency/schema constraints. Both hosts pass all 325 Chromium tests, 95 frontend
units and zero-warning Svelte checks. All 770 backend plus two study-tool source/fixture hashes
and 188 web hashes agree. Python (105), OpenAPI, release, documentation and Unraid checks pass.
Picard remains unavailable and excluded as requested. No UI or installed sidecar changed;
production services and the Riker backup are untouched. Required exact-head CI is recorded in
the delivery PR before merge. D3 file actions remain disabled.

## Remaining D3 work, in dependency order

1. Complete cleanup, inventory reconciliation and import/manager ownership coordination after
   transactional job admission and bounded retention/delivery finalization. Fence remaining runtime scratch helpers before their filesystem I/O. Upload staging and
   interrupted-transfer cleanup are fenced as described above. Bounded startup recovery is guarded as described above;
   cleanup of unowned disposable trees still needs qualified ownership. Cover aliases of parent
   directories for absent destinations. Capture every physical alias and all affected references without silently widening the selected scope. Add races between each
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
