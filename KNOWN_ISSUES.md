# Known Issues

Tracked problems that are understood but not yet fully resolved. Each entry notes the impact,
the current safety/mitigation status, and the intended fix. Fixed items are moved to the
CHANGELOG, not kept here.

> Safety note: none of the open issues below can destroy an original file. Originals are only ever
> replaced via the verified replace flow, which quarantines the original first (recoverable by
> rollback), and a failed/again-eligible job never touches the source.

## Open

### 1. Output filename collision when two sources share a stem (move/replace)

- **Impact:** The output name is `{stem}.{targetExtension}`, so two source files that differ only
  by extension (e.g. `photo.bmp` and `photo.tif`, both → `photo.webp`) resolve to the **same
  output path**. In move-on-complete mode the second output **overwrites the first**
  (`File.Move(..., overwrite: true)`); in replace mode two files genuinely cannot both occupy
  `photo.webp` in one directory.
- **Mitigation (in place):** Originals are never lost — in replace mode they are quarantined first
  and are recoverable. Only an *optimised output* can be overwritten (wasted work, not source
  loss). Most relevant to libraries that convert many files to one target format (images).
- **Intended fix:** make the work/output path unique per source file, and fail a job safely (with
  a clear "would collide with an existing optimised file" reason) rather than overwrite a
  different file at the final destination.

### 2. Animated images — partially addressed

- **Status:** Animated GIF/WebP files are now **skipped as candidates** (detected via the probed
  frame count), so they are no longer flattened into a broken single-frame output. Previously such
  a job produced a malformed, larger output that verification correctly failed (original
  untouched).
- **Remaining:** animated images are simply left alone. Proper animated-to-modern-format
  conversion (e.g. animated WebP/AV1) is future work; for now they are out of scope for the still
  pipeline.

## Resolved recently

These were found during live testing and fixed (see CHANGELOG for details):

- The image optimisation marker did not round-trip (ffmpeg's still encoders drop `-metadata`). It is
  now written and read with exiftool in the EXIF/XMP `Software` field, so the marker is portable for
  JPEG/WebP/AVIF — surviving a database wipe or a move to another machine.
- Non-video libraries discovered no files (scanner was video-only).
- Discovered files were never probed automatically, so a scanned library produced no candidates
  and could not be enqueued.
- The video rule profile (e.g. `ConservativeHevc`) was shown for Music/Photo libraries on the
  Libraries page and as the "Profile" column for audio/image rows on the Candidates page, where it
  is meaningless.
