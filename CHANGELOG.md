# Changelog

## Unreleased

### Verification: decode-timestamp monotonicity gate

- Verification now checks the converted output's **video decode timestamps are
  monotonic**. A new pure, unit-tested `PacketTimestampParser` reads ffprobe's
  per-packet `dts_time` stream and tallies any timestamp that steps backward; a
  `TimestampIntegrityCheck` gathers it with a cheap metadata-only probe (no decode).
  Out-of-order packets can stall or desync playback even on a file that otherwise
  decodes, so any regression fails verification and blocks replacement. The gate is
  always on when the output's packet timestamps are readable and simply abstains when
  they are not, never blocking on missing evidence. Closes one of the two remaining
  Phase 9 container-integrity items (truncated-GOP detection is still to come).

### UI polish and iOS Safari fix

- Fixed the white strips that appeared at the top and bottom of the app in iOS Safari:
  the document background is now painted in the theme colour (so safe-area insets and
  rubber-band overscroll no longer reveal white), the shell uses the dynamic viewport
  height (`100dvh`) and honours the notch/home-indicator safe areas (`viewport-fit=cover`),
  and `theme-color`/`color-scheme` follow light and dark.
- Buttons, banners, and empty states across the Libraries page now carry consistent icons
  (add, scan, enqueue, configure, delete, save/cancel, success/error), with a friendlier
  empty state and a spinning indicator while a scan runs.
- Status messages and empty states are now shared `Banner` (error/success/info, with a
  leading icon) and `EmptyState` components, replacing the per-page inline markup so every
  page's error/success banner looks and behaves the same.
- The sidebar application mark is now much larger when expanded (and a touch larger when
  collapsed), with the responsive `srcset`/`sizes` hints updated so the bigger logo still
  renders crisply on hi-DPI displays.

### Simpler library configuration

- The per-library form now leads with just the essentials — name, path, media type, and a
  preset with a plain-language summary of what it does — so adding a library is a few
  clicks. Every technical knob (target codec/container, HDR handling, encoder preset,
  quality, VMAF thresholds, resolution/size limits, exclude paths, and move-on-complete)
  is tucked behind a single **Advanced options** disclosure, which opens automatically when
  you edit a library that already uses one of them.
- Numeric settings are now **sliders** instead of raw fields: queue priority is a
  Lowest–Highest slider with a live label, quality (CRF) shows a Smaller↔Sharper scale, and
  the VMAF gate override is a toggle that reveals Average/Worst-frame sliders. Booleans are
  toggles throughout. No behaviour or saved values change — only how they're presented.

### Per-library automatic optimisation

- A library can now **scan and enqueue itself automatically** on a daily schedule, turning
  Optimisarr into a set-and-forget optimiser. Each library has an opt-in auto-enqueue
  toggle and a **daily time window**; a pure, unit-tested `AutoEnqueueScheduleEvaluator`
  fires it **once per window occurrence** (equal start/end means all day, i.e. once daily;
  a 01:00–06:00 window runs a single nightly scan-and-enqueue when it opens, including
  windows that cross midnight). A new `AutoEnqueueWorker` background service does the
  scan → enqueue and stamps `LastAutoEnqueueAt`, surfaced on each library card.
- Automation changes **nothing about safety or limits**: the worker only *creates queued
  jobs*, never starts them. Execution stays with the single-writer `QueueDispatcher`, so
  several libraries enqueuing at once still honour the **global concurrency limit** and the
  **global processing window**, and the existing idempotent enqueue + history guard mean a
  repeated run never duplicates a job or re-optimises a file. The schedule is editable per
  library and carried in config export/import.

### Sidebar brand, build version, and cyan palette

- The sidebar now leads with a **large, centred application mark** that scales
  gracefully — a bigger logo when expanded, a compact badge when collapsed — served
  from a responsive `BrandMark` whose `srcset` lets the browser pick the sharpest icon
  for the rendered size and the display's pixel density.
- The current **build version (git short hash)** is shown at the bottom of the sidebar,
  linking to the matching commit, with the full `version+hash` on hover. It is injected
  at build time by Vite (`__GIT_HASH__`/`__APP_VERSION__`); the Docker web stage has no
  `.git`, so the hash is passed in as a `GIT_HASH` build arg (wired through CI from
  `github.sha`) and falls back to `unknown` when unavailable.
- The accent palette is retuned from emerald to **cyan** to match the application icon's
  glowing cube — active navigation, primary buttons, focus rings, toggles, sliders, and
  progress fills now read cyan, while green/red are reserved for pass/fail and other
  status, so success and "selected" are no longer the same colour.

### Don't optimise the same file twice

