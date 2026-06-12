# Optimisarr Roadmap

This roadmap is intentionally implementation-focused. The goal is to build a
small, reliable core first, then widen codec, GPU, and automation support once
the replacement workflow is trustworthy.

## Current status (2026-06-12)

- **Phase 0 (Foundation): done.** Repo, three .NET projects + Svelte UI, Docker
  image building and publishing to GHCR via CI, health endpoint, SQLite under
  `/config` via EF Core migrations.
- **Phase 1 (Discovery & Inventory): done, extended.** Recursive settling-aware
  scanning, ffprobe inspection, inventory UI. Extended beyond the original plan
  to support **multiple libraries**, each with its own media type and rule
  profile, plus a folder-picker for paths.
- **Phase 2 (Candidate Rules): largely done.** A pure, unit-tested
  `CandidateEvaluator` turns per-library rule profiles into eligibility
  decisions with a human-readable reason for every file (eligible or skipped):
  min size, resolution limit, HDR/Dolby Vision exclusion, path exclusions,
  codec/container matching, and already-processed detection. Surfaced via
  `GET /api/candidates` and a Candidates page. Per-library overrides (target
  codec/container, HDR handling, size/resolution limits, path exclusions,
  priority) are editable from expandable cards on the Libraries page and resolved
  by a pure `RuleResolver`. **Already-processed detection** now also covers prior
  jobs: a file optimised (or failed) for its current version is held back by a pure
  `OptimisationHistoryEvaluator` so the queue never loops on it, and a genuinely
  changed file becomes eligible again. Still to come: a measured minimum-saving
  estimate (today's proxy is "already in the target codec").
- **Phase 3 (Queue and Worker): done.** A `Job` state machine, a pure
  `JobScheduler` (priority-then-FIFO, global `maxConcurrentJobs`), a pure
  `FfmpegCommandBuilder`/`TranscodeSpecResolver`, and a single-writer
  `QueueDispatcher` background worker that runs ffmpeg out-of-process with
  cancellation and crash recovery. Live progress is pushed to the SignalR client
  in the Queue UI: a determinate bar with encode speed and ETA while
  transcoding (parsed by a pure, unit-tested `FfmpegProgressParser`) and an
  indeterminate sweep for the probing/verifying phases. Enqueue from a library's
  eligible candidates; manage from the Queue page. Outputs land in `/work` as
  `ReadyToReplace` — originals are never touched.
- **Phase 4 (Verification): done.** A clean ffmpeg exit no longer trusts the
  output. The worker runs a real `Verifying` step — a full-decode health check
  (`DecodeHealthCheck`), an output ffprobe, and a comparison against the original —
  and feeds the evidence to a pure, unit-tested `VerificationEvaluator`. The
  `VerificationReport` (decode health, output readable, video stream present,
  duration tolerance, audio/subtitle retention, size saving) is persisted on the
  job and surfaced on the Queue page. Only a passing report advances toward
  replacement; a failure marks the job `Failed` with the output retained for
  inspection and the original untouched. Thresholds are fixed conservative
  defaults for now (`VerificationPolicy.Default`).
- **Phase 5 (Safe Replacement and Rollback): done.** A verified `ReadyToReplace`
  job can replace its original — the original is quarantined under `/trash` first,
  then the verified output is moved into place, with a recorded `Replacement` as
  the rollback path. Moves are atomic on one filesystem and fall back to a verified
  copy-plus-delete across mounts (reported in the UI). A final-path integrity check
  and re-probe follow; the job moves to `Completed`. Rollback restores the original
  and removes the replacement. Pure `ReplacementPlanner` and the replace/rollback
  service are unit tested; originals are retained in quarantine until the
  configurable retention window (Phase 6) expires.
  Surfaced via the Queue **Replace** action and the new **Quarantine** page.
- **Phase 6 (Scheduling and Resource Controls): in progress.** Processing
  windows, global max concurrent jobs, CPU thread limits, and disk-space safety
  pause are wired into queue dispatch/FFmpeg arguments and surfaced in
  Settings/Queue. Running jobs are never interrupted; the gates only decide
  whether new jobs may start. Hardware capability detection for FFmpeg
  accelerators and encoders is surfaced on Tools, and global encoder mode
  selection is wired into generated FFmpeg arguments. Verification policy is
  configurable from Settings. Replacement/quarantine policy is now configurable —
  cross-filesystem fallback is opt-in and a background worker enforces the
  quarantine retention window, purging originals (and dropping their rollback path)
  once it expires. Optional **service-activity pauses** are done too: configurable
  Plex/Jellyfin/Emby watchers hold new jobs while a server is streaming, decided by
  a pure, unit-tested evaluator that ignores unreachable servers so one offline
  server never wedges the queue. Phase 6 is now feature-complete. **Per-library
  automatic optimisation** extends this: a library can scan-and-enqueue itself once
  per day within its own time window (pure `AutoEnqueueScheduleEvaluator` +
  `AutoEnqueueWorker`), while execution still obeys the global processing window and
  concurrency limit — the worker only fills the queue, never starts jobs.
- **Phase 7 (GPU Support): largely done.** Encoder/hwaccel capability detection on Tools,
  global encoder-mode selection wired into FFmpeg args, and jobs that fail fast with a clear
  reason when a selected encoder is unavailable. See the Phase 7 section for the few remaining
  items (hardware-specific preset notes, a tested GPU compose example).
- **Phase 8 (Library Integration): feature-complete.** Authenticated Plex (OAuth/PIN),
  Jellyfin (Quick Connect/API key), and Emby (API key) connections; targeted re-scan after a
  replacement/rollback; Sonarr/Radarr import-aware exclusions; notifications (webhook/ntfy/
  Apprise); secret-free config import/export.
- **Phase 9 (Gold-Standard Verification): feature-complete.** Opt-in VMAF gate (plus PSNR/SSIM
  signals), always-on HDR/colour/A-V-sync/timestamp/tail integrity gates, audio channel/
  sample-rate retention, opt-in EBU R128 loudness + true-peak clipping gates, per-library VMAF
  overrides. All gate logic pure and unit tested.
- **Phase 10 (Multi-Media Optimisation): in progress.** Done so far: media-kind detection;
  lossless-audio optimisation with per-library audio codec/bitrate; **audio-codec selection for
  video transcodes** (AAC default); **stereo downmix** across both pipelines; and **media-type-
  scoped library Advanced options**. Next up (see the Phase 10 section): any-source audio
  transcode, sane default per-container profiles, and image optimisation (WebP/AVIF/JXL).

## Guiding principles

- Safety beats savings.
- No original file is deleted until verification has passed.
- Every destructive action must have a rollback path.
- Defaults should be conservative and understandable.
- The app should feel familiar to Docker media-stack users.
- One container should be enough for normal use.

## Phase 0: Project Foundation

Goal: create a working repo skeleton with repeatable local and Docker builds.

Deliverables:

- `src/Optimisarr.Api` ASP.NET Core backend.
- `src/Optimisarr.Core` domain logic.
- `src/Optimisarr.Data` EF Core SQLite persistence.
- `web` Svelte 5 + TypeScript frontend.
- Multi-stage Dockerfile.
- `compose.example.yml`.
- `.env.example`.
- CI for backend tests, frontend checks, and Docker build.
- Basic app health endpoint.
- Static Svelte assets served by ASP.NET Core in production.

Exit criteria:

- `docker compose up` starts the app.
- Web UI loads.
- `/api/health` returns healthy.
- SQLite database is created under `/config`.

## Phase 1: Media Discovery and Inventory

Goal: scan bind-mounted libraries and understand files before making any
changes.

Deliverables:

- Library root configuration.
- Recursive scanner with include/exclude rules.
- File settling checks based on modified time and size stability.
- ffprobe integration using JSON output.
- Media file table:
  - path
  - size
  - modified time
  - probe hash/fingerprint
  - container
  - video/audio/subtitle stream summaries
  - current optimisation status
- UI inventory page.
- Manual scan button.

Exit criteria:

- A user can mount `/data`, scan it, and see discovered media.
- Probe failures are visible and do not crash scanning.

## Phase 2: Candidate Rules

Goal: decide what should and should not be optimised before queueing work.

Deliverables:

- Rule profiles:
  - Conservative HEVC
  - Compatibility H.264
  - Experimental AV1
  - Remux/cleanup only
- Eligibility checks:
  - minimum file size
  - minimum expected saving
  - codec/container matching
  - resolution limits
  - HDR/Dolby Vision exclusion
  - path exclusions
  - already-processed detection
- Candidate preview UI.
- Per-file "why eligible" and "why skipped" explanation.

Exit criteria:

- The app can show a safe optimisation candidate list without running FFmpeg.
- Every skipped file has a human-readable reason.

## Phase 3: Queue and Worker

Goal: transcode one file at a time reliably, with visible progress.

Deliverables:

- Job table and state machine:
  - queued
  - probing
  - transcoding
  - verifying
  - ready_to_replace
  - completed
  - failed
  - cancelled
- Background worker based on ASP.NET Core `BackgroundService`.
- FFmpeg command builder.
- Process cancellation.
- Progress parsing from FFmpeg.
- SignalR hub for job progress and queue updates.
- Queue UI with pause/resume/cancel.
- Job detail page with logs and generated command.

Exit criteria:

- A user can enqueue a file and produce an output in `/work`.
- Progress updates stream live to the UI.
- Cancelling a job stops FFmpeg and marks the job cancelled.

## Phase 4: Verification

Goal: prove converted media is healthy before replacement is even possible.

Deliverables:

- Output ffprobe.
- Full decode health check with FFmpeg.
- Duration tolerance check.
- Stream policy comparison:
  - required video stream exists
  - required audio streams retained or intentionally converted
  - required subtitle streams retained or intentionally converted
  - chapters and metadata policy recorded
- Size saving check.
- Verification report model.
- UI verification report.

Exit criteria:

- Jobs cannot enter replacement flow unless verification passes.
- Failed verification leaves original untouched and output retained for review
  or deleted according to settings.

## Phase 5: Safe Replacement and Rollback

Goal: replace originals safely and reversibly.

Deliverables:

- Quarantine layout under `/trash`.
- Replacement transaction record.
- Atomic move path where source/output/trash are on one filesystem.
- Copy-plus-verify fallback when mounts prevent atomic moves.
- Final-path probe after replacement.
- Rollback action.
- Retention policy for quarantined originals.
- UI for quarantine and rollback.

Exit criteria:

- A verified output can replace an original.
- The original can be restored from quarantine.
- Cross-filesystem replacement is detected and reported clearly.

## Phase 6: Scheduling and Resource Controls

Goal: make the app safe to run continuously on a home server.

Deliverables:

- Processing windows. **Done.**
- Max concurrent jobs, initially defaulting to 1. **Done.**
- CPU thread limits. **Done.**
- GPU/CPU worker mode selection. **Done.**
- Pause while disk free space is below threshold. **Done.**
- Pause while configured services are active (Plex/Jellyfin/Emby). **Done.**
- Per-library priority. **Done.**
- Configurable verification policy. **Done.**

Exit criteria:

- A user can restrict processing to overnight windows.
- The app stops queueing work when disk space is unsafe.

## Phase 7: GPU Support

Goal: make hardware encoding discoverable, explicit, and testable.

Deliverables:

- Encoder capability detection: **Done** (FFmpeg hwaccels, known encoder
  availability, NVIDIA runtime, `/dev/dri` mapping surfaced on Tools).
  - CPU x264/x265
  - NVIDIA NVENC
  - Intel QSV
  - VAAPI
  - AV1 where available
- UI hardware status page.
- Compose examples:
  - NVIDIA GPU reservation
  - `/dev/dri` Intel/AMD mapping
- Encoder presets with quality/speed notes. **Partly done** (CPU presets exist;
  hardware-specific notes still to come).
- Startup warnings when selected GPU mode is unavailable. **Done for jobs** (jobs
  fail before FFmpeg starts with a clear unavailable-encoder reason).

Exit criteria:

- The app can detect available encoders and prevent invalid preset selection.
- A GPU-enabled compose example is documented and tested.

## Phase 8: Library Integration

Goal: fit cleanly into existing media stacks without becoming a second media
manager.

Status: started. A verified replacement (and a rollback) now triggers a
best-effort re-scan on connected Plex/Jellyfin/Emby servers, reusing the Phase 6
activity-pause connections (per-watcher "refresh after replacements" toggle, pure
unit-tested `LibraryRefreshRequestBuilder`). Token acquisition is now interactive
too: **Plex OAuth/PIN** and **Jellyfin Quick Connect** sign-in flows fill in the
token instead of the user pasting a raw one (Emby keeps a manual API key).
**Notifications** are done: webhook/ntfy/Apprise targets fire best-effort on
replacement and failure, with pure unit-tested message/request builders.
**Import/export** is done too: a secret-free config snapshot (settings, libraries,
watchers, notification targets, Sonarr/Radarr connections) exports to JSON and
imports back as a validated, non-destructive merge (pure unit-tested
`ConfigSnapshotValidator`). **Sonarr/Radarr import-aware exclusions** are done as
well: connected managers are polled for in-progress imports and any file whose
folder an import is landing in is held back from queueing (pure unit-tested
`ArrQueueParser` and `ArrImportExclusionEvaluator`), so Optimisarr never fights an
import. Phase 8 is now feature-complete.

Deliverables:

- **Authenticated media-server connections** so Optimisarr can tell the server to
  re-scan a title after a verified replacement (a replaced file keeps its path but
  changes container/codec/size, so the server should re-read it). Each provider's
  login is different and must use the provider's own supported flow — we never ask
  the user to paste a raw password we store:
  - **Plex — OAuth/PIN flow.** Create a PIN (`POST https://plex.tv/api/v2/pins`
    with `X-Plex-Product` and a stable `X-Plex-Client-Identifier`), send the user
    to `https://app.plex.tv/auth#?clientID=…&code=…&forwardUrl=…`, then poll
    `GET https://plex.tv/api/v2/pins/{id}?code=…` until `authToken` is present.
    Store that token and call the server with `X-Plex-Token`. Refresh a section
    with `GET /library/sections/{id}/refresh` — and prefer a **targeted** refresh
    of just the changed file via `?path=<dir>` so a replacement doesn't trigger a
    full library scan.
  - **Jellyfin — Quick Connect (preferred) or an admin API key.** Quick Connect:
    initiate a code, the user approves it from a signed-in Jellyfin session, then
    poll until it yields an access token; alternatively accept an admin-issued API
    key. Authenticate with `Authorization: MediaBrowser Token=<token>`. Trigger a
    rescan via the "Scan Media Library" scheduled task
    (`POST /ScheduledTasks/Running/{taskId}`) or notify a specific path with
    `POST /Library/Media/Updated` (`Path` + `UpdateType`).
  - **Emby — admin API key (its Connect login is account-owned).** Same
    MediaBrowser lineage as Jellyfin: authenticate with `X-Emby-Token` / the
    `api_key` query / `Authorization: MediaBrowser Token=`, refresh with
    `POST /Library/Refresh`, or notify a path with `POST /Library/Media/Updated`.
  - Tokens/keys are stored as provider connections (encrypted at rest), validated
    on save, and **reusable by the Phase 6 activity-pause watchers** so a user
    configures each server once. A connection that fails auth is surfaced, not
    silently ignored.
- **Trigger a targeted refresh after each successful replacement** (and on
  rollback), best-effort and never blocking or undoing the replacement if the
  server is unreachable.
- Optional Sonarr/Radarr path-aware exclusions. **Done.**
- Optional notifications (Apprise, ntfy, webhook). **Done.**
- Import/export settings. **Done.**

Exit criteria:

- A user can connect Plex (via OAuth/PIN), Jellyfin (via Quick Connect or API
  key), and Emby (via API key), and Optimisarr validates each connection.
- A verified replacement triggers a targeted re-scan of just the changed title on
  every connected server, and a server being offline never affects the
  replacement's safety.
- Integrations remain optional and disabled by default.

## Phase 9: Gold-Standard Health Verification

Goal: raise verification from "the output decodes and roughly matches" to a
defensible, evidence-backed guarantee that the converted file is as good as it
needs to be — so replacing an original is a decision the user can fully trust.
This deepens Phase 4 rather than replacing it; every existing gate stays.

Status: feature-complete. The output is scored against the original with `libvmaf`
(opt-in, fail-closed VMAF gate with harmonic-mean and per-frame-minimum floors, plus
PSNR/SSIM as corroborating signals), and the image bundles a libvmaf-enabled ffmpeg
(jellyfin-ffmpeg) so the gate can run without disturbing the transcode path. Always-on
gates cover **HDR preservation** (an HDR original whose library preserves HDR must keep
its HDR10/HDR10+/HLG/Dolby Vision signal, while an intentional tone-map to SDR passes),
**colour primaries/transfer/matrix** preservation, **A/V sync**, **monotonic decode
timestamps**, **audio channel/sample-rate retention**, and full-file **decode-error
counting**; an opt-in **EBU R128 loudness** gate, an opt-in **true-peak clipping** gate
(sharing the loudness decode pass), and **per-library VMAF threshold overrides** round it
out. **Truncated/partial-last-GOP detection** (the output's latest presentation time
falling materially short of the source runtime, sharing the monotonicity probe) closes the
last container-integrity item. All gate logic is pure and unit tested.

Deliverables:

- **Perceptual/structural quality scoring** of the output against the original:
  VMAF (with model selection), plus SSIM and PSNR as corroborating signals,
  computed by FFmpeg's `libvmaf`/filters and parsed by a pure, unit-tested
  evaluator. A configurable minimum VMAF gate (conservative default) blocks
  replacement when quality drops too far. **VMAF gate done** (harmonic-mean +
  per-frame minimum floors); PSNR/SSIM corroboration and VMAF model selection still
  to come.
- **Per-frame decode integrity**, not just a single full-decode pass: count and
  surface decoder errors/corrupt frames, dropped/duplicated frames, and any
  packet-level read errors over the whole file.
- **Container and stream integrity**: A/V sync drift within tolerance, monotonic
  timestamps, no truncated/partial last GOP, correct frame count within
  tolerance, and color metadata (primaries/transfer/matrix, HDR10/HDR10+/Dolby
  Vision side data) preserved or intentionally transformed. **HDR signal
  preservation, colour primaries/transfer/matrix preservation, A/V-sync,
  monotonic decode-timestamp, and truncated/partial-last-GOP checks done.**
- **Audio fidelity checks** appropriate to the operation: channel layout and
  sample-rate retention, loudness (EBU R128) drift within tolerance, and no
  clipping introduced. **Channel/sample-rate retention, EBU R128 loudness drift, and
  true-peak clipping detection done** (pure unit-tested parsers/evaluators).
- **Per-rule-profile thresholds** so an "archive" profile can demand near-lossless
  scores while a "space-saver" profile accepts more, all surfaced in the
  `VerificationReport` with the measured numbers, not just pass/fail. **Done** via
  per-library VMAF threshold overrides resolved by a pure `VerificationPolicyResolver`.

Exit criteria:

- A passing report includes measured quality scores (e.g. VMAF) and integrity
  counts, and a configurable quality gate can fail a job that decodes cleanly but
  looks materially worse than the original.
- All scoring logic is pure and unit tested against captured FFmpeg output; no
  live FFmpeg in tests.

## Phase 10: Multi-Media Optimisation (Video, Audio, Images)

Goal: extend the same safe pipeline — candidate rules, transcode, gold-standard
verification, quarantine/rollback — from video to **audio-only files and
images**, so a library of music or photos benefits from the same guarantees.

Status: in progress. **Media-kind detection, audio optimisation, per-library audio
rules, audio-codec selection for video transcodes, stereo downmix, and media-type-scoped
Advanced options are done.** A pure, unit-tested `MediaKindClassifier` classifies every probed file as
video, audio, or image (cover-art-aware, so an album-art picture never makes an audio file
look like video), stored on `MediaFile.MediaKind` and surfaced as a Kind column in the
Inventory. Lossless audio is re-encoded through the full pipeline — candidate rules,
transcode (cover art + metadata + the same optimisation marker as video, so audio files are
never re-optimised either), kind-aware verification, and the usual reversible replacement.
Each library can override the audio target codec (Opus/AAC/MP3) and bitrate. Image
optimisation is next.

Deliverables:

- **Media-kind detection** in scanning/probe so each file is classified as video,
  audio, or image, with kind-specific inventory columns. **Done.**
- **Audio optimisation**: target codec/bitrate/sample-rate rules (e.g. lossless →
  efficient lossy or re-pack), with verification on loudness, channel layout,
  duration, and decode health. Tag/metadata and embedded-art preservation. **Done**
  (lossless → configurable codec/bitrate, default Opus 128 kbps; outputs carry the
  optimisation marker like video).
- **Transcode from any audio source, not just lossless.** Today only lossless sources are
  eligible (re-encoding already-lossy audio risks generational loss). Extend candidacy so a
  library can opt to re-encode *any* source whose codec/bitrate differs from the configured
  target when it would genuinely save space (e.g. a 320 kbps MP3 → Opus 128), while keeping
  the conservative lossless-only default. **Planned.**
- **Audio codec selection for video transcodes.** A video re-encode used to copy its audio
  tracks untouched (`-c:a copy`). A per-library option now also transcodes the audio of a
  video to a chosen codec/bitrate (AAC — the broadly compatible default — Opus, or MP3),
  reusing the audio-target encoder mapping so the audio rules are shared between audio-only
  jobs and video jobs. The default stays "copy" so nothing changes unless the operator opts
  in; the audio-fidelity gate understands the intentional re-encode and allows sample-rate
  normalisation while still catching a silent downmix or a dropped rate on a copied track.
  **Done.**
- **Stereo downmix (channel reduction) across the audio *and* video pipelines.** A
  per-library option now downmixes multichannel audio (e.g. 5.1 → 2.0 stereo) on re-encode,
  applied both to audio-only jobs and to the re-encoded audio tracks of a video transcode (it
  pairs with the audio-codec selection above; a copied track keeps its layout). The
  verification audio-fidelity gate treats a requested downmix as intentional (not a silent
  channel loss), while an unrequested downmix or a total loss of audio still fails. Saves space
  where surround is not needed; defaults to off. **Done.**
- **Well-researched, sane default profiles per container/use-case.** Ship opinionated,
  documented defaults that pair a container with matched video + audio codecs and quality
  settings, rather than leaving every knob to the operator — for example **MP4 → H.265 (HEVC)
  video + AAC audio** for broad device/player compatibility, **MKV → H.265 (or AV1) video +
  Opus audio** for maximum efficiency, and a compatibility **H.264 + AAC** profile for older
  clients. Defaults should cite the reasoning (transparent bitrate ranges, hardware-decode
  support, container/codec compatibility) so a user can pick a profile and trust it without
  tuning. Per-library overrides still layer on top. **Planned.**
- **Image optimisation**: modern formats (WebP/AVIF/JXL) and lossless re-encode,
  with quality scoring (SSIM/Butteraugli-style) and EXIF/ICC-profile preservation
  as verification gates; configurable max-dimension downscaling.
- **Per-kind rule profiles and encoder settings**, reusing the existing
  per-library override model. **Audio target codec/bitrate, video audio codec/bitrate, and
  stereo downmix done**; image rules to follow.
- **Scope the library Advanced-options UI to the library's media type.** The form now shows
  only the controls that apply to the library's `MediaType` — video settings (codec/container,
  CRF, encoder preset, max resolution, HDR handling, VMAF, and the video-audio codec/bitrate)
  for Film/TV, audio settings (audio target codec/bitrate) for Music — while a mixed "Other"
  library still shows everything and the stereo-downmix toggle stays visible for every type
  (it applies wherever audio is re-encoded). Purely a UI refinement; the underlying per-library
  overrides are unchanged. **Done.**
