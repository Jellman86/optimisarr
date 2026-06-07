# Changelog

## Unreleased

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
