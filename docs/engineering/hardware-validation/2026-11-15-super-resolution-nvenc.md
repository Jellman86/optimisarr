# Super-resolution with NVIDIA NVENC — 15 November 2026

The server-side super-resolution pipeline now accepts H.264, HEVC and AV1 NVENC. NVENC uploads
the converted software frames itself; 10-bit HEVC/AV1 use its P010 input. Quality uses the normal
NVENC CQ/VBR controls, with the resolved preset and library tuning. Intel QSV, VA-API and
VideoToolbox remain refused. No verification or replacement gate is relaxed.

## Physical acceptance

An isolated native Windows instance built from current `dev` plus this change ran on an RTX 4070
with driver 617.42 and .NET 10.0.404. The generated fixtures are eight seconds at 12 fps, with
320×180 SDR pictures, lossless FLAC audio, a SubRip subtitle and an attachment. Real-ESRGAN's
official 20220424 Windows release used `realesr-animevideov3` at 2×. The resulting 640×360
candidates passed all five cases:

- H.264, HEVC and AV1 NVENC with 8-bit inputs;
- HEVC and AV1 NVENC with 10-bit inputs.

Every case passed the application's full-file VMAF v1 broken-frame policy and structural checks.
Independent decoding/probing confirmed all 96 pictures, retained bit depth and colour tags,
unchanged decoded audio, retained subtitle/attachment metadata and unchanged A/V start alignment.
Automatic replacement remained suppressed despite being enabled on each test library. Explicit
replacement, repeated replacement and rollback restored each original SHA-256.

The initial 128×72 fixture was too small for VMAF v1's CAMBI feature and was rejected. The fixture
was enlarged; the quality gate was kept enabled throughout.

The [measurement and source-hash record](2026-11-15-super-resolution-nvenc.json) contains the exact
tool version, engine archive checksum, all five measured results and the five execution-source
hashes independently matched between Windows and Linux. Reproduce with the
[super-resolution acceptance regression](../../development/media-acceptance.md#super-resolution-and-nvenc-regression).

## Repository checks and limits

Linux passes a zero-warning Release build and all 4,464 backend tests, including the new encoder,
admission and process-registration regressions. Native Windows passes a zero-warning build and
4,322 backend tests with 142 documented platform skips. Shared worker-core tests on Linux pass
639 cases with three Windows-only skips. The Python suite passes 215 tests with one platform skip;
OpenAPI, documentation, release and Unraid metadata checks pass. Frontend sources are unchanged:
the existing check passes, and all 580 browser cases pass across the full run and targeted rerun
of seven initial timeouts.

This qualifies the tested RTX 4070/native Windows/Real-ESRGAN path. It is not a GTX 1050, NVIDIA
Linux-container, remote-worker or Real-CUGAN hardware qualification, nor an assessment of whether
the model improves real-world footage. No installed worker, production library or deployment was
changed.