- A file that has already been optimised — or that already failed — for its current
  version is no longer offered as a candidate or re-queued, so the queue can't loop on
  the same file (which previously affected failed jobs and the move-on-complete flow,
  where the original never changes). The rule is version-aware via a pure, unit-tested
  `OptimisationHistoryEvaluator`: a job only counts if it finished at or after the
  file's modified time, so a genuinely changed file (a fresh rip) becomes eligible
  again. The Candidates list explains why with "Already optimised" or "Previously
  failed".
- Failed and cancelled jobs can be **retried** from the queue (`POST /api/jobs/{id}/retry`),
  which re-queues the file as a fresh attempt — the deliberate way to re-run a file the
  history guard is holding back. The Queue page surfaces this with a **Retry** action on
  failed/cancelled rows, status **filter chips** (All / Active / Completed / Failed with
  live counts), an inline failure reason instead of a bare "error" label, and a shared
  inline-SVG `Icon` set on the row actions.

### libvmaf-enabled ffmpeg and PSNR/SSIM

- The image now bundles **jellyfin-ffmpeg** (a Debian-packaged ffmpeg with libvmaf and
  its models, plus hardware encoders) and points the quality/loudness measurement at
  it via `OPTIMISARR_FFMPEG_VMAF`, so the VMAF and EBU R128 gates actually run. The
  proven Debian ffmpeg still drives transcoding and probing, so enabling the gates
  never disturbs the existing encode path.
- The VMAF pass now also computes **PSNR and SSIM** in the same run, surfaced in the
  verification report alongside VMAF as corroborating quality signals.

### Per-library VMAF thresholds

- A library can now override the perceptual-quality (VMAF) gate's harmonic-mean and
  worst-frame thresholds, so an archive library can demand near-lossless quality
  while a space-saver accepts more — resolved per job by a pure, unit-tested
  `VerificationPolicyResolver` that falls back to the global thresholds when no
  override is set (and only when the gate is enabled). Editable on each library and
  carried in config export/import.

### True-peak clipping detection

- An opt-in **true-peak clipping** gate now catches a re-encode that introduces clipping.
  The `ebur128` measurement runs with `peak=true`, so the output's true peak comes from
  the same decode the loudness gate already does — no extra pass. A pure, unit-tested
  parser reads the dBTP value and the evaluator fails the job only when the output rises
  above a configurable ceiling (default 0 dBTP) *and* is hotter than the original, so a
  source that was already clipping is never blamed on the re-encode. The gate fails closed
  if the true peak can't be measured, is configurable from Settings (with a negative
  ceiling like −1 dBTP allowed for a stricter margin), and is carried in config
  export/import.

### Colour metadata and A/V sync integrity

- Verification now compares the output's **colour primaries, transfer, and matrix**
  against the original and fails a definite mismatch (e.g. BT.709 re-tagged as
  BT.601); a dropped tag is treated as benign. It also checks **A/V sync** from the
  output's video and audio start times, failing only a gross offset (> 0.5 s) so
  normal priming offsets don't trip it. Both reuse existing probe data, are pure and
  unit tested, and only run when the relevant metadata is present.

### Audio fidelity checks

- Verification now guards audio. An always-on **channel/sample-rate retention** gate
  (when audio retention is required) fails a job that silently downmixes — e.g. 5.1 to
  stereo — or drops the sample rate, reusing a quick re-probe of the original for no
  decode cost. An opt-in **EBU R128 loudness** gate measures the integrated loudness of
  the original and output with FFmpeg's `ebur128` filter and fails the job if they
  drift beyond a configurable tolerance (default 1 LU); it adds a decode pass per file
  so it is off by default and fails closed if loudness can't be measured. The loudness
  parsing and both gate decisions are pure and unit tested.

### Never silently strip HDR

- Verification now includes an always-on **HDR preservation** gate: if the original
  carries an HDR signal (HDR10/HDR10+/HLG transfer or Dolby Vision side data) and the
  library is set to preserve it, the output must still be HDR or the job fails — so a
  transcode can never quietly drop HDR. When the library intentionally tone-maps HDR
  to SDR, the gate passes; SDR originals are unaffected. It reuses the existing probe
  data, so it adds no extra FFmpeg work, and the decision is pure and unit tested.

### Perceptual quality gate (VMAF)

- Verification can now measure the output's **perceptual quality against the
  original with FFmpeg's libvmaf** (VMAF, plus PSNR/SSIM when the build reports
  them) and gate replacement on it. It is **opt-in** — measuring VMAF needs an
  ffmpeg built with libvmaf and roughly doubles verification time — and configured
  on Settings with two floors: a **harmonic-mean** VMAF (penalises bad frames) and
  a **per-frame minimum** (catches short artifact bursts a healthy average hides),
  defaulting to 93 and 80.
