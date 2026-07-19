# Optimisarr Roadmap

This roadmap is intentionally implementation-focused. The goal is to build a
small, reliable core first, then widen codec, GPU, and automation support once
the replacement workflow is trustworthy.

## Up next (priority order, updated 2026-07-19)

1. **Phase 14 gold-standard hardening** — the next maturity pass is about making
   Optimisarr safer to expose, easier to automate, and easier to change without
   weakening the transcode → verify → replace pipeline. This phase is grounded in
   the project review at
   [`docs/reviews/2026-06-27-project-quality-and-gold-standard-review.md`](reviews/2026-06-27-project-quality-and-gold-standard-review.md)
   and its peer response. Most of the phase has shipped — admin-token auth (with
   end-to-end coverage), the self-describing CI-checked OpenAPI contract, the pipeline
   robustness pass, endpoint modularization, large-library paging, the diagnostics
   bundle, the maintained hardware validation matrix, and the roadmap/docs split are all done.
   **Real-host AMD VA-API validation** remains gated on access to suitable hardware.

   - **Adaptive language selector: done.** The sidebar language menu now measures the available
     viewport space and opens upward or downward as appropriate, with keyboard navigation and
     accessible listbox semantics retained in either direction.

   - **Opt-in perceptual-quality (VMAF) gate with a quality slider: done.** VMAF can protect video
     re-encodes at selectable floors — each library offers Off (the default), Space-saver
     (80/60), Balanced (85/70), High (90/75), Visually lossless (93/80), and Archival (96/90). It is
     off by default because it fully decodes both files and scores every frame, roughly doubling
     verification time; while off, the structural, duration and size gates plus quarantine rollback
     still guard every replacement. Remux and non-video work skip the inapplicable extra decode, and
     existing saved choices remain unchanged. The long VMAF pass now reports real 0–100% progress in
     the queue (hero and rows), is named explicitly as the VMAF stage, and shows a live CPU-usage
     graph so the load is visible. Measurement is self-configuring: deterministic
     timebase/timestamp/range/pixel-format alignment, reference-size bicubic scaling, bounded
     threading, automatic HDTV/4K model selection, and like-for-like HDR→SDR reference tone-mapping.
     The report records the selected model and preparation. Unit tests own the exact production graph,
     while CI executes that graph at mismatched resolutions and separately proves the bundled 4K model
     and HDR-reference tone-map run inside the final image.

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

   - **Cross-media pipeline standards audit: done.** The video, music, and still-image paths were
     rechecked against the repository's fail-closed replacement standard and the shipped FFmpeg
     capabilities. Music now preserves tags/artwork or refuses an unsafe container (including the
     shipped FFmpeg's demonstrated M4A attached-picture limitation), audio bitrate
     scales with retained channels, compatibility presets include compatible AAC, images guard
     animation/alpha/bit depth, expose only the shipped encoders (JPEG/WebP), and default to SSIM
     plus metadata verification, while video timing and
     encoded signal structure are evidence-checked. Probing, decode checks, and transcoding use one
     configured FFmpeg/ffprobe pair. Final-container CI now runs real representative music, JPEG,
     WebP, CFR/VFR video, frame-aligned preview VMAF, metadata, quality, stream-structure, and
     decode assertions.

   - **Endpoint modularization: done.** API routes are extracted into focused
     `src/Optimisarr.Api/Endpoints/*.cs` extension methods, including the later Setup and Calibration
     modules, leaving `Program.cs` as the composition root. The move and subsequent modules are
     protected by the generated OpenAPI document and full test suite; endpoints that need startup
     locals take them as parameters, and shared behaviours remain in endpoint helpers.

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

   - **Hardware validation matrix: maintained; AMD evidence pending.** The public
     [matrix](setup/hardware-validation-matrix.md) records CPU, NVIDIA NVENC, Intel QSV, VA-API,
     hardware decode, VMAF, and GPU metrics by platform, with date, evidence, known limits, and a
     repeatable evidence checklist. It distinguishes "implemented and unit-tested" from "validated
     on real hardware." AMD VA-API remains the important open validation target.

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
     2. **VFR → A/V-sync drift: done, corrected by audit.** The first job-3334 workaround forced every
        MP4 re-encode to CFR, which FFmpeg implements by duplicating/dropping frames. The final policy
        persists positive VFR evidence from nominal and average probe rates, applies `-fps_mode vfr`
        with the demuxer encoder timebase only to identified VFR re-encodes, and leaves CFR/unknown
        sources and all remuxes unmodified. Relative A/V-sync, timestamps, duration, and tail checks
        remain the replacement backstops. Migration `TrackVariableFrameRate`. (FFmpeg documentation.)
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

2. **Gold-standard first-run setup wizard: complete** — turn a new, empty installation into safe,
   understandable libraries without hiding Docker-level mistakes or weakening Optimisarr's
   fail-closed defaults. This is the next independently actionable product item while the hardware
   validation matrix remains gated on access to non-Intel GPUs.

   - **Trigger, resume, and ownership: done.** A versioned `SetupState` now distinguishes
     a genuinely new database from an upgrade, persists each completed step, resumes after refresh or
     restart, accepts duplicate progress writes idempotently, and permits completion only from final
     review. Upgraded installations are marked complete and never forced into onboarding. Back retains
     applied choices, and the Settings header offers **Run setup again** without deleting configuration.
     Connections remain explicitly skipped on the final review and can be added later, so an optional
     provider can never block first use.
   - **Five stable, task-oriented steps: done.** One heading and primary action drive:
     (1) welcome, safety model, and private-network/auth exposure; (2) system readiness; (3) any
     number of libraries and their optimisation rules; (4) verification, scheduling, quarantine, and replacement
     safety; (5) review and apply. Keep integrations optional after the core path, or as a clearly
     skippable sub-step, so Plex/Jellyfin/Sonarr/Radarr availability can never block first use. A
     stable step indicator shows “step N of 5”, current/completed/pending text, `aria-current`, and
     separate Back/Continue controls—the [USWDS step-indicator guidance](https://designsystem.digital.gov/components/step-indicator/)
     recommends this pattern for linear processes with three or more high-level sections.
   - **Prove the environment instead of merely collecting fields: shipped.** The readiness
     step runs the existing tool/capability checks and non-destructive probes for `/config`, `/work`,
     `/trash`, and the chosen media root: existence, effective read/write permissions, available
     space, and whether media, work, and quarantine remain below one container mount boundary for
     atomic replacement. It detects
     encoder support with the same real test encode used by Tools. Database connectivity,
     required/optional tools, detected hardware encoders, and effective read/write access for config,
     work, quarantine, and every configured library root are visible and gate progress. Each row now
     carries free/total capacity plus filesystem, mount, type, and boundary evidence; configured
     libraries state whether work and quarantine moves are atomic. Missing, unreadable, unwritable,
     and low-space states produce exact local, Compose, Unraid, and TrueNAS recovery steps. A
     loading/announced **Re-test system** action reruns the real probes and clears resolved advice.
     The container never pretends it can create a host bind mount or change host permissions. Docker
     documents that mounts must be explicitly granted to a service and recommends
     [secrets rather than environment variables for sensitive values](https://docs.docker.com/compose/how-tos/environment-variables/set-environment-variables/).
   - **Safe recommendations, not silent automation: done.** Start in dry-run
     mode, one concurrent job, auto-replace off, conservative free-space/quarantine settings, and no
     auto-enqueue until the user reviews them. Explain encoder-specific quality, preview one
     representative candidate when possible, and show the estimated effect before saving. The wizard
     may recommend hardware decode, a VMAF tier, and a schedule from detected capabilities, but every
     recommendation remains visible and reversible. It never scans, enqueues, transcodes, replaces,
     or deletes an original until the
     user confirms the review screen. The flow visibly applies dry-run/concurrency, creates
     every new library with auto-enqueue, auto-replace, and VMAF off unless explicitly changed in the
     full embedded rules editor, and starts no work. Proved HEVC hardware support now drives a visible,
     reversible encoder/hardware-decode recommendation; CPU VMAF stays off by default, while a proved
     NVIDIA CUDA-VMAF path may recommend Balanced. The overnight window remains opt-in and never turns
     auto-enqueue on. A representative probed candidate can launch the established disposable preview;
     an honest empty state explains why a fresh, unscanned library has nothing to preview yet.
   - **Review before commitment: done.** The final form groups security, storage, library, encoder,
     quality, scheduling, integration, and replacement choices; each section has an accessible Change action
     that returns directly to that step with values pre-populated. GOV.UK's
     [check-answers pattern](https://design-system.service.gov.uk/patterns/check-answers/) uses this
     review to raise confidence and reduce submission errors. Applying the plan uses one validated
     database transaction, rolls back on failure, is idempotent after a lost response, returns a clear
     no-work-started receipt, and links directly to candidate review rather than starting destructive work.
   - **Accessible recovery: done.** Validate when Continue is pressed, preserve all
     user input, place a concise error summary at the top, move focus to it, and associate inline
     errors with their fields. W3C requires logical
     [keyboard focus order](https://www.w3.org/WAI/WCAG22/Understanding/focus-order.html) and recommends
     concise, actionable [form notifications](https://www.w3.org/WAI/tutorials/forms/notifications/);
     WCAG 2.2 also adds minimum target size, unobscured focus, redundant-entry, and accessible-
     authentication criteria. Status from readiness tests uses a polite live region without stealing
     focus, and progress never relies on colour alone.
   - **Gold-standard acceptance matrix: automated core shipped.** API/domain tests cover state transitions,
     ordered/idempotent progress, upgrade bypass, rejected and duplicate final apply, settings persistence,
     recommendation policy, and the no-source-mutation invariant. Browser tests cover
     Back/Continue/Change/Skip/Re-test and final apply across light/dark mode,
     all nine locales, keyboard and role/name semantics, reduced motion, the 320px WCAG 400%-reflow
     equivalent, 390px mobile width, and landscape. Readiness/policy tests cover missing, unreadable,
     unwritable and low-space paths, absent VMAF, and unproved GPU encoders; authentication tests cover
     every setup mutation. Optional integrations are not contacted during setup by design. The final
     apply transaction explicitly rolls back on an exception, and applying setup leaves a sentinel
     source file byte-for-byte unchanged while preview work remains disposable under `/work`.

3. **Phase 13 release hardening** — release controls are in progress; dry-run mode,
   config-and-secrets backups, migration smoke coverage, synthetic-media integration
  coverage, GHCR publishing, README quickstart hardening, troubleshooting, and security
  notes are shipped. Backups intentionally omit media, jobs, replacements, quarantine,
  and rollback history. CI stays on standard GitHub-hosted public-repo runners and avoids
  paid external services.

4. **First-class diagnostics & observability API** — make "why did this fail?" answerable
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

5. **Full translation parity with YA-WAMF: done.** The Optimisarr UI and
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
   - **Backend-originated UI messages and language parity: done.** JSON endpoint failures now
     carry stable machine-readable codes plus an English compatibility fallback; the web client
     resolves those codes through the same typed locale contract. This covers settings and query
     validation, filesystem/library/media/job errors, exclusions, replacements, and integrations.
     Persisted job failure categories likewise drive translated queue and diagnostics summaries,
     while raw encoder/backend output remains available only as explicitly labelled technical
     detail. **German, Spanish, French, Italian, Japanese, Portuguese, Russian, and Simplified
     Chinese are complete** across the same 832-message typed contract. CI audits interpolation
     placeholders across all eight translations so tokens such as `{count}` and `{path}` cannot be
     dropped silently. Locale modules remain lazy-loaded, keeping every translation out of the
     initial application chunk until selected. Native-speaker refinements remain welcome through
     normal issue reports, but no language is navigation-only or structurally incomplete.

6. **Packaging, app-store templates, and discovery** — make Optimisarr easy to find and
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

7. **Blind quality calibration ("placebo panel"): video, audio, and image slices done** — help a user choose
   the most space-efficient quality that they cannot reliably distinguish from their own source,
   without revealing the setting or estimated saving early. The design is informed by the source,
   observer, presentation, repetition, and paired-comparison principles in
   [ITU-R BT.500](https://www.itu.int/rec/R-REC-BT.500-15-202305-I/en) and
   [ITU-T P.910](https://www.itu.int/rec/T-REC-P.910-202310-I/en); it is a personal calibration aid,
   not a standards-conformant laboratory study or a claim of perceptual equivalence.

   - **Shipped for SDR video.** A saved video library can use one chosen, probed source. Optimisarr
     prepares 12-second early, middle, and late clips for the four complete library-slider presets,
     presents the original as a fixed reference beside four shuffled anonymous candidates, and
     asks the user to classify each candidate once as Indistinguishable, Acceptable, or Visibly worse. The most
     compressed acceptable candidate is recommended; if none is acceptable, the current setting is kept.
   - **Bias and interaction controls are shipped.** The setting, encoder and saving remain hidden
     until every candidate classification is complete. The original is marked so every judgement has
     a stable baseline; candidate quality labels remain blind. The reference and lettered candidates share
     one large viewport and relative playback position, support mouse, keyboard, and touch controls,
     and native playback fails closed if any sample cannot be decoded. Video supports browser
     fullscreen for close inspection. Applying
     the recommendation is a separate, explicit action and is refused if the library's relevant
     settings changed during the session.
   - **Long-GOP reference alignment is shipped.** Video candidates retain each preset's complete
     output contract while their original-side references remain a bit-for-bit stream
     copy, including the preceding keyframe packets required to decode a mid-file scene; Optimisarr
     records that hidden pre-roll, verifies the intended 12-second window, and starts playback at
     the matching frame. References live for the session and are removed with its other disposable
     work. A shared accessible 0–12-second control hides raw container duration from the observer.
     This prevents false Duration/Tail failures without weakening either gate, revealing the
     original, or introducing a second-generation reference encode.
   - **HDR video is shipped with a fail-closed presentation contract.** Non-Dolby-Vision HDR is
     offered only when the library preserves HDR, the browser reports an HDR-capable display path,
     and the user confirms the intended display is presenting HDR. This follows the signal and
     viewing-condition principles in [ITU-R BT.2100](https://www.itu.int/rec/R-REC-BT.2100) and uses
     the W3C `video-dynamic-range`/`dynamic-range` capability signal. It does not silently tone-map
     or claim a laboratory-grade HDR result. Dolby Vision remains excluded because its RPU dynamic
     metadata cannot safely survive Optimisarr's current re-encode path.
   - **Level-matched audio is shipped.** Music and mixed libraries can test Opus, AAC, or MP3
     bitrate ladders using three repeatable 15-second excerpts. Each original-side excerpt is a
     lossless FLAC derivative; the full lineup is measured with EBU R128 integrated loudness and
     every version is attenuated to the quietest one in the browser, preserving files and avoiding
     clipping. Instant reference/A–E switching keeps relative playback position. This follows the
     hidden-reference and controlled-listening principles in
     [ITU-R BS.1116](https://www.itu.int/rec/R-REC-BS.1116-3-201502-I/en) and the measurement method
     in [EBU Tech 3341](https://tech.ebu.ch/publications/tech3341).
   - **Synchronized image comparison is shipped.** Photo and mixed libraries can test five output
     quality levels against a lossless PNG derivative in one shared viewport. Reference/A–E switching keeps
     zoom and pan identical, all streams preload and fail closed, and animated images are excluded.
     This follows the observer-controlled comparison principles in
     [ISO 20462-1](https://www.iso.org/standard/38330.html) without claiming laboratory compliance.
   - **Cheap and safe by construction is shipped.** Candidates are disposable jobs isolated under
     `/work/calibration`; they bypass normal candidate scheduling but still receive structural
     verification. They cannot replace, move, or delete a source and are hidden from the normal
     queue. Every saved Film, TV, Music, Photo, and mixed library exposes the compatible personal
     check from its own configuration page. Leaving the full-page lab or restarting Optimisarr removes the
     session's database rows and scratch files. The original is only read.
   - **Next research and implementation.** Select representative sources using spatial/temporal
     complexity rather than file size alone and support a small multi-source result; correlate the
     revealed choice with sampled VMAF without turning the metric into a hint. Extend toward a
     multi-source result only after each media kind remains separately validated rather than
     stretching one perception method across unlike tasks.

8. **VMAF performance on modest hardware: done.** VMAF is the slow part of verification, and on a
   low-power host (e.g. an Intel N100) a full-file measurement is effectively unusable, which is why
   the gate ships off by default. Make it fast enough to actually turn on.

   - **The honest hardware picture: done.** The only GPU acceleration for the VMAF computation itself is
     **VMAF-CUDA** (`libvmaf_cuda`, part of VMAF 3.0 / FFmpeg 6.1) — **NVIDIA only**. There is no
     Intel (QSV/VAAPI), AMD, Vulkan, OpenCL, or NPU/OpenVINO backend for the VMAF feature
     extractors; Intel/AMD silicon can hardware-accelerate decode and scaling but not the scoring.
     Document this plainly so users don't expect QSV/VAAPI/NPU VMAF that does not exist.
   - **Optional CUDA VMAF when an NVIDIA GPU is present: done.** Detect the filter and switch to NVDEC
     decode + `scale_cuda` + `libvmaf_cuda` (CUDA frames end to end), falling back to the CPU path
     otherwise. Reported ~4.4× throughput. Needs an ffmpeg built with `--enable-nonfree`
     `--enable-libvmaf` and `--enable-ffnvcodec` (and the CUDA VMAF library), so it is a build/runtime capability check,
     not an assumption.
   - **CPU-side wins for everyone else (the N100 case): done.** In impact order: score a short
     representative **clip** instead of the whole file (reuse the preview-clip mechanism — the
     biggest single win); optionally **hardware-decode the two inputs** with QSV/VAAPI to offload
     decode while VMAF stays on the CPU; expose an `n_subsample` setting (less scoring work by measuring every
     Nth frame, with the caveat that it can step over a bad frame the catastrophic floor exists to
     catch). The incidental PSNR/SSIM video measurements were dropped when only the VMAF gate decision
     is needed. All accelerated paths fall back to software, HDR stays on its established
     software colour pipeline, and `n_threads` remains bounded to the core count.


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
