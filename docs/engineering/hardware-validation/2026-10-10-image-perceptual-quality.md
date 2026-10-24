# Experimental image quality qualification: 10 October 2026

[PR #395](https://github.com/Jellman86/optimisarr/pull/395) adds optional server-local
SSIMULACRA2 reports and a separate explicit minimum-score gate. Both controls start off.
This is development evidence for the first SDR still-image slice of
[issue #332](https://github.com/Jellman86/optimisarr/issues/332), not a new release or
photographic calibration. Broader colour/format coverage and image worker placement remain planned.

## Source and selected matrix

The initial qualified implementation is `22ac0a750254373fe2b362e2720a015168b10f8c`.
Downloading a later Mac CI artifact exposed an undeclared Homebrew `giflib` dependency:
its runner tests passed, but the binary could not start on this Mac. The build now disables
optional GIF/OpenEXR codecs and tcmalloc. CI, the Mac build script and relocated-package
verification reject non-system Mac dynamic libraries and deployment targets newer than 14.0.
This changes packaging, not the qualified metric or pixel preparation. Final artifact reruns
are recorded in the PR qualification; the baseline hashes below identify the original runs. The native tool uses libjxl revision
`a7a9c787341cf703dede03c2009fa460cae5e5df`, preparation
`sdr-srgb-native-still-v1`, and the exact dependencies in the
[source manifest](../../../tools/image-quality-native/sources.json).

All media is generated, with isolated configuration, databases, workspaces and loopback
API processes. Riker and Quark tests execute owned temporary tools inside existing
containers. They do not deploy images, modify production libraries/pairing/settings or
exercise image-worker placement. Image work remains server-local.

| Surface | Native qualification | Application workflow and limits |
| --- | --- | --- |
| Development Mac, Apple Silicon | Fresh Release build; all 9 native test methods pass | 7 of 8 harness checks pass, including JPEG, report-only/off, quality rejection, unsupported-input rejection and exact rollback. WebP encoding fails before verification because the installed FFmpeg lacks `libwebp`; this matrix is not fully green. |
| Riker, Linux x64 server container | CI-built helper; all 9 native test methods pass | All 8 workflow checks pass, including JPEG and WebP. Live running and queued cancellation pass. |
| Quark, Linux x64 sidecar container | Same CI-built helper; all 9 native test methods pass | All 8 workflow checks pass. The API is an isolated local server, not the sidecar's image protocol. Live running and queued cancellation pass. |
| Quark, Linux x64 host | Freshly rebuilt pinned native tool; all 9 test methods pass | Maximum-size resource comparison and live cancellation pass. |
| GitHub Linux, macOS and Windows runners | All 9 native test methods pass on each platform in [CI](https://github.com/Jellman86/optimisarr/actions/runs/38042777299) | Separate backend, frontend, sidecar, installer and final-container checks remain enforced by the PR workflows. CI is not physical Picard acceptance. |
| Picard, physical Windows/RTX 4070 | Unavailable | LAN and Tailscale SSH fail; Tailscale reports offline after the operator suggested it might be on. No physical Windows, NVENC or installed MSI claim is made for this slice. |

The Linux helper above is the `image-quality-ubuntu-latest` artifact from that CI run.
Its SHA-256 is `9847a9aca6e79bf07b7a9676eb02a42f55f0099351e3d2fe07db3bc23e669a4a`.
The Mac local helper SHA-256 is
`ad859b995e604fdbf6de1dbb6f2d96802af85b5e94dcc22a98019464d0424c9e`.
Neither is an installed release package. The existing Mac desktop upgrade still awaits
an unlocked desktop; no OS lock was bypassed.

The eight workflow checks include preflight and test-setting restoration, plus JPEG,
WebP, explicit quality rejection, report-only, disabled and unsupported-input cases.
They check unchanged originals, hash-bound evidence, no replacement history after
rejection, idempotent replacement and exact byte-for-byte rollback. Existing decode,
SSIM, metadata and size policies remain applicable. The synthetic gates use 0 for
success and 100 for deliberate JPEG damage; these are test policies, not recommended floors.

## Safety, resources and numerical variation

Native fixtures cover opaque/alpha identity and deliberate damage, JPEG/WebP and embedded
sRGB profiles, malformed/incomplete/conflicting metadata, non-identity EXIF, animation,
higher depths, mismatched dimensions, Unicode paths and compressed metadata expansion.
A reproduced regression showed multiple individually valid PNG text chunks escaping a
per-chunk memory limit. PNG profile/text/EXIF now shares a combined 4 MiB budget, counting
container bytes and expanded payloads, before decoder allocation. Oversized and unsupported
inputs return no score. Required unavailable evidence fails closed.

The actual process proof starts a comparison through `ImagePerceptualQualityService`,
observes its native PID, cancels a second comparison waiting for the shared lane, then
cancels the first. Mac, Riker and Quark observed process exit and unchanged source hashes.
The unit test independently proves the queued cancellation starts no additional tool.
The native process has a 90-second deadline; concurrency is one measurement per server.

A generated 4000 by 4000 opaque PNG/JPEG pair exercises the 16-megapixel limit:

| Build/surface | Wall time | Peak resident memory | Additional observation |
| --- | ---: | ---: | --- |
| Mac fresh Release helper | 1.40 s | 2.32 GiB | 2.83 GiB peak memory footprint; 1.06 s user and 0.28 s system CPU time |
| Quark host Release helper | 3.98 s | 2.32 GiB | 2.28 s user and 1.42 s system CPU time; no swaps |
| CI Linux helper inside Quark container | 2.88 s | 2.32 GiB | Same inputs and score as the Quark host build |

Riker's full-size benchmark was deliberately skipped because current memory headroom
was below the qualification run's conservative 4 GiB reserve. Its small workflow and live
cancellation checks passed. These observations are workload samples, not hard memory
ceilings or throughput guarantees. Encoding and the server need additional memory.
No scratch images or runtime downloads are used by the measurement tool.

Resource input SHA-256 values:

- PNG: `bce4a4dd5642d233d6e4fe4694e7fe330e3665d04fbae7102cf678aeef902566`.
- JPEG: `31e464ddea0d31bdb69c920f580d29159294e53442b910b6e166bd01f51e9124`.

Small Mac/Quark fixture scores differed by less than 0.000001. The large pair scored
83.7102996187 on Mac and 83.7102077625 on Quark, a difference below 0.0001.
An initial Windows WebP fixture differed by 0.00134. Pinned score tests therefore allow
0.005 across compilers and CPU paths. Gate decisions use the raw measured score with
no rounding allowance or tolerance. No automatic default threshold or quality search is added.

## Application and UI checks

The zero-warning Release build and 2,911 backend tests pass. Tests include additive
migration reruns, preservation of saved SSIM choices, format-4 backup/import compatibility,
strict result parsing, inclusive score limits, alpha preservation, hash binding,
unsupported coverage and the single measurement lane. Windows backend CI passes with
the existing documented POSIX-only skips.

Frontend checks, 97 unit tests, all 306 Chromium end-to-end tests and 12 targeted WebKit
layout/accessibility checks pass. New measured/unavailable reports were inspected on phone
and desktop in both themes. The controls preserve drafts, associate errors with the
explicit score field and keep reporting separate from enforcement. Documentation captures
use fabricated data and original artwork; the illustrated floor of 80 is not calibration.

All 105 Python tests and documentation, OpenAPI, release metadata and Unraid validation
pass. The final-container image workflow is a mandatory CI gate alongside existing media
acceptance. Raw reports, process evidence and generated fixtures are retained privately.
Windows signing remains deferred by the operator and is not a release blocker.
