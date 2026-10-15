# Native Mac runtime integration, 15 October 2026

Development qualification for [draft #415](https://github.com/Jellman86/optimisarr/pull/415).
The draft remains unmerged and undeployed. Protocol 10 remains advertised; automatic protocol
11 stays disabled. This record does not close full audio/image parity or packaged crash recovery.

## Admission and resource lifetime

The app and headless session now share one native-work runtime with their job runner. Its fixed
Application Support journal uses an independent file-based Keychain checkpoint. Startup restores
outstanding charges before checking tool availability. Store opening runs off the menu actor,
with a bounded waiter and one opening operation that survives waiter cancellation. Missing or
unreadable accounting cannot trigger a fresh store, a temporary-directory fallback or a reset.

Before new claims, reconciliation checks recorded actual roots and completes bounded legacy-owner
recovery. Busy, invalid, deferred or incomplete scans drain new work while pairing and check-ins
remain available. Completing the journal load alone cannot enable claims. The menu explains the
pause. An active native attempt excludes recovery through admission, preparation, execution and
independent finalization; heartbeats cannot retire a live attempt between reservation and launch.

The runner uses helper v2 and registry-backed reservations and RAM creation. Unknown allocation
outcomes retain their full charge. Disk fallback after an attempted RAM allocation requires
positive retirement. Helper preparation, final scope and physical-volume capacity checks precede
the final cancellation/live-memory check and running checkpoint. Delivery and resource retirement
remain separate: a surviving writer can retain accounting after successful upload. Finalization
cannot enable claims until a fresh complete recovery scan passes.

Recovery uses the parent's exact root/control/gate scope on every page. Missing coordination
inside an existing control directory is deferred. An absent or recreated control directory cannot
hide surviving native workspace leaves: a bounded root scan holds work without deleting anything
on filename evidence. Late claims after unpair, sleep, quit or re-pairing acknowledge interruption
independently of the cancelled heartbeat. An old server's lease is handled before checking for a
colliding job ID on the new server.
Native assignments end their claim batch so the next heartbeat refreshes native grants before
another offer; this avoids an unnecessary capacity cooldown while preserving legacy slot filling.

## Test-first evidence

Regressions reproduced premature finalization, stale final memory admission, legacy claims before
accounting recovery, incomplete scans enabling claims, missing coordinator metadata reporting clean
and late claims starting after draining. Follow-up tests cover moved/recreated control directories,
bounded root enumeration, cross-server job-ID collisions and failed interruption acknowledgement.
The regressions preserve source files, owner evidence and safety deadlines.

The final serial Swift gate passes 489 registered core tests and nine UI tests. The separate live
RAM gate passes 17 registered cases; its FFmpeg-only legacy job remains explicitly skipped without
the tool variable. Release builds pass with two existing legacy Keychain API warnings. Parallel
runs under host load produced timing failures and remain recorded as failures; serial execution
preserves every assertion and deadline.

Fresh zero-warning .NET builds pass on Mac, Quark and native Picard. Backend suites pass 3,526 on
Mac/Quark and 3,503 plus 23 platform skips on Picard. Shared suites pass 554 plus three platform
skips on Mac/Quark and 557 on Picard; Quark Linux passes 31. Every one of the 977 C# source/project
files matches across the final shared-suite qualification. On Windows the kernel vetoes relocation
of a control directory containing a live exclusively held owner file; tests prove that veto and
the retained busy result. Mac/Linux tests exercise the actual missing/recreated-directory cases.
Python 154, documentation and Unraid checks pass. The first browser run passed 336 of 346 tests;
ten visibility, animation or timeout failures are retained with traces. All ten pass in a focused
serial rerun, and the complete serial browser gate passes all 346 without altered assertions or
deadlines.

## Actual media boundary

A Developer ID signed disposable executable linked to the final normal release library completed
AAC, Opus and MP3 disk workflows and an actual AAC RAM workflow through the real Swift claim/job
runner and newly published self-contained helper. Every workflow exercised
bounded selection, a deliberately lost committed probe response, sealed upload, independent final
verification, repeated replacement and exact rollback. Worker admission used live Mac memory.
The test server's stored protocol-11 elevation and 8 GiB offer capacity were explicit fixture
inputs; this does not prove automatic protocol-11 placement or fresh server-side capacity collection.

Earlier attempts remain recorded: one omitted a required fixture proof path; two advertised no
audio encoder and received no assignment. The successful attempt advertised the installed AAC
encoder after verifying it. The RAM volume retired and its covered mount leaf was removed.
Temporary anchors were removed only after an independently anchored empty ledger was established.
Installed pairing, preferences, services and media originals were not changed.

The final signed Swift bridge and newly published helper also pass actual scope/recovery checks:
wrong scope defers, malformed/downgraded requests are refused, missing gates and missing/recreated
control metadata with an orphan leaf keep claims drained, and fresh complete recovery restores
readiness. Disposable orphan contents remain unchanged. The independently anchored empty test
store is positively checked before its temporary anchor is removed. This is bridge qualification;
the synthetic orphan is not a media-job parent-death trial.

The first final-binary AAC repeat correctly returned capacity-unavailable before downloading when
the Mac's live memory provider reported unknown capacity. An Opus attempt timed out during isolated
server migration before pairing or worker startup. Neither is a positive media qualification.

Two connected lifetime trials also pass. A private FFmpeg launcher delegates unchanged commands
to the installed FFmpeg and injects one detached child retaining the actual inherited owner guard.
This is a controlled surviving-writer fault, not evidence that FFmpeg ordinarily leaves that child.

| Connected boundary | Observed result |
| --- | --- |
| Delivery completes while the injected writer survives | The signed runner retains the full 512 MiB charge. Fresh signed processes restore that charge and block claims. Releasing the writer permits positive retirement, clears accounting and allows idempotent ready recovery; verification, replacement and exact rollback complete. |
| Swift parent receives SIGKILL after the injected writer detaches | The writer survives; restart restores the full charge and drains claims. The shared helper acknowledges `WorkerInterrupted`, with zero media failures and unchanged source. After the writer releases its guard, positive retirement clears accounting; the same worker identity reclaims the job and completes verification, replacement and exact rollback. |

Both trials use the final normal release library and newly published helper. All temporary anchors
are removed only after positive empty-ledger checks; no injected writer remains. Two initial harness
attempts failed in reporting or an incorrectly named expected enum after reaching the retained-charge
boundary. Those attempts remain failures; the failed-crash fixture was positively recovered before
its empty anchor was removed. Corrected fresh runs pass the complete workflows above.

## Remaining qualification

Broaden connected helper-death boundaries and qualify full packaged reconnect/reboot/remount
behavior. The connected trials above do not prove machine-reboot recovery, every cancellation
phase or an installed menu-bar app. Positive full-image execution under live Mac admission remains unproved.
Full-encode process reaping, fixed-image workers, adaptive retained soundtracks and preview/
calibration/source-history coverage remain in the [parity contract](../../development/adaptive-audio-image-parity.md).