- The libvmaf JSON parsing (`QualityScoreParser`) and the gate decision
  (`VerificationEvaluator`) are pure and unit tested against captured output; the
  measured numbers are shown in the verification report. The gate **fails closed**:
  if quality cannot be measured (e.g. an ffmpeg without libvmaf) the check fails so
  an unproven output is never allowed to replace an original. The distorted stream
  is scaled to the reference (`scale2ref`) so a downscaled encode is still compared
  like-for-like. Note: Debian's stock ffmpeg lacks libvmaf, so the gate needs a
  libvmaf-enabled ffmpeg in the image (tracked as a Phase 9 infra follow-up).

### Don't fight Sonarr/Radarr imports

- Connect **Sonarr** and **Radarr** (base URL + API key, key write-only) on the
  Settings page. Before queueing a library, Optimisarr asks each connected manager
  which titles it is currently importing (via `/api/v3/queue` with the title
  embedded) and **holds back any file whose folder an import is landing in**, so a
  transcode never competes with — or is overwritten by — an import. Held-back files
  become eligible again on the next enqueue once the import settles, and the enqueue
  summary reports how many were held back.
- The queue parsing (`ArrQueueParser`) and the segment-aware path match
  (`ArrImportExclusionEvaluator`) are pure and unit tested. Querying is best-effort
  and self-isolating: a manager that is offline, rejects the key, or returns junk
  contributes no exclusions and never wedges the queue, exactly like the streaming
  activity-pause gate. Connections are included in config export/import (without the
  key).

### Export and import your configuration

- A new **Backup & restore** section on Settings exports your settings, libraries,
  activity watchers, and notification targets to a JSON file, and imports one back.
  For safety, **provider tokens are never exported** — the file carries no
  credentials, so it is safe to store or share, and tokens are re-entered after
  importing.
- Import is **validated in full before anything is written** (schema version,
  recognised setting keys, valid enums and required fields) by a pure, unit-tested
  `ConfigSnapshotValidator`, so a malformed or newer-than-supported file is rejected
  whole. It then applies as a **non-destructive merge**: libraries are matched on
  path, watchers and targets on name, so importing never deletes existing
  configuration and never overwrites a stored token with a blank.

### Notifications on replacement and failure

- Optimisarr can now POST to a **webhook**, an **ntfy** topic, or an **Apprise**
  endpoint when a file is replaced or a job fails. Targets are managed on the
  Settings page, each with its own URL, optional bearer token (write-only — stored
  but never returned), and per-event toggles so you can be alerted on failures only,
  replacements only, or both.
- Message rendering (`NotificationMessages`) and per-type request shaping
  (`NotificationRequestBuilder` — JSON for webhook/Apprise, plain text with a Title
  header for ntfy) are pure and unit tested. Delivery is **best-effort**: it runs
  after the replacement is committed or the job is already marked failed, with a 10s
  timeout, so a target that is offline or rejects the POST is logged and never
  affects processing.

### Sign in to Plex and Jellyfin instead of pasting a token

- Adding a Plex or Jellyfin watcher no longer requires hunting down a raw token.
  **Sign in with Plex** runs Plex's OAuth/PIN flow — Optimisarr creates a PIN,
  opens `app.plex.tv` for you to approve, and polls until Plex issues a token,
  which it fills into the form. **Quick Connect** does the Jellyfin equivalent:
  it shows a code to enter in your Jellyfin session and polls until you approve,
  then exchanges it for an access token. Emby keeps the manual API key (it has no
  comparable flow), and pasting a token by hand still works for all three.
- Plex ties an issued token to a client identifier, so Optimisarr generates a
  stable one once (`connect.plexClientIdentifier`) and reuses it across the
  create-PIN and poll steps and across restarts. The JSON parsing for both flows
  (`PlexPinParser`, `JellyfinQuickConnectParser`) is pure and unit tested; the
  `/api/connect/*` endpoints report a provider that can't be reached as a 502.

### Refresh media servers after a replacement

- After a verified replacement (and after a rollback), Optimisarr now asks the
  connected media servers to re-scan the changed title, so Plex/Jellyfin/Emby pick
  up the new file without waiting for their own scheduled scan. It reuses the same
  server connections configured for activity-pause — configure each server once.
- A new **Refresh after replacements** toggle on each watcher (default on) controls
  this; the watcher list shows a "refresh" badge for enabled targets. Plex is asked
  to refresh its sections (`/library/sections/all/refresh`); Jellyfin and Emby are
  notified of the changed folder via `/Library/Media/Updated` so the scan is
  targeted. The request shaping is a pure, unit-tested `LibraryRefreshRequestBuilder`.
