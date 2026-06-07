# Changelog

## Unreleased

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
