# Sidecar RAM storage and GPU processing — 2026-09-27

## Assessment

Reviewed current `dev` at `7feaa04`, following the 0.2.16 release. The local primary checkout was
107 commits behind that revision; this work uses an isolated branch from current `dev`.
The backend and sidecar protocol suites are healthy, but RAM storage had a real integration gap
that the existing mount/write test did not cover. Remote workers remain an opt-in preview.

| Path | Observed result |
| --- | --- |
| Mac RAM working storage | Broken capacity check reproduced, then fixed and tested through the job runner. |
| Mac VideoToolbox | Hardware encode and decode probes pass; a real HEVC job in RAM delivers a decodable candidate. |
| Windows RAM working storage | No built-in RAM-disk creation or memory preference. The service uses its configured disk directory. |
| Windows CUDA → HEVC NVENC | Pass on an RTX 4070: CUDA surfaces reach the encoder, 100 output frames, clean software decode. |
| Windows CUDA → AV1 NVENC | Pass on the same GPU: 100 output frames, clean software decode. |
| Windows GPU VMAF | Installed FFmpeg has no `libvmaf_cuda` filter. CPU VMAF is the shipped scoring path. |

GPU processing and RAM working storage are independent. The Windows tests used files on disk.
Mac VideoToolbox returns decoded frames to system memory for the current filter pipeline.
The server deliberately selects software decode for cropping, scaling and frame-rate filtering;
hardware encoding can still run. Neither platform promises an entirely GPU-resident pipeline for
every job. No production pairing, service, library or original media was changed for this review.

## Failures reproduced and corrected

1. **A writable Mac RAM volume was reported as full.**
   `volumeAvailableCapacityForImportantUsage` returned zero on the HFS RAM volume. The earlier
   test successfully wrote a file but never called the job runner's capacity check. Actual jobs
   therefore returned their lease before downloading. Capacity now also reads the filesystem's
   free blocks, retaining the larger nonnegative value. Unreadable capacity still fails closed.
2. **RAM-volume commands sometimes waited 20 seconds after exiting.**
   Repeated live tests failed during formatting or mount-info commands. A process sample showed
   the reading thread stuck in Foundation `waitUntilExit`, with no corresponding child still
   running. Completion now comes from the process termination callback plus drained output.
   Timeouts remain bounded and terminate the child, escalating to a kill if it stays alive.
3. **An oversized source size could trap before validation.**
   The outer job runner performed unchecked arithmetic before its inner overflow guard.
   Invalid and overflowing sizes now release the lease before storage allocation or download.

The live RAM suite is serialized because its stray-volume sweep must not eject another test's
active volume. CI now runs the real volume/capacity/cleanup checks. The optional FFmpeg test uses
the production job runner, real RAM volumes and bundled FFmpeg with a simulated HTTP server.
It checks source and candidate paths, delivery, decode health and ejection. Injected encoder
failure and cancellation also have to release the job and eject the volume.

## Validation

- Backend: 2,281 tests passed; Release build had zero warnings/errors.
- Windows sidecar core: 226 tests passed on macOS. This is portable-core coverage, not a native
  Windows service/installer test.
- Mac: 223 default tests passed; Release build succeeded.
- Web: type/localization/unit checks passed; 182 browser tests passed on an isolated preview port.
- Real Mac capability tests: VideoToolbox encode/decode, software encoders and bundled audio/VMAF
  capabilities passed.
- Real Windows hardware: installed FFmpeg `n8.1.2-52-g5a03dfa0f6-20260914`, RTX 4070, driver 617.14.
  CUDA/NVENC HEVC and AV1 each produced a 100-frame candidate that passed `ffprobe` and full decode.
  Verbose HEVC logs identify CUDA input surfaces at the encoder. A separate CUDA-VMAF probe failed
  with `No such filter: libvmaf_cuda`, matching the installed build manifest.

Reproduce the Mac checks from `sidecars/macos`:

```sh
swift test
OPTIMISARR_LIVE_RAMDISK=1 swift test --filter LiveRamDisk
OPTIMISARR_LIVE_RAMDISK=1 \
  OPTIMISARR_FFMPEG=/Applications/OptimisarrSidecar.app/Contents/Resources/ffmpeg \
  swift test --filter 'LiveRamDisk|LiveCapability'
swift build -c release
```

The opt-in tests create small RAM volumes and sweep volumes named `OptimisarrWork-*`. Run them
with no other sidecar jobs using RAM. Hardware probes use synthetic media. These checks do not
certify full-film endurance, HDR, every GPU, or production server verification/replacement.

## Follow-up hardening implemented

- One atomic process-wide ledger reserves the complete RAM volume before creation. Reservations
  release after successful ejection, remain held if ejection fails, and respect budget reductions.
- Allocation uses the source plus the frozen candidate limit, 64 MiB working allowance, and
  filesystem overhead. Unknown limits and adaptive searches use disk with a visible explanation.
- The activity card identifies RAM/disk storage and explains fallbacks. Startup volume cleanup
  completes before the session can restore pairing and start jobs.
- Source downloads now stream directly to the working file with an ephemeral URLSession and a
  bounded buffer. Interrupted/cancelled ranges roll back to their previous length before retry.
  Tests prove bytes arrive before HTTP completion and cancellation preserves the valid prefix.

## Remaining project priorities

1. **Finish sidecar verification and upgrade work already tracked:**
   [missing strict timestamp evidence](https://github.com/Jellman86/optimisarr/issues/294),
   [Windows service restart after upgrade](https://github.com/Jellman86/optimisarr/issues/295), and
   [duration drift](https://github.com/Jellman86/optimisarr/issues/289).
   The timestamp-evidence report says the server refused incomplete evidence and retained the
   original; it is a reliability failure, not evidence that unsafe replacement occurred.

A Linux headless sidecar is requested in
[issue 293](https://github.com/Jellman86/optimisarr/issues/293); it is not an implemented third
native sidecar in this revision. Windows-managed RAM storage would likewise be new functionality,
requiring a supported volume provider and service-lifecycle tests.
