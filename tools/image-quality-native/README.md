# Experimental native image quality

`optimisarr-image-quality original candidate` emits one bounded JSON measurement
using SSIMULACRA2 from the pinned libjxl revision. Both paths are native arguments,
including Windows UTF-16 paths. It does not download tools or write scratch images.

Build and qualify on macOS/Linux:

```sh
scripts/build_image_quality.sh /tmp/image-quality-build /tmp/image-quality-install
```

On Windows, configure with CMake, build target `optimisarr-image-quality` in Release,
run `ctest -C Release -R '^native_image_quality$' --output-on-failure`, then install
component `ImageQuality`. This component excludes upstream tools and test executables.
libpng uses its pinned prebuilt configuration to avoid an upstream include-path issue
on hosts without system zlib headers. Optional GIF/OpenEXR decoders and tcmalloc are disabled
to prevent runner-installed libraries entering the payload. All codec libraries are linked
statically. Mac builds and relocated packages reject non-system dynamic dependencies.

Exact dependencies are recorded in `sources.json`. Selected libjxl submodules are
fixed by its Git tree. The payload includes their licences and this source manifest;
sidecar source archives retain every selected dependency separately.

Supported inputs are same-size, straight-alpha 8-bit SDR sRGB PNG/JPEG/WebP stills,
at least 8 by 8, at most 16 megapixels and 128 MiB per file. Declared ICC, cICP,
gamma, chromaticities and EXIF must be valid and unambiguous. Untagged RGB uses sRGB.
PNG profile/text/EXIF metadata shares a combined 4 MiB budget, counting container bytes
and expanded compressed payloads. JPEG/WebP profiles are limited to 4 MiB each; legacy
PNG raw profiles are unavailable. Other profiles, non-identity orientation, animation, resizing, HDR and higher depths
are unavailable. Container metadata is checked before decode as well as after it;
discarded ICC/EXIF must never turn into a passing score.

Transparency is measured against backgrounds 0.1 and 0.9 using the worst score.
Alpha differences are reported independently, including when only one input has alpha.
Scores are finite and at most 100; negative values are valid. There is no calibrated
default threshold. The server verifies source, candidate and tool SHA-256 before and
after measurement, runs one metric at a time, and kills the process tree on timeout
or cancellation. A 16-megapixel synthetic comparison used about 2.3 GiB resident memory
on both the development Mac and Quark; the Mac peak memory footprint was 2.8 GiB.
Reserve memory alongside encoding; this is not a lightweight
thumbnail metric.

`test_native.py` uses the Python standard library and synthetic, public fixtures.
`generate_fixtures.py` regenerates the small JPEG/WebP fixtures using Pillow 12.0.0;
Pillow is not a build, application or test-runtime dependency. These fixtures establish
coverage and rejection behavior, not photographic calibration or human perception.
The pinned score fixtures allow 0.005 for floating-point differences between compilers
and CPU paths. The first Windows run differed by 0.00134 for the WebP fixture; Mac and
Quark differed by less than 0.000001. Gate decisions always use the measured score
without rounding or a tolerance.

For an isolated application workflow, set `OPTIMISARR_IMAGE_QUALITY` to the built
helper and run `scripts/media_acceptance.py --regression image-quality` with an owned
root, the built API and qualified FFmpeg/FFprobe. It checks JPEG/WebP encoding, report-only
and disabled modes, explicit rejection, unsupported input, source preservation,
replacement idempotency and exact rollback. Missing encoding capabilities remain failures.
Image jobs currently run on the server; packaging the tool in sidecars does not add
image-worker protocol support.
