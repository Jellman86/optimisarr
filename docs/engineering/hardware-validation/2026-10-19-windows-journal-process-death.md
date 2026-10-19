# Windows journal process-death qualification

Native qualification on **19 October 2026**, continuing [draft #415](https://github.com/Jellman86/optimisarr/pull/415)
and [#332](https://github.com/Jellman86/optimisarr/issues/332).

## Scope

The experimental NTFS journal now has **49 actual process-termination cases**, in addition
to its existing injected lost-acknowledgement tests. A dedicated managed fixture stops at a
nominated adapter callback, flushes a readiness line containing a fresh trial nonce and the
exact boundary/occurrence, and waits indefinitely. The test kills that process without a
managed unwind, joins its exit, and opens the surviving files with the ordinary journal API.
An unexpected boundary, early exit, extra output or normal operation return fails the test.

The matrix covers:

- 13 provisioning boundaries: directory creation, coordinator creation, initial checkpoint
  and initial ledger. Incomplete authority must refuse reopening and reprovisioning.
- 15 commit boundaries: pending checkpoint, ledger replacement and committed checkpoint,
  including each checkpoint's separate callback occurrence.
- Seven restart reconfirmation boundaries with already committed debt.
- Seven more reconfirmation boundaries after a killed commit leaves pending authority and
  the new ledger visible, and seven with the old ledger still visible.

Successful recovery retains the exact visible ledger bytes, store identity and expected
revision. Existing obligations retain their complete canonical bytes; newly admitted debt
retains its Intent phase and both resource charges. Reopening resolves pending authority;
it cannot silently initialize a replacement store. The fixture calls the Windows storage
candidate directly and adds no standalone bootstrap, admission or service caller.

## Native evidence

Picard (Windows 11, .NET 10.0.402) passes **865 shared-worker tests**, with 23 Linux-only
skips, including all 49 new cases. The Mac passes **748 shared-worker tests**, with 46
platform skips. Quark passes **817 shared-worker tests**, with 23 platform skips, and
**3,573 backend tests**. Builds on both native hosts and the Quark solution have zero warnings.
Quark also passes all 31 Linux-sidecar tests, 154 Python tests and 347 browser tests with
one worker. Documentation, release metadata, Unraid metadata and OpenAPI checks pass.
The browser run uses the matching downloaded Chromium and the existing user-space browser
libraries; initial missing-browser/library setup failures are retained with the private logs.

The source archive was verified on both hosts against SHA-256
`3fe55d300a9bc5c0ed3b3522ba6c695e9573dd482bf6a328f8188131fcfb1f58`.
It contains base `dc58e397` plus this slice's test and fixture changes, before documentation.
Raw build/test logs, TRX results and archives remain outside Git.

Test-first evidence: all 35 initial cases failed before the dedicated fixture existed.
The matrix was then expanded to include interrupted pending old/new recovery. The first native
attempt also confirmed that the host's shared temporary directory has untrusted mutation
permissions and is correctly refused. The final Windows run sets process-local TEMP/TMP to a
new test-owned folder inside the isolated checkout. No host ACL or protection was changed.

## Limits

Only the managed writer process dies; Windows, NTFS and device caches remain running. These
tests do **not** establish power-loss durability, directory synchronization, physical reboot
or remount behaviour, packaged-worker lifecycle acceptance, or a qualified production
bootstrap. Callback names ending in `directory-flushed` remain adapter instrumentation and
do not establish a documented Win32 durability guarantee. Physical/VM power-cut qualification
and its fault-model calibration remain required before standalone wiring. Protocol 11 stays
disabled.

Both the Mac and Picard are reachable again and their installed protocol-10 sidecars are
checking in. Installed services, pairing and personal media were not changed by these tests.