- The refresh is strictly **best-effort**: it runs only after the replacement is
  already safely committed, and a server that is offline or rejects the call is
  logged and ignored — it can never affect the replacement's outcome or safety.

### Pause processing while a media server is streaming

- New **activity watchers** let Optimisarr pause the queue while Plex, Jellyfin, or
  Emby has active playback, so a transcode never competes with someone's stream.
  Running jobs are never interrupted — the gate only decides whether *new* jobs may
  start, alongside the existing processing-window and free-disk gates, and the
  pause reason ("Paused while Living room Plex is active (1 stream)") shows on the
  Queue page.
- Watchers are a dedicated table with CRUD on the Settings page (type, base URL,
  token/API key, enabled). Tokens are write-only — they are stored but never
  returned to the browser, and a blank token on edit keeps the stored secret. Plex
  is polled via `/status/sessions` (XML) and Jellyfin/Emby via the shared
  MediaBrowser `/Sessions` (JSON); a session counts only when it is actually
  playing something.
- The parsing (`PlexSessionsParser`, `JellyfinSessionsParser`) and the pause
  decision (`ActivityPauseEvaluator`) are pure and unit tested. An **unreachable**
  server is treated as not-active on purpose, so one offline server or a stale
  token can never wedge the queue. `ActivityMonitor` caches results briefly so the
  dispatch loop and status endpoint share one set of HTTP calls.

### Quarantine retention: purge old originals

- The `replacement.quarantineRetentionDays` setting is now enforced. A background
  worker sweeps quarantine on startup and every six hours, and once a replaced
  original has sat in quarantine longer than the configured window it is deleted
  and its replacement is marked **Purged** (recorded with a `PurgedAt` timestamp,
  added by a new additive migration). The default of `0` keeps originals
  indefinitely, so nothing is ever purged until a retention window is set.
- The decision of which originals have expired is a pure, unit-tested
  `QuarantineRetentionEvaluator`; the `QuarantinePurgeService` only ever touches
  `Replaced` rows and is best-effort about a file that is already gone, so a
  failed delete never aborts the sweep. Purging deliberately discards the rollback
  path — the Quarantine page shows a Purged badge, drops the Roll back action, and
  notes that purged originals can no longer be restored.

### Live queue progress over SignalR

- The Queue page now subscribes to the jobs SignalR hub instead of only polling:
  the server pushes live transcode telemetry and the UI updates instantly (a slow
  poll remains as a safety net and to refresh free-disk/running counts).
- Transcoding rows show a determinate bar with **encode speed and ETA**, parsed
  from FFmpeg's stderr by a new pure, unit-tested `FfmpegProgressParser`. The
  probing and verifying phases show an indeterminate sweep, and queued rows read
  "waiting" — every phase now communicates state instead of a bare dash.
- Added a `jobProgress` hub event carrying `{ progress, fps, speed, etaSeconds }`
  so telemetry streams without a database round-trip; `job.Progress` is still
  persisted for durability.

### UI: consistent control primitives

- Polished and unified the form controls into a small, consistent set: every
  button, input, select, and checkbox now shares one emerald focus ring, selects
  draw a custom chevron instead of the raw OS widget, and a new `Toggle` switch
  replaces bare checkboxes for on/off feature settings (with the whole row
  clickable and keyboard-operable). Added a `.btn-ghost` variant, replacing the
  ad-hoc `!border-0 !bg-transparent` overrides in the sidebar and folder picker.
- Simplified the Settings page: the two duplicate Save buttons became a single
  Save action, and Replacement moved out of the Verification card into its own
  section so each card covers one concern.

### Safe replacement: cross-filesystem guard

- Replacement now refuses by default to fall back to a cross-filesystem
  copy-plus-delete, since the atomic rename is what makes quarantine-then-replace
  safe. A new `replacement.allowCrossFilesystem` setting opts into the fallback
  for intentional split-mount layouts, and `replacement.quarantineRetentionDays`
  records how long quarantined originals are kept (`0` = indefinitely). Both are
  stored in `AppSettings` (no migration) and exposed on the Settings page.

### Phase 6: scheduling and resource controls

- Added queue dispatch policy controls for processing windows and free disk
  safety. The worker now checks the configured local processing window and the
  free space on the work filesystem before starting new jobs; running jobs are
  not interrupted by these gates.
- Expanded global settings with `scheduleEnabled`, `scheduleWindowStart`,
  `scheduleWindowEnd`, and `minFreeDiskBytes`. These are stored in the existing
  `AppSettings` table, so no schema migration is required.
- Added `GET /api/queue/status`, exposing whether dispatch can currently start
  work, the blocked reason, running/max jobs, work root, and measured free disk
  space.
- Added a global CPU thread limit setting, wired into generated FFmpeg arguments
  as `-threads`; `0` leaves thread selection to FFmpeg.
