# Run the NVIDIA quality comparison

This temporary comparison helps the project choose a safe NVIDIA archival-quality profile. It does
not change Optimisarr's normal encoding settings and never modifies the supplied clips.

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
sampled for GPU use. Spatial and temporal AQ are tested separately.

Temporary encoded files are removed after measurement. To retain them for private inspection, add
`--keep-outputs` to the command.

When testing finishes, the terminal prints the path to a report like:

```text
/data/Optimisarr-NVENC-Test/results/optimisarr-nvenc-results-20260725T120000Z.txt
```

Upload that text file to [GitHub issue #37](https://github.com/Jellman86/optimisarr/issues/37).
The report contains no source filenames or paths. The original clips remain unchanged.
