#!/usr/bin/env bash
# Builds the ffmpeg and ffprobe the sidecar bundles, from source.
#
# Built rather than downloaded because no prebuilt macOS binary met the requirement. The one
# Apple Silicon build available published a checksum that did not match the file it served, and
# omitted libvmaf entirely; the build that does ship libvmaf is Intel-only. Since the worker has to
# measure the quality of what it encodes, and the control plane will only accept that measurement
# bound to exact hashes, an ffmpeg without libvmaf cannot do the job at all.
#
# Everything is pinned to a git tag. Codec libraries are built static into a private prefix so the
# result carries no dependency on Homebrew or on anything else that happens to be installed — a
# binary inside a signed app must not stop working because someone ran `brew uninstall`.
#
# Takes a while. x265 in particular is not quick.
set -euo pipefail

cd "$(dirname "$0")/.."

VENDOR="$(pwd)/vendor"
BUILD="$(pwd)/.build-ffmpeg"
PREFIX="${BUILD}/prefix"

# Pinned. Moving any of these is a deliberate act, not a drift.
# Each tag can be overridden from the environment for a one-off experiment; the defaults are
# the pinned build.
X264_TAG="${X264_TAG:-stable}"
X265_TAG="${X265_TAG:-4.2}"
# SVT-AV1 is the AV1 encoder Optimisarr already names for a software AV1 target, so bundling it is
# what lets an AV1 library run on a Mac. Apple ships no AV1 encoder in VideoToolbox on any Apple
# Silicon, M5 included — 27 encoders are advertised and not one is AV1 — so software is the only
# way to encode AV1 here.
SVTAV1_TAG="${SVTAV1_TAG:-v4.2.0}"
VMAF_TAG="${VMAF_TAG:-v3.0.0}"
DAV1D_TAG="${DAV1D_TAG:-1.5.3}"
# n7.1.2, not n7.1. The libx265 wrapper in the base n7.1 tag guards the multi-layer encoder API
# with `#if X265_BUILD >= 210` and no upper bound. x265 reverted that API at build 213, so a
# wrapper built against x265 4.2 passes an array of *pointers* where the library now expects an
# array of *pictures*. x265 writes picture data over ffmpeg's pointers, ffmpeg reads
# `x265pic_lyrptr_out[0]` back as NULL, and dereferences it: a segfault on the very first frame of
# any libx265 encode. Upstream added the missing `&& X265_BUILD < 213` bound, which is in n7.1.1
# onwards. Diagnosed here 2026-09-13 from the crash's own disassembly.
# n8.0.3. AV1 *decode* on the M5 is a hardware path the chip really has, and ffmpeg only gained the
# VideoToolbox AV1 hwaccel in 8.0 — 7.1 has no such thing at any patch level. 8.0 also carries the
# x265 build guard that 7.1.2 was pinned for.
FFMPEG_TAG="${FFMPEG_TAG:-n8.0.3}"
# Extra cmake flags for x265, e.g. -DENABLE_ASSEMBLY=OFF while chasing a crash.
X265_CMAKE_FLAGS="${X265_CMAKE_FLAGS:-}"

JOBS="$(sysctl -n hw.ncpu)"

# Checked up front rather than discovered forty minutes in. x264, x265 and ffmpeg need only cmake
# and a compiler, but libvmaf builds with meson and ninja, so a machine without them gets most of
# the way through and then stops with a bare "command not found".
missing=()
for tool in git cmake meson ninja pkg-config; do
  command -v "${tool}" >/dev/null 2>&1 || missing+=("${tool}")
