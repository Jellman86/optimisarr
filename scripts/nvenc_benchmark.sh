#!/usr/bin/env bash
set -euo pipefail

if [[ $(id -u) -eq 0 ]] && command -v gosu >/dev/null 2>&1 && [[ -z ${OPTIMISARR_NVENC_BENCHMARK_NO_DROP:-} ]]; then
  exec gosu "${PUID:-1000}:${PGID:-1000}" "$0" "$@"
fi

keep_outputs=false
case "${1:-}" in
  "") ;;
  --keep-outputs) keep_outputs=true ;;
  --help|-h)
    cat <<'EOF'
Usage: nvenc_benchmark.sh [--keep-outputs]

Run once to create /data/Optimisarr-NVENC-Test/input. Add exactly three short,
8-bit SDR video clips there, then run the same command again. The originals are
opened read-only and are never changed.
EOF
    exit 0
    ;;
  *)
    printf 'Unknown option: %s\n' "$1" >&2
    exit 2
    ;;
esac

benchmark_root="${OPTIMISARR_NVENC_BENCHMARK_ROOT:-/data/Optimisarr-NVENC-Test}"
input_dir="$benchmark_root/input"
results_dir="$benchmark_root/results"
work_dir="$benchmark_root/work"
ffmpeg="${OPTIMISARR_FFMPEG:-/usr/lib/jellyfin-ffmpeg/ffmpeg}"
ffprobe="${OPTIMISARR_FFPROBE:-/usr/lib/jellyfin-ffmpeg/ffprobe}"
vmaf_ffmpeg="${OPTIMISARR_FFMPEG_VMAF:-/usr/local/lib/optimisarr/ffmpeg-vmaf}"
nvidia_smi="${OPTIMISARR_NVIDIA_SMI:-nvidia-smi}"
clip_seconds=30
quality=20

mkdir -p "$input_dir" "$results_dir"

mapfile -d '' -t input_files < <(
  find "$input_dir" -maxdepth 1 -type f \
    \( -iname '*.mkv' -o -iname '*.mp4' -o -iname '*.m4v' -o -iname '*.mov' -o -iname '*.avi' -o -iname '*.webm' -o -iname '*.ts' -o -iname '*.m2ts' \) \
    -print0 | sort -z
)

