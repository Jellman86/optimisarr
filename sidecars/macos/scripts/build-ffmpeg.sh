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
X264_TAG="stable"
X265_TAG="4.2"
VMAF_TAG="v3.0.0"
FFMPEG_TAG="n7.1"

JOBS="$(sysctl -n hw.ncpu)"

mkdir -p "${VENDOR}" "${BUILD}" "${PREFIX}"
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
clone_at "https://github.com/Netflix/vmaf.git" "${VMAF_TAG}" vmaf
clone_at "https://github.com/FFmpeg/FFmpeg.git" "${FFMPEG_TAG}" ffmpeg

echo "==> Recording exactly what was built"
{
  echo "built: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  for d in x264 x265 vmaf ffmpeg; do
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

if [[ ! -f "${PREFIX}/lib/libx265.a" ]]; then
  echo "==> x265"
  # x265 predates CMake 4, which removed both the pre-3.5 minimum and the OLD setting for policies
  # the project sets explicitly. CMAKE_POLICY_VERSION_MINIMUM alone is not enough — an explicit
  # cmake_policy(SET ... OLD) still errors — so the tag is the fix and this flag covers the
  # minimum-version half.
  (cd "${BUILD}/x265/build" && cmake ../source -DCMAKE_INSTALL_PREFIX="${PREFIX}" \
      -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
      -DENABLE_SHARED=OFF -DENABLE_CLI=OFF >/dev/null && make -j"${JOBS}" >/dev/null && make install >/dev/null)
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
    --enable-gpl \
    --enable-version3 \
    --enable-static --disable-shared \
    --enable-libx264 \
    --enable-libx265 \
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
echo "non-system dynamic links (should be none):"
otool -L "${VENDOR}/ffmpeg" | tail -n +2 | grep -v '/usr/lib/\|/System/' || echo "  none"
