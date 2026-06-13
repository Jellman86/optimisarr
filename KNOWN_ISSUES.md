# Known Issues

Tracked problems that are understood but not yet fully resolved. Each entry notes the impact,
the current safety/mitigation status, and the intended fix. Fixed items are moved to the
CHANGELOG, not kept here.

> Safety note: none of the open issues below can destroy an original file. Originals are only ever
> replaced via the verified replace flow, which quarantines the original first (recoverable by
> rollback), and a failed/again-eligible job never touches the source.

## Open

### 1. Animated images — partially addressed

- **Status:** Animated GIF/WebP files are now **skipped as candidates** (detected via the probed
  frame count), so they are no longer flattened into a broken single-frame output. Previously such
  a job produced a malformed, larger output that verification correctly failed (original
  untouched).
- **Remaining:** animated images are simply left alone. Proper animated-to-modern-format
  conversion (e.g. animated WebP/AV1) is future work; for now they are out of scope for the still
  pipeline.

## Resolved recently

These were found during live testing and fixed (see CHANGELOG for details):

- Output filename collision when two sources shared a stem (e.g. `photo.bmp` and `photo.tif`, both
  → `photo.webp`). The work output is now namespaced per media file (`/work/<id>/…`) so jobs never
  clobber each other's output, and a replacement whose destination is already occupied by a
  different file now fails safely with a clear reason, leaving the original untouched.
- The image optimisation marker did not round-trip (ffmpeg's still encoders drop `-metadata`). It is
  now written and read with exiftool in the EXIF/XMP `Software` field, so the marker is portable for
  JPEG/WebP/AVIF — surviving a database wipe or a move to another machine.
- Non-video libraries discovered no files (scanner was video-only).
- Discovered files were never probed automatically, so a scanned library produced no candidates
  and could not be enqueued.
- The video rule profile (e.g. `ConservativeHevc`) was shown for Music/Photo libraries on the
  Libraries page and as the "Profile" column for audio/image rows on the Candidates page, where it
  is meaningless.
