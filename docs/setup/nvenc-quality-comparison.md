# Run the NVIDIA quality comparison

This diagnostic comparison helps the project evaluate possible NVIDIA archival-quality profiles
against real hardware. It does not change Optimisarr's normal encoding settings and never modifies
the supplied clips.

## Before starting

- Update the Optimisarr container to the latest `dev` image.
- Confirm **Settings → Tools** shows NVIDIA H.264 and HEVC encoding as available.
- Have enough free space for temporary versions of three short clips.

Use three ordinary **8-bit SDR** clips between roughly 30 seconds and 2 minutes:

1. a calm or slow-moving scene;
2. a fast-moving scene;
3. a dark, grainy, or noisy scene.

Use clips you are comfortable testing locally. The generated report deliberately omits their names
and paths.

## Run the comparison

Open the Unraid terminal, or a terminal on the Docker host, and run:

```bash
docker exec -it Optimisarr /app/scripts/nvenc-benchmark
```

The first run creates this folder inside the container's mapped storage root:

```text
/data/Optimisarr-NVENC-Test/input
```

With the standard Unraid template, the corresponding host folder is:

```text
/mnt/user/media/Optimisarr-NVENC-Test/input
```

If the **Storage root** mapping was changed, use the matching host folder instead. Add exactly three
clips directly to `input`, then run the same command again:

```bash
docker exec -it Optimisarr /app/scripts/nvenc-benchmark
```

If the container has a different name, replace `Optimisarr` in the command with that name.

## What happens

The harness uses only the first 30 seconds of each clip. It compares the current Efficient baseline
with full-resolution multipass, lookahead, spatial AQ, and temporal AQ for both supported H.264 and
HEVC NVENC encoders. Each output is decoded, scored with VMAF, measured for size and speed, and
sampled for NVENC-engine and overall GPU use. Spatial and temporal AQ are tested separately.

The report uses millisecond timing so short hardware encodes remain comparable. If an optional
combination is unavailable on the selected GPU or driver, it is recorded as a capability skip with a
privacy-safe diagnostic rather than presented as a broken benchmark. A baseline failure still fails
the run honestly.

Temporary encoded files are removed after measurement. To retain them for private inspection, add
`--keep-outputs` to the command.

When testing finishes, the terminal prints the path to a report like:

```text
/data/Optimisarr-NVENC-Test/results/optimisarr-nvenc-results-20260725T120000Z.txt
```

The report contains no source filenames or paths. Keep it for comparison or attach it when an
Optimisarr maintainer requests hardware evidence. The original clips remain unchanged.

## Current evidence

The first contributed hardware report used a GeForce GTX 1080, driver 580.142, FFmpeg
7.1.4-Jellyfin, and three anonymous 20-second samples. No tested combination consistently improved
both VMAF and output size over the current Efficient baseline:

- H.264 full-resolution multipass reduced aggregate size by about 2.7% while reducing average mean
  VMAF by about 0.065.
- H.264 spatial AQ improved one difficult sample by about 0.51 mean VMAF, but increased that sample's
  size by about 5% and did not improve every sample.
- HEVC multipass and lookahead were effectively neutral.
- HEVC temporal AQ was unavailable on that GPU/driver combination.

These results do not justify adding independent raw NVENC controls or changing the production
profile. The harness remains available so materially different NVIDIA generations can be evaluated
without weakening Optimisarr's portable settings or safety model.
