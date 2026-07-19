# Known issues

This page lists only reproducible problems in the current release. Fixed problems belong in the
[changelog](CHANGELOG.md), future work belongs in the [roadmap](docs/roadmap.md), and hardware paths
that are implemented but still need physical-host evidence belong in the
[hardware validation matrix](docs/setup/hardware-validation-matrix.md).

> Safety note: the issue below cannot destroy an original file. Optimisarr replaces an original only
> after verification succeeds, and moves that original to quarantine before promoting the output.

## Compatibility H.264 is not broadly compatible for sources above 8-bit

**Affected use:** a 9-bit or 10-bit video source with the **Compatibility H.264** preset.

**Current behaviour:** in **Auto** encoder mode, normal, preview, and personal-quality jobs select
the bundled 10-bit-capable `libx264` encoder and preserve the source bit depth. This avoids the
unsafe implicit 10-to-8-bit conversion that supported hardware H.264 encoders would otherwise
require. A forced hardware mode fails before FFmpeg starts and asks for Auto/CPU or HEVC/AV1.

**Impact:** the resulting H.264 High 10 output is less widely playable than the preset name implies,
uses the CPU, and can be larger than an efficient 10-bit HEVC source. With the normal smaller-output
gate enabled, a larger result fails verification and may be automatically excluded under the
size-failure policy. Personal quality jobs are disposable and do not affect normal failure counts or
exclusions.

**Workaround:** prefer Balanced/Conservative HEVC or Efficiency AV1 for sources above 8-bit. Use
Compatibility H.264 only when the intended clients support H.264 High 10 and the additional CPU and
size cost is acceptable.

**Planned resolution:** normal-job eligibility should treat Compatibility H.264 as unsuitable above
8-bit and recommend or skip to HEVC/AV1. Any future 10-to-8-bit compatibility conversion must be an
explicit policy whose verification contract understands the intentional depth change; Optimisarr
will not introduce that lossy conversion implicitly.

**Safety:** sources above 10-bit fail before encoding because no supported H.264 encoder can preserve
their depth. An output whose structure or bit depth cannot be proved does not pass replacement
verification, and a failed size gate leaves the original untouched.
