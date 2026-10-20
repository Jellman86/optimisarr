# Registry-backed Mac RAM creation, 15 October 2026

Development component evidence for [draft #415](https://github.com/Jellman86/optimisarr/pull/415).
Startup/runner integration is now implemented with [separate qualification limits](2026-10-15-native-work-runtime.md). Protocol 11 remains disabled.
These results qualify the factory boundaries described here, not complete media parity.

## Creation and recovery

The registry records creation intent before attaching a tagged RAM image, then persists the
original owner, boot, physical media/backing IDs and allocation size before permitting formatting.
Only the live factory allocation can format or mount; a serialized recovery receipt cannot.
The original raw-device descriptor is inherited as standard input by the fixed native tools.
Creation retains that pin and the shared physical exclusion slot until the actual child exits,
even when cancellation or timeout has already returned to the caller. A cancellation/launch
gate prevents a command starting after cancellation won the permission race.

The private mount parent must be an existing, local, owner-only directory. Canonical paths and
no-follow mounting avoid symlink aliases. Retained parent and covered-leaf descriptors prevent
cleanup following a replaced directory. The mounted filesystem is measured through a new root
handle, including its volume UUID; cached Disk Arbitration metadata can lag direct mounting.
The full identity and root are durable before helper scope preparation. A restart at this
boundary therefore restores the charge and can retire the actual allocation without inventing
a helper receipt.

Factory creation excludes finish/recovery while its registry call is active. After a bounded
caller return, the physical exclusion slot still blocks retirement while an owned command
survives. Late durability callbacks cannot revive a cancelled creation attempt. Unknown
allocation outcomes remain charged until the boot changes. Known partial or mounted allocations
release charges only after positive retirement; partial fallback cannot unmount a filesystem.

## Physical and signed-process qualification

Three new physical 48 MiB factory cases pass on the current Apple Silicon Mac:

- Durable mounted identity before helper scope; a busy disposable copy prevents retirement.
- Journal restart before helper scope; the full charge is restored and reconciled.
- Mount-parent preparation failure; the known partial allocation stays charged until retired.

An isolated Developer ID signed executable linked against the normal release library uses
the actual factory, journal and file-based Keychain checkpoint. Three SIGKILL boundaries pass:

| Durable boundary before parent death | Same-boot restart result |
| --- | --- |
| Creation attempted, before attach | Full charge restored; repeated recovery retains unknown-creation debt |
| Original partial allocation committed, before formatting | Full charge restored; original allocation positively retired; repeated recovery is idempotent |
| Mounted root committed, before helper scope | Full charge restored; original volume positively retired; repeated recovery is idempotent |

The normal signed case also mounts, writes and reads a disposable copy, retires and replays
recovery. All four temporary test anchors are removed. Before/after RAM-image inventories agree.
The intentionally unknown, pre-attach fixture leaves private forensic ledger evidence; it
created no RAM image. No installed credentials or original media are changed.

Separate Foundation/native-tool probes kill the parent before releasing a gated child into
the real formatter or mounter. The inherited original device pin survives parent death and
both native commands complete against the original allocation. These are narrow tool-lifetime
probes, not exhaustive app death during a packaged encode or machine reboot qualification.

Unit regressions cover suspended durability callbacks, late cancellation, format/mount commands
ignoring termination, shared physical exclusion, mount-parent replacement and symlink cleanup.
Live fixtures register cleanup before allocation, use production identity validation and remove
mount directories nonrecursively. Unconfirmed cleanup retains its recovery evidence.

## Source verification and limits

The Swift core suite registers 466 tests plus nine UI tests. The separate live-RAM gate registers
17 tests; its FFmpeg-only legacy job case remains explicitly skipped without its tool variable.
Release builds retain two existing legacy Keychain API deprecation warnings.

Native Mac and Quark backend suites pass 3,526 tests. Picard passes 3,503 with 23 platform skips.
Quark shared tests pass 547 with three Windows-only skips; Picard passes all 550, and Quark
passes 31 Linux tests. The .NET builds have zero warnings. A loaded Windows CI scan test had
returned `Running` at its fixed two-second test wait. It now polls under a bounded 30-second
cancellation token while retaining the same result, authorization and media-preservation checks;
this changes no product timeout.

The first separate live run returned unconfirmed retirement once immediately after a manual
fixture unmount; its identity-bound cleanup subsequently retired the allocation, and no RAM
images remained. That failed attempt is retained as evidence rather than counted as a pass.

Production anchored startup recovery, registry-backed execution, full-encode process reaping,
exact-package crash/reconnect/reboot/remount trials, positive live Mac image admission,
fixed-image execution, adaptive retained soundtracks and preview/calibration parity remain open.
See the [completion matrix](../../development/adaptive-audio-image-parity.md).