- Added hardware capability detection for FFmpeg hardware accelerators, known
  CPU/NVENC/QSV/VAAPI encoders, NVIDIA runtime availability, and `/dev/dri`
  mapping. Surfaced through `GET /api/system/hardware` and the Tools page.
- Added global encoder mode selection (`Auto`, `CPU`, `NVIDIA NVENC`, `Intel QSV`,
  `VAAPI`). The worker validates the requested mode against detected FFmpeg
  encoders before starting a job and uses the selected encoder in generated
  FFmpeg arguments.
- Added configurable verification policy settings for duration tolerance, audio
  retention, subtitle retention, and required size reduction. Decode health,
  output readability, and video-stream presence remain mandatory safety gates.
- UI: the Settings page now edits the processing window and minimum free work
  disk threshold plus encoder mode, CPU thread limit, and verification gates; the
  Queue page shows whether dispatch is ready or paused.
- Added tests for the new settings defaults, persistence, and invalid-value
  fallback, building on the pure `DispatchPolicyEvaluator` tests.
- Fixed hardware encoders being reported as available purely because FFmpeg was
  built with them, which made `Auto` mode pick `hevc_nvenc` on a host with no
  usable GPU and fail every job immediately (`Cannot load libcuda.so.1`). A
  hardware encoder is now only available when the hardware is actually usable:
  NVENC requires a working NVIDIA runtime and QSV/VAAPI require a DRI render
  device, so `Auto` correctly falls back to CPU when no GPU is present.

### Phase 5: safe replacement and rollback

- A verified `ReadyToReplace` job can now replace its original — the first
  destructive action in Optimisarr, and a fully reversible one. The original is
  moved to **quarantine** (`/trash`) *first*, then the verified output is moved
  into the original's place; the move is recorded as a `Replacement` so it can be
  rolled back. If any step fails, the original is restored from quarantine, so a
  failure never loses data.
- The output takes the original's directory and base name but the output's
  extension (a transcode may change container, e.g. `.avi` → `.mkv`), computed by
  the pure, unit-tested `ReplacementPlanner`. Quarantine paths are timestamped so
  same-named files never collide and rollback always has a unique source.
- Cross-filesystem aware: an atomic rename is used when source and destination
  share a filesystem; otherwise a **verified copy-plus-delete** runs (the source is
  removed only after the copy exists and its size matches). The fallback is
  recorded and surfaced in the UI as a "copied" badge.
- After replacement a final-path integrity check confirms the placed file matches
  the verified output, and the inventory is re-probed from the file now at the
  original's location. The job moves to `Completed`.
- **Rollback** restores the original from quarantine and removes the replacement
  output; the `Replacement` is marked `RolledBack`. Originals are retained in
  quarantine (never auto-purged) so rollback stays available.
- Added the `Replacement` entity (`AddReplacements` migration) and endpoints:
  `POST /api/jobs/{id}/replace`, `GET /api/replacements`,
  `POST /api/replacements/{id}/rollback`.
- UI: the **Quarantine** page (nav enabled) lists replacements with size saving,
  cross-filesystem indicator, and a roll-back action; the **Queue** page gained a
  **Replace** action on verified `ReadyToReplace` jobs.
- The compose example documents putting `/trash` on the same filesystem as `/data`
  so replacement can use an atomic move.

### Phase 4: verification

- A clean ffmpeg exit no longer sends a job straight to `ReadyToReplace`. The
  worker now runs a real **`Verifying`** step first: a full software-decode health
  check (`ffmpeg -v error -xerror -i <output> -f null -`, via the new
  `DecodeHealthCheck`), an ffprobe of the output, and a comparison against the
  original. Only a passing report advances the job toward replacement; a failing
  report marks the job `Failed` with the failed gate names, **retaining the output
  for inspection**. The original is never touched either way.
- Added a pure, fully unit-tested `VerificationEvaluator` (in `Optimisarr.Core`)
  that turns gathered evidence into a `VerificationReport` of per-check outcomes:
  decode health, output readable, video stream present, duration within tolerance,
  audio retention, subtitle retention, and size saving. Thresholds live in a
  conservative `VerificationPolicy.Default` (1% duration tolerance, audio must be
  retained, output must be smaller; subtitle retention off by default).
- Added `VerificationService` (the only place verification touches FFmpeg/disk),
  which gathers the decode + probe results and hands them to the pure evaluator.
- Persisted the result on the job: `OutputSizeBytes`, `VerificationPassed`,
  `VerificationReportJson`, and `VerifiedAt` (`AddJobVerification` migration,
  additive and backwards-safe). Surfaced through `GET /api/jobs`.
