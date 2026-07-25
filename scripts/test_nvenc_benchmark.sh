#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
harness="$repo_root/scripts/nvenc_benchmark.sh"
temp_root="$(mktemp -d)"
cleanup() {
  rm -rf "$temp_root"
}
trap cleanup EXIT

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

first_run_root="$temp_root/first-run"
set +e
first_run_output="$(OPTIMISARR_NVENC_BENCHMARK_ROOT="$first_run_root" bash "$harness" 2>&1)"
first_run_status=$?
set -e

[[ $first_run_status -eq 2 ]] || fail "first run should stop with status 2 after preparing the input folder"
[[ -d "$first_run_root/input" ]] || fail "first run should create the input folder"
[[ -d "$first_run_root/results" ]] || fail "first run should create the results folder"
[[ "$first_run_output" == *"Add exactly three short video clips"* ]] || fail "first run should explain what the tester needs to add"

fake_bin="$temp_root/fake-bin"
mkdir -p "$fake_bin"
cat > "$fake_bin/ffmpeg" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
args=" $* "
if [[ "$args" == *" -version "* ]]; then
  printf 'ffmpeg version test-fixture\n'
  exit 0
fi
if [[ "$args" == *" -encoders "* ]]; then
  printf ' V....D h264_nvenc NVIDIA NVENC h264 encoder\n'
  printf ' V....D hevc_nvenc NVIDIA NVENC hevc encoder\n'
  exit 0
fi
if [[ "$args" == *" encoder=h264_nvenc "* || "$args" == *" encoder=hevc_nvenc "* ]]; then
  printf '%s\n' '  -multipass <int>' '  -rc-lookahead <int>' '  -spatial_aq <boolean>' '  -temporal_aq <boolean>' '  -aq-strength <int>'
  exit 0
fi
if [[ "$args" == *" -f null - "* ]]; then
  exit 0
fi
output="${!#}"
mkdir -p "$(dirname "$output")"
printf 'encoded fixture\n' > "$output"
EOF
cat > "$fake_bin/ffprobe" <<'EOF'
#!/usr/bin/env bash
printf '30.000000\n'
EOF
cat > "$fake_bin/ffmpeg-vmaf" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
if [[ ${FAKE_VMAF_FAIL:-0} == 1 ]]; then
  exit 1
fi
for argument in "$@"; do
  if [[ "$argument" =~ log_path=([^:]+) ]]; then
    log_path="${BASH_REMATCH[1]}"
    cat > "$log_path" <<'JSON'
{
  "pooled_metrics": {
    "vmaf": {
      "min": 90.000,
      "max": 99.000,
      "mean": 95.123
    }
  }
}
JSON
    exit 0
  fi
done
exit 1
EOF
cat > "$fake_bin/nvidia-smi" <<'EOF'
#!/usr/bin/env bash
if [[ " $* " == *" utilization.gpu,memory.used "* ]]; then
  printf '42, 2048\n'
else
  printf 'NVIDIA Test GPU, 555.12, 8192 MiB\n'
fi
EOF
chmod +x "$fake_bin"/*

successful_root="$temp_root/successful-run"
mkdir -p "$successful_root/input"
printf x > "$successful_root/input/private-family-name-calm.mkv"
printf x > "$successful_root/input/private-family-name-motion.mp4"
printf x > "$successful_root/input/private-family-name-dark.mov"

OPTIMISARR_NVENC_BENCHMARK_ROOT="$successful_root" \
OPTIMISARR_FFMPEG="$fake_bin/ffmpeg" \
OPTIMISARR_FFPROBE="$fake_bin/ffprobe" \
OPTIMISARR_FFMPEG_VMAF="$fake_bin/ffmpeg-vmaf" \
OPTIMISARR_NVIDIA_SMI="$fake_bin/nvidia-smi" \
bash "$harness" >/dev/null

mapfile -t reports < <(find "$successful_root/results" -maxdepth 1 -type f -name 'optimisarr-nvenc-results-*.txt')
[[ ${#reports[@]} -eq 1 ]] || fail "a successful run should create one text report"
report="${reports[0]}"
grep -q 'Sample 1' "$report" || fail "the report should use anonymous sample labels"
grep -q 'Current Efficient baseline' "$report" || fail "the report should include the baseline"
grep -q 'Full-resolution multipass with temporal AQ' "$report" || fail "the report should include every planned comparison"
grep -q 'Options: -multipass fullres -rc-lookahead 32 -spatial_aq 1 -aq-strength 8' "$report" || \
  fail "the report should record the exact spatial AQ options"
grep -q 'Options: -multipass fullres -rc-lookahead 32 -temporal_aq 1' "$report" || \
  fail "the report should record the exact temporal AQ options"
grep -q '95.123' "$report" || fail "the report should include the measured VMAF score"
if grep -q 'private-family-name' "$report"; then
  fail "the report must not expose source filenames"
fi
[[ ! -d "$successful_root/work" ]] || fail "temporary encoded files should be removed by default"
[[ $(grep -c 'Status: completed and decoded successfully' "$report") -eq 30 ]] || fail "three samples should run five variants for both supported encoders"

failed_vmaf_root="$temp_root/failed-vmaf-run"
mkdir -p "$failed_vmaf_root/input"
printf x > "$failed_vmaf_root/input/calm.mkv"
printf x > "$failed_vmaf_root/input/motion.mp4"
printf x > "$failed_vmaf_root/input/dark.mov"
FAKE_VMAF_FAIL=1 \
OPTIMISARR_NVENC_BENCHMARK_ROOT="$failed_vmaf_root" \
OPTIMISARR_FFMPEG="$fake_bin/ffmpeg" \
OPTIMISARR_FFPROBE="$fake_bin/ffprobe" \
OPTIMISARR_FFMPEG_VMAF="$fake_bin/ffmpeg-vmaf" \
OPTIMISARR_NVIDIA_SMI="$fake_bin/nvidia-smi" \
bash "$harness" >/dev/null
failed_vmaf_report="$(find "$failed_vmaf_root/results" -maxdepth 1 -type f -name 'optimisarr-nvenc-results-*.txt')"
grep -q 'Status: FAILED — VMAF measurement could not be completed' "$failed_vmaf_report" || \
  fail "a missing quality measurement should be reported as a failed comparison"
grep -q '30 comparison(s) failed' "$failed_vmaf_report" || \
  fail "the summary should count every missing VMAF measurement"

grep -qF 'COPY --chmod=0755 scripts/nvenc_benchmark.sh /app/scripts/nvenc-benchmark' \
  "$repo_root/Dockerfile" || fail "the final image should include the benchmark harness"
grep -qF 'bash scripts/test_nvenc_benchmark.sh' "$repo_root/.github/workflows/ci.yml" || \
  fail "CI should run the benchmark harness tests"

printf 'NVENC benchmark harness tests passed.\n'
