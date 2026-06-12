# Known Issues

Tracked problems that are understood but not yet fully resolved. Each entry notes the impact,
the current safety/mitigation status, and the intended fix. Fixed items are moved to the
CHANGELOG, not kept here.

> Safety note: none of the open issues below can destroy an original file. Originals are only ever
> replaced via the verified replace flow, which quarantines the original first (recoverable by
> rollback), and a failed/again-eligible job never touches the source.

## Open

### 1. WebP optimisation marker does not round-trip (images)

- **Impact:** Optimisarr stamps every output with an `optimisarr` container tag so a re-optimised
  file is recognised even without the database. ffmpeg's `libwebp` encoder **silently drops
  `-metadata`**, so a WebP output carries no marker (verified in-container: empty `format.tags`
  and `stream.tags`). Audio (m4a) and video markers are unaffected.
- **Mitigation (in place):** Re-optimisation is still prevented by (a) the database optimisation
  history, which holds back a file already optimised for its current version, and (b) the
  "already in the target format" candidate check (a WebP targeting WebP is skipped). The only lost
  guarantee is *portability* of the marker for images — surviving a database wipe or moving the
  file to another machine.
- **Intended fix:** write the marker into an EXIF/XMP field with a tool ffmpeg lacks (e.g.
  `exiftool`) as a post-encode step, and have the probe read it back from there. This adds a
  dependency to the image, so it is a deliberate decision rather than a silent change.

### 2. Output filename collision when two sources share a stem (move/replace)

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

### 3. Animated images — partially addressed

- **Status:** Animated GIF/WebP files are now **skipped as candidates** (detected via the probed
  frame count), so they are no longer flattened into a broken single-frame output. Previously such
  a job produced a malformed, larger output that verification correctly failed (original
  untouched).
- **Remaining:** animated images are simply left alone. Proper animated-to-modern-format
  conversion (e.g. animated WebP/AV1) is future work; for now they are out of scope for the still
  pipeline.

## Resolved recently

These were found during live testing and fixed (see CHANGELOG for details):

- Non-video libraries discovered no files (scanner was video-only).
- Discovered files were never probed automatically, so a scanned library produced no candidates
  and could not be enqueued.
- The video rule profile (e.g. `ConservativeHevc`) was shown for Music/Photo libraries on the
  Libraries page and as the "Profile" column for audio/image rows on the Candidates page, where it
  is meaningless.