- UI: the **Queue** page gained a Verification column showing pass/fail and the
  output size, expandable to the full per-check report with reasons.

### Phase 3: transcode queue (worker)

- Added `JobEnqueueService` + `POST /api/libraries/{id}/enqueue`, which turns a
  library's eligible candidates into queued jobs. Idempotent: a media file with an
  active (non-terminal) job is never enqueued twice.
- Added `QueueDispatcher`, a `BackgroundService` that drives the queue: a single
  loop owns all job-state writes (SQLite has one writer), selects work via the
  pure `JobScheduler` up to the global `maxConcurrentJobs`, and runs ffmpeg
  out-of-process through an argument list (never a shell) with a `CancellationToken`
  and captured output. Progress is parsed from ffmpeg and pushed live over SignalR
  (`JobsHub`). A job only ever writes to the work directory; the original is never
  touched (successful jobs land in `ReadyToReplace`, pending safe replacement).
- Crash recovery: on startup, jobs left mid-flight are reset to Queued (or Failed
  after too many attempts) and their partial outputs cleaned up.
- Added `GET /api/jobs` and `POST /api/jobs/{id}/cancel` (cancelling stops the
  running ffmpeg and marks the job Cancelled).
- UI: new **Queue** page (nav enabled) listing jobs with status, a live progress
  bar for the running transcode, and cancel; it polls while open. Library cards
  gained an **Enqueue** action that queues the library's eligible files.
- Per-library **move-on-complete**: a library can opt to move a finished output
  to a target folder (with the `AddLibraryMoveOnComplete` migration and a pure,
  unit-tested `MoveTarget` resolver). The original is still never touched; this
  only relocates our own work output, falling back to copy+delete across
  filesystems. Off by default — outputs stay in the work directory as
  `ReadyToReplace`. The library form gained a "Completed output" toggle and a
  target-folder picker.
- Fixed `GET /api/jobs` returning HTTP 500 (the Queue page failed to load):
  SQLite cannot translate an `ORDER BY` over the `DateTimeOffset` `EnqueuedAt`
  column, so the priority/enqueue ordering is now applied client-side after
  materialisation. Extracted into `JobQueries.ListAsync` with a regression test.

### Phase 3: transcode queue (foundation)

- Added the `Job` entity and `JobStatus` state machine (Queued → Probing →
  Transcoding → Verifying → ReadyToReplace → Completed, plus Failed/Cancelled),
  with the `AddJobs` migration and indexes for the scheduler's queries. A job
  never touches the original; it only produces an output under `/work`.
- Added a pure, unit-tested `JobScheduler` that selects which queued jobs to start
  given the running count and global concurrency limit, ordered priority-desc then
  FIFO (enqueue time, then id).
- Added a pure, unit-tested `FfmpegCommandBuilder` that emits an ffmpeg argument
  list (never a shell string): codec→encoder mapping (libx265/libx264/libsvtav1),
  CRF and preset, remux-only (`-c copy`), HDR→SDR tone-map filter, `-map 0`, and
  audio/subtitle passthrough.
- Added a pure, unit-tested `TranscodeSpecResolver` that builds a `TranscodeSpec`
  from a library's resolved rules and a media file (work-root output path, target
  codec/container, and tone-map only when re-encoding an HDR source that the
  library asks to tone-map).
- Added per-library encoder settings (`QualityCrf`, `EncoderPreset`) via the
  `AddLibraryEncoderSettings` migration, surfaced in the library config card.
- Polished the library config card: free-text fields are now dropdowns (target
  codec, container, encoder preset, HDR handling, resolution limit, priority),
  quality is a CRF slider with an "encoder default" toggle, and the form is
  grouped into "Target output" and "Eligibility & queue" sections.
  `GET /api/library-options` now also returns the codec/container/preset vocab.

### Per-library configuration and global queue settings

- Each library can now override how it is optimised, on top of its rule-profile
  preset: target video codec, target container, HDR handling, minimum file size,
  maximum resolution (height), path exclusions, and a queue priority. Overrides
  are resolved by a new pure `RuleResolver` (profile defaults + non-null
  overrides), kept fully unit tested.
- Reworked HDR handling from a boolean into an `HdrHandling` enum
  (`Preserve` / `Exclude` / `TonemapToSdr`); `Exclude` remains the safe default
  for the conservative and compatibility profiles. `RuleSettings` also gained an
  explicit `TargetContainer` (replacing the hard-coded matroska keyword set).
- New `Library` columns (`Priority`, `MinFileSizeBytes`, `MaxHeight`,
  `TargetVideoCodec`, `TargetContainer`, `HdrHandling`, `ExcludePaths`) via the
  `AddLibraryRuleOverrides` migration; all nullable/defaulted, so applying it to
  an existing database is non-destructive.
