# Optimisarr Roadmap

This roadmap is intentionally implementation-focused. The goal is to build a
small, reliable core first, then widen codec, GPU, and automation support once
the replacement workflow is trustworthy.

## Up next (priority order, updated 2026-07-12)

1. **Phase 14 gold-standard hardening** — the next maturity pass is about making
   Optimisarr safer to expose, easier to automate, and easier to change without
   weakening the transcode → verify → replace pipeline. This phase is grounded in
   the project review at
   [`docs/reviews/2026-06-27-project-quality-and-gold-standard-review.md`](reviews/2026-06-27-project-quality-and-gold-standard-review.md)
   and its peer response. Most of the phase has shipped — admin-token auth (with
   end-to-end coverage), the self-describing CI-checked OpenAPI contract, the pipeline
   robustness pass, endpoint modularization, large-library paging, the diagnostics
   bundle, and the roadmap/docs split are all done. The **hardware validation matrix**
   below is the remaining open item, gated on access to non-Intel GPUs.

   - **Optional admin-token auth: done.** `OPTIMISARR_ADMIN_TOKEN`
     now gates the administrative API and SignalR hub with bearer-token authentication
     when set. The static SPA shell remains public so it can show a token prompt; useful
     API calls are blocked until the token is supplied. `/api/health`, `/api/ready`, and
     `/api/auth/status` stay open for health checks and discovery. Token comparison uses
     constant-time comparison over fixed-size token hashes, the UI stores the token
     locally and sends it on API, hub, and media-preview requests, and the deployment
     docs keep reverse-proxy authentication as the preferred public-access boundary.
     The full destructive/secret-bearing endpoint set — settings read/save/export/import,
     library create/delete/enqueue, job clear/cancel/retry/remove/replace, replacement
     rollback/approve, and the diagnostics bundle — is now covered by end-to-end tests
     that boot the real host with the token set and prove each is rejected with `401`
     without it, that the open endpoints stay reachable, and that a valid token passes.

   - **Generated, CI-checked OpenAPI contract: done.** The
     runtime OpenAPI 3.1 document is generated from the app into `docs/openapi.json`,
     and CI fails when the checked-in document drifts from the running API. The docs
     checker also verifies every path/method listed in `docs/api.md` exists in the
     generated spec. The document is now self-describing too: a titled/versioned/described
     info block, every operation grouped under an area tag (System, Settings, Libraries,
     Inventory, Queue, Replacements, Integrations, Realtime), and a documented `401` on
     every admin-token-protected operation (the three open endpoints correctly have none),
     produced by a single document transformer over a pure route→tag/protection mapping.

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
     import-aware holds. The dispatcher also re-checks a queued job against the current
     rules immediately before it transcodes, so a job that became ineligible while sitting
     in a long backlog (e.g. an already-efficient source enqueued before the floor existed)
     is skipped — marked `Cancelled` with the reason — rather than wasting an encode the
     size-saving gate would only reject. The skip is a soft, reversible rule decision, not a
     blacklist entry, so the file becomes eligible again automatically if the profile changes.

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

   - **Edge-case hardening (from upstream research).** A pass grounded in real failure modes that
     Tdarr/Unmanic/HandBrake and ffmpeg users hit, prioritised by product risk. In order:

     1. **Dolby Vision — the one safety gap: done.** The probe now flags DV distinctly (DOVI side-data
        or a `dvhe`/`dvh1`/`dav1` codec tag) and the candidate evaluator skips DV sources regardless of
        the HDR setting unless a per-library **Optimise Dolby Vision** opt-in is enabled (off by
        default, settable in the library form, preserved across config backup). This closes the gap
        where a DV Profile 5 source could be re-encoded to green/pink and still pass verification with
        VMAF off. Migration `AddDolbyVisionHandling`. (HandBrake #5597, FFmpeg HDR/DoVi notes.)
     2. **VFR → A/V-sync drift in MP4: done.** A video re-encode to an MP4-family container now forces
        a constant frame rate (`-fps_mode cfr`), the documented fix for the MP4/MOV-only VFR drift that
        caused the live job 3334 failure; Matroska carries VFR natively and is left untouched, and a
        remux is never re-timed. The A/V-sync gate remains the backstop for any residual start-offset
        cases. (VideoHelp, EncodeX.)
     3. **MP4 + MP4-incompatible audio copied → mux failure: done.** The resolver now falls back
        MP4→MKV when a source carries audio MP4 cannot mux (Dolby TrueHD, Blu-ray/DVD LPCM) and that
        audio is being copied rather than re-encoded to a compatible codec — the same pattern already
        used for image-based subtitles. (Unmanic #454.)
     4. **Robustness polish: partly done.** Done: every video job regenerates presentation timestamps
        (`-fflags +genpts`) so a source with missing/non-monotonic DTS muxes cleanly; and a hardware
        encode now drops data streams (timecode/GPMF) even for a Matroska output (Tdarr's `-dn` fix
        generalised). Deferred as speculative without hardware to reproduce against: a classified
        NVENC session-limit error (low risk — concurrency defaults to 1), and a single transient-retry
        of the encode on known-transient NVENC/QSV errors. (Tdarr #613/#729, IPCamTalk.)

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

4. **Full translation parity with YA-WAMF** — internationalise the Optimisarr UI and
   user-facing API/status strings, then provide complete translations for the same language
   set currently carried by YA-WAMF: English, German, Spanish, French, Italian, Japanese,
   Portuguese, Russian, and Chinese (`en`, `de`, `es`, `fr`, `it`, `ja`, `pt`, `ru`, `zh`).

   - **Foundation and completeness gate: done.** A typed locale system under `web/src/lib/i18n/`
     with English as the source of truth (`type Messages = typeof en`) and every other locale typed
     against it, so a missing or misspelled key fails `npm run check` — translation completeness is a
     compile-time CI gate, not something that can silently rot. A Svelte 5 runes store exposes the
     active locale's messages with a `t()` interpolation helper and a `plural()` helper; the choice
     is persisted to `localStorage` and falls back to the browser language, then English. A language
     selector lives in the sidebar footer.
   - **Every page migrated: done.** The app shell and navigation, plus Dashboard, Schedule,
     Quarantine, Inventory, Queue, Settings, and Libraries — including confirm dialogs, InfoTip help
     text, preset/profile/HDR label maps, empty states, and count-aware/pluralised strings. **German**
     ships complete across all of it as the first translated locale.
   - **Shared components: done.** `MediaCompare`, `FolderPicker`, `BottomSheet`,
     `FailuresPanel`, `ToolsPanel`, and `CandidateTable` now use the typed locale contract.
     `VerificationChecks` owns no prose (its names/details come from the backend), and
     `PreviewCompare` now translates its progress, errors, safety copy, comparison labels, stats,
     and verification summary too.
   - **Backend-originated UI messages: done for the shipped locales.** JSON endpoint failures now
     carry stable machine-readable codes plus an English compatibility fallback; the web client
     resolves those codes through the same typed locale contract. This covers settings and query
     validation, filesystem/library/media/job errors, exclusions, replacements, and integrations.
     Persisted job failure categories likewise drive translated queue and diagnostics summaries,
     while raw encoder/backend output remains available only as explicitly labelled technical
     detail. **Spanish is now complete** across the same 832-message typed contract, using
     YA-WAMF's Spanish terminology as the vocabulary reference. CI also audits interpolation
     placeholders across every translation so tokens such as `{count}` and `{path}` cannot be
     dropped silently. Remaining: populate the remaining six locales (`fr`, `it`, `ja`, `pt`, `ru`,
     `zh`) toward parity — ideally with native review — so the feature is genuinely
     full translation rather than navigation-only localisation.

5. **Packaging, app-store templates, and discovery** — make Optimisarr easy to find and
   install where Docker media-stack users already look. Keep the Docker contract stable and
   make every template expose the same core surface: image tag, web port, admin token,
   `/config`, work/output, quarantine, media-library paths, health check, optional hardware
   acceleration, update-channel guidance, and a smoke-test checklist.

   - **Unraid Community Applications template: template shipped; discovery listing remains.**
     `unraid/optimisarr.xml` (volume mappings for config, media, work, and quarantine, the `8787`
     port, optional `OPTIMISARR_ADMIN_TOKEN`, PUID/PGID/UMASK, and an optional `/dev/dri` device),
     a repository-root `ca_profile.xml`, and `docs/setup/unraid.md` are shipped. Remaining: promote
     the template + profile to the default branch at release and pursue the Community Applications
     discovery listing so users don't need to paste the raw template URL.

   - **TrueNAS custom-app docs, then catalog submission.** First document the low-friction
     TrueNAS Custom App path so users can deploy the existing container before catalog
     acceptance. Then submit a community-train app to `truenas/apps` with the expected
     Docker Compose catalog files: `app.yaml`, `ix_values.yaml`, `questions.yaml`, a Jinja2
     `templates/docker-compose.yaml`, `README.md`, and `templates/test_values/basic-values.yaml`.
     The TrueNAS wizard should expose the web port, private admin token, storage mappings,
     optional GPU/device settings, CPU/memory limits, a portal link, and a health check against
     `/api/ready` or `/api/health`. Validate with the TrueNAS apps CI render/deploy workflow
     before opening the PR, and open a draft PR early to catch catalog-review issues.

   - **Container registry discoverability.** Continue publishing GHCR images and add Docker Hub
     publishing once release tags are stable, with matching descriptions, labels, README text,
     supported architectures, and examples. Keep image names, environment variables, health
     checks, and permissions in sync across docs and templates.

   - **Additional self-hosting templates.** Add a Portainer app-template/stack example and
     evaluate CasaOS/ZimaOS packaging after the Unraid and TrueNAS paths settle. Consider YunoHost
     only if the install and upgrade model can be made appliance-like enough to avoid fragile
     maintenance.

   - **Project directories and community launch points.** Submit or announce Optimisarr in the
     places media-stack and self-hosting users already discover tools: Awesome Selfhosted,
     selfh.st/apps, AlternativeTo, the Unraid and TrueNAS forums, and relevant communities such
     as r/selfhosted, r/unRAID, r/truenas, r/radarr, and r/sonarr. Prioritise a short, honest
     positioning statement, screenshots, quickstart links, and clear safety guarantees over
     generic promotion.


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