if [[ ${#input_files[@]} -ne 3 ]]; then
  printf 'Optimisarr NVIDIA quality comparison\n\n'
  printf 'Add exactly three short video clips to:\n  %s\n\n' "$input_dir"
  printf 'Good choices are one calm clip, one fast-moving clip, and one dark or grainy clip.\n'
  printf 'Please use ordinary 8-bit SDR clips between roughly 30 seconds and 2 minutes.\n'
  printf 'Found %s suitable video file(s). Then run this same command again.\n' "${#input_files[@]}"
  printf 'Your original clips will not be changed.\n'
  exit 2
fi

require_executable() {
  local executable="$1"
  local description="$2"
  if [[ "$executable" == */* ]]; then
    [[ -x "$executable" ]] || {
      printf '%s is unavailable: %s\n' "$description" "$executable" >&2
      exit 1
    }
  elif ! command -v "$executable" >/dev/null 2>&1; then
    printf '%s is unavailable: %s\n' "$description" "$executable" >&2
    exit 1
  fi
}

require_executable "$ffmpeg" 'FFmpeg'
require_executable "$ffprobe" 'ffprobe'
require_executable "$vmaf_ffmpeg" 'VMAF FFmpeg'
require_executable "$nvidia_smi" 'nvidia-smi'

encoder_list="$($ffmpeg -hide_banner -encoders 2>&1)"
for encoder in h264_nvenc hevc_nvenc; do
  grep -q "$encoder" <<<"$encoder_list" || {
    printf 'The bundled FFmpeg does not provide %s. No test was run.\n' "$encoder" >&2
    exit 1
  }
  encoder_help="$($ffmpeg -hide_banner -h "encoder=$encoder" 2>&1)"
  for option in multipass rc-lookahead spatial_aq temporal_aq aq-strength; do
    grep -q -- "-$option" <<<"$encoder_help" || {
      printf '%s does not support the required %s option. No test was run.\n' "$encoder" "$option" >&2
      exit 1
    }
  done
done

for index in "${!input_files[@]}"; do
  duration="$($ffprobe -v error -select_streams v:0 -show_entries format=duration \
    -of default=noprint_wrappers=1:nokey=1 "${input_files[$index]}" 2>/dev/null || true)"
  if ! awk -v value="$duration" 'BEGIN { exit !(value ~ /^[0-9]+([.][0-9]+)?$/ && value > 0) }'; then
    printf 'Sample %s is not a readable video with a known duration. No test was run.\n' "$((index + 1))" >&2
    exit 1
  fi
done

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$work_dir"
report_tmp="$results_dir/.optimisarr-nvenc-results-$timestamp-$$.tmp"
report="$results_dir/optimisarr-nvenc-results-$timestamp.txt"

cleanup() {
  if [[ -n ${gpu_sampler_pid:-} ]]; then
    kill "$gpu_sampler_pid" >/dev/null 2>&1 || true
    wait "$gpu_sampler_pid" 2>/dev/null || true
    gpu_sampler_pid=''
  fi
  rm -f "$report_tmp"
  if [[ $keep_outputs == false ]]; then
    rm -rf "$work_dir"
  fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

variant_names=(
  'Current Efficient baseline'
  'Full-resolution multipass'
  'Full-resolution multipass with lookahead'
  'Full-resolution multipass with lookahead and spatial AQ'
  'Full-resolution multipass with temporal AQ'
)

variant_arguments() {
  case "$1" in
    0) : ;;
    1) printf '%s\0' -multipass fullres ;;
    2) printf '%s\0' -multipass fullres -rc-lookahead 32 ;;
    3) printf '%s\0' -multipass fullres -rc-lookahead 32 -spatial_aq 1 -aq-strength 8 ;;
    4) printf '%s\0' -multipass fullres -rc-lookahead 32 -temporal_aq 1 ;;
  esac
}

encode_failure_kind() {
  local log="$1"
  if grep -qi 'cannot load libcuda' "$log"; then
    printf 'driver'
  elif grep -qiE 'no capable devices|no nvenc capable devices' "$log"; then
    printf 'unsupported'
  elif grep -qiE 'invalid argument|option not found|not supported' "$log"; then
    printf 'unsupported'
  else
    printf 'encode'
  fi
}

encode_failure_message() {
  case "$1" in
    driver) printf 'CUDA driver could not be loaded' ;;
    unsupported) printf 'GPU or driver does not support this option combination' ;;
    *) printf 'FFmpeg could not complete this encode' ;;
  esac
}

encode_failure_detail() {
  local log="$1"
  if grep -qi 'cannot load libcuda' "$log"; then
    printf 'FFmpeg could not load libcuda'
  elif grep -qiE 'no capable devices|no nvenc capable devices' "$log"; then
    printf 'NVENC reported no capable devices'
  elif grep -qi 'option not found' "$log"; then
    printf 'FFmpeg reported that an option was not found'
  elif grep -qi 'not supported' "$log"; then
    printf 'FFmpeg reported that the option combination is not supported'
  elif grep -qi 'invalid argument' "$log"; then
    printf 'FFmpeg rejected an invalid argument'
  else
    printf 'No privacy-safe diagnostic was available'
  fi
}

sample_gpu() {
  local sample
  if sample="$("$nvidia_smi" --id=0 --query-gpu=utilization.encoder,utilization.gpu,memory.used \
    --format=csv,noheader,nounits 2>/dev/null)"; then
    printf '%s\n' "$sample"
  elif sample="$("$nvidia_smi" --id=0 --query-gpu=utilization.gpu,memory.used \
    --format=csv,noheader,nounits 2>/dev/null)"; then
    printf ', %s\n' "$sample"
  fi
}

start_gpu_sampler() {
  local log="$1"
  sample_gpu >"$log"
  (
    while sleep 0.1; do
      sample_gpu
    done
  ) >>"$log" &
  gpu_sampler_pid=$!
}

stop_gpu_sampler() {
  if [[ -n ${gpu_sampler_pid:-} ]]; then
    kill "$gpu_sampler_pid" >/dev/null 2>&1 || true
    wait "$gpu_sampler_pid" 2>/dev/null || true
    gpu_sampler_pid=''
  fi
}

summarise_gpu_samples() {
  local log="$1"
  awk -F, '
    {
      gsub(/[^0-9.]/, "", $1)
      gsub(/[^0-9.]/, "", $2)
      gsub(/[^0-9.]/, "", $3)
      if ($1 != "") {
        encoder_total += $1
        encoder_count++
        if ($1 > encoder_peak) encoder_peak = $1
      }
      if ($2 != "") {
        gpu_total += $2
        gpu_count++
        if ($2 > gpu_peak) gpu_peak = $2
      }
      if ($3 != "" && $3 > memory) memory = $3
    }
    END {
      if (gpu_count == 0) {
        print "unavailable"
      } else if (encoder_count == 0) {
        printf "encoder utilisation unavailable; average GPU %.1f%%; peak GPU %.1f%%; peak memory %.0f MiB",
          gpu_total / gpu_count, gpu_peak, memory
      } else {
        printf "average encoder %.1f%%; peak encoder %.1f%%; average GPU %.1f%%; peak GPU %.1f%%; peak memory %.0f MiB",
          encoder_total / encoder_count, encoder_peak, gpu_total / gpu_count, gpu_peak, memory
      }
    }
  ' "$log"
}

extract_vmaf_mean() {
  local log="$1"
  awk -F: '
    /"pooled_metrics"[[:space:]]*:/ { pooled=1; next }
    pooled && /"vmaf"[[:space:]]*:/ { in_vmaf=1; next }
    in_vmaf && /"mean"[[:space:]]*:/ {
      gsub(/[,[:space:]]/, "", $2)
      print $2
      exit
    }
  ' "$log"
}

{
  printf 'Optimisarr NVENC quality comparison\n'
  printf 'Harness version: 2\n'
  printf 'Run time (UTC): %s\n' "$timestamp"
  printf 'Inputs: 3 anonymous samples; first %s seconds of each\n' "$clip_seconds"
  printf 'Quality: CQ %s; preset P7; uncapped VBR\n' "$quality"
  printf 'Original files changed: no\n'
  printf 'Temporary outputs retained: %s\n\n' "$keep_outputs"
  printf 'NVIDIA environment\n'
  "$nvidia_smi" --id=0 --query-gpu=name,driver_version,memory.total \
    --format=csv,noheader 2>/dev/null || printf 'Unavailable\n'
  printf '\nFFmpeg\n'
  "$ffmpeg" -version 2>/dev/null | head -n 1
  printf '\nResults\n'
} > "$report_tmp"

failures=0
skips=0
for sample_index in "${!input_files[@]}"; do
  source_file="${input_files[$sample_index]}"
  source_duration="$($ffprobe -v error -select_streams v:0 -show_entries format=duration \
    -of default=noprint_wrappers=1:nokey=1 "$source_file")"
  measured_duration="$(awk -v duration="$source_duration" -v limit="$clip_seconds" \
    'BEGIN { if (duration < limit) printf "%.3f", duration; else printf "%.3f", limit }')"
  printf '\nSample %s (%.3f seconds tested)\n' "$((sample_index + 1))" "$measured_duration" >> "$report_tmp"

  for encoder in h264_nvenc hevc_nvenc; do
    printf '  Encoder: %s\n' "$encoder" >> "$report_tmp"
    for variant_index in "${!variant_names[@]}"; do
      mapfile -d '' -t extra_arguments < <(variant_arguments "$variant_index")
      output="$work_dir/sample-$((sample_index + 1))-$encoder-variant-$((variant_index + 1)).mkv"
      encode_log="$work_dir/encode-$sample_index-$encoder-$variant_index.log"
      gpu_log="$work_dir/gpu-$sample_index-$encoder-$variant_index.log"
      vmaf_log="$work_dir/vmaf-$sample_index-$encoder-$variant_index.json"
      start_gpu_sampler "$gpu_log"
      start_time_ns="$(date +%s%N)"

      set +e
      "$ffmpeg" -nostdin -hide_banner -v warning -y \
        -i "$source_file" -t "$measured_duration" -map 0:v:0 -an -sn -dn \
        -c:v "$encoder" -pix_fmt yuv420p -rc vbr -cq "$quality" -b:v 0 -preset p7 \
        "${extra_arguments[@]}" "$output" >"$encode_log" 2>&1
      encode_status=$?
      set -e
      end_time_ns="$(date +%s%N)"
      stop_gpu_sampler
      elapsed_ms=$(((end_time_ns - start_time_ns + 500000) / 1000000))
      (( elapsed_ms > 0 )) || elapsed_ms=1
      elapsed_seconds="$(awk -v milliseconds="$elapsed_ms" 'BEGIN { printf "%.3f", milliseconds / 1000 }')"
      gpu_summary="$(summarise_gpu_samples "$gpu_log")"

      if [[ ${#extra_arguments[@]} -eq 0 ]]; then
        option_summary='(none; current Efficient defaults)'
      else
        option_summary="${extra_arguments[*]}"
      fi
      printf '    %s\n' "${variant_names[$variant_index]}" >> "$report_tmp"
      printf '      Options: %s\n' "$option_summary" >> "$report_tmp"
      if [[ $encode_status -ne 0 ]]; then
        failure_kind="$(encode_failure_kind "$encode_log")"
        failure_message="$(encode_failure_message "$failure_kind")"
        failure_detail="$(encode_failure_detail "$encode_log")"
        if [[ $variant_index -gt 0 && $failure_kind == unsupported ]]; then
          printf '      Status: skipped — %s\n' "$failure_message" >> "$report_tmp"
          skips=$((skips + 1))
        else
          printf '      Status: FAILED — %s\n' "$failure_message" >> "$report_tmp"
          failures=$((failures + 1))
        fi
        printf '      Detail: %s\n' "$failure_detail" >> "$report_tmp"
        printf '      GPU: %s\n' "$gpu_summary" >> "$report_tmp"
        continue
      fi

      if ! "$ffmpeg" -nostdin -hide_banner -v error -i "$output" -map 0:v:0 -f null - \
        >/dev/null 2>&1; then
        printf '      Status: FAILED — encoded output did not decode cleanly\n' >> "$report_tmp"
        printf '      GPU: %s\n' "$gpu_summary" >> "$report_tmp"
        failures=$((failures + 1))
        continue
      fi

      set +e
      "$vmaf_ffmpeg" -nostdin -hide_banner -v error \
        -i "$output" -i "$source_file" -t "$measured_duration" \
        -lavfi "[0:v]settb=AVTB,setpts=PTS-STARTPTS,format=yuv420p[dist];[1:v]trim=duration=$measured_duration,settb=AVTB,setpts=PTS-STARTPTS,format=yuv420p[ref];[dist][ref]libvmaf=model=version=vmaf_v0.6.1:n_threads=1:n_subsample=5:log_fmt=json:log_path=$vmaf_log:shortest=1:repeatlast=0" \
        -f null - >/dev/null 2>&1
      vmaf_status=$?
      set -e
      vmaf_mean='unavailable'
      if [[ $vmaf_status -eq 0 && -s $vmaf_log ]]; then
        vmaf_mean="$(extract_vmaf_mean "$vmaf_log")"
        [[ -n $vmaf_mean ]] || vmaf_mean='unavailable'
      fi

      output_size="$(stat -c '%s' "$output")"
      speed="$(awk -v duration="$measured_duration" -v milliseconds="$elapsed_ms" \
        'BEGIN { printf "%.2f", duration * 1000 / milliseconds }')"
      if [[ $vmaf_mean == unavailable ]]; then
        {
          printf '      Status: FAILED — VMAF measurement could not be completed\n'
          printf '      Time: %s seconds (%sx real time)\n' "$elapsed_seconds" "$speed"
          printf '      Output size: %s bytes\n' "$output_size"
          printf '      VMAF mean: unavailable\n'
          printf '      GPU: %s\n' "$gpu_summary"
        } >> "$report_tmp"
        failures=$((failures + 1))
        continue
      fi

      {
        printf '      Status: completed and decoded successfully\n'
        printf '      Time: %s seconds (%sx real time)\n' "$elapsed_seconds" "$speed"
        printf '      Output size: %s bytes\n' "$output_size"
        printf '      VMAF mean: %s\n' "$vmaf_mean"
        printf '      GPU: %s\n' "$gpu_summary"
      } >> "$report_tmp"
    done
  done
done

{
  printf '\nSummary\n'
  if [[ $failures -eq 0 ]]; then
    if [[ $skips -eq 0 ]]; then
      printf 'All comparisons completed and decoded successfully.\n'
    else
      printf '%s comparison(s) skipped because the GPU or driver did not support them.\n' "$skips"
      printf 'All supported comparisons completed and decoded successfully.\n'
    fi
  else
    printf '%s comparison(s) failed. The successful results above are still useful.\n' "$failures"
    if [[ $skips -gt 0 ]]; then
      printf '%s comparison(s) skipped because the GPU or driver did not support them.\n' "$skips"
    fi
  fi
  printf 'Source filenames and paths were intentionally omitted for privacy.\n'
} >> "$report_tmp"

mv "$report_tmp" "$report"
printf '\nTesting finished. Thank you for helping Optimisarr!\n'
printf 'Please upload this text file to GitHub issue #37:\n  %s\n' "$report"
if [[ $keep_outputs == true ]]; then
  printf 'Temporary encoded files were kept in:\n  %s\n' "$work_dir"
fi
