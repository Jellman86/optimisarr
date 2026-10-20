# Native image workflow follow-up

Development qualification on **20 October 2026**, continuing
[draft #415](https://github.com/Jellman86/optimisarr/pull/415) and
[#332](https://github.com/Jellman86/optimisarr/issues/332).

## Exact source and scope

The isolated server and native runners use source
`7636a8663655ea175e4cc119a2d4596b4787b757`. Both native hosts verified the Git
archive SHA-256 as
`04acdf90d6a740b019d7a9b9cfbd0f7bc844f98f734948c4d925f51d3caaec2b`.
This revision has passing CI and the earlier
[native source-suite qualification](2026-10-20-linux-scratch-open-flags.md#fresh-native-mac-and-windows-follow-up).

Trials use generated media, disposable server configuration and fixture databases.
Pairing negotiates protocol 10. Only the fixture worker row is then elevated to
protocol 11 so native work can be exercised. Production protocol advertisement,
installed sidecars, pairing and application deployments are unchanged.

## Picard: JPEG and WebP pass

A disposable Windows service launches the fixture coordinator outside OpenSSH's
job object. A small acceptance executable invokes the actual capability probers,
resource refresh, protocol client, transfer logic and shared production `JobRunner`
with `PerceptualJobRuntime.Native`. It does not substitute a memory provider or
grant. The two collected memory readings are 26,040,152,064 and 26,029,457,408 bytes;
scratch is disk-backed. The installed NVIDIA RTX 4070 is available with driver
617.42, but these image encoders are CPU paths and this is not GPU qualification.

The generated RGB PNG contains a test pattern and noise: 1024×768 and 1,330,113
bytes. Both policies require SSIM ≥0.8 and SSIMULACRA2 ≥50, with adaptive image
selection enabled. These are fixture thresholds, not calibrated quality defaults.

| Output | Selected quality | Output bytes | Final SSIM | Final SSIMULACRA2 |
| --- | --- | --- | --- | --- |
| JPEG (`mjpeg`) | 98 | 131,689 | 0.847295 | 77.6548221088 |
| WebP (`libwebp`) | 95 | 210,622 | 0.922730 | 54.2667878853 |

Both bounded four-candidate searches finish without fallback. JPEG candidates at
80 and 91 miss the configured SSIM floor; the accepted candidate satisfies both
quality gates. Each run deliberately drops one successful, committed probe
response, then completes through replay, sealed upload and final worker verification
accepted by the server. The job reaches `ReadyToReplace` with all nine verification
checks passing. The source remains unchanged before replacement.

Each fixture then requests replacement twice, proves exactly one replacement
record, checks candidate and quarantined-source hashes, and rolls back to the exact
original SHA-256. Both runs finish with zero reserved memory, retired owner records
and no media in worker scratch. The retained control metadata passes the scratch
validator. The original hash is
`3c0f2e4bf9c7d72610aa1ffff046417a4e91fe43decb6e3a91d3301b404bc6ed`.

The fixture source carries no ICC profile or EXIF. Its metadata gate confirms that
absence; these runs do not extend earlier metadata-preservation qualification.
Master, runner and temporary service builds report zero warnings. The fixture
service is removed afterward; the installed sidecar service is running before and
after the trial. Drivers, reports, generated media and raw logs remain outside Git.

### Conservative refusals retained

The first, smaller fixture falls below the product's 0.2 MB image minimum and is
not offered. A larger fixture run through OpenSSH reports unknown memory capacity
and is also not offered. PowerShell `Start-Process -Wait` creates another job object
even under the disposable service, reproducing the capacity hold. Waiting on the
process handle instead allows the ordinary native provider to report live memory.
No product threshold or job-ancestry guard was weakened to admit these trials.

## Mac: signed JPEG and WebP pass on disk and RAM

The current server and self-contained ARM64 helper build successfully, and the
release `AcceptanceWorker` links the current production `SidecarCore`. Swift emits
the existing legacy-Keychain API deprecation warnings. The previously qualified
JPEG/WebP, metric and metadata tools are present.

The initial SSH launch fails Developer ID signing with `errSecInternalComponent`.
A read-only Keychain status query in that SSH session reports the login keychain
locked. An ad-hoc development signature allows process startup, but production accounting remains blocked during
all three startup attempts, before pairing or media transfer. An independent helper
recovery request succeeds with no invalid, deferred or busy owners. Live available
memory is 4,819,982,746 bytes, with no reserved memory; memory is no longer the only
identified prerequisite for retrying the image workflow.

The later desktop-session check supersedes the assumption that the desktop keychain
was also locked: the operator has it unlocked, and a Terminal-launched check reports
`unlocked=true` while SSH still reports `false`. The prepared launcher runs from
that desktop session. Developer ID signing succeeds for the native helper, its
nested libraries and the release Swift acceptance executable. Strict signature
verification passes for both executable entry points. Production accounting becomes
ready on its first startup attempt, and real capability probing, pairing and
heartbeat succeed. No password is requested, stored or used by the fixture.

The first desktop JPEG trial advertises only 2,573,441,434 available memory bytes,
below the 3.5 GiB image reservation. The server refuses the claim before source
transfer or probe reporting. The job remains queued with zero media failures,
zero reserved memory and clean scratch. After the operator closes unused apps,
the live provider reports 5,820,701,082 available bytes and the positive trials resume.

The signed release launcher uses the production Swift capability collection,
`NativeWorkRuntime`, independent Keychain checkpoint and `NativeQualityJobRunner`
with the self-contained shared helper's scoped v2 contract. Each trial owns a new
journal, Keychain service and scratch root. No capacity override is supplied.
Paired live-memory readings are 5,359,163,802 and 5,334,194,586 bytes for the disk
runs, and 5,234,153,882 and 5,229,156,762 for the RAM runs.

The Mac's generated 1024×768 PNG is 1,342,945 bytes, with SHA-256
`ce97093dc6056393ce317d8e213bbc898ebf84afc10aa56b9dd9a9fc1cd9066d`.
It has the same fixture quality policy as Picard and carries no ICC profile or
EXIF. Disk and RAM runs produce matching candidate hashes for each codec.

| Output | Storage trials | Selected quality | Output bytes | Final SSIM | Final SSIMULACRA2 |
| --- | --- | --- | --- | --- | --- |
| JPEG (`mjpeg`) | Disk and actual RAM | 98 | 130,973 | 0.847330 | 77.7662259972 |
| WebP (`libwebp`) | Disk and actual RAM | 95 | 209,216 | 0.925160 | 54.1488779499 |

All four searches complete without fallback and deliberately lose one committed
probe response each. All four jobs deliver sealed uploads, pass all nine final
worker verification checks accepted by the server, and reach `ReadyToReplace`.
Each repeats replacement with exactly one record, checks original/quarantine and
candidate/final hashes, then restores the exact original through rollback.

Both RAM runs report `inMemory=true`; neither uses disk fallback. Completion reports
zero reserved memory for all four runs. Each persisted ledger's payload checksum
is independently checked and its obligation list is empty. The owned RAM mount
paths are absent, `hdiutil info` reports no fixture mounts and no remaining RAM
images, and worker scratch contains no media or owner records. Strict signature
verification passes for all 16 native/helper/Swift Mach-O files checked afterward.

These results close the positive full-image trial under live Mac admission on disk
and RAM. They exercise the signed release library and shared helper through a
disposable executable; they do not qualify the installed menu-bar app or every
package lifecycle. Reports, signing logs, generated media and cleanup proof remain
outside Git.

## Remaining qualification

These Windows and signed Mac runtime trials prove current image workflows with live
capacity, final verification and exact rollback. They do not qualify installed
service/menu-bar lifecycle, standalone durable-accounting integration, Windows RAM
disks, reboot/remount recovery or process crashes during these particular image
jobs. The Mac's current release-library trial covers both disk and real RAM storage.
Automatic protocol-11 placement remains disabled. Wider source coverage, fixed-image
workers, retained-soundtrack parity and other completion requirements remain in the
[parity matrix](../../development/adaptive-audio-image-parity.md).
