# Optimisarr Roadmap

This roadmap is intentionally implementation-focused. The goal is to build a
small, reliable core first, then widen codec, GPU, and automation support once
the replacement workflow is trustworthy.

## Up next (priority order, updated 2026-06-27)

1. **Phase 14 gold-standard hardening** — the next maturity pass is about making
   Optimisarr safer to expose, easier to automate, and easier to change without
   weakening the transcode → verify → replace pipeline. This phase is grounded in
   the project review at
   [`docs/reviews/2026-06-27-project-quality-and-gold-standard-review.md`](reviews/2026-06-27-project-quality-and-gold-standard-review.md)
   and its peer response.

   - **Optional admin-token auth: initial implementation done.** `OPTIMISARR_ADMIN_TOKEN`
     now gates the administrative API and SignalR hub with bearer-token authentication
     when set. The static SPA shell remains public so it can show a token prompt; useful
     API calls are blocked until the token is supplied. `/api/health`, `/api/ready`, and
     `/api/auth/status` stay open for health checks and discovery. Token comparison uses
     constant-time comparison over fixed-size token hashes, the UI stores the token
     locally and sends it on API, hub, and media-preview requests, and the deployment
     docs keep reverse-proxy authentication as the preferred public-access boundary.
     Follow-up: add integration
     coverage over the full destructive/secret-bearing endpoint set — settings
     export/import, settings update, enqueue, cancel/retry/remove, replace, approve,
     rollback, and purge/history clearing — once the OpenAPI/test-host work below is in
     place.

   - **Generated, CI-checked OpenAPI contract: initial implementation done.** The
     runtime OpenAPI 3.1 document is generated from the app into `docs/openapi.json`,
     and CI fails when the checked-in document drifts from the running API. The docs
     checker also verifies every path/method listed in `docs/api.md` exists in the
     generated spec. Follow-up: improve route grouping, response metadata, status codes
     for destructive/state-gated operations, auth annotations, and descriptions for
     safety-sensitive endpoints so the generated contract becomes more useful to client
     generators and API browsers.

   - **Pipeline robustness pass.** The project has proven the originals stay safe, but
     recent live fixes showed that safe can still mean failed, looping, or wasteful.
     Add adversarial tests around the behavior that actually carries product risk:
     `FfmpegCommandBuilder` stream/container permutations (attachments, data streams,
     cover art, bitmap/image subtitles, audio-only, still image, HDR, remux, MP4/MKV);
     replacement/reconcile state transitions (missing source, missing work output,
     destination occupied, concurrent replace callers, dry-run, rollback after partial
     failure, cross-filesystem fallback); and candidate decisions for already-optimised
     siblings, already-efficient sources, repeated failures, exclusions, and
     Sonarr/Radarr import-aware holds. Known live failure classes should be represented
     by tests before this phase is considered done.

   - **Endpoint modularization.** Split `src/Optimisarr.Api/Program.cs` into endpoint
     modules after auth/OpenAPI/pipeline tests are in place. Target shape:
     `HealthEndpoints`, `SettingsEndpoints`, `LibraryEndpoints`, `InventoryEndpoints`,
     `QueueEndpoints`, `ReplacementEndpoints`, `IntegrationEndpoints`, and
     `StatsEndpoints`, all mapped from a slim `Program.cs`. This is primarily a
     maintainability and reviewability change; it should be a pure move with no
     behavior change and must stay under a green test suite.

   - **Large-library API scalability.** `/api/jobs` and `/api/media` now have server-side
     filtering and pagination (`status`, `search`/`category`, date, `page`/`pageSize`, total in
     `X-Total-Count`), with a `(LibraryId, RelativePath)` index so a large inventory pages without a
     table sort. Remaining: paginate/filter the fleet-wide candidate views, and adopt the paging in
     the Inventory/Queue UI tables (bounded or virtualized) so the frontend stays responsive at tens
     of thousands of rows.

   - **Diagnostics bundle and admin health details.** Build on the shipped failure
     categories, failure summary endpoint, Failures tab, and captured ffmpeg logs. Add
     an authenticated diagnostics bundle containing version/build, redacted settings
     summary, enabled libraries, tool/hardware capability output, queue/failure summary,
     selected recent logs, and environment facts needed for support. Secrets and provider
     tokens must be redacted by default. Add an authenticated health-details endpoint for
     admins; keep `/api/ready` small and orchestration-friendly.

   - **Hardware validation matrix.** Create a maintained matrix that records CPU,
     NVIDIA NVENC, Intel QSV, VA-API, hardware decode, and GPU metrics validation by
     platform, with date, evidence, and known limits. AMD VA-API remains the important
     open validation target. The matrix should distinguish "implemented and unit-tested"
     from "validated on real hardware."

   - **Roadmap/docs split.** Keep this roadmap readable for users by moving dense
     engineering history into dated engineering notes once the current release hardening
     stabilizes. Preserve the useful detail, but let `docs/roadmap.md` answer "what is
     next?" while engineering notes answer "what exactly changed and why?"