done
if (( ${#missing[@]} )); then
  echo "error: missing build tools: ${missing[*]}" >&2
  echo "       brew install ${missing[*]}" >&2
  exit 1
fi

mkdir -p "${VENDOR}" "${BUILD}" "${PREFIX}"
# PKG_CONFIG_LIBDIR, not PKG_CONFIG_PATH. PATH *adds* to pkg-config's built-in search path, so on
# any machine with Homebrew — every CI runner, most developer Macs — ffmpeg's configure still finds
# /opt/homebrew/lib/pkgconfig and links whatever it discovers there. The published binary then
# refuses to start on a user's Mac: "Library not loaded: /opt/homebrew/opt/libxcb/lib/libxcb.1.dylib".
# LIBDIR *replaces* that search path, so only what this script built is visible.
export PKG_CONFIG_LIBDIR="${PREFIX}/lib/pkgconfig"
export PKG_CONFIG_PATH="${PREFIX}/lib/pkgconfig"
export PATH="${PREFIX}/bin:${PATH}"

clone_at() {
  local url="$1" tag="$2" dir="$3"
  if [[ -d "${BUILD}/${dir}/.git" ]]; then
    echo "  ${dir}: already cloned"
    return
  fi
  echo "  ${dir}: cloning ${tag}…"
  git clone --depth 1 --branch "${tag}" "${url}" "${BUILD}/${dir}" >/dev/null 2>&1
}

echo "==> Fetching sources"
clone_at "https://code.videolan.org/videolan/x264.git" "${X264_TAG}" x264
clone_at "https://bitbucket.org/multicoreware/x265_git.git" "${X265_TAG}" x265
clone_at "https://gitlab.com/AOMediaCodec/SVT-AV1.git" "${SVTAV1_TAG}" svtav1
clone_at "https://code.videolan.org/videolan/dav1d.git" "${DAV1D_TAG}" dav1d
clone_at "https://github.com/Netflix/vmaf.git" "${VMAF_TAG}" vmaf
clone_at "https://github.com/FFmpeg/FFmpeg.git" "${FFMPEG_TAG}" ffmpeg

echo "==> Recording exactly what was built"
{
  echo "built: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  for d in x264 x265 svtav1 dav1d vmaf ffmpeg; do
    printf '%-8s %s %s\n' "${d}" "$(git -C "${BUILD}/${d}" describe --tags --always 2>/dev/null || echo '?')" \
      "$(git -C "${BUILD}/${d}" rev-parse HEAD 2>/dev/null || echo '?')"
  done
} > "${VENDOR}/BUILD-INFO.txt"
cat "${VENDOR}/BUILD-INFO.txt"

if [[ ! -f "${PREFIX}/lib/libx264.a" ]]; then
  echo "==> x264"
  (cd "${BUILD}/x264" && ./configure --prefix="${PREFIX}" --enable-static --enable-pic \
      --disable-cli --disable-opencl >/dev/null && make -j"${JOBS}" >/dev/null && make install >/dev/null)
fi

# The marker invalidates an older 8-bit-only cached archive.
if [[ ! -f "${PREFIX}/lib/x265-multilib-8-10-12" ]]; then
  echo "==> x265 (8, 10 and 12 bit)"
  for depth in 12 10 8; do
    build_dir="${BUILD}/x265/build/multilib-${depth}"
    mkdir -p "${build_dir}"
    depth_flags=()
    if [[ "${depth}" != 8 ]]; then
      depth_flags=(-DHIGH_BIT_DEPTH=ON -DEXPORT_C_API=OFF)
      [[ "${depth}" == 12 ]] && depth_flags+=(-DMAIN12=ON)
    else
      cp "${BUILD}/x265/build/multilib-10/libx265.a" "${build_dir}/libx265_main10.a"
      cp "${BUILD}/x265/build/multilib-12/libx265.a" "${build_dir}/libx265_main12.a"
      depth_flags=(-DLINKED_10BIT=ON -DLINKED_12BIT=ON
        '-DEXTRA_LIB=x265_main10.a;x265_main12.a' "-DEXTRA_LINK_FLAGS=-L${build_dir}")
    fi
    cmake -S "${BUILD}/x265/source" -B "${build_dir}" -DCMAKE_INSTALL_PREFIX="${PREFIX}" \
      -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
      -DENABLE_SHARED=OFF -DENABLE_CLI=OFF "${depth_flags[@]}" ${X265_CMAKE_FLAGS} >/dev/null
    cmake --build "${build_dir}" -j "${JOBS}" >/dev/null
  done
  cmake --install "${BUILD}/x265/build/multilib-8" >/dev/null
  libtool -static -o "${PREFIX}/lib/libx265.a" \
    "${BUILD}/x265/build/multilib-8/libx265.a" \
    "${BUILD}/x265/build/multilib-10/libx265.a" \
    "${BUILD}/x265/build/multilib-12/libx265.a"
  touch "${PREFIX}/lib/x265-multilib-8-10-12"
fi

if [[ ! -f "${PREFIX}/lib/libdav1d.a" ]]; then
  echo "==> dav1d (software AV1 decode for verification)"
  meson setup "${BUILD}/dav1d/build" "${BUILD}/dav1d" --buildtype release \
    --default-library static --prefix "${PREFIX}" -Denable_tools=false \
    -Denable_tests=false -Denable_docs=false >/dev/null
  ninja -C "${BUILD}/dav1d/build" install >/dev/null
fi

if [[ ! -f "${PREFIX}/lib/libSvtAv1Enc.a" ]]; then
  echo "==> SVT-AV1"
  mkdir -p "${BUILD}/svtav1/build"
  (cd "${BUILD}/svtav1/build" && cmake .. -DCMAKE_INSTALL_PREFIX="${PREFIX}" \
      -DCMAKE_BUILD_TYPE=Release \
      -DBUILD_SHARED_LIBS=OFF -DBUILD_APPS=OFF -DBUILD_TESTING=OFF >/dev/null \
    && make -j"${JOBS}" >/dev/null && make install >/dev/null)
fi

if [[ ! -f "${PREFIX}/lib/libvmaf.a" ]]; then
  echo "==> libvmaf"
  (cd "${BUILD}/vmaf/libvmaf" && meson setup build --buildtype release --default-library static \
      --prefix "${PREFIX}" -Denable_tests=false -Denable_docs=false >/dev/null \
    && ninja -C build >/dev/null && ninja -C build install >/dev/null)
fi

echo "==> ffmpeg"
# VideoToolbox comes from the platform rather than a third-party library, and is what makes hardware
# encoding possible here at all. libvmaf is the reason this script exists.
(cd "${BUILD}/ffmpeg" && ./configure \
    --prefix="${PREFIX}" \
    --pkg-config-flags="--static" \
    --extra-cflags="-I${PREFIX}/include" \
    --extra-ldflags="-L${PREFIX}/lib" \
    `# libvmaf is partly C++ (its SVM model parser), and ffmpeg links through the C driver, so the` \
    `# C++ runtime has to be named explicitly or the link fails on __cxa_throw and friends.` \
    --extra-libs="-lc++" \
    `# Nothing is linked unless it is named below. Without this, configure quietly picks up` \
    `# whatever happens to be installed on the build machine, and the result only runs there.` \
    --disable-autodetect \
    --enable-zlib \
    --enable-gpl \
    --enable-version3 \
    --enable-static --disable-shared \
    --enable-libx264 \
    --enable-libx265 \
    --enable-libsvtav1 \
    --enable-libdav1d \
    --enable-libvmaf \
    --enable-videotoolbox \
    --disable-doc \
    --disable-debug \
    --disable-ffplay \
    >/dev/null && make -j"${JOBS}" >/dev/null)

cp "${BUILD}/ffmpeg/ffmpeg" "${VENDOR}/ffmpeg"
cp "${BUILD}/ffmpeg/ffprobe" "${VENDOR}/ffprobe"
chmod +x "${VENDOR}/ffmpeg" "${VENDOR}/ffprobe"

echo
echo "==> Built"
"${VENDOR}/ffmpeg" -hide_banner -version | head -1
echo "libvmaf present: $("${VENDOR}/ffmpeg" -hide_banner -filters 2>/dev/null | grep -c libvmaf)"
echo "videotoolbox encoders: $("${VENDOR}/ffmpeg" -hide_banner -encoders 2>/dev/null | grep -c videotoolbox)"
echo "AV1 encoder: $("${VENDOR}/ffmpeg" -hide_banner -encoders 2>/dev/null | grep -c libsvtav1)"
echo "AV1 hardware decode: $("${VENDOR}/ffmpeg" -hide_banner -h decoder=av1 2>/dev/null | grep -ci videotoolbox)"
# A hard gate, not a note. This exact check printed the Homebrew libxcb dependency that shipped in
# 0.1.0 and 0.1.1 and made both unusable on any Mac but the one that built them; it printed it and
# the build carried on. A binary that is bundled into an app must depend on nothing but the OS.
echo "==> Checking the binaries are self-contained"
portable=true
for binary in ffmpeg ffprobe; do
  foreign="$(otool -L "${VENDOR}/${binary}" | tail -n +2 | grep -v '/usr/lib/\|/System/' || true)"
  if [[ -n "${foreign}" ]]; then
    echo "error: ${binary} links libraries that will not exist on a user's Mac:" >&2
    echo "${foreign}" >&2
    portable=false
  fi
done
if [[ "${portable}" != true ]]; then
  echo "       Something on this machine was picked up at configure time. The build is not" >&2
  echo "       redistributable; do not ship it." >&2
  exit 1
fi
echo "  both link only the OS"

# Listings alone missed both bugs: an encoder can silently reduce bit depth, and AV1 hardware
# decode does not provide the software decoder used by full verification.
python3 ../../scripts/check_media_tool_bundle.py --ffmpeg "${VENDOR}/ffmpeg" --ffprobe "${VENDOR}/ffprobe"
