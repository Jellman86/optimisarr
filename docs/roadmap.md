# Optimisarr Roadmap

This roadmap is intentionally implementation-focused. The goal is to build a
small, reliable core first, then widen codec, GPU, and automation support once
the replacement workflow is trustworthy.

## Current status (2026-06-07)

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
  `GET /api/candidates` and a Candidates page. Still to come: per-library
  include/exclude path rules editable in the UI, and a measured minimum-saving
  estimate (today's proxy is "already in the target codec").
- **Next: Phase 3 (Queue and Worker)** — a robust transcode queue feeding from
  these candidates, with a global concurrency limit plus per-library priority,
  priority-then-FIFO scheduling, and crash-safe recovery.

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

- Processing windows.
- Max concurrent jobs, initially defaulting to 1.
- CPU thread limits.
- GPU/CPU worker mode selection.
- Pause while disk free space is below threshold.
- Pause while configured services are active, optional later.
- Per-library priority.

Exit criteria:

- A user can restrict processing to overnight windows.
- The app stops queueing work when disk space is unsafe.

## Phase 7: GPU Support

Goal: make hardware encoding discoverable, explicit, and testable.

Deliverables:

- Encoder capability detection:
  - CPU x264/x265
  - NVIDIA NVENC
  - Intel QSV
  - VAAPI
  - AV1 where available
- UI hardware status page.
- Compose examples:
  - NVIDIA GPU reservation
  - `/dev/dri` Intel/AMD mapping
- Encoder presets with quality/speed notes.
- Startup warnings when selected GPU mode is unavailable.

Exit criteria:

- The app can detect available encoders and prevent invalid preset selection.
- A GPU-enabled compose example is documented and tested.

## Phase 8: Library Integration

Goal: fit cleanly into existing media stacks without becoming a second media
manager.

Deliverables:

- Optional Plex/Jellyfin library refresh webhook after replacement.
- Optional Sonarr/Radarr path-aware exclusions.
- Optional notifications:
  - Apprise
  - ntfy
  - webhook
- Import/export settings.

Exit criteria:

- Replaced media can trigger downstream library refreshes.
- Integrations remain optional and disabled by default.

## Phase 9: Release Hardening

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
