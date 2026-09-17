# Installed Mac sidecar and deployed container — 2026-09-17

The installed macOS GUI sidecar and the existing Linux container were tested through their normal
pairing and public application API. The matrix finished with **52 passing and 3 failing cases**.
A separate 60-second 720p HEVC Mac run passed all three cases, including cleanup. The failing
results remain failures; this is not a clean bill of health for the deployed build.

## Environment

| Item | Observed value |
|---|---|
| Server CPU | Intel N100 |
| Server kernel | `6.12.99-production+truenas` |
| Container | Existing `ghcr.io/jellman86/optimisarr:dev` deployment |
| Running image ID | `sha256:ae62b757458f8682b5a0024c435375ec9d08bef5dfa5a2cf561ba36ce13f7f2b` |
| Mac | Apple M5, macOS 27.0 |
| Installed sidecar | 0.4.1, build `202609152113` |
| Mac bundled FFmpeg | `n8.0.3` |

The retained private evidence includes the installed executable and FFmpeg hashes, exact container
tools, application reports, raw reference VMAF frames, source hashes and sampled worker attribution.
No rebuilt sidecar replaced the installed app, and the deployed container was neither rebuilt nor
restarted. Its encoder policy stayed Auto with hardware decode enabled.

## Cases and outcomes

The four generated fixtures cover SDR, variable frame rate, a nonzero timestamp and 10-bit pixels.
Six checksum-locked Creative Commons excerpts cover Big Buck Bunny and Tears of Steel.

- The container passed all 29 positive cases: H.264 QSV on nine fixtures, HEVC QSV on ten, and
  software AV1 on ten. H.264 10-bit was excluded from this run; eligibility refusal is covered by
  the separate isolated matrix.
- The installed Mac passed 17 of 19 H.264/HEVC VideoToolbox cases, including 10-bit HEVC and
  every film excerpt. Both VFR cases failed application/reference score agreement.
- The installed Mac passed the additional AV1 encode. Its verification report identifies
  **server-side VMAF**, so this does not certify AV1 scoring inside the Mac bundle.
- Four fixture-generation cases and ownership-scoped cleanup passed.
- The container's deliberately impossible VMAF gate exposed a retry failure described below.

Positive cases required full independent decode, matching picture counts/timestamps, expected
pixel format and streams, unchanged decoded audio, expected encoder/worker attribution and
raw-frame VMAF agreement. Gates remained harmonic mean 93, fifth percentile 80 and minimum 50,
with aggregate comparison tolerance 0.25 points.

The extra 60-second 1280×720/24 fps Mac fixture passed HEVC VideoToolbox, independent verification
and cleanup. A sampled local FFmpeg process plus the live worker lease confirms that work actually
ran on the Mac. The encode completed quickly; subsequent independent scoring ran on the server.
This short sustained case is not a thermal or resource-leak soak.

## Failures and fixes

**VFR reporting:** the installed worker reported 77 scored frames while the independent common-
cadence comparison scored 96. Original and output picture counts/cadence matched, so this was a
measurement-plan mismatch, not lost video frames. Independent harmonic scores were approximately
98.48 for both outputs. The branch supplies freshly probed source cadence in the worker quality
plan; the isolated Mac and Windows runs validated that change. It has not been deployed here.

**Retry directory:** the impossible 100/100/100 policy correctly rejected the first QSV candidate.
Cleaning up that candidate pruned its empty output directory. The software-decode retry then
failed to open its output because it did not recreate/reserve the directory. The final job was
Failed with `verificationPassed` reset to null. The original stayed unchanged and no replacement
occurred. The raw one-off report says “Impossible gate was accepted”; that assertion text is
misleading: the gate rejected the output, but the required completed rejection state was lost to
an unrelated retry failure. Raw evidence has been preserved unchanged.

The branch now creates and reserves the output directory at every FFmpeg attempt, including both
fallback paths and adaptive samples. Regression tests cover pruning followed by a retry, failed
creation and cancellation. The reusable harness additionally tests a compressed H.264 source,
hardware decode and an impossible quality policy; it requires evidence that software-decode
verification actually completed. See the [Windows follow-up](2026-09-17-windows-container-acceptance.md)
for before/after real-container evidence. The deployed Intel image remains unfixed until rollout.

## Safety and limits

All work used newly created, explicitly owned fixture libraries with automatic enqueue and
replacement disabled. No replacement was requested; production replacement/rollback behaviour
was instead exercised in isolated runs. Source hashes stayed unchanged. All 50 matrix libraries
and the one longer-run library were removed, their owned work outputs cleaned, and both installed
workers' original undrained state restored. Global settings compared equal before and after.

These operator-requested live checks are a separately retained, one-off adapter. The committed
harness intentionally accepts only new disposable instances. It cannot accidentally target an
arbitrary production URL.

This run adds Intel encode/VMAF and installed Mac integration evidence, not VAAPI, HDR/Dolby Vision,
CUDA VMAF, full hardware-decode certification, sampled-window certification or a long soak. The
Mac bundle's local AV1 software-decoder and 8-bit-only x265 limitations from the initial native
run remain separate outstanding issues; successful VideoToolbox 10-bit HEVC does not fix x265.
