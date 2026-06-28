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

   - **Pipeline robustness pass: done.** The behaviour that carries product risk is now
     covered by adversarial tests, and every known live failure class is represented.
     `FfmpegCommandBuilder` stream/container permutations — attachments, data streams,
     cover art, image-based subtitles (the MP4→MKV fallback that avoids the `mov_text`
     trap), audio-only, still image, HDR tone-map, remux, and MP4/MKV — are tested.
     Replacement/reconcile state transitions are tested end to end: missing source,
     missing work output, destination occupied by a different file, concurrent replace
     callers (the job 3327 corruption), dry-run, rollback after a partial mid-move
     failure, rollback when the quarantined original is gone, and the cross-filesystem
     fallback. Candidate decisions are tested for already-optimised siblings and
     marker-tagged files, already-efficient sources, repeated failures (auto-exclude and
     optimisation history), path and HDR/resolution exclusions, and Sonarr/Radarr
     import-aware holds.

   - **Endpoint modularization: done.** All 72 endpoints are extracted into nine
     `src/Optimisarr.Api/Endpoints/*.cs` extension methods (settings, integration, exclusion, health,
     system, library, stats, replacement, media/queue), leaving `Program.cs` a 418-line composition
     root (down from 1,960). A pure move verified by the byte-identical generated OpenAPI document and
     the full test suite; the few endpoints that need startup locals (`adminToken`, `configDirectory`)
     take them as parameters, and the range-aware file server is a shared `FileServing` helper.

   - **Large-library API scalability.** `/api/jobs` and `/api/media` now have server-side
     filtering and pagination (`status`, `search`/`category`, date, `page`/`pageSize`, total in
     `X-Total-Count`), with a `(LibraryId, RelativePath)` index so a large inventory pages without a
     table sort. The Inventory page now drives its filter chips, counts, and pager from a combined
     `GET /api/inventory` (media paired with rule verdict, filtered/counted/paged server-side), so the
     browser fetches one page instead of every row and every candidate. The Queue table also pages
     100 rows at a time client-side so a large queue stays responsive, and the shared candidate table
     (the fleet-wide Candidates page and the per-library Candidates tab) now pages the same way, so a
     large library's candidate list renders one page at a time. Done.

   - **Diagnostics bundle and admin health details.** Shipped as `GET /api/diagnostics` (admin-only):
     version, environment, settings, per-library and integration summaries, dashboard stats, and the
     failure summary, assembled from non-secret data only (a single pure redaction step keeps provider
     tokens, API keys, and webhook URLs out; verified against real data). `/api/ready` stays small and
     orchestration-friendly. The bundle also carries tool (FFmpeg/ffprobe) and hardware-encoder
     capability and the most recent captured ffmpeg logs, so it is self-contained for a support
     ticket. Done.

   - **Hardware validation matrix.** Create a maintained matrix that records CPU,
     NVIDIA NVENC, Intel QSV, VA-API, hardware decode, and GPU metrics validation by
     platform, with date, evidence, and known limits. AMD VA-API remains the important
     open validation target. The matrix should distinguish "implemented and unit-tested"
     from "validated on real hardware."

   - **Roadmap/docs split: done.** The dense recently-shipped log, current status, and per-phase
     implementation detail moved to [`engineering/history.md`](engineering/history.md), so this
     roadmap answers "what is next?" while the engineering notes answer "what exactly changed and
     why?".

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


## Guiding principles

- Safety beats savings.
- No original file is deleted until verification has passed.
- Every destructive action must have a rollback path.
- Defaults should be conservative and understandable.
- The app should feel familiar to Docker media-stack users.
- One container should be enough for normal use.


## Engineering history and phase detail

The dated record of what has shipped, the per-phase implementation plan, and the current
phase-by-phase status now lives in [`engineering/history.md`](engineering/history.md), to keep this
roadmap focused on what is next.
