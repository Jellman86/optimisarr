# Shared native-work accounting

Tracked by [#332](https://github.com/Jellman86/optimisarr/issues/332) and
[draft #415](https://github.com/Jellman86/optimisarr/pull/415). The shared .NET ledger and
checkpoint protocol are a **pure foundation**. Standalone workers do not yet use durable
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

Windows requires a fixed machine-context, ACL-protected checkpoint separate from the journal
directory. Linux requires a private, no-follow checkpoint on persistent storage outside the
replaceable journal directory, with atomic replacement and file/directory synchronization.
Restoring both Linux files together from one configuration snapshot cannot be detected by this
two-file protocol; that limitation must remain explicit. No access or decode failure may fall
back to fresh empty authority.

These adapters and effect ordering remain unimplemented. Their acceptance must cover every
commit boundary, contention, stale continuations, previous roots, active-owner exclusion,
capability cancellation, callback failures and same-heartbeat overclaim. See the
[parity matrix](adaptive-audio-image-parity.md) and
[component evidence](../engineering/hardware-validation/2026-10-15-native-process-ownership.md).