2. **Phase 13 release hardening** — release controls are in progress; dry-run mode,
   config-and-secrets backups, migration smoke coverage, synthetic-media integration
  coverage, GHCR publishing, README quickstart hardening, troubleshooting, and security
  notes are shipped. Backups intentionally omit media, jobs, replacements, quarantine,
  and rollback history. CI stays on standard GitHub-hosted public-repo runners and avoids
  paid external services.

3. **First-class diagnostics & observability API** — make "why did this fail?" answerable
   from the API alone, without SSH-ing the host or reading container logs. Today failed-job
   detail *is* reachable (`GET /api/jobs` carries `ErrorMessage`, `FfmpegArguments`, and the
   verification report per job), but it is unfiltered, unaggregated, and lossy. Scope:
   - **Status-filtered job queries: done.** `GET /api/jobs?status=Failed` narrows server-side so
     callers don't fetch every row and filter client-side. (Library/reason/date filters and
     pagination remain to add.)
   - **Failure aggregation endpoint: done.** `GET /api/jobs/failures` groups failures by classified
     reason (size-saving gate, container incompatibility, image-based subtitles, replacement
     collision, source/output missing, verification, other) with counts and recent sample jobs,
     largest first. Backed by a pure, shared `FailureClassifier` so the buckets drive both the API
     and (later) the UI.
   - **Process-log capture: done.** A failed ffmpeg run keeps its substantive stderr (stream mapping,
     warnings, the ending error; progress frames filtered, long logs head/tail-elided) on the job,
     served at `GET /api/jobs/{id}/log` as plain text. The rich stderr that explains a failure is no
     longer container-log-only. Stored only on ffmpeg failure to keep the DB lean (verification
     failures are explained by their report). Migration `AddJobProcessLog`.
   - **Structured failure category on `Job`: done.** The classified reason is written to a
     `FailureCategory` column the moment a job fails, so the summary groups in the database and the
     class survives an edited message (older rows fall back to on-read classification). Surfaced per
     job on `GET /api/jobs` too. Migration `AddJobFailureCategory`.
   - **Failures UI: done.** A Failures tab on the Queue page groups failed jobs by reason (count,
     description, recent samples) with an inline "View log" drill-in to the captured ffmpeg log —
     deliberately a Queue tab, not a sidebar entry, to keep job views together and the sidebar lean.
   - **Filters and pagination: done.** `GET /api/jobs` takes `libraryId`, `category`, `since`/`until`,
     and `page`/`pageSize` (total returned in the `X-Total-Count` header; body unchanged); the failure
     summary takes a `libraryId`. SQL-translatable filters run in the database, the date filter and
     ordering in memory (SQLite can't order/compare a `DateTimeOffset`). This completes the item.
   The classification then feeds back into the dashboards and reports rather than the eligibility
   logic, which now handles the "skip before we waste an encode" cases directly — see the
   *already-optimised sibling skip* and *already-efficient source skip* notes below.

**Recently shipped (2026-06-26).**

- **Already-efficient source skip: done.** A video already encoded at a very low bitrate for its
  resolution (e.g. a ~1.6 Mbps 1080p h264 episode) is skipped at eligibility instead of being
  transcoded and then rejected by the size-saving gate. Uses a per-profile efficiency floor in bits
  per pixel-second (resolution/frame-rate independent), calibrated against real library data; HEVC and
  H.264 set a floor, AV1 sets none. Conservative (uses total-file bitrate), with the size-saving gate
  still the backstop. Remaining follow-up: expose the floor as a per-library override (needs a
  Library column + migration + UI) for operators who want to tune it.
- **Already-optimised sibling skip: done.** When an Optimisarr-produced output (a marked re-container,
  e.g. an hevc `.mp4`) still sits beside its original (e.g. the h264 `.mkv`), the original is now
  skipped at eligibility ("An optimised copy already exists alongside this file") instead of being
  transcoded again only to collide at replacement time. Detected purely from the probed inventory
  (same library, same path stem, marked sibling) via the pure `OptimisedSiblingEvaluator` overlay.
- **Permanently blocked auto-replace no longer loops: done.** A `ReadyToReplace` job that can never be
  applied (destination occupied by a different optimised file, verified output gone, or original gone)
  is now failed once rather than retried every reconcile cycle. Because it becomes terminally failed,
  the "previously failed" overlay and auto-exclusion then stop the file being re-queued.
- **MP4 container compatibility fix: done.** A re-encode/remux to an MP4-family output now drops
  Matroska attachment (`-0:t`) and data (`-0:d`) streams, which MP4 cannot mux. Previously a source
  carrying a font/cover attachment (reported by ffmpeg as "codec none in stream #N") aborted the whole
  job before a frame was written; the original was always left untouched, but the file never optimised.

- **Preview clip mode: done.** Long video previews encode a 60-second segment from the middle of the
  source, verify against a temporary clipped reference from that same window, and label the compare
  report as segment-only so VMAF/loudness/duration/size checks are interpreted correctly.
- **Dry-run mode: done.** A global Settings → General → Replacement switch lets operators scan,
  queue, transcode, verify, and preview normally while blocking manual replacement,
  auto-replace, and quarantine purge. Verified outputs stop at Ready to replace for
  review; rollback remains available for existing replacements because it restores
  the protected original.
- **Migration smoke test: done.** The test suite now applies all EF migrations to an empty
  SQLite database and asserts no pending migrations remain, catching broken migration chains
  separately from the `EnsureCreated`-based unit tests.
- **Release docs hardening: done.** The quickstart now covers compose selection, writable
  mounts, readiness checks, dry-run-first operation, and authenticated reverse-proxy exposure.
  Troubleshooting covers dry-run replacement blocks, readiness failures, config import
  validation, and stale UI after updates; security notes now call out the administrative
  surface and secret-bearing exports explicitly.
- **Synthetic-media integration coverage: done for candidate flow.** Hermetic tests now
  create synthetic video, audio, and image files, scan them through the real inventory
  service, apply synthetic ffprobe JSON through the parser, and verify candidate decisions
  through the real candidate service.
- **GHCR publishing: done.** CI builds the production container, runs the container
  readiness smoke test, and publishes GHCR images on non-PR branch/tag builds. The
  workflow builds that production image once, smoke-tests it, then pushes the same tags
  to avoid spending CI time on duplicate Docker builds.

- **Media thumbnails in lists: done.** Every row on the Inventory page and the per-library Candidates
  tab shows a kind-appropriate thumbnail: **film/TV** a poster (Radarr/Sonarr first — an exact, local
  match keyed to the imported file, with TV rows showing the series poster — then a connected media
  server); **music** the file's embedded cover art (extracted with ffmpeg, no external service);
  **images** a down-scaled still of the image itself. All bytes are produced/proxied by the backend so
  no token reaches the browser, lazy-loaded into a fixed box with a silent placeholder fallback.
  Reusable `<Thumbnail>` component + `GET /api/media/{id}/thumbnail`. Beyond the original roadmap scope.
- **Custom preset stop: done.** The per-library video slider ends in a **Custom** stop, so a manual
  codec/container choice is a deliberate "Custom" configuration rather than an amber "Overridden"
  warning (advances Phase 12's "richer, explicit preset sliders").
- **Inventory reconciliation: done.** A scan now prunes inventory rows whose file has vanished (e.g. a
  Radarr/Sonarr upgrade-and-rename), cascading their now-meaningless jobs, while preserving rows with
  replacement history — so an upgraded title no longer leaves a phantom candidate and a job that fails
  with "No such file". A job whose source disappears now fails fast with a clear reason.
- **Replacement safety hardened.** Fixed a concurrency race where the post-verify auto-replace and the
  background reconcile sweep could replace the same job at once and destroy the verified output (the
  original was always preserved). Replacement is now serialised per job, and a ReadyToReplace job
  whose verified output has vanished is failed rather than retried forever.

**Inventory master-detail refactor: done.** Inventory uses a bounded, paged file list with a
per-file detail card beneath it (probe values, eligibility reason, and actions).

**Queue operational UI: done.** Queue has a top-of-page current-work hero card
with stage, progress, encoder, speed, ETA, and live CPU/GPU telemetry. Queue and
Quarantine use the same bottom-sheet detail interaction as Inventory, shrinking
their tables while the selected report is open instead of expanding a row inline.

**Inventory & Candidates unified: done.** The separate Candidates page is gone — the **Inventory**
page now shows every file's stream detail alongside its eligibility (Eligible / Skipped / Not probed
+ reason), with an eligibility filter, so the file list and "what the rules select" are one view.
The Libraries workspace keeps its own per-library Candidates tab (that's the per-library focus; this
is the fleet-wide list). `#/candidates` redirects to Inventory.

**Explicit video preset sliders: done.** Every position on the per-library video slider now shows
the codec it resolves to, and the "Selects: …" detail (codec/container/CRF) plus the per-position
codecs are driven by the backend's `RuleProfileDefaults` (served via `/api/library-options`) rather
than a hard-coded UI map, so the slider can never drift from what the server actually does. A first
extra preset position is shipped — **"Scott's Settings"** (`RuleProfile.ScottsSettings`): HEVC/MP4
with HDR preserved and audio re-encoded to AAC 96 kbps stereo. Adding further positions remains
optional future work.

**Re-encode oversized same-codec files: done.** A per-library option re-encodes files already in
the target codec when they exceed a configurable size (default 20 GB), to shrink large same-codec
remuxes; the size-saving gate still protects the original.

**File exclusions: done.** Individual files can be excluded from optimisation — manually (e.g. from a
stuck Queue job) or automatically after three failures — via a durable, path-keyed list, managed on
a per-library **Excluded** tab. This replaces relying on the soft "previously failed" skip, which was
lost when queue history was cleared.

**Dashboard outcomes: done.** The Dashboard leads with a persistent lifetime **space-saved** total
(resettable, surviving restarts and history clearing), the work in flight, and live CPU/GPU usage
while a job encodes.

**Quarantine compare-to-approve: core done.** The Quarantine page now expands each replacement into
a compare panel (original vs replacement size/saving + the full verification report), with
**Approve & free space** (purge the original now) and **Reject (roll back)** actions. Visual media
preview (thumbnails/players) is deferred. See the Phase 11 note.

**Phase 12 (Unified Library & Candidates workspace): core done.** Opening a library now shows its
rules and the candidates those rules select as two tabs in one view, with re-resolve on save and
per-library eligible/skipped tallies on the list. The all-libraries candidate list now lives on the
unified **Inventory** page (see the Inventory & Candidates note above), not a separate Candidates page.
See the Phase 12 section for the remaining optional polish.

## Current status (2026-06-26)

- **Phase 0 (Foundation): done.** Repo, three .NET projects + Svelte UI, Docker
  image building and publishing to GHCR via CI, health endpoint, SQLite under
  `/config` via EF Core migrations.
- **Phase 1 (Discovery & Inventory): done, extended.** Recursive settling-aware
  scanning, ffprobe inspection, inventory UI. Extended beyond the original plan
  to support **multiple libraries**, each with its own media type and rule
  profile, plus a folder-picker for paths. **Scans now reconcile deletions:** a row
  whose file has vanished from disk (e.g. a Sonarr/Radarr upgrade renamed it) is
  pruned — cascading its jobs, preserving rows with replacement history — so the
  inventory matches reality and stale candidates/jobs don't linger.
- **Phase 2 (Candidate Rules): largely done.** A pure, unit-tested
  `CandidateEvaluator` turns per-library rule profiles into eligibility
  decisions with a human-readable reason for every file (eligible or skipped):
  min size, resolution limit, HDR/Dolby Vision exclusion, path exclusions,
  codec/container matching, and already-processed detection. Surfaced via
  `GET /api/candidates`, shown as the eligibility column on the Inventory page (and as a
  per-library Candidates tab in the Libraries workspace). Per-library overrides (target
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
  `ReadyToReplace` — originals are never touched. **Queue detail view:** clicking a
  job opens a slide-up sheet (the shared `BottomSheet`, also used by Inventory) with a
  large progress bar, fps/speed/ETA, the resolved encoder (GPU/CPU), output size, the
  verification report, and inline replace/retry/cancel — plus a **live CPU/GPU usage
  graph** while it encodes (see Phase 7). The **sidebar** Queue item shows a throbbing
  GPU chip for hardware-accelerated work or a snail for CPU work, with a running-job
  count, driven by one app-wide SignalR connection.
- **Phase 4 (Verification): done.** A clean ffmpeg exit no longer trusts the
  output. The worker runs a real `Verifying` step — a full-decode health check
  (`DecodeHealthCheck`), an output ffprobe, and a comparison against the original —
  and feeds the evidence to a pure, unit-tested `VerificationEvaluator`. The
  `VerificationReport` (decode health, output readable, video stream present,
  duration tolerance, audio/subtitle retention, size saving) is persisted on the
  job and surfaced on the Queue page. Only a passing report advances toward
  replacement; a failure marks the job `Failed` with the output retained for
  inspection and the original untouched. Thresholds are fixed conservative
  defaults for now (`VerificationPolicy.Default`). Queue filters surface all,
  verified, and verification-failed jobs; selecting a row opens its full gate
  report in the shared detail sheet.
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
  **Hardened (2026-06-26):** replacement is serialised per job, closing a race where
  the post-verify auto-replace and the reconcile sweep could act on one job at once
  and destroy the verified output (the original was always preserved); and a
  `ReadyToReplace` job whose verified output has vanished is now failed rather than
  retried indefinitely.
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
  automatic optimisation** extends this: a library has its own local-time window
  (pure `AutoEnqueueScheduleEvaluator`), and inside that window its eligible files
  are continuously queued **and** dispatched; outside it, that library's jobs do not
  start. There is no longer a global processing window — global settings hold only
  the library scan interval, and manually queued jobs run whenever the queue can
  start one (subject to concurrency, activity-pause, and disk-safety). A dedicated
  **Schedule page** surfaces dispatch status (ready/paused + reason, running/limit,
  free work-disk) and the per-library auto-optimise table with each library's window,
  in/out-of-window state (overnight windows handled), auto-replace setting, and last run.
- **Phase 7 (GPU Support): largely done.** Encoder/hwaccel capability detection on Tools,
  global encoder-mode selection wired into FFmpeg args, and jobs that fail fast with a clear
  reason when a selected encoder is unavailable. **NVENC is now confirmed working end-to-end**
  (an earlier bug silently ran video re-encodes on CPU when a file was classified `Unknown`; the
  encoder is now resolved whenever the spec re-encodes video, and the command builder emits
  per-encoder rate control — NVENC `-cq`, QSV `-global_quality`, VAAPI `-qp` — instead of `-crf`
  for all). Transcoding runs through **jellyfin-ffmpeg** (bundles the Intel iHD driver + oneVPL and
  NVENC), and the compose example documents `/dev/dri` + the render group, so **Intel QSV/VA-API
  (e.g. an N100) and AMD VA-API** are wired — pending on-hardware validation (see KNOWN_ISSUES).
  Encoder availability is now **confirmed by a real test encode** per encoder (cached, with a Tools
  Refresh to re-probe) rather than inferred from device-node presence, so the capability list reflects
  what actually works. **Intel QSV is now validated on real hardware** — hardware *encode* and
  *decode* both confirmed on an Intel iGPU host (CPU dropped from ~142% to ~22% on a 4K encode with
  the render/video engines busy). **GPU hardware decoding** is wired and on by default
  (`queue.hardwareDecode`): when a hardware encoder is in use the source is decoded on the GPU
  (`-hwaccel` + `-hwaccel_output_format`, no `hwupload`), skipped for HDR→SDR tonemap jobs, with an
  automatic software-decode retry for sources the GPU can't decode. **Live, unprivileged CPU/GPU
  metrics** stream to the Queue graph over SignalR — `/proc/stat` for CPU and per-process DRM fdinfo
  (Intel/AMD) → AMD sysfs → `nvidia-smi` for GPU, with no root/CAP_PERFMON or compose change required.
  Remaining: hardware-specific preset notes; AMD VA-API on-hardware validation; optional NVIDIA
  (`cuda`) decode acceleration.
- **Phase 8 (Library Integration): feature-complete.** Authenticated Plex (OAuth/PIN),
  Jellyfin (Quick Connect/API key), and Emby (API key) connections; targeted re-scan after a
  replacement/rollback; Sonarr/Radarr import-aware exclusions; notifications (webhook/ntfy/
  Apprise); config-and-secrets backup/import.
- **Phase 9 (Gold-Standard Verification): feature-complete.** Opt-in VMAF gate (plus PSNR/SSIM
  signals), always-on HDR/colour/A-V-sync/timestamp/tail integrity gates, audio channel/
  sample-rate retention, opt-in EBU R128 loudness + true-peak clipping gates, per-library VMAF
  overrides. All gate logic pure and unit tested.
- **Phase 10 (Multi-Media Optimisation): in progress.** Done so far: media-kind detection;
  lossless-audio optimisation with per-library audio codec/bitrate; **audio-codec selection for
  video transcodes** (AAC default); **stereo downmix** across both pipelines; **any-source
  (lossy) audio re-encoding** gated on a proven bitrate saving; and **media-type-scoped library
  Advanced options**; and sane default per-container profiles. **Image optimisation (WebP) works
  end-to-end**: candidate rules, command building, kind-aware verification, per-library overrides,
  a `Photo` media type, animated-image skipping, and the UI — verified in a container (still
  PNG/BMP/TIFF → WebP with large savings, dimensions retained, verification passing). **Image
  optimisation is now broadly complete:** three output formats on a compatibility→efficiency slider
  (**JPEG** default for max compatibility incl. Plex, **WebP**, **AVIF** — all wired in the command
  builder), per-library **downscaling** (named 4K/1080p caps, custom max long-edge, or percentage —
  aspect-preserving, never upscaling, with a downscale-aware Dimensions gate), an opt-in **image
  SSIM quality gate**, and a **portable optimisation marker** for every image format via exiftool
  (EXIF/XMP `Software`), closing the marker round-trip gap, an opt-in **EXIF/ICC-retention gate**
  (an image that drops the original's ICC colour profile or EXIF on re-encode fails verification;
  reads both with exiftool, fails closed, flags loss only), and the **output-filename collision fix**
  (work output is namespaced per media file, and a replacement whose destination is already occupied
  fails safely). Still to come: in-container validation of the AVIF quality mapping.

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

- Time windows. **Done** (now per-library auto-optimise windows; the global processing window was removed).
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
**Import/export** is done too: a secret-bearing config snapshot (settings, libraries,
watchers, notification targets, Sonarr/Radarr connections) exports to JSON and
imports back as a validated, non-destructive merge (pure unit-tested
`ConfigSnapshotValidator`). The file must be stored securely; it intentionally
does not include jobs, replacements, quarantine, or rollback history.
**Sonarr/Radarr import-aware exclusions** are done as
well: connected managers are polled for in-progress imports and any file whose
folder an import is landing in is held back from queueing (pure unit-tested
`ArrQueueParser` and `ArrImportExclusionEvaluator`), so Optimisarr never fights an
import. Phase 8 is now feature-complete.

Deliverables:

- **Authenticated media-server connections** so Optimisarr can tell the server to
  re-scan a title after a verified replacement (a replaced file keeps its path but
  changes container/codec/size, so the server should re-read it). Each provider's
  login is different and must use the provider's own supported flow — Optimisarr
  never asks the user to paste a raw password for storage:
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
rules, audio-codec selection for video transcodes, stereo downmix, any-source (lossy) audio
re-encoding, and media-type-scoped Advanced options are done.** A pure, unit-tested `MediaKindClassifier` classifies every probed file as
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
- **Transcode from any audio source, not just lossless.** A per-library "re-encode lossy audio"
  opt-in now also makes already-lossy sources eligible, but only when re-encoding would
  genuinely save space: probing records the source audio bitrate, and a lossy file is eligible
  only when that bitrate exceeds the target by a safety margin (≥25%, `AudioTarget.LossyReencodeSaves`).
  A file at/near the target, or one whose bitrate ffprobe could not report, is left untouched
  with a clear reason. The conservative lossless-only behaviour stays the default. **Done.**
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
- **Well-researched, sane default profiles per container/use-case.** Each profile now ships a
  matched container + visually-transparent CRF (researched against 2026 device/codec support):
  **Conservative HEVC → MP4 + AAC, CRF 24** for broad device/Apple/TV compatibility;
  **Compatibility H.264 → MP4 + AAC, CRF 20** for older clients; **Experimental AV1 → MKV + Opus,
  CRF 30** for maximum efficiency; Remux/Cleanup unchanged. A new profile-level `DefaultCrf`
  replaces the encoder's arbitrary built-in default; per-library overrides still layer on top.
  Audio defaults to copy (the recommended codec is documented, not forced) so a re-encode never
  silently downgrades the original. The preset is now picked via a simple compatibility↔efficiency
  **slider** (with a separate "no re-encode" toggle for Remux/Cleanup), keeping every exact knob
  behind Advanced options. **Done.**
- **Image optimisation**: modern formats (WebP/AVIF/JXL) and lossless re-encode,
  with quality scoring (SSIM/Butteraugli-style) and EXIF/ICC-profile preservation
  as verification gates; configurable max-dimension downscaling. **Candidate rules done**
  (pure `ImageTarget` + image branch in `CandidateEvaluator`: lossless PNG/BMP/TIFF/GIF eligible,
  lossy JPEG behind a per-library opt-in, already-target-format and too-small skipped, with
  WebP/AVIF/JXL targets and conservative defaults). **WebP command building is done** (pure
  `TranscodeSpecResolver`/`FfmpegCommandBuilder` image path: `libwebp` encode, source EXIF/ICC
  preserved via `-map_metadata 0`, output stamped with the optimisation marker, and the
  dispatcher no longer resolves a video encoder for image jobs; AVIF/JXL throw a clear
  not-implemented error until their quality mapping is validated). **Kind-aware verification is
  done** (a still is judged on decode health, readability, a present picture, retained
  dimensions, and a size reduction; the time-based/stream gates that don't apply to an image are
  skipped rather than failed). **Per-library image overrides are done** (target format, quality,
  and a re-encode-lossy toggle — three nullable `Library` columns via migration, wired through
  `RuleOverrides`/resolver/DTO/parser/config-snapshot, with an Images section in Advanced scoped to
  Other libraries; the format picker is gated to encodable formats so only WebP is offered until
  AVIF/JXL encode lands). **Animated images are skipped** (a multi-frame GIF/WebP is detected via
  the probed frame count and left untouched rather than flattened into a broken still). Verified
  end-to-end in a container: still PNG/BMP/TIFF optimise to WebP with large savings, dimensions
  retained, and verification passing. **Per-image SSIM quality scoring and the opt-in EXIF/ICC-retention
  gate are now done** (the latter fails an image that drops the original's ICC profile or EXIF).
- **Image re-optimisation marker.** The intent is for image outputs to carry the same
  `OptimisationMarker` as video/audio. In-container testing showed ffmpeg's `libwebp` encoder
  **silently drops `-metadata`**, so a WebP output carries no container tag — writing it needs an
  EXIF/XMP tool (e.g. `exiftool`) ffmpeg lacks. Until then, re-optimisation is prevented by the
  database optimisation history and the "already in the target format" check; the portable marker
  for images is tracked in [`KNOWN_ISSUES.md`](../KNOWN_ISSUES.md).
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

Status: core done, clip follow-up done. A **Preview** action on each eligible candidate (Inventory and the Libraries
workspace) queues a throwaway `Preview` job — the real probe→transcode→verify pipeline on one file
with the library's resolved settings — that never moves or replaces anything, writes to its own
`/work/preview/<id>` scratch, is hidden from the queue, is deleted on close, and never survives a
restart. The compare panel shows the original next to the encoded result with a per-media-type
viewer (image/video/audio, range-streamed), a size/codec/resolution/audio stats table with the %
size saving, and the full Phase 9 verification report. Long video previews use a 60-second sample
from the middle of the source and verify against a temporary clipped reference from that same window,
so segment-only scores stay meaningful. Deferred: apply-from-preview (settings are already saved, so
previewing then enqueuing already works). The Quarantine compare-to-approve half was delivered
earlier.

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
- **Clip mode: done.** Video previews encode a 60-second middle segment for fast turnaround, verify
  against the same segment of the original, and label scores as segment-only.
- **Apply-from-preview**: if happy, the same settings are already saved; the
  preview output is discarded and the real queue run uses them.
- Pure helpers for the comparison statistics; the temporary-job lifecycle reuses
  the existing worker with a "preview" job type that is exempt from replacement.
- **Quarantine compare-to-approve (related, on the Quarantine page). Core done.** Each replacement
  on the Quarantine page expands into a compare panel showing the quarantined original against the
  in-place replacement — size + saving % and the full Phase 9 verification report (the measured
  VMAF/SSIM, duration, audio-retention, etc. gates) — so an operator can **approve** (here: delete
  the quarantined original now to reclaim space, keeping the replacement) or **reject** (roll back to
  the original) from one screen. Reuses the existing rollback and quarantine-purge services — a
  review UI over actions that already exist, never a new destructive path. The **visual** half
  (in-sync players / matched-frame thumbnails) is deferred; today's compare is stats + report only,
  since the original's pre-replacement probe isn't persisted.

Exit criteria:

- A user can preview settings on a file, watch original vs optimised side by side,
  read the size/quality deltas, and decide — with the original guaranteed
  untouched and the preview artifact cleaned up.
- From the Quarantine page, a user can compare a replaced file against its quarantined
  original and approve or roll back, with the safety model unchanged.

## Phase 12: Unified Library & Candidates Workspace

Status: core done. Opening a library is now a tabbed workspace — a **Rules** tab (the existing
preset + Advanced form) and a **Candidates** tab showing the eligible/skipped decisions for *that*
library, reusing the shared `CandidateTable` component. The candidate list **re-resolves on Save**
(and after Scan/Enqueue) so it always reflects the persisted rules, and the editor stays open after
Save so the cause-and-effect loop happens in one place. The **Libraries list** shows each library's
eligible/skipped **tally** (a lightweight `/api/candidates/summary` that reuses the pure
`CandidateEvaluator`), and the all-libraries candidate list now lives as the eligibility column on
the unified **Inventory** page (the separate Candidates page was merged into Inventory). No new
domain logic; enqueue remains the only action and only queues. Remaining optional polish: optimistic
live preview of *unsaved* edits (folds into Phase 11).

Goal: stop treating a library's *configuration* and the *files that configuration
selects* as two separate screens. Today the **Libraries** page edits a library's
rules and the **Candidates** page (filtered by a library dropdown) shows the
resulting eligible/skipped decisions — but they are the two halves of one mental
loop: *change a rule → see what it now selects → enqueue it*. Splitting that loop
across two pages means the operator tunes a setting on one screen, navigates to
another, re-selects the same library, and reads the effect with no memory of what
changed. Bringing them together makes the cause-and-effect immediate and is the
natural home for the Phase 11 settings preview.

This is a UX consolidation, not new domain logic; it can land independently of the
phase numbering and reuses the existing `/api/libraries` and `/api/candidates`
endpoints. It must be done *thoughtfully* — combining the views badly (one giant
scrolling page, or burying the candidate list behind the rule form) would be worse
than leaving them apart.

Deliverables:

- **A per-library workspace** (a library opens into its own view) that shows, in
  one place: the library's identity and simple choice (name/path/type + the
  compatibility↔efficiency preset) up top, its **Advanced options** still gated and
  collapsed, and — alongside, not buried beneath — the **candidate list for that
  library** with the same eligible/skipped reasons shown on the Inventory page.
- **Live cause-and-effect**: editing a rule re-resolves the candidate decisions for
  that library (eligible/skipped counts and reasons update) so the operator sees
  what a change selects *before* committing it. Decisions come from the existing
  pure `CandidateEvaluator`, so this is a re-fetch/re-render, not new logic.
- **A summary still reachable across libraries**: keep a way to see candidates (or
  at least eligible counts) for *all* libraries at once, so the per-library focus
  does not lose the fleet-wide view — the Libraries list shows each library's
  eligible/skipped tally, and the all-libraries view lives on the unified Inventory page.
- **Richer, explicit preset sliders.** Give the per-library **video** slider more
  positions/options (e.g. a finer compatibility→efficiency axis, and surfacing more of
  the per-codec choices that currently live only in Advanced) and make every slider
  **explicit about what it selects** — show the exact codec/container/CRF (video) or
  format/quality (image) each position resolves to, so the slider is never a black box.
  The "Selects: …" badges under the video/image sliders are the first cut; extend this to
  cover every position and keep it accurate against the backend `RuleProfileDefaults`.
- **Honest, unchanged safety semantics**: enqueue still only queues; nothing here
  replaces or deletes. The workspace must not imply a rule change retroactively
  un-optimises already-processed files.
- A clear migration for the navigation: the sidebar's separate "Libraries" and
  "Candidates" entries are reconsidered together (one "Libraries" workspace, with
  candidates as a tab/panel within it) rather than left as two peers that overlap.

Open questions to resolve during design (not pre-decided here):

- Whether the candidate list lives as a **tab** within the library view, a **side
  panel**, or an expandable section under each library card.
- How live the re-resolve should be: on **save** (simplest, honest) vs. an
  optimistic **preview** of unsaved edits (more powerful, closer to Phase 11, but
  must never be mistaken for the persisted state).
- Where the **all-libraries** candidate overview ultimately belongs (its own page,
  the Dashboard, or a roll-up on the Libraries list).

Exit criteria:

- An operator can open one library, adjust its rules, and see the eligible/skipped
  candidate list for that library update in the same place, then enqueue — without
  hopping between two screens and re-selecting the library.
- The fleet-wide candidate/eligibility overview is still reachable.
- No change weakens or misrepresents the safety model; enqueue remains the only
  action and it only queues.

## Phase 13: Release Hardening

Goal: make the first public image safe for real libraries.

Deliverables:

- Dry-run mode. **Done.**
- Backup/export of SQLite config. **Done for portable config-and-secrets snapshots; raw
  SQLite state backup remains external/operator-owned.**
- Database migrations tested. **Done for empty-database migration smoke coverage.**
- Integration tests with synthetic media fixtures. **Done for scan → parsed probe → candidates across video, audio, and images.**
- Docker image published to GHCR. **Done via CI after container readiness smoke.**
- README quickstart. **Done.**
- Troubleshooting guide. **Done.**
- Security notes around mounted volumes and reverse proxies. **Done.**

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

This provides an end-to-end app shape before adding transcoding or
replacement behaviour.
