# Identity-bound Mac RAM retirement, 15 October 2026

Development component evidence for [draft #415](https://github.com/Jellman86/optimisarr/pull/415).
The production native runner remains on private helper protocol v1; startup/runner integration
and full app/tool hard-death acceptance are still open. Automatic protocol 11 remains disabled.

## Physical identity and cleanup

The new lifecycle component captures the current kernel boot UUID, exact IOMedia and original
disk-image backing-driver registry IDs, volume UUID, original mount/root identity and the complete
allocation size. A bounded `hdiutil info` snapshot taken while the original raw/root handles remain
pinned must identify exactly one matching image with the expected `ram://` sector count. Physical
disk images or changed roots cannot borrow a freshly created RAM disk's provenance. The parent ledger's development format is now version 2 so the backing-driver identity
is mandatory; unsupported older private formats fail closed. No production journal was activated
and this is not a database schema change.

Cleanup opens and retains the raw device, validates its kernel device mapping and original media
identity, then validates the original volume/root through an event-only directory handle. It uses
nonforce `funmount` on that exact volume handle and `DKIOCEJECT` on the retained device handle.
A surviving writer vetoes unmounting. After an interrupted unmount, the original media/backing
and volume identity can finish ejection without reopening a potentially reused mount path.
Both original media and backing driver must be confirmed absent before retirement succeeds.

These choices follow [Apple's descriptor-based unmount implementation](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/vfs/vfs_syscalls.c)
and [storage driver's open-minor/eject behavior](https://github.com/apple-oss-distributions/IOStorageFamily/blob/main/IOMediaBSDClient.cpp).
Device or mount names alone are not retirement authority. The tested host permits these operations
as its normal logged-in user, without privilege escalation or force.

IOKit's optimized missing-ID lookup returned success with a null iterator on this physical host.
That alone cannot establish absence: [the kernel can return success while wrapping a null iterator](https://github.com/apple-oss-distributions/xnu/blob/main/iokit/Kernel/IOUserClient.cpp).
The component therefore requires a complete, valid, bounded service scan checking both original
IDs. Invalid iteration, unreadable IDs, allocation failure, cancellation and scan limits remain
unconfirmed. Acquired registry objects are released on every path.

Caller waits are bounded independently of OS calls. Cancellation or timeout returns unconfirmed;
the physical allocation's shared in-process slot remains held until the underlying operation
actually exits. A later lifecycle instance cannot start overlapping work on the same allocation.
Checks between effects prevent a late unmount from initiating ejection after permission expires.

The SDK marks `funmount` available from macOS 16.0 (macOS 26+ under current release numbering).
The symbol is weak-linked and guarded; older OS versions cannot use this new capture path.
Production wiring must choose the ordinary conservative disk fallback before creating RAM on
unsupported hosts. No older-OS execution claim is made from the weak-link inspection alone.

## Verification

Six explicit live tests passed on the physical Mac with disposable approximately 49 MiB RAM
allocations:

- An open writer vetoes unmount; its copy remains intact. Closing it allows retirement and replay.
- Changed root or volume UUID retains the original allocation.
- The OS actually reuses the first allocation's BSD name for a second disk. Replaying the first
  identity leaves the second disk and its copied sentinel intact.
- Redirected device/mount fields and a wrong backing-driver ID cannot eject either of two live disks.
- Permission withdrawn immediately after successful unmount returns unconfirmed; a later replay
  retires the original unmounted allocation and replays successfully again.
- A disposable file-backed HFS+ image with the expected name prefix and allocation size is rejected.
  This failed before the RAM-source provenance check was added.

Six ordinary tests cover timeout, cancellation, pre-cancellation/invalid identities, cross-instance
coordination, invalid iteration, complete absence versus a present target, scan limits and ID-read
failure. Live fixture cleanup uses captured physical identity even when the mount has disappeared.
Five command-lifecycle tests cover normal exits, failure, timeout, output limits and a live descendant
retaining stderr. The latter reproduced a reader/descriptor leak before the fix: timeout returned
while one reader remained blocked. Nonblocking reads and bounded polls now stop both readers
before the descendant is released. Oversized stdout/stderr is refused, and success requires both
real EOFs. Pure provenance checks also cover wrong sectors, ambiguous devices and file-backed images.

The final Swift suite completed successfully with 425 registered core tests and nine UI tests;
live/paired gates are opt-in and the six new RAM tests passed separately. All 18 tests in the four
RAM/provenance/command suites also passed together with the live gate enabled. Release build passed
with three existing Keychain API deprecation warnings; the new component adds none. The release
binary's `funmount` reference is weak. Astra reviewed the implementation and regressions and found
no further actionable defects after its findings were fixed; it did not independently execute tests.

Initial Swift live tests refused post-ejection confirmation because of the null-iterator behavior;
the corrected lookup and explicit invalid-iterator tests now pass. The original RAM driver and
attachment disappeared even in those refused responses. Final readback found no test RAM images
remaining. The block-device experiment refused opening a mounted volume; the qualified path uses
the retained raw device instead. Private evidence keeps those negative results and exact source hashes.

No original media, installed pairing, settings or services changed. Small disposable RAM fixtures
do not qualify full-image memory admission, a newly signed package, app restart/reboot behavior or
the complete [audio/image parity contract](../../development/adaptive-audio-image-parity.md).
Connect these components to production accounting and qualify typed RAM creation failures and
complete app/tool lifecycles before closing the P1 defect or activating protocol 11.