- `GET /api/candidates` now evaluates each file against its library's resolved
  rules (including overrides). `GET /api/library-options` also returns the HDR
  handling vocabulary.
- New global settings: `GET`/`PUT /api/settings` with `maxConcurrentJobs`
  (default 1), stored via `SettingsStore`. This is the global concurrency limit
  the upcoming transcode queue will honour.
- UI: the Libraries page is now Add-button + expandable cards — each card expands
  to a handling form (preset, target codec/container, HDR, size/resolution
  limits, path exclusions, priority). New global **Settings** page for the
  concurrency limit; the Settings nav item is now enabled.

### Phase 2: candidate rules

- Added a pure, fully unit-tested eligibility engine in `Optimisarr.Core`
  (`CandidateEvaluator`) that decides whether a probed file should be optimised
  under a rule profile — without ever running FFmpeg. Every decision carries a
  human-readable reason, so the UI can always explain why a file is eligible or
  skipped. Checks: no video stream, path exclusions, HDR/Dolby Vision exclusion,
  minimum file size, resolution limit, already-target-codec, and (for
  remux/cleanup) already-clean container.
- Each `RuleProfile` resolves to concrete `RuleSettings` via `RuleProfileDefaults`
  (HEVC/H264/AV1 targets, remux-only has no target codec; conservative profiles
  exclude HDR by default).
- Moved the domain enums `MediaType` and `RuleProfile` from `Optimisarr.Data` to
  `Optimisarr.Core` (`Optimisarr.Core.Domain`) so rules logic owns its vocabulary.
  Stored as strings, so no migration was required.
- Probing now detects HDR/HLG (transfer characteristics) and Dolby Vision (stream
  side data); persisted via the new `MediaFile.IsHdr` column
  (`AddMediaFileIsHdr` migration, non-destructive default `false`).
- New endpoint `GET /api/candidates` (optional `libraryId` filter) backed by
  `CandidateService`, which evaluates each probed file against its library's
  profile. New **Candidates** page in the UI with all/eligible/skipped filters
  and a per-file reason column.

### Continuous integration (Node 24)

- Opted CI into Node 24 for JavaScript-based actions
  (`FORCE_JAVASCRIPT_ACTIONS_TO_NODE24`) ahead of GitHub forcing it on
  2026-06-16, and verified the pipeline passes on Node 24.

### Multiple libraries with per-library rules

- Replaced the single `library.root` setting with a first-class `Library`
  entity: each library has a name, path, media type (Film/Tv/Music/Other) and
  rule profile (ConservativeHevc/CompatibilityH264/ExperimentalAv1/RemuxCleanup),
  so different content can be optimised by different rules. Rule enforcement
  lands in Phase 2; profiles are stored per library now.
- `MediaFile` now belongs to a `Library` (nullable FK, cascade delete). The
  `LibraryId` is nullable so the schema applies to an existing database without
  a foreign-key violation; scans always set it.
- Added the `AddLibraries` migration and an idempotent startup seeder that
  migrates any pre-existing `library.root` into a default library and relinks
  already-discovered files to it.
- New endpoints: `GET /api/libraries`, `POST /api/libraries`,
  `PUT/DELETE /api/libraries/{id}`, `POST /api/libraries/{id}/scan`,
  `POST /api/libraries/scan` (all enabled), `GET /api/library-options`, and
  `GET /api/fs/browse` for directory browsing. `GET /api/media` accepts an
  optional `libraryId` filter.
- Added tests for library-scoped scanning, idempotency, and cascade delete.

### Web UI redesign (yawamf-style sidebar)

- Rebuilt the frontend around a collapsible sidebar shell with dark mode and
  hash-based routing (Tailwind CSS v4 via the Vite plugin).
- Pages: Dashboard, Libraries (add/edit/scan/delete), Inventory (per-library
  filter + probe), Tools. Queue/Verification/Quarantine/Schedule/Settings appear
  as disabled "soon" items to show the roadmap.
- Library paths are chosen with a folder-picker dialog (backed by
  `GET /api/fs/browse`) instead of free-text entry.

### Continuous integration

- Added `.github/workflows/ci.yml`: backend build (`-warnaserror`) and tests,
  frontend `npm run check`, and a Docker image build.
- The Docker job publishes to GHCR as `ghcr.io/jellman86/optimisarr` using the
  built-in `GITHUB_TOKEN` (no PAT required): `dev` -> `:dev`, `main` ->
  `:main` and `:latest`, and `vX.Y.Z` tags -> semver tags. Pull requests build
  the image but never push.
- Fixed the `Dockerfile` to copy `Optimisarr.slnx` (the repo uses the `.slnx`
  solution format); it previously referenced a non-existent `Optimisarr.sln`,
  which would have failed the image build.
