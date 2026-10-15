# Native Mac partial RAM allocation, 15 October 2026

Development component evidence for [draft #415](https://github.com/Jellman86/optimisarr/pull/415).
The production runner/startup and typed allocation factory remain unconnected. Protocol 11
stays disabled; this record does not establish complete app-crash or media-workflow parity.

## Interrupted creation

A RAM image can exist before formatting, mounting or helper workspace preparation finishes.
The parent now records its original boot, owner nonce, media/backing IDs, device and exact byte
allocation separately from the mounted filesystem identity. Capture requires the owner nonce
on the backing driver, the exact media size and a matching `ram://` image. The documented
`hdiutil -drivekey` option carries the fresh owner nonce into the backing driver's registry.

The registry persists creation intent before allocation identity, requires that identity before
mounted preparation and checks owner, boot, size and physical disk agreement. Unknown creation
remains charged until the boot changes. A known partial allocation remains charged through an
unsuccessful retirement or journal write. Recovery restores both native and RAM obligations.
The development ledger is now schema 3; unsupported prior private formats fail closed. No
production journal or database migration is activated by this component change.

A partial receipt authorizes neither formatting nor unmounting. Retirement validates the
original tagged physical allocation and pins a fresh raw handle after provenance probes end.
A mounted filesystem or surviving device user vetoes nonforce ejection. Positive completion
still requires confirmed absence of both original registry IDs. Partial and mounted receipts
share the same in-process exclusion slot, including operations that outlive a caller timeout.

The journal also explicitly unlocks its acquired coordinator before closing it. Close-on-exec
alone leaves a fork-before-exec interval in which a child can retain the same open-file
description. A deterministic duplicate-descriptor regression reproduced a spurious contention
failure before this fix. Failed contenders cannot unlock the incumbent journal.

## Qualification and next factory work

Three physical 48 MiB fixture cases pass on the current Mac:

- Capture and retire an unformatted tagged image; wrong owner, boot or size cannot qualify it.
- Reuse the actual BSD device name; replay of the old receipt preserves the new allocation.
- Mount a disposable filesystem; a partial receipt preserves its copy and refuses unmounting,
  then succeeds after the fixture closes its exact root and explicitly unmounts it.

All physical RAM fixtures share a serialized parent suite so their device-reuse assertions do
not compete. Cleanup is registered immediately after allocation. Unconfirmed fixture cleanup
retains a recovery intent rather than detaching a stale name. No original media is involved.

Separate development probes showed that direct `mount_hfs` works as the normal user at a private
owned mountpoint, and the owner tag and media/backing IDs survive formatting and mounting.
Disk Arbitration metadata can initially lag behind that mount. Factory integration must use
the actual mounted root, canonical paths with no-follow mounting, and an inherited original
device pin through confirmed child termination. Those Foundation inheritance/parent-death
and complete factory boundaries still need qualification. Creation authority must end before
recovery can retire the allocation. Disk scratch remains the conservative fallback where the
qualified direct-mount path is unavailable.

The [existing retirement record](2026-10-15-native-ram-retirement.md) describes descriptor-bound
mounted cleanup and OS support. [Apple's storage implementation](https://github.com/apple-oss-distributions/IOStorageFamily/blob/main/IOMediaBSDClient.cpp)
explains why all creation/probe raw opens must close before acquiring the fresh ejection pin.

The final documented gates passed: 449 registered Swift core tests plus nine UI tests,
a separate live-RAM run with 14 registered tests (the FFmpeg-only case remains explicitly
skipped without its tool variable), and the release build with two existing legacy Keychain
API deprecation warnings. The nine identity/partial-allocation physical cases executed.
Documentation links and Unraid metadata checks also passed. C# and frontend sources are
unchanged; their exact prior head completed all CI checks successfully.

The first combined all-tests/live run exposed the inherited journal-lock regression and
competition between independent physical suites. After those fixes, a combined loaded run
hit existing session/termination timing assertions. The documented ordinary and separate
physical gates above passed with their original deadlines; the overloaded attempts are
retained in private evidence and are not reported as passes.

A rebuilt Developer ID signed accounting fixture also passed 35 actual Keychain operations,
including four SIGKILL transition stages, rejection of unrelated older ledger bytes and
unreadable-item retention. All six temporary anchors were removed. This verifies the current
release library's journal/checkpoint path, not an installed production app.
Astra's final static reviews found no further actionable defects; it did not run physical tests.
