# VMAF model study

Optimisarr's quality gates (harmonic mean, fifth percentile, catastrophic floor) and every
calibration were tuned on Netflix's `vmaf_v0.6.1` model (`vmaf_4k_v0.6.1` for UHD). A different
model scores the same pictures on its own scale. VMAF v1 (libvmaf 3.2.0 and later), for example,
scores a lightly noisy clip about four points lower than v0.6.1. Switching models without restating
the gates would silently make every library stricter or more lenient.

The study harness measures the same encodes under both models so the gates can be restated from
evidence rather than guessed.

## What it runs

For each source, the harness:

1. probes it and plans the three 40-second adaptive sample windows, as the server does;
2. encodes each window at every quality on the ladder with the server's own sample encode command;
3. scores every clip twice with the server's own measurement service, including its per-window
   alignment: once with the model the server uses today and once with the candidate model.

The encoded bytes and alignment are identical. The baseline retains its established preparation; v1 measures SDR at 10-bit precision and passes the actual encoded width, height and bit depth to CAMBI. Both use the reference picture dimensions. See [Netflix’s v1 model guidance](https://github.com/Netflix/vmaf/blob/master/resource/doc/models_v1.md). These results must not be mixed with earlier 8-bit v1 studies.

It writes:

- `scores.csv`, with one row per clip and model: harmonic mean, fifth percentile, lowest frame, mean
  and frame count;
- `report.md`, with a fitted line between the models for each metric, each current gate restated
  on the candidate model (through the line, and as the score that keeps the same share of windows
  passing), how many windows keep the same verdict, the shift by source, and a per-source table.

Read the shift by source first. In the first pilot, VMAF v1 scored live action about 2 to 4 points
lower than v0.6.1 and animation about 2 points higher, so the pooled line looked like no change
while about one window in six would still have changed verdict at a gate of 90.

HDR, unknown bit depth/frame rate, and sources at 45 fps or above are outside this study. They make the run incomplete with a nonzero exit status; HFR needs a separately validated model policy. V1 currently uses CPU scoring, including when acceleration is requested: the accelerated graphs use 8-bit surfaces and have not established 10-bit v1 parity.

## Running it

Use an FFmpeg that can load the selected models. The encoding and measurement binaries may differ: on Linux the Intel encoding build may have an older libvmaf than the container's `/usr/local/lib/optimisarr/ffmpeg-vmaf`. The harness preflights both models before encoding each source and records executable hashes and versions in `run.json`.

Then point the harness at some sources:

```bash
dotnet run --project tools/Optimisarr.VmafStudy -c Release -- \
  --ffmpeg /path/to/encoding/ffmpeg \
  --measurement-ffmpeg /path/to/ffmpeg-vmaf \
  --ffprobe /path/to/ffprobe \
  --out /tmp/vmaf-study/run-1 \
  /media/film.mkv /media/episode.mkv
```

Options: `--qualities 18,22,26,30,34` (the CRF ladder), `--encoder libx265` (or `libx264`,
`libsvtav1`, `hevc_videotoolbox`…), `--preset medium`, and `--model-hd` / `--model-uhd` to compare
a different candidate model. Encoded clips are kept and reused only when their content hash matches and the source content, encoding binary and exact encoding arguments match the cache key. Model changes reuse the same encoded bytes. Each clip has a metadata record with its command, source identity, encode hash and probed format. Interrupted encodes never become valid cache entries.

`scores.csv`, `report.md` and `run.json` are checkpointed after each measurement. Exit code 0 and `run.json.completed: true` require every requested pair, finite scores, matching frame counts and matching clip identities, with no failures. Unavailable tools/models, invalid sources and incomplete pairs fail the run. Ctrl-C terminates child processes and retains partial evidence. Use one output directory per run; concurrent writers are refused.

`--out DIR --report-from DIR/scores.csv` regenerates a report without measuring. It checks pair integrity but cannot establish the original requested coverage; retain the original `run.json` as the completion record. Do not combine studies with different preparation policies or duplicate pairs.

The report also fits conservative gates with each source held out: the source being evaluated never contributes to its own threshold. Every training fold needs both passing and failing examples. A candidate fails this regression check if it accepts any legacy failure or rejects more than 20% of legacy passes. This is an explicit engineering criterion, not a subjective quality claim or automatic permission to change production gates. Multiple episodes of one title still need a separate review of title independence.

## What makes a study good enough to switch models

### Verify the source timeline first

The September 2026 fleet investigation found VC-1 sources with decode timestamps but no
presentation timestamps (#294). Encoding already uses `+genpts`; source packet verification must
use it too. Otherwise one FFmpeg build can report no picture endpoint, while another reports an
incomplete endpoint and falsely fails duration (#289). A retained 32,275-packet source/candidate
pair differed by only 0.002 seconds after matching the input demuxing, despite its earlier 1.06%
duration failure. Candidate timestamps are still measured without reconstruction.

Resolve these measurement discrepancies before attributing a changed verdict to the VMAF model.
The current harness binds cached clips to source and encoding provenance. Old basename-only cache directories are not imported automatically.

### Coverage and acceptance

- **Cover the quality range.** The ladder must include encodes that clearly pass and clearly fail
  today's gates, or the fitted line only describes the middle.
- **Cover the library.** Animation, grain, dark scenes, SD, 720p, 1080p and UHD behave differently.
  A handful of titles per kind is the minimum.
- **Cover the encoders people use.** The model judges the picture, but each encoder's artefacts
  differ: software x265, NVENC, QSV and VideoToolbox should all appear.
- **Look at the disagreements.** The "same verdict" column counts windows whose pass/fail would
  change. Those are the cases to watch before deciding whether the new model is right about them.

The gates a switch would adopt, and the evidence for them, belong in the pull request that
changes the model.
