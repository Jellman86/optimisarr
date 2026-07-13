#!/usr/bin/env bash
set -euo pipefail

image="${1:?usage: ci_container_smoke.sh IMAGE}"
name="optimisarr-ci-smoke-$$"
root="$(mktemp -d)"
cleanup() {
  docker rm -f "$name" >/dev/null 2>&1 || true
  rm -rf "$root"
}
trap cleanup EXIT

mkdir -p "$root"/{config,data,work,trash}
docker run -d --name "$name" -p 127.0.0.1::8787 \
  -e PUID="$(id -u)" -e PGID="$(id -g)" \
  -v "$root/config:/config" -v "$root/data:/data" -v "$root/work:/work" -v "$root/trash:/trash" \
  "$image" >/dev/null

for _ in {1..30}; do
  port="$(docker port "$name" 8787/tcp | awk -F: '{print $NF}')"
  if curl --fail --silent "http://127.0.0.1:$port/api/ready" >/dev/null; then
    docker exec "$name" sh -ec '
      vmaf_log=/tmp/optimisarr-ci-vmaf.json
      "$OPTIMISARR_FFMPEG_VMAF" -nostdin -v error \
        -f lavfi -i "testsrc2=size=48x48:rate=2:duration=1" \
        -f lavfi -i "testsrc2=size=64x64:rate=2:duration=1" \
        -lavfi "[0:v]settb=AVTB,setpts=PTS-STARTPTS,scale=64:64:flags=bicubic:in_range=auto:out_range=tv,format=yuv420p[dist];[1:v]settb=AVTB,setpts=PTS-STARTPTS,scale=64:64:flags=bicubic:in_range=auto:out_range=tv,format=yuv420p[ref];[dist][ref]libvmaf=model=version=vmaf_v0.6.1:feature=name=psnr\|name=float_ssim:n_threads=1:n_subsample=1:log_fmt=json:log_path=$vmaf_log:shortest=1:repeatlast=0" \
        -f null -
      grep -Eq "\"vmaf\"[[:space:]]*:" "$vmaf_log"

      # The automatic UHD plan depends on this bundled model. Loading it against
      # the same tiny frames keeps the smoke quick while proving the final image
      # contains a usable 4K model, not merely the libvmaf filter symbol.
      rm -f "$vmaf_log"
      "$OPTIMISARR_FFMPEG_VMAF" -nostdin -v error \
        -f lavfi -i "testsrc2=size=64x64:rate=1:duration=1" \
        -f lavfi -i "testsrc2=size=64x64:rate=1:duration=1" \
        -lavfi "[0:v]settb=AVTB,setpts=PTS-STARTPTS[dist];[1:v]settb=AVTB,setpts=PTS-STARTPTS[ref];[dist][ref]libvmaf=model=version=vmaf_4k_v0.6.1:n_threads=1:log_fmt=json:log_path=$vmaf_log:shortest=1:repeatlast=0" \
        -f null -
      grep -Eq "\"vmaf\"[[:space:]]*:" "$vmaf_log"

      # Exercise the HDR-reference preparation too. The synthetic pixels are not
      # intended to produce a meaningful score; their BT.2020/PQ tags force the
      # exact production zscale/Hable/Rec.709 chain to initialise and emit a log.
      rm -f "$vmaf_log"
      "$OPTIMISARR_FFMPEG_VMAF" -nostdin -v error \
        -f lavfi -i "testsrc2=size=64x64:rate=1:duration=1" \
        -f lavfi -i "testsrc2=size=64x64:rate=1:duration=1,setparams=range=limited:color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc" \
        -lavfi "[0:v]settb=AVTB,setpts=PTS-STARTPTS,scale=64:64:flags=bicubic:in_range=auto:out_range=tv,format=yuv420p[dist];[1:v]settb=AVTB,setpts=PTS-STARTPTS,zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable,zscale=t=bt709:m=bt709:r=tv,format=yuv420p,scale=64:64:flags=bicubic:in_range=auto:out_range=tv,format=yuv420p[ref];[dist][ref]libvmaf=model=version=vmaf_v0.6.1:n_threads=1:log_fmt=json:log_path=$vmaf_log:shortest=1:repeatlast=0" \
        -f null -
      grep -Eq "\"vmaf\"[[:space:]]*:" "$vmaf_log"
      rm -f "$vmaf_log"
    '
    exit 0
  fi
  sleep 1
done

docker logs "$name"
echo "Optimisarr container did not become ready" >&2
exit 1
