# Native working-file recovery qualification, 15 October 2026

This is development evidence for [draft #415](https://github.com/Jellman86/optimisarr/pull/415)
and [audio/image parity](../../development/adaptive-audio-image-parity.md). It does not qualify
full parity, an installed upgrade or automatic protocol-11 placement. Tests use isolated
configuration, synthetic copies and task-owned processes. Installed services and personal
media are unchanged.

## Ownership and recovery

Each native job or capability proof creates a fresh, private working directory and a bounded
owner record in `.optimisarr-native`. A stable, non-inherited `root.lock` serialises creation,
recovery and retirement. The exclusive owner handle is inheritable: a native tool keeps the
ownership veto after its parent is killed. Recovery acquires that same exclusive guard before
inspecting or removing anything. Time, PID, file age and apparent inactivity never authorise
native cleanup. Legacy or unrecorded working trees are not adopted.

Recovery compares physical root, coordinator, record, work-directory and marker identities.
POSIX traversal uses opened directory descriptors and refuses descendant symlinks; Windows
holds directory handles and refuses reparse points. Nested mounts, shared files, special files,
changed identities and incomplete records retain their data. The complete bounded tree is
inspected before deletion. A flushed deletion-intent record precedes the first removal;
interrupted deletion can resume after a missing marker or retire after the work tree is gone.
Unknown control filenames remain untouched and cannot consume owner pages or become invalid
continuation cursors. Recognised records with invalid contents remain explicit refusals.

Recovery pages contain at most 64 owners by default, with an explicit continuation cursor.
The coordinator scan stops at 4,096 entries; cleanup stops at 128 tree entries and depth 16.
Swift processes at most four pages per root under a cancellable deadline. Bounds and unknown
files can retain work for later attention; none permit an unbounded deletion fallback.

The shared job runner recovers before reserving capacity and holds ownership through search,
verification and delivery. Capability qualification uses the same guard. A private helper
`recover` operation needs no pairing, assignment, memory grant or media tool execution and
never creates a missing root. Swift invokes it at startup, pairing, working-folder changes and
before native job admission, including the default and synthetic capability roots. Unpair,
sleep, quit and caller cancellation stop pending recovery. Unavailable recovery returns a
native job as temporary capacity pressure rather than a permanent file-refusal opinion.

## Tests and actual host trials

The same C# source snapshot is checksum-verified on Mac, native Linux and native Windows.
Zero-warning Release builds and the shared suites pass: **509 passed plus three Windows-only
skips** on Mac and Quark; **512 passed** on Picard. The suites include forced parent death with
a surviving managed child, live-owner exclusion, partial deletion replay, malformed records,
redirected/shared files, bounded paging, unknown-name starvation and pre-cancelled recovery.
Backend tests pass **3,522**; Linux worker tests pass **31**.

The Mac passes **366 core and nine UI tests**, including real RAM creation, media-job cleanup,
busy-volume retention and recovery cancellation. Release compilation succeeds; the existing
SDK Keychain deprecation diagnostics remain. Python validation passes **154 tests** and the
browser suite passes **346**. Scripted fixtures that report unavailable dependencies remain
fixture-output assertions, not additional hardware qualification.

A separate actual-FFmpeg trial passes on each physical host:

1. Create an owned source copy with the product's `NativeScratchWorkspace` and start FFmpeg.
2. Confirm recovery reports a busy owner; forcibly stop only the parent.
3. Confirm recovery still reports busy and the source copy remains unchanged.
4. Stop the owned FFmpeg process; confirm recovery removes its working tree and owner record.
5. Confirm the independent original fixture is unchanged and only the empty coordinator remains.

The rebuilt Developer ID signed Mac helper additionally passes that trial on a real RAM volume
through the actual Swift recovery bridge. Ejection fails while the surviving FFmpeg holds its
inherited guard. After FFmpeg exits, Swift recovery reclaims the work and the volume ejects.
A regression trial reproduced the previous forced-detach behaviour before the fix. Normal
job cleanup now uses non-forced ejection; a failed eject keeps the RAM reservation. Creation
failures before any media writer starts retain their separate device-cleanup path.

Relocated signed-bundle verification passes managed startup, JPEG/WebP compressed ICC/EXIF
and marker operations, both pinned metrics, nested minimum-OS checks and all 20 rendered UI
fixtures. Its scratch check permits only the ordinary, empty stable `root.lock`; media, owner
records, links and unexpected control files fail qualification. Failed package checks preserve
working trees rather than recursively deleting files still held by surviving tools.

Raw logs, source/package hashes and original/copy checksums remain in the private qualification
record. Test executables stop their owned children; no live trial leaves an encoder or RAM
volume running.

## Remaining limits

These trials prove process-death/restart ownership and specific packaged tool operations.
They do not prove complete packaged audio/image job restart/reconnect, a machine reboot,
filesystem remount or device renumbering. Persisted physical identities can conservatively
refuse recovery after a changed volume identity. Torn or unknown ownership evidence remains
on disk. Those cases require further qualification; age-based cleanup must not bypass them.

Exact packaged workflows, positive image execution under real Mac memory admission,
fixed-image workers, adaptive selection for every retained re-encoded soundtrack,
preview/calibration guidance, broader source/timing and retry/history/export acceptance
remain in the [completion matrix](../../development/adaptive-audio-image-parity.md).
Protocol 10 stays advertised and protocol 11 stays disabled.
