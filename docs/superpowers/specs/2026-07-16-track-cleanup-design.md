# Track Cleanup profile, subtitle language rule, and queue reasons

**Date:** 2026-07-16
**Status:** Approved

## Problem

Optimisarr can already remove audio tracks that are not in a library's kept
languages, but only as a side effect of an optimise or remux job. An operator
who wants *only* that — strip foreign audio from their movies without
re-encoding video or changing the container type — has no profile that does it.
The RemuxCleanup profile comes closest but targets MKV, so a non-MKV file gets
its container changed.

Three requests, designed together:

1. A processing mode that only removes unwanted tracks — no transcode, no
   container change.
2. Subtitle tracks removable by language, alongside audio.
3. Queue rows that show *why* a file is queued.

A note on mechanics, reflected honestly in the UI: removing a track always
rewrites the file (FFmpeg stream-copies every kept stream into a new file,
which then goes through the normal verify-and-replace flow). The video and
audio bits and the container *type* are untouched — an `.mkv` stays `.mkv`, an
`.mp4` stays `.mp4` — but in-place track deletion is not possible.

## 1. New rule profile: `TrackCleanup`

A new `RuleProfile.TrackCleanup` ("Track cleanup — remove unwanted
languages").

- `TargetVideoCodec = null` — never re-encodes, exactly like RemuxCleanup.
- `TargetContainer = null` — never changes the container. `RuleSettings.TargetContainer`
  becomes `string?`, where `null` means "keep the source container".
  `TranscodeSpecResolver` derives the output extension from the input path.
  The MP4 image-subtitle / incompatible-audio MKV fallback does not apply:
  every kept stream already lives in that container.
- `MinFileSizeBytes = 0`, `Hdr = Preserve` — stream copy is lossless, so no
  size floor or HDR exclusion is needed.

**Eligibility** (`CandidateEvaluator`): a file is eligible only when it has at
least one removable audio or subtitle track under the library's kept-language
rules. Otherwise it is skipped with an explicit reason:

- No kept languages configured on the library:
  "No kept audio or subtitle languages configured — nothing to remove".
- Kept languages configured but nothing removable:
  "No removable tracks (all tracks match the kept languages or are unknown)".
- Track languages not yet captured (row predates capture): stays conservative,
  skipped until a re-probe captures them (same job-time re-probe pattern the
  audio work established).

**Library form:** when this profile is selected and both kept-language fields
are empty, show an inline hint that the profile will do nothing until at least
one field is set. No hard validation — save is allowed.

## 2. `KeepSubtitleLanguages` — a per-library rule for all profiles

A separate "Keep subtitle languages" field mirroring "Keep audio languages":
comma-separated ISO 639 codes, empty (default) keeps every subtitle track. It
applies to any video job on any profile, exactly as the audio rule does.

**Selection logic:** a new pure `SubtitleTrackSelection` in
`Optimisarr.Core.Queue`, sharing the audio rule's canonicalisation
(ISO 639-1/-2 B/T spellings match each other) — extract the shared language
table/matching out of `AudioTrackSelection` rather than duplicating it.
Safety semantics differ deliberately:

- A subtitle track whose language is unknown (`und`/`zxx`/`mul`/untagged/
  private-use) is **never** removed — same as audio.
- **No** keep-at-least-one-match guard: subtitles are optional streams, so if a
  file's only subtitles are all in non-kept languages they are all removed and
  the file ends with zero subtitle tracks. (Audio keeps its guard: a file
  never loses all audio.)

**Probe and inventory** (mirrors the audio-language pattern exactly):

- `MediaProbeService` captures each subtitle track's language positionally.
- `MediaFile` gains a `SubtitleLanguages` ordered-summary column
  (+ EF migration `AddKeepSubtitleLanguages`, which also adds the library's
  `KeepSubtitleLanguages` column). Scans clear it when a file changes.
- A job whose row predates subtitle-language capture re-probes the source once
  at job time.

**Command building:** `FfmpegCommandBuilder` adds explicit `-map -0:s:N`
exclusions alongside the existing `-map -0:a:N` audio exclusions.
`TranscodeSpec` gains `RemoveSubtitleStreamIndexes`.

**RemuxCleanup consistency:** a container-clean file with removable subtitle
tracks becomes eligible for a stream-copy cleanup, exactly as it already does
for removable audio tracks.

## 3. Verification — tightened, not relaxed

- The subtitle-retention gate becomes removal-aware like the audio gate:
  the output must have **exactly** original-minus-planned subtitle tracks, so
  an encode that accidentally drops an extra stream still fails.
- For `TrackCleanup` jobs, verification additionally confirms the output
  container matches the source container and the video/audio codecs are
  unchanged (stream copy produced what it promised).
- The size-saving gate is unchanged: removing a track always shrinks the file,
  so `output < original` holds.

## 4. Queue shows why a file is queued

Each queued job records the eligibility reason already computed at enqueue
time (the `CandidateDecision` reason string) and the queue UI displays it per
row. Reason strings are extended to name the languages being removed, not just
counts, e.g.:

- "Remove 2 audio tracks (fra, deu) + 1 subtitle track (spa)"
- "Remux to mkv (avi → mkv)"
- "h264 → hevc"

If the `Job` row does not already carry the reason, it gains a column
(+ migration) populated at enqueue time; the queue endpoint and Svelte queue
page surface it.

## 5. Plumbing

- Library form field + profile entry in all nine locales.
- Request parser validates/normalises the subtitle language list with the same
  `TryNormaliseLanguageList` rules as audio.
- Config backup/restore carries the new library column.
- `CHANGELOG.md` (Unreleased) records the change.

## Testing

TDD throughout; all new logic is pure and unit-tested without FFmpeg, a
database, or the filesystem:

- `SubtitleTrackSelection`: unknown never removed; all-foreign sets fully
  removed (no guard); canonical spellings match; empty list keeps all.
- `TranscodeSpecResolver`: `TargetContainer = null` keeps the source
  extension; subtitle removals resolved positionally.
- `FfmpegCommandBuilder`: `-map -0:s:N` arguments emitted; combined
  audio + subtitle removals.
- `CandidateEvaluator`: TrackCleanup eligibility/skip reasons, including the
  empty-rule and unknown-language cases; RemuxCleanup subtitle eligibility.
- `VerificationEvaluator`: exact subtitle retention after planned removal;
  container/codec-unchanged checks for TrackCleanup.
- Migration idempotency tests follow the existing `MigrationTests` pattern.

## Out of scope

- Removing tracks by anything other than language (codec, title, "commentary"
  detection).
- Forced-subtitle special-casing (a forced track follows the same language
  rules).
- Attachment/chapter stripping.
