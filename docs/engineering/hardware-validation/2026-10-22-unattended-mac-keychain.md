# Unattended Mac keychain qualification — 22 October 2026

The Apple M5 MacBook Air (`scotts-mac`, macOS 27.0.1, Xcode 27.1) was
accessed over SSH from Quark. Its default login keychain remained locked
(`SecKeychainGetStatus`: unlocked false). The user search list contained only
`login.keychain-db` before and after the trials. No default-keychain, search-list,
login password, installed app or production pairing changes were needed.

## Change and failure checks

The Swift checkpoint adapter accepts an explicitly selected file keychain.
Queries search only that keychain; inserts select it explicitly. The ordinary
installed application's default selection is unchanged. A disposable acceptance
process creates a random-password keychain for its own accounting journal.
Session recovery accepts an explicit set of owned scratch roots. The SSH
coordinator also supplies a separate `TMPDIR`, because native execution recovers
its temporary directory as well as the configured scratch root.

The real-keychain regression first failed because the adapter had no explicit
keychain parameter. A subsequent run caught `SecKeychainOpen` treating a missing
path as openable; an explicit regular-file check now rejects it. The passing
regression proves create/read/update, compare-and-swap refusal, equal-service
isolation, locked and missing stores, symbolic-link refusal, incompatible
backend refusal and unchanged search preferences. Session recovery has a
separate regression proving it visits only the injected root.

The signing wrapper reads the authorized certificate backup's export password
on stdin, imports into a process-owned keychain and explicitly trusts
`/usr/bin/codesign`. Every signature selects that keychain with `--keychain`.
The first physical build exposed the missing signing ACL: import succeeded but
signing returned `errSecInternalComponent`. With the explicit ACL the full
Developer ID build and deep strict signature verification pass unattended.
A separate signed executable probe also passes. A command returning failure and
an invalid certificate password both return failure and leave zero temporary
signing keychains. Cleanup after a forcibly terminated acceptance process belongs
to its coordinator; it deletes only that run's keychain after stopping that worker.

## Automated verification

- .NET solution Release build: zero warnings/errors; 3,579 tests pass.
- Shared worker: 625 pass, 3 Windows-only skips on Quark; Linux sidecar: 31 pass.
- Python: 155 pass; Unraid metadata check passes.
- Mac Swift: 493 tests in 91 suites pass; Release build passes.
- Browser: 350 tests pass using Quark's existing browser library bundle.

## Live worker trial

The current server and the real Swift acceptance session use protocol 11 with
strict worker verification. The native helper, metrics, metadata utility and
FFmpeg come from the newly built Developer ID signed bundle. Pairing is kept
in memory, with one worker slot, a new test journal, scratch directory, temporary
directory and checkpoint keychain. This qualifies the disposable session and
signed bundle; it does not claim an installed menu-bar package upgrade or
production restart lifecycle.

The rerun passes **8/8 worker cases**: adaptive AAC, Opus and MP3; audio
quality rejection; adaptive JPEG and WebP; image metadata preservation; and
image quality rejection. Successful cases include final verification, actual
replacement and exact-hash rollback. Rejections leave originals untouched and
have no replacement history. Worker shutdown and test-setting restoration pass.
The first run's metadata fixture setup failed on Quark because an older ExifTool
copy lacked its Perl module path; the rerun uses the pinned self-contained 13.55
package. No production behavior or verification thresholds were changed to pass.
A 479-file SHA-256 manifest pins the signed bundle in the artifact directory.

The source baseline is rebased PR #432 at `328ac0e9`, plus the keychain and
acceptance changes in this record's commit. Detailed logs and generated media
are retained outside Git under Quark's SSD artifact directory
`optimisarr-qualification-20261022`; no credentials are included in this record.

## CI timing regression follow-up

The first CI run at `bf0c6d01` failed an existing retirement test because its
elapsed-time assertion observed 6.013 seconds against a six-second cutoff.
The runtime still uses the original three-second service timeout and one-second
TERM/KILL waits. The test now checks the safety outcome directly: the published
helper PID must be absent when retirement returns, and a marker written only
on its ten-second natural-exit fallback must not exist. Fixture readiness waits
for the complete PID, and failed assertions still stop the owned helper.

A mutation trial omitting only the helper's forced kill fails both output-open
and output-closed variants on the new liveness assertion. Restoring the unchanged
production source makes the entire 493-test Mac suite pass. This replaces a
scheduler-sensitive performance assertion with process-lifecycle evidence;
no runtime grace period, admission rule or retirement acknowledgment changed.