- Codified the CI/CD process and image-tagging rules in `CLAUDE.md` (§9).
- Added `curl` to the runtime image so container healthchecks (e.g.
  `GET /api/health`) work out of the box.
- Fixed the container failing to start with `exec: "/entrypoint.sh":
  permission denied`: `entrypoint.sh` was tracked as non-executable, so the
  copied file had no execute bit. Copy it with `--chmod=0755` and mark it
  executable in git.

### Engineering standards

- Added `CLAUDE.md` as the authoritative engineering standard for the repo
  (safety-first, test-driven development, database excellence and idempotency,
  self-documenting code, clean operational UI, definition of done, local
  commands, and project layout).
- Added `AGENTS.md` pointing all agents and contributors to `CLAUDE.md`.

### Phase 1: media discovery and inventory (first slice)

- Switched startup database creation from `EnsureCreated` to EF Core migrations
  (`Database.MigrateAsync`) for versioned, idempotent schema management, and
  added the `InitialCreate` migration.
- Added the `MediaFile` entity and `DbSet` with a unique index on `Path`,
  enum-as-string status, and probe-result columns.
- Added `SettingKeys` and a single configurable library root stored in
  `AppSettings`.
- Added `LibraryScanner` (Core): recursive, settling-aware media discovery with
  extension filtering; pure and unit tested.
- Added `MediaProbeService` (Core): ffprobe JSON inspection via an explicit
  argument list, with a unit-tested parser.
- Added `SettingsStore` and `LibraryInventoryService` (Api) for idempotent scan
  upserts and probe persistence.
- Added endpoints: `GET /api/settings`, `PUT /api/settings/library-root`,
  `POST /api/library/scan`, `GET /api/media`, `POST /api/media/{id}/probe`.
- Added a Library/Inventory panel to the Svelte UI: set library root, scan, view
  discovered media, and probe individual files.
- Added xUnit tests for the scanner and ffprobe parser (6 tests, all passing).

### Project setup

- Created the `Jellman86/optimisarr` GitHub repository.
- Created and pushed the `dev` branch.
- Added initial planning documents:
  - `docs/product-and-architecture.md`
  - `docs/roadmap.md`
- Chose the implementation stack:
  - C# / ASP.NET Core on .NET 10 LTS for the backend and worker host.
  - Svelte 5 + TypeScript + Vite for the frontend.
  - SQLite under `/config` for local state.
  - FFmpeg and ffprobe as external media tools.
  - SignalR reserved for live job and queue updates.
- Installed .NET SDK `10.0.300` locally under `/tmp/dotnet` for this session
  because WSL did not have `dotnet` installed globally.

### Backend scaffold

- Added `Optimisarr.sln`.
- Added .NET 10 projects:
  - `src/Optimisarr.Api`
  - `src/Optimisarr.Core`
  - `src/Optimisarr.Data`
  - `tests/Optimisarr.Tests`
- Added EF Core SQLite package references.
- Added `OptimisarrDbContext` with an initial `AppSetting` table.
- Added startup database creation for `/config/optimisarr.db` or local
  `config/optimisarr.db` outside Docker.
- Replaced template weather endpoint with:
  - `GET /api/health`
  - `GET /api/system/tools`
- Added FFmpeg/ffprobe detection through `ToolDetectionService`.
- Added an empty SignalR `JobsHub` at `/hubs/jobs`.

### Frontend scaffold

- Added Svelte 5 TypeScript app under `web`.
- Replaced the Vite starter page with an Optimisarr status dashboard.
- The dashboard calls:
  - `/api/health`
  - `/api/system/tools`
- Configured Vite to build static assets into
  `src/Optimisarr.Api/wwwroot` so ASP.NET Core can serve the UI.

### Docker scaffold

- Added `.env.example`.
- Added `compose.example.yml` using media-stack style mounts:
  - `/config`
  - `/data`
  - `/work`
  - `/trash`
- Added optional `/dev/dri` mapping for Intel/AMD VAAPI/QSV.
- Documented the NVIDIA Compose GPU reservation block.
- Added a multi-stage Dockerfile:
  - Node 24 builds the Svelte UI.
  - .NET 10 SDK publishes the API.
  - .NET 10 ASP.NET runtime image runs the app.
  - Runtime image installs FFmpeg, ffprobe, gosu, and timezone data.
- Added `docker/entrypoint.sh` with `PUID`, `PGID`, and `UMASK` support.

### Current verification status

- `dotnet build Optimisarr.slnx` succeeds with zero warnings.
- `dotnet test Optimisarr.slnx` is green (6 tests passing).
- `npm run check` is clean (zero errors, zero warnings).
