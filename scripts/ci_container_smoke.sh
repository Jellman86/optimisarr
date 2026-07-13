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

      # Exercise representative production transcode argument shapes with the FFmpeg that ships
      # for real jobs. These are deliberately small synthetic fixtures, but unlike unit tests they
      # prove the selected encoders, muxers, stream maps, metadata, and artwork dispositions work
      # together in the final image.
      transcode="$OPTIMISARR_FFMPEG"
      # Production probing and verification use the ffprobe paired with the transcode build.
      probe="$OPTIMISARR_FFPROBE"
      fixture=/tmp/optimisarr-pipeline-smoke
      rm -rf "$fixture"
      mkdir -p "$fixture"

      # Music: FLAC + JPEG cover + tags -> the default AAC/M4A policy. The output must contain
      # AAC, the attached picture, and the exact source artist tag.
      "$transcode" -nostdin -v error -y \
        -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=1" \
        -f lavfi -i "color=c=blue:size=48x48:rate=1:duration=1" \
        -map 0:a -map 1:v -c:a flac -c:v mjpeg -frames:v 1 -disposition:v attached_pic \
        -metadata artist="Optimisarr Smoke Artist" "$fixture/source.flac"
      "$transcode" -nostdin -v error -y -i "$fixture/source.flac" \
        -map_metadata 0 -map 0:a -c:a aac -b:a 128k \
        -map 0:v? -c:v copy -map 0:s? -c:s mov_text \
        -metadata optimisarr=ci-smoke -movflags use_metadata_tags "$fixture/output.m4a"
      test "$("$probe" -v error -select_streams a:0 -show_entries stream=codec_name \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.m4a")" = aac
      "$probe" -v error -select_streams v -show_entries stream_disposition=attached_pic \
        -of default=noprint_wrappers=1 "$fixture/output.m4a" | grep -qx "attached_pic=1"
      test "$("$probe" -v error -show_entries format_tags=artist \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.m4a")" = "Optimisarr Smoke Artist"

      # Still image: an RGBA PNG -> production lossless-WebP policy. Compare decoded RGBA frame
      # hashes, so a nominally successful encoder invocation cannot hide alpha or pixel loss.
      "$transcode" -nostdin -v error -y -f lavfi \
        -i "color=c=red@0.5:size=48x48:rate=1:duration=1,format=rgba" \
        -frames:v 1 "$fixture/source.png"
      exiftool -overwrite_original -EXIF:Artist="Optimisarr Smoke Artist" "$fixture/source.png" >/dev/null
      "$transcode" -nostdin -v error -y -i "$fixture/source.png" \
        -map_metadata 0 -map 0:v:0 -c:v libwebp -lossless 1 -quality 80 "$fixture/output.webp"
      exiftool -overwrite_original -TagsFromFile "$fixture/source.png" \
        -EXIF:all -ICC_Profile:all --Orientation --ThumbnailImage --PreviewImage --JpgFromRaw \
        --ImageWidth --ImageHeight --ExifImageWidth --ExifImageHeight "$fixture/output.webp" >/dev/null
      test "$(exiftool -s3 -EXIF:Artist "$fixture/output.webp")" = "Optimisarr Smoke Artist"
      "$transcode" -nostdin -v error -i "$fixture/source.png" -pix_fmt rgba \
        -f framemd5 "$fixture/source.framemd5"
      "$transcode" -nostdin -v error -i "$fixture/output.webp" -pix_fmt rgba \
        -f framemd5 "$fixture/output.framemd5"
      diff -u "$fixture/source.framemd5" "$fixture/output.framemd5"
      image_ssim_log="$fixture/image-ssim.log"
      "$OPTIMISARR_FFMPEG_VMAF" -nostdin -v error \
        -i "$fixture/output.webp" -i "$fixture/source.png" \
        -lavfi "[0:v]settb=AVTB,setpts=PTS-STARTPTS,scale=48:48:flags=bicubic:in_range=auto:out_range=full,format=gbrap[dist];[1:v]settb=AVTB,setpts=PTS-STARTPTS,scale=48:48:flags=bicubic:in_range=auto:out_range=full,format=gbrap[ref];[dist][ref]ssim=stats_file=$image_ssim_log:shortest=1:repeatlast=0" \
        -f null -
      grep -Eq "All:[[:space:]]*(1|0\\.9)" "$image_ssim_log"

      # Exercise the other two selectable image targets with their exact production quality
      # mappings. These fixtures are opaque because JPEG and the current AVIF path intentionally
      # reject alpha before queueing.
      "$transcode" -nostdin -v error -y -f lavfi \
        -i "color=c=blue:size=48x48:rate=1:duration=1" -frames:v 1 "$fixture/source-opaque.png"
      exiftool -overwrite_original -EXIF:Artist="Optimisarr Smoke Artist" \
        "$fixture/source-opaque.png" >/dev/null
      "$transcode" -nostdin -v error -y -i "$fixture/source-opaque.png" \
        -map_metadata 0 -map 0:v:0 -c:v mjpeg -q:v 8 "$fixture/output.jpg"
      exiftool -overwrite_original -TagsFromFile "$fixture/source-opaque.png" \
        -EXIF:all -ICC_Profile:all --Orientation --ThumbnailImage --PreviewImage --JpgFromRaw \
        --ImageWidth --ImageHeight --ExifImageWidth --ExifImageHeight "$fixture/output.jpg" >/dev/null
      test "$(exiftool -s3 -EXIF:Artist "$fixture/output.jpg")" = "Optimisarr Smoke Artist"
      test "$("$probe" -v error -select_streams v:0 -show_entries stream=codec_name \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.jpg")" = mjpeg
      test "$("$probe" -v error -select_streams v:0 -show_entries stream=width \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.jpg")" = 48
      test "$("$probe" -v error -select_streams v:0 -show_entries stream=height \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.jpg")" = 48
      "$transcode" -nostdin -v error -i "$fixture/output.jpg" -f null -

      "$transcode" -nostdin -v error -y -i "$fixture/source-opaque.png" \
        -map_metadata 0 -map 0:v:0 -c:v libaom-av1 -still-picture 1 -pix_fmt yuv420p \
        -crf 13 -b:v 0 "$fixture/output.avif"
      exiftool -overwrite_original -TagsFromFile "$fixture/source-opaque.png" \
        -EXIF:all -ICC_Profile:all --Orientation --ThumbnailImage --PreviewImage --JpgFromRaw \
        --ImageWidth --ImageHeight --ExifImageWidth --ExifImageHeight "$fixture/output.avif" >/dev/null
      test "$(exiftool -s3 -EXIF:Artist "$fixture/output.avif")" = "Optimisarr Smoke Artist"
      test "$("$probe" -v error -select_streams v:0 -show_entries stream=codec_name \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.avif")" = av1
      test "$("$probe" -v error -select_streams v:0 -show_entries stream=width \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.avif")" = 48
      test "$("$probe" -v error -select_streams v:0 -show_entries stream=height \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.avif")" = 48
      "$transcode" -nostdin -v error -i "$fixture/output.avif" -f null -

      # Video: H.264/AAC MP4 -> the current production HEVC MP4 stream-map and codec policy.
      "$transcode" -nostdin -v error -y \
        -f lavfi -i "testsrc2=size=64x64:rate=12:duration=1" \
        -f lavfi -i "sine=frequency=880:sample_rate=48000:duration=1" \
        -map 0:v -map 1:a -c:v libx264 -pix_fmt yuv420p -c:a aac "$fixture/source.mp4"
      "$transcode" -nostdin -v error -y -fflags +genpts -i "$fixture/source.mp4" \
        -map 0 -map -0:t -map -0:d -c copy -c:v:0 libx265 -crf 24 -preset ultrafast \
        -c:a copy -c:s mov_text \
        -metadata optimisarr=ci-smoke -movflags use_metadata_tags "$fixture/output.mp4"
      test "$("$probe" -v error -select_streams v:0 -show_entries stream=codec_name \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.mp4")" = hevc
      test "$("$probe" -v error -select_streams v:0 -show_entries stream=width \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.mp4")" = 64
      test "$("$probe" -v error -select_streams v:0 -show_entries stream=height \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.mp4")" = 64
      test "$("$probe" -v error -select_streams v:0 -show_entries stream=pix_fmt \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.mp4")" = yuv420p
      test -n "$("$probe" -v error -select_streams v:0 -show_entries stream=profile \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.mp4")"
      test "$("$probe" -v error -select_streams a:0 -show_entries stream=codec_name \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output.mp4")" = aac
      "$transcode" -nostdin -v error -i "$fixture/output.mp4" -f null -

      # VFR policy: create irregular presentation intervals, then run the production evidence-based
      # timing options and prove the MP4 output still advertises distinct nominal/average rates.
      "$transcode" -nostdin -v error -y -f lavfi \
        -i "testsrc2=size=64x64:rate=12:duration=2" \
        -vf "select=eq(mod(n\,5)\,0)+eq(mod(n\,5)\,1)+eq(mod(n\,5)\,3)" \
        -fps_mode vfr -c:v libx264 -pix_fmt yuv420p "$fixture/source-vfr.mkv"
      "$transcode" -nostdin -v error -y -fflags +genpts -i "$fixture/source-vfr.mkv" \
        -map 0 -map -0:t -map -0:d -c copy -c:v:0 libx265 -crf 24 -preset ultrafast \
        -fps_mode vfr -enc_time_base:v:0 demux -c:a copy -c:s mov_text \
        -metadata optimisarr=ci-smoke -movflags use_metadata_tags "$fixture/output-vfr.mp4"
      nominal="$("$probe" -v error -select_streams v:0 -show_entries stream=r_frame_rate \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output-vfr.mp4")"
      average="$("$probe" -v error -select_streams v:0 -show_entries stream=avg_frame_rate \
        -of default=noprint_wrappers=1:nokey=1 "$fixture/output-vfr.mp4")"
      test -n "$nominal"
      test "$nominal" != "$average"
      "$transcode" -nostdin -v error -i "$fixture/output-vfr.mp4" -f null -
      rm -rf "$fixture"
    '
    exit 0
  fi
  sleep 1
done

docker logs "$name"
echo "Optimisarr container did not become ready" >&2
exit 1
