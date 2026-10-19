# Shared native-work accounting

Tracked by [#332](https://github.com/Jellman86/optimisarr/issues/332) and
[draft #415](https://github.com/Jellman86/optimisarr/pull/415). The shared .NET ledger and
checkpoint protocol and serialized transaction boundary are **unconnected foundations**. Standalone workers do not yet use durable
startup reconciliation, and protocol 11 remains disabled. The connected Mac runtime has its
own [qualified implementation and limits](../engineering/hardware-validation/2026-10-15-native-work-runtime.md).

## Recorded obligation

`NativeWorkLedger` schema 1 stores a nonempty store ID, a nonnegative revision and at most
16 obligations in a 256 KiB document. Each obligation records:

| Field | Meaning |
| --- | --- |
| Owner and purpose | A fresh owner nonce; either job or capability work. Job work also requires a positive job ID and nonempty lease. Capability work carries neither. |
| Root and scope | The actual absolute, normalized working root and its pinned physical root/control/gate identities. |
| Volume identity | An opaque physical volume identifier obtained from that same pinned root; a mount path alone cannot establish ownership. |
| Native memory and scratch bytes | Separate positive peak requirements. Memory charge adds scratch only for memory-backed storage; disk-space admission still reserves the working filesystem's scratch capacity. |
| Phase | Intent, Authorized, Retired or Released. Every phase through Retired retains the complete charge. |

Snapshots use immutable collections. Encoding has fixed property order, explicit nulls, string
enums, GUID spelling and escaping. Decode accepts only exact canonical bytes and rejects unknown,
duplicate, omitted, malformed or oversized fields. Golden tests freeze the format; any future
encoding change needs a new schema and explicit migration.

Transition validation requires the same store and exactly one checked revision increment.
An existing owner's purpose, job/lease, root, scope, volume and charges cannot change. Newly
admitted owners start at Intent. Authorized work can retire, and Retired work can release.
Intent can retire without granting execution permission only when the coordinator supplies a
scoped no-child-authorized cleanup proof. Missing owner metadata alone is insufficient.
Only Released records may be removed. The future coordinator must generate fresh nonces and
never reuse a released owner; the bounded snapshot does not retain unlimited tombstones.

## Independent checkpoint

`NativeWorkCheckpointProtocol` schema 1 binds one store to the journal's normalized path,
physical directory and coordinator-lock identities. Its committed SHA-256 covers the complete
ledger bytes. A pending transition adds exactly one permitted replacement hash after validating
the ledger transition. A pending save cannot be nested or overwritten.

Reopening accepts only the exact committed or pending snapshot. Missing authority, changed
bindings and older valid snapshots fail closed, including an old empty copy from before a debt
was admitted. Resolution removes pending authority for the selected bytes. These methods make
pure decisions; they do not persist, flush, lock or authorize a process launch themselves.

Initialization accepts only an empty revision-zero snapshot. The OS adapter may call it only
when provisioning a positively new store under exclusive ownership. It must not use this
method to repair a missing checkpoint for an existing journal, even when that journal is empty.
First-start crash recovery needs its own independently authorized bootstrap ordering.

## Resource pool identity

`PerceptualJobResourcePool.ForPhysicalVolumes()` creates a pool with one fixed physical-ID
namespace. `PhysicalVolumeId` preserves its exact value, including case and path-like characters;
it never passes through path normalization. IDs require nonblank, valid UTF-8 text of at most
512 bytes without NUL. Every typed pool boundary also refuses null or uninitialized identifiers.

The default pool retains its existing path semantics. Neither mode can change during its lifetime,
even when empty. Calling the wrong API mode refuses admission/restoration and reports zero
available capacity; it cannot introduce a separate namespace that hides another charge. Each
pool still shares one owner table and memory total across its volumes, with checked overflow,
idempotent restoration and exactly-once release.

The coordinator must inject the same physical pool into every native admission, recovery and
capacity-reporting caller. It must check every restoration result and hold admission when one
fails; zero reported availability is not proof that debt was successfully restored. The OS adapter
still needs to obtain a canonical physical ID and capacity from the same pinned root. Opaque IDs
must never be substituted with display names or mount paths.

## Serialized transaction boundary

The internal `NativeWorkJournal` owns an injected, exclusively locked storage lifetime.
`OpenExisting` never initializes missing authority. It selects only checkpoint-authorized bytes,
durably reconfirms that exact visible ledger generation, then saves and reads back the resolved
checkpoint. Ledger reconfirmation must synchronize the file and its containing directory before
old/new authority is discarded; reading visible bytes alone is insufficient after a failed rename sync.

Each commit validates the transition before I/O, verifies the existing ledger and checkpoint,
saves and reads back pending authority, durably replaces and reads back the ledger, then saves
and reads back committed authority. Physical bindings are checked throughout. The new immutable
snapshot becomes visible only after the final exact readbacks. Any I/O or binding uncertainty
poisons the instance, stops future effects and prevents a caught storage exception from reporting
successful execution. An invalid in-memory transition without I/O does not poison a trusted store.

One asynchronous gate spans preparation, the actual media body and finalization. Only the currently
issued preparation/finalization session can commit; the body receives a cancellation token. Phase
completion revokes that session and joins any synchronous commit already in progress. Retained,
manufactured or expired sessions cannot write, including after storage ownership ends. Preparation
and finalization must join their own work; the media body must await all owned children and previews.

Closing rejects queued work, cancels the active body and waits for its actual completion and
finalization before disposing storage. Active finalization retains write authority during closing;
poisoned accounting grants none. Preparation/body failures and cancellation still finalize, while
finalization failure holds further work. Cancellation observers cannot skip finalization. Callbacks
must not await disposal of the journal that owns their own lifetime.

The **44 injected-storage tests** cover every commit I/O boundary, lost write acknowledgements,
authorized old/new reopening, missing/changed authority, stale scopes, caught commit failures,
close during authorization/finalization, and a body that ignores cancellation. These are deterministic
transaction and lifetime tests, not physical power-loss, OS durability or packaged-worker evidence.

## Required integration

The portable coordinator and OS adapters must enforce this ordering:

1. Hold a stable, non-inherited coordinator lock; load the independent checkpoint and ledger.
2. Restore every outstanding charge before advertising capacity. Recover recorded actual roots
   even when the configured working folder changed, then reconcile legacy configured roots.
3. Persist intent, create the accounted workspace under its pinned scope, then durably commit
   execution authorization before any media child can launch.
4. Establish positive writer retirement, durably record Retired, consume its receipt, durably
   record Released, then release the process-local charge exactly once and compact separately.

A save must durably write and read back pending authority before atomic ledger replacement,
confirm file/directory durability, then commit and read back the selected checkpoint before
exposing effects. Reopen must durably finalize any allowed old/new selection. A failed or
uncertain commit must fence every old continuation; closing the store cannot release its lock
while an admitted operation or finalizer can still act.

Busy, incomplete or untrusted recovery must drain all claims while retaining pairing and
heartbeats. Live owners need explicit exclusion from maintenance. Cancellation and observer
failures cannot skip finalization. Capacity reporting, admission and recovery must share one
pool. The physical-ID API above is available, but identity capture and coordinator injection remain
required; existing standalone callers still use the path pool.

`NativeWorkJournalBootstrap` now supplies an unconnected Linux-only portion of steps 1–2.
Its normal open path requires an existing Linux store, opens it under the coordinator lock,
then restores every unreleased obligation into a newly created physical-ID pool. That pool is
published only with the successfully opened journal; restoration failure disposes the journal
and discards the staging pool. Provisioning is a separate explicit operator/install call and
is never a normal-open fallback. Windows and Darwin are refused before filesystem access.
There are still no service, admission, reporting, probe-gating or job-lifecycle callers, so this
module does not implement durable startup reconciliation in a standalone worker.

Windows requires a fixed machine-context, ACL-protected checkpoint separate from the journal
directory. Linux requires a private, no-follow checkpoint on persistent storage outside the
replaceable journal directory, with atomic replacement and file/directory synchronization.
Restoring both Linux files together from one configuration snapshot cannot be detected by this
two-file protocol; that limitation must remain explicit. No access or decode failure may fall
back to fresh empty authority.

The Linux adapter (`LinuxNativeWorkJournalStorage`) implements this with no-follow owner-only
directories, `statx` identities, an exclusive coordinator lock and file/directory `fsync` at
every boundary. Its persisted binding key must survive a remount: Btrfs assigns anonymous
`st_dev` numbers per mount (observed `0:51 → 0:34 → 0:51` across three guest boots, with the
journal bytes intact), so on filesystems whose `fstatfs` `f_fsid` is UUID-derived (ordinary
Btrfs, ext4) the key is `linux:<f_type>:<f_fsid>:<inode>` and a zero `f_fsid` is refused. XFS
derives `f_fsid` from the device number and ZFS may substitute one, so there the key keeps the
device identity and a changed device fails closed; remount stability is **not qualified** for
XFS, ZFS or cloned Btrfs until a filesystem-specific identity passes its own remount trials.
In-process validation keeps the full `statx`/`statfs` identity (device, mount and `f_fsid`)
strict. Stores provisioned under the earlier device-only key refuse on binding mismatch — the
adapter is unconnected and unreleased, so no migration is offered. An exact copy of both
journal and authority on a cloned volume remains indistinguishable, as the two-file protocol
already states. A first Windows design built on the shared workspace directory with
`FlushFileBuffers` on directory handles was reviewed on 19 October 2026 and **rejected**: no
documented Win32 guarantee equates a directory flush with `fsync(directory)` after replacement,
the shared workspace walk resolves reparse ancestors before checking them, and its handle
sharing conflicts with atomic replacement. A second review the same day allowed an
**unconnected experimental candidate** (`WindowsNativeWorkJournalStorage`, NTFS only):
a journal-specific no-follow walk retaining every ancestor handle, objects created with a
protected private descriptor, write-through temporaries renamed by pinned handles, 128-bit
`FILE_ID_INFO` identities and security revalidated through pinned handles. Microsoft documents
that a write-through request causes NTFS to flush resulting metadata changes, including a rename
([CreateFileW caching behaviour](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew#caching-behavior));
applying that sentence to this exact rename sequence remains an inference. The candidate
claims only what its Windows tests demonstrate in a running OS — private creation-time ACLs,
identity and exclusive-ownership checks, exact visible readback and fail-closed recovery.
It may not claim directory synchronization, Linux-equivalent namespace durability,
power-loss-safe commits, race-free predecessor replacement (an identity recheck narrows the
same check-to-rename interval Linux has), or protocol-11 qualification. Its admission also
requires the running account to hold write access on the parent so directory flushes can open
their handle. Physical/VM power-cut
qualification covering bootstrap, every commit write and restart reconfirmation remains the
release gate, and the candidate stays unwired from admission until then.

Standalone service bootstrap/admission wiring for every platform, the Windows candidate's
admission path, a macOS .NET adapter and coordinator effect ordering remain unimplemented.
Their acceptance must cover every
commit boundary, contention, stale continuations, previous roots, active-owner exclusion,
capability cancellation, callback failures and same-heartbeat overclaim. See the
[parity matrix](adaptive-audio-image-parity.md) and
[component evidence](../engineering/hardware-validation/2026-10-15-native-process-ownership.md).
