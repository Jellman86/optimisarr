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

Only the model differs between the two measurements.

It writes:

- `scores.csv`, with one row per clip and model: harmonic mean, fifth percentile, lowest frame, mean
  and frame count;
- `report.md`, with a fitted line between the models for each metric, each current gate restated
  on the candidate model (through the line, and as the score that keeps the same share of windows
  passing), how many windows keep the same verdict, and a per-source table.

HDR sources are skipped: the v1.0.16 models are SDR models.

## Running it

It needs an FFmpeg built against libvmaf 3.2 or later, which compiles the v1.0.16 models in. On a
Mac, the sidecar's build script produces one without touching the sidecar's bundled binary if it is
run from a copy of the script outside the repository:

```bash
mkdir -p /tmp/vmaf-study/scripts
cp sidecars/macos/scripts/build-ffmpeg.sh /tmp/vmaf-study/scripts/
(cd /tmp/vmaf-study && VMAF_TAG=v3.2.1 bash scripts/build-ffmpeg.sh)
```

The copy stops at its final bundle check, which looks for a repository script; the binaries in
`/tmp/vmaf-study/vendor/` are already complete by then.

Then point the harness at some sources:

```bash
dotnet run --project tools/Optimisarr.VmafStudy -c Release -- \
  --ffmpeg /tmp/vmaf-study/vendor/ffmpeg \
  --ffprobe /tmp/vmaf-study/.build-ffmpeg/ffmpeg/ffprobe \
  --out /tmp/vmaf-study/run-1 \
  /media/film.mkv /media/episode.mkv
```

Options: `--qualities 18,22,26,30,34` (the CRF ladder), `--encoder libx265` (or `libx264`,
`libsvtav1`, `hevc_videotoolbox`…), `--preset medium`, and `--model-hd` / `--model-uhd` to compare
a different candidate model. Encoded clips are kept and reused, so a second run with another
model only re-measures.

## What makes a study good enough to switch models

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
