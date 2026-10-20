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

## Mac: prepared, blocked before pairing

The current server and self-contained ARM64 helper build successfully, and the
release `AcceptanceWorker` links the current production `SidecarCore`. Swift emits
the existing legacy-Keychain API deprecation warnings. The previously qualified
JPEG/WebP, metric and metadata tools are present.

Developer ID signing fails with `errSecInternalComponent`. A read-only Keychain
status query confirms that the login keychain is locked. An ad-hoc development
signature allows process startup, but production accounting remains blocked during
all three startup attempts, before pairing or media transfer. An independent helper
recovery request succeeds with no invalid, deferred or busy owners. Live available
memory is 4,819,982,746 bytes, with no reserved memory; memory is no longer the only
identified prerequisite for retrying the image workflow.

The operator cannot unlock the Mac locally at present. The isolated run is retained
for a later retry; no password is requested, stored or used by the fixture. A failed
startup is not positive image-admission or signed-package acceptance.

## Remaining qualification

These Windows trials prove current shared-runner image workflows with live capacity
and disk scratch. They do not qualify the installed service's new-feature lifecycle,
standalone durable-accounting integration, RAM disks, reboot/remount recovery or
process crashes during these particular image jobs. The Mac's complete image job
under live admission remains open until its Keychain prerequisite is available.
Automatic protocol-11 placement remains disabled. Wider source coverage, fixed-image
workers, retained-soundtrack parity and other completion requirements remain in the
[parity matrix](../../development/adaptive-audio-image-parity.md).