- Pure, unit-tested resolvers/evaluators per kind; the worker dispatches by media
  kind to the right command builder and verifier.

Exit criteria:

- A user can point a library at music or photos, see kind-appropriate candidates,
  and run optimise → verify → replace with rollback, never losing an original.
- Image/audio verification gates block replacement on quality or metadata loss.

## Phase 11: Settings Preview and Compare

Goal: let a user *try* a library's configured settings on a real file and see the
result before committing — a temporary, throwaway optimisation shown side by side
with the original, with hard numbers, so tuning settings is empirical instead of
guesswork.

Deliverables:

- **One-off preview job**: optimise a chosen file (or a representative sample/clip
  to keep it fast) into a temporary location using the library's resolved
  settings, never touching the original and never entering the replace flow;
  auto-cleaned afterwards.
- **Side-by-side compare UI**: original vs optimised players in sync (or matched
  frame thumbnails), plus a statistics panel — file size and % change, bitrate,
  codec/container, resolution, audio layout, and the Phase 9 quality scores
  (VMAF/SSIM) and verification summary.
- **Clip mode** to preview just a segment (e.g. 60 s) for a fast turnaround on
  large files, with an explicit note that scores are for the sampled segment.
- **Apply-from-preview**: if happy, the same settings are already saved; the
  preview output is discarded and the real queue run uses them.
- Pure helpers for the comparison statistics; the temporary-job lifecycle reuses
  the existing worker with a "preview" job type that is exempt from replacement.

Exit criteria:

- A user can preview settings on a file, watch original vs optimised side by side,
  read the size/quality deltas, and decide — with the original guaranteed
  untouched and the preview artifact cleaned up.

## Phase 12: Release Hardening

Goal: make the first public image safe for real libraries.

Deliverables:

- Dry-run mode.
- Backup/export of SQLite config.
- Database migrations tested.
- Integration tests with synthetic media fixtures.
- Docker image published to GHCR.
- README quickstart.
- Troubleshooting guide.
- Security notes around mounted volumes and reverse proxies.

Exit criteria:

- A careful user can run Optimisarr against a real library with dry-run,
  verification, quarantine, and rollback available.

## First implementation slice

The first working slice should be deliberately small:

1. Scaffold .NET API, Svelte UI, Dockerfile, and compose example.
2. Add `/api/health`.
3. Add `/api/system/tools` to report FFmpeg and ffprobe availability.
4. Add one library root setting.
5. Scan a directory and store file paths.
6. Probe one selected file and show streams in the UI.

This gives us an end-to-end app shape before we add any transcoding or
replacement behaviour.
