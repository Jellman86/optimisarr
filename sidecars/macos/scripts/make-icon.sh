#!/usr/bin/env bash
# Builds AppIcon.icns from Resources/AppIcon.png.
#
# Without an icon the app shows Finder's blank placeholder, which in a disk-image window sitting
# next to the Applications folder looks like something went wrong with the download. The mark is
# Optimisarr's own, copied from the web app so the Mac app is recognisably the same product.
#
# The source is 192px, which is what the project has. Every size up to 128 is a downscale and
# crisp; 256 and above are upscaled and correspondingly soft. Finder shows 128 or less almost
# everywhere, including the disk image window, so this is the right trade until a larger master
# exists.
set -euo pipefail

cd "$(dirname "$0")/.."

SOURCE="Resources/AppIcon.png"
ICONSET="build/AppIcon.iconset"
ICNS="build/AppIcon.icns"

[[ -f "${SOURCE}" ]] || { echo "error: ${SOURCE} is missing." >&2; exit 2; }

rm -rf "${ICONSET}"
mkdir -p "${ICONSET}"

# The names are fixed: iconutil rejects an iconset containing anything it does not recognise.
for spec in "16 icon_16x16" "32 icon_16x16@2x" "32 icon_32x32" "64 icon_32x32@2x" \
            "128 icon_128x128" "256 icon_128x128@2x" "256 icon_256x256" "512 icon_256x256@2x" \
            "512 icon_512x512" "1024 icon_512x512@2x"; do
  size="${spec%% *}"
  name="${spec##* }"
  sips -s format png -z "${size}" "${size}" "${SOURCE}" --out "${ICONSET}/${name}.png" >/dev/null
done

iconutil --convert icns --output "${ICNS}" "${ICONSET}"
rm -rf "${ICONSET}"
echo "Built ${ICNS}"
