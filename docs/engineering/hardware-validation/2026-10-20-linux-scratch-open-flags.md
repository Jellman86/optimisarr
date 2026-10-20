# Linux scratch ownership open flags

Development qualification on **20 October 2026**, continuing [draft #415](https://github.com/Jellman86/optimisarr/pull/415)
and [#332](https://github.com/Jellman86/optimisarr/issues/332).

## Defect and change

The Linux journal already selected architecture-specific open flags, but
`NativeWorkspaceDirectory` still used x86-64 values for directory walks and file opens.
On ARM64 these values do not request the intended directory and no-follow guards.
The kernel's [ARM64 UAPI header](https://github.com/torvalds/linux/blob/master/arch/arm64/include/uapi/asm/fcntl.h)
defines `O_DIRECTORY` as `0x4000` and `O_NOFOLLOW` as `0x8000`; the
[generic header](https://github.com/torvalds/linux/blob/master/include/uapi/asm-generic/fcntl.h)
supplies the x86-64 values `0x10000` and `0x20000` and the common close-on-exec,
nonblocking, creation and exclusive bits.

Scratch root/child opens, enumeration and file opens now consume the same verified
`LinuxNativeOpenFlags.For` mapping as the journal. Unsupported Linux architectures throw
before the native open that would create or adopt scratch objects. Read/write access,
exclusive creation, explicit inheritance for writer guards and FIFO nonblocking behavior
remain independent of the architecture-specific guards. Windows handling and Darwin's
existing flags/ARM64 variadic-call convention retain their existing paths.

## Test-first evidence

The existing bit composition was first extracted into methods used by the real open paths,
without correcting the architecture choice. Seventeen pure ABI cases then produced **ten
failures and seven passes**: ARM64 directory/file guards and unverified architectures failed,
while the x86-64 behavior passed. The corrected mapping passes all seventeen cases.
These contracts run on every host and include locked reads, explicit inherited guards,
exclusive creation and open-or-create.

Seven additional native Linux cases check real file-leaf symlink refusal across open modes,
real-directory success, directory-link/ordinary-file refusal and bounded FIFO rejection.
The FIFO test waits for managed fixture readiness before timing the open; its failure
cleanup kills and joins the owned process rather than leaving a blocked thread behind.
Sentinel file contents and redirected links are retained. The focused
suite, including existing scratch ownership and retirement checks, passes **70 tests** on
Quark's native Linux x86-64 host.

## Independent ARM64 syscall probe

A statically linked ARM64 ELF probe invokes Linux `openat` directly through QEMU user-mode
10.2.2, without a foreign libc or a managed runtime. Its owned root is opened with known ARM64
directory flags, then it tests the old and corrected leaf flag combinations independently.
With the old file flags, opening the file symlink succeeds: the probe returns **11**, its
explicit linked-file-accepted failure. With corrected flags, that leaf fails with `ELOOP`,
an ordinary file/directory opens successfully, and directory links and files-as-directories
are refused: the probe returns **0**. The sentinel original's SHA-256 is unchanged in both runs.

The probe source and ELF binaries, raw results and logs remain outside Git. This demonstrates
the flag semantics under ARM64 syscall emulation; it does not execute the complete managed
worker on a physical Linux ARM64 machine or establish package/lifecycle qualification.
The retained probe source has SHA-256
`a344303c29164d2ef8c1d5f58bb70d18d63b72060755845c69169e0814f15538`.

Full Quark verification passes **3,575 backend tests**, **841 shared-worker tests** (23
platform skips), and **31 Linux-sidecar tests**. Solution and Linux-sidecar builds have zero
warnings; documentation, release metadata and OpenAPI checks pass.
All **347 browser tests** pass with one worker.

## Fresh native Mac and Windows follow-up

Both hosts were reachable again at the later **10:00 UTC, 20 October** check. Their installed
protocol-10 sidecars had fresh server check-ins and were idle. This supersedes the earlier
SSH timeouts and Picard's earlier offline report.

Isolated native checkouts verify exact source `dacbd438afc04f37ef90affa4c0e655d3ad30969`:
both hosts use .NET SDK 10.0.402, and the Mac's runtime is native `osx-arm64`.

- Mac: **3,575 backend tests** and **765 shared-worker tests**, with 49 platform skips.
  Both the solution and shared-worker builds have zero warnings.
- Picard: **882 shared-worker tests**, with 26 Linux-only skips and a zero-warning build.
  This includes the Windows journal's 49 forced-process-death cases and the new pure ABI
  contracts. The test process uses a new owned TEMP/TMP folder, preserving the host's ACLs.

Both hosts verify the Git source archive's SHA-256 as
`9cb05dd90a19a369a4e613e3c6b7c2312ad24c7200a8dc1c6ae2d53782f0ef41`.
Archives, native build/test logs and Windows TRX evidence remain outside Git. The installed
sidecars and their pairing were not changed. CI evidence remains separate from these runs.

## Remaining qualification

The Mac runs Darwin, so these tests do not replace physical Linux ARM64 qualification.
These are source/API/process tests, not installed-package lifecycle or new GPU media trials.
No automatic protocol-11 placement or standalone durable journal wiring is enabled by this
fix. Physical power-loss, package lifecycle and broader audio/image parity remain open in
the [completion matrix](../../development/adaptive-audio-image-parity.md).
