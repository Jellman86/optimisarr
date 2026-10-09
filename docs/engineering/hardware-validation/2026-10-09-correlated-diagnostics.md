# Correlated diagnostic acceptance: 9 October 2026

This development change implements opt-in correlated diagnostic capture across the server
and native Mac, Windows and Linux sidecars. The physical runs use disposable workers and
isolated servers, generated 16-second SDR media, strict sidecar verification, ordinary server verification with
VMAF shadow comparisons, and the existing
quality, timing, decode, size and replacement gates. Production libraries, installed pairing
and managed container lifecycle were not changed by these runs.

The physical matrices below exercise development revision `89bc9361`, including the
confirmed Opus 5.5 review fixes. Later playback-artwork and export-dialog updates have
separate backend, native and browser coverage below. These development runs do not certify
an installed release.

## Physical media matrix

| Host | Actual worker path | Strict worker verification | Ordinary server verification + VMAF shadow |
| --- | --- | --- | --- |
| Mac | Native Swift / HEVC VideoToolbox | 32 checks passed | 32 checks passed |
| Picard | Native Windows / HEVC NVENC, RTX 4070 | 32 checks passed | 32 checks passed |
| Riker | Linux worker in the existing Intel container / HEVC QSV | 32 checks passed | 32 checks passed |
| Quark | Linux worker in the existing Intel sidecar container / HEVC QSV | 32 checks passed | 32 checks passed |

Each run proves capability discovery, GPU encoding and decoding, strict verified delivery,
adaptive quality, subtitle/container handling, quality rejection, audio gates, cancellation,
worker placement, concurrency isolation, reconnect after server restart, generated damaged
candidate rejection, replacement and exact rollback. The isolated server runs on the Mac;
remote workers fetch and upload through HTTP from their physical hosts. Existing containers
provide their installed GPU tools and runtime; only owned disposable processes are launched.
These results do not certify a newly installed release or every possible media/GPU combination.

An earlier concurrent ordinary-verification matrix at `94aa4632` rejected one server-local AV1 calibration
candidate during Quark's run because decoding reported invalid data. The original remained intact
and the diagnostic bundle retained its frozen failed decode report. Quark's complete isolated
rerun passes all 32 checks with the same fixtures, encoders and verification gates. The first
failure remains in the evidence; the rerun does not establish its root cause.

## Diagnostic evidence

All four runs collect authenticated local sidecar records, frozen server verification reports,
transfer acknowledgements and replacement outcomes into redacted schema 4 bundles. Every real
worker acknowledges final collection with zero local rotation omissions. The synthetic
protocol-fault worker is explicitly identified as unsupported for local capture.

The local journals also export successfully: 59 acknowledged records on Mac and 53 on each
other worker in the initial complete matrix. Tool SHA-256 identities are present; both Linux
hosts identify their separate VMAF FFmpeg executable independently of the encoding executable.
The bare Swift acceptance executable has no application bundle version, so its version is
honestly unknown; packaged Mac builds read their actual Info.plist version/build.

Collection pins the first capture, restarts the server, proves its event history and pin survive,
and proves a subsequent default capture stops on restart. A database integration test models
a rejected Mac/VideoToolbox attempt followed by a software-decode retry on Picard/NVENC and
verifies both verdicts remain after reopening. This is a deterministic regression scenario,
not a claim that the historical real-source defect was reproduced during these generated runs.

## Automated verification and defects caught

The change adds tests for explicit consent, expiry, job scope, worker/lease ownership, replay,
final receipts, local rotation and recovery, secret exclusion, frozen reports, restart defaults,
pinning, failure retention, stale tracked storage counters, scheduling reasons, migration upgrade
and repeat application, export size and receipt-time ranges. Browser tests cover retained
historical gates, Stop and collect/pinning on desktop and phone layouts, and local Linux export
without a server connection. Final local checks pass 2,899 backend tests, all 297 browser tests in each of Chromium and
WebKit, 97 frontend unit tests, 283 Swift tests, three live Mac RAM-volume checks, 328 shared
sidecar tests (three Windows-only skips), 31 Linux dashboard/host tests and 101 Python harness
tests. Native Picard passes all 331 shared tests and 2,891 backend tests with the eight
documented POSIX-only skips. Quark and Riker each pass all 2,899 backend tests, 328 shared
tests with the same three Windows-only skips, and 31 Linux tests on their native hosts. Picard’s Ubuntu WSL environment passes 2,899 backend
tests, 328 shared tests with three Windows-only skips, 31 Linux tests and 99 Python tests.

Testing caught and fixed several concrete faults: expired local records remaining on disk,
unsafe recovered Swift fields, capture enabled mid-job missing its stage/tool identity,
a rejected report being cleared before the capture interceptor could retain it, a stale storage
counter bypassing the cap, final receipts hidden by older worker registrations, pretty-printed
JSON nesting exceeding the export byte budget, cleared optional job inputs retaining an invalid
numeric state, Stop and collect inheriting an earlier manual-download time filter, and a second
Windows tray journal instance pruning newer service records. Tray exports now use a read-only
snapshot; tests prove newer, expired and oversized service files remain untouched. The remote harness now explicitly disables
observations of the Mac's local dashboard and scratch when its worker command launches over SSH.

Actual Opus 5.5 UI and backend reviews identified additional defects. Regression tests now
cover transaction lock ordering, exact session and attempt attribution, concurrent report
arrival, cancellation and newer-attempt races during automatic replacement, bounded omission
reporting, secure journal recovery and acknowledgement, active controls while browsing older
captures, failed collection polls, retention validation, clipboard fallback, delete confirmation
and the diagnostic Settings anchor. No semaphore is held across database write acquisition.

A headed browser against an isolated real server also passes desktop and phone start, stop and
collect, pin/unpin, delete cancellation and confirmation, invalid-range guidance, cleared-input
validation, selection reset and banner-to-panel refresh. Neither viewport overflows horizontally
and no page exception occurs. Refreshed screenshots use fabricated data. Playback holds show exact film/show
posters and square album covers proxied from Plex, Jellyfin and Emby. Tests cover
header-only credentials, disabled watchers, unknown keys, non-image/redirect/oversized
responses, private caching, viewer privacy and stable missing-image fallbacks.
A real isolated API with fabricated provider responses verifies all three proxy paths
and rejects unauthenticated image requests. Picard opens the
actual native Save As dialog in its logged-in session; it starts in Documents, and
both saving a valid local diagnostic JSON export and cancelling exit the helper with the service
and journal unchanged. The saved fixture is copied to private evidence and its owned Documents
file is removed after hash verification. The unelevated tray requests normal administrator access
for export; automated tests did not approve a UAC prompt.

Native Windows shared tests include the Event Log cases. The native backend suite retains its
eight documented POSIX-only skips; native Windows server hosting remains outside supported
production deployment scope. Linux shared tests retain three Windows-only skips. Mac RAM-volume
checks create and remove real test volumes. No verification gate was relaxed to obtain a pass.

## Evidence boundaries and operation

Private raw logs, generated media, credentials, addresses and disposable host paths remain
outside the repository. The maintained harness supports `--diagnostics`, and the server smoke
and paired Linux fleet CI runs now exercise collection and restart. See the
[acceptance guide](../../development/media-acceptance.md#correlated-diagnostic-acceptance).

Bundles are bounded summaries. Unknown legacy provenance, missing offline records, local
rotation and omitted context are explicit; arbitrary process logs and commands are excluded.
A manifest reference identifies the selected captured history, not the entire mutable export.
Release deployment must follow the managed-stack runbook and verify the exact installed
sidecar revision separately from these disposable acceptance processes.
