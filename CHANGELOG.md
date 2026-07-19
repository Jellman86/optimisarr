# Changelog

## Unreleased

### Fixed

- **Video verification now compares the same frames and identifies damaged sources honestly.**
  Sampled VMAF places the source and output on the source's measured frame cadence before trimming,
  preventing millisecond-versus-codec timebases from pairing adjacent pictures and producing false
  zero scores at movement or cuts. Tail verification now compares the output with the source video's
  actual packet endpoint and reports a source picture stream that ends early as **Source video
  timeline**, rather than blaming the encode for the container's audio-only remainder. Unmeasured
  VMAF evidence still fails closed, but no longer triggers a pointless higher-quality retry or an
  immediate automatic exclusion.
- **Personal quality-check clips with a non-zero media epoch no longer fail Duration.** The
  verifier now compares the measured picture span with the exact calibration window instead of
  mistaking an absolute end timestamp—or copied subtitle/attachment padding—for clip runtime.
  Full-file jobs retain their existing container-duration check.
- **The personal quality check is no longer part-English for translated installs.** Eleven strings
  on that screen were hardcoded in the component instead of going through the locale files,
  including the **Temporary stream verification** and **Ignore active media streams for this check**
  options and the sample-deck instruction — so every non-English user saw them in English next to
  correctly translated text. They now resolve through `i18n` and ship in all ten languages. The
  locale audit only validates the locale files, so it could not catch strings that never reached
  them.
- **The personal quality check now has one name.** The page introduced itself as "Quality lab"
  under an eyebrow reading "Personal quality check", while the control that opens it said a third
  thing. The heading, the browser tab, the library card, and its button now all say **Personal
  quality check**.
- **Track cleanup no longer points at controls it hides.** Selecting "Only remove unwanted
  audio/subtitle languages" warns that the preset does nothing until a keep-language rule is set —
  but **Keep audio languages** and **Keep subtitle languages** lived inside the collapsed Advanced
  drawer, so the warning referred to fields that were not on the page. They are now rendered
  directly beneath that warning whenever track cleanup is selected, and stay in Advanced > Video for
  the encode and remux modes that treat them as a refinement.

### Changed

- **The library configuration form is easier to scan.** Every top-level group is now named the way
  the Advanced sections already were — **Library** for the name, path, and media type, and
  **Automation** for the enabled/auto-optimise/auto-replace switches, which previously floated
  between the quality policy and Advanced with no heading of their own. Save and Cancel appeared
  twice on the same screen, in the header and again at the foot of the form; there is now one pair,
  in an action bar that stays pinned to the bottom of the viewport so Save remains reachable when
  Advanced options make the form several screens tall. The perceptual-quality control no longer
  repeats itself in a paragraph above and below the picker, the "Custom" stop on the quality slider
  no longer renders a dash where a codec would go, and the calibration card, its button, and its
  tooltip all now call the feature **Personal quality check** instead of alternating with "Blind
  quality calibration". The gap between top-level groups was three different values (16/20/24px)
  with dividers on some boundaries and not others; it is now one rhythm, matching the uniform
  spacing the Advanced sections already used. The three processing-mode cards no longer stretch to
  the height of the longest one, which had left the two shorter cards with a block of dead space.
  No rule, default, or safety gate changed.

### Fixed

- **Unrecoverable quality and size failures no longer burn repeated full encodes.** A VMAF-only
  rejection still receives one encoder-aware higher-quality retry, but a second VMAF rejection now
  auto-excludes the file. Size-saving failures—including combined size and VMAF failures—exclude
  immediately because raising quality worsens size and silently lowering the configured quality would
  violate the selected output. Other failures keep the conservative three-attempt threshold;
  cancellations and worker-restart interruptions never count. Every exclusion is reasoned, reversible,
  and leaves the original untouched.
- **Short video quality samples no longer fail duration verification because of harmless audio
  padding.** Video calibration now compares the candidate picture stream's duration with the exact
  12-second reference window instead of using a container duration extended by AAC frames or codec
  delay. Decode, timestamp, tail-integrity, audio-retention, and container checks still run against
  the complete preset output.
- **Personal quality-check preparation now shows live CPU and GPU usage.** The preparation window
  reuses the same rolling system-usage graphs as active work in the Queue hero, including the
  detected GPU engine and the honest unavailable state when the host cannot expose GPU telemetry.
- **Video quality checks no longer mistake complete preset samples for truncated output, and their
  failures remain diagnosable.** Candidate files still encode each preset's complete video,
  container, and audio contract, while the unchanged original reference again uses the exact
  video-only picture window required for duration, tail, and frame-alignment verification. Failed
  preview and personal-quality jobs retain only their small database diagnostics after scratch
  media is removed. The failure API and Queue failure view now identify comparison jobs and expose
  every failed verification gate with its measured detail; the same detail is written to container
  logs. **Clear errored** removes these retained diagnostic rows.
- **Personal quality-check preparation now moves forward steadily and can explicitly bypass active
  playback.** The displayed preparation percentage keeps the highest measured session progress, so
  FFmpeg stage changes cannot make it jump backwards. A per-check, default-off option allows only
  that disposable calibration session to start while Plex, Jellyfin, or Emby reports active
  playback; normal optimisation jobs remain paused behind the media-activity safety gate.
- **Video quality checks now test the real preset outputs and sample selection responds on the
  first click.** The video lineup is the library slider's four complete presets—Compatibility
  H.264/MP4, Balanced HEVC/MP4, Efficiency AV1/MKV, and Scott's bundle—instead of five CRF values
  encoded through one codec/container. Applying a result selects that preset and clears stale
  codec/container overrides without queueing normal work. Candidate cards no longer combine drag
  and click gestures or discard a click while another stream is seeking; the latest selection wins
  while frame alignment remains fail-closed. A temporary, opt-in-at-the-API diagnostic mode is on
  by default in the lab. Verification mode now uses one native browser video player and replaces
  that element's media resource on every selection instead of keeping several hidden players. It
  exposes the element's real `currentSrc`, native controls, and a direct link that opens that exact
  resource in a browser tab, while retaining the aligned seek before the replacement frame appears.
- **Personal quality checks are now finite, reference-led, frame-aligned, and usable for a complete
  session.** The former repeated A/B/X trial loop is replaced by one marked original reference and
  shuffled anonymous candidates. Only the candidates require classification, so each rating
  has a stable quality baseline without forcing a worst-to-best ranking. Video switching waits for
  the destination stream to seek to the same source frame before
  it becomes visible or resumes, removing the timing jump that could reveal a version, and playback
  continues across all scenes instead of stopping after the first comparison.
- **Personal quality checks now explain queue waits instead of appearing frozen at 0%.** If all
  processing slots are occupied, calibration preparation shows an indeterminate waiting state and
  says it will start automatically. Once a worker is available, the panel returns to real sample
  preparation progress; no running encode is interrupted or reordered unsafely.
- **Personal quality checks are now reachable from Music and Photo libraries.** The audio and still-image
  comparison pipelines were available, but their per-library entry point was incorrectly nested
  inside the video preset controls. Saved Music and Photo libraries now show the same **Personal
  quality check** action as Film, TV, and mixed libraries.
- **Blind video checks no longer reject healthy samples as too short.** Some long-GOP TV and film
  sources can only be stream-copied from the keyframe immediately before the requested scene. That
  harmless decode pre-roll made a 12-second original-side clip appear longer than its encoded match,
  so every quality level failed the Duration and Tail integrity gates. Optimisarr now verifies the
  requested 12-second picture window, records the hidden pre-roll, and starts original playback at
  the matching frame. Original references
  remain available until the disposable session is closed or expires instead of being removed as
  soon as verification finishes. One accessible shared timeline now controls every blind stream,
  so native container duration cannot reveal which slot carries hidden decode pre-roll. No source
  file is changed.

### Added

- **Temporary stream verification is now opt-in.** Personal video quality checks start blind by
  default; native player controls and exact-resource diagnostics appear only when explicitly enabled.
- **Revealed video quality checks now include VMAF evidence in both the lab and API.** Every preset
  is measured across all three scenes without turning VMAF into a hidden pass/fail gate or allowing
  it to override the user's blind ratings. The result shows harmonic mean, worst-scene fifth
  percentile, and lowest frame; the session API also returns the complete per-scene measurements,
  model, preprocessing, frame counts, and explicit measurement errors. A new authenticated active-
  session endpoint makes current results discoverable without knowing their session IDs.
- **Track cleanup profile.** A new rule profile that only removes audio/subtitle tracks outside
  the library's kept languages — no re-encode, no container change (an `.mkv` stays `.mkv`).
  Every kept stream is copied bit-identically; a file with nothing to remove is skipped with a
  clear reason, and the library form presents encode, remux, and track cleanup as three exclusive
  processing modes. Irrelevant encode, VMAF, audio-only, image, and eligibility controls are hidden
  in cleanup mode. Verification additionally confirms the output container and retained audio
  codecs match the source.
  Migration `AddTrackCleanupSupport`.
- **Per-library "Keep subtitle languages" removes unwanted subtitle tracks.** Mirrors the audio
  rule on every profile: comma-separated ISO 639 codes, `-map -0:s:N` exclusions, unknown-language
  tracks never removed, common spellings of the same language match each other. Only positively
  registered individual ISO 639 languages may authorise removal; malformed, unregistered,
  collective, special-purpose, and private-use identifiers fail closed and keep the track. A
  partly invalid stored rule disables the whole rule rather than silently broadening deletion.
  One deliberate
  difference from audio: subtitles are optional streams, so there is no keep-at-least-one guard —
  an all-foreign subtitle set is removed entirely. The probe now records each subtitle track's
  language (rows probed before the upgrade are queued for background re-probing), and every
  destructive dispatch requires a fresh successful source probe. The subtitle-retention gate
  expects exactly the planned removal and verifies the identities of retained known-language
  tracks, so an encode that drops or swaps the wrong stream still fails.
- **The queue shows why each job is queued.** Every job records its eligibility reason at enqueue
  time (e.g. `h264 → hevc`, `Remove 2 audio track(s) (fra, deu) not in the kept languages`) and
  the Queue page shows it on the active-job card and each row. Track-removal reasons name the
  languages being removed, not just counts.
- **A full-page personal quality lab for video.** A saved video library can prepare short,
  disposable samples from the beginning, middle, and end of a representative file, then compare a
  marked original reference and four shuffled anonymous candidates covering the library's real
  preset slider. Candidate settings and estimated sizes remain hidden until all four are
  classified as Indistinguishable, Acceptable, or Visibly worse. One large synchronized viewer,
  scene controls, and real browser fullscreen support close inspection without a 25-trial loop. The
  result recommends the most compressed acceptable setting but never claims encodes are equivalent.
  The original is read-only, scratch clips
  are removed when the panel closes, after being abandoned for two hours, or when the app restarts;
  nothing enters the normal queue or replacement path, and the suggested preset changes the
  library only after an explicit Apply.
  Native playback fails closed when the browser cannot decode a candidate. HDR is available only
  when the library preserves HDR and the browser reports an HDR-capable display path; the user must
  also confirm that the normal viewing display is actually presenting HDR. Dolby Vision remains
  unavailable because a re-encode cannot safely retain its dynamic metadata. Content-complexity
  source guidance and metric correlation remain on the roadmap.
- **A level-matched blind audio check.** Music and mixed libraries can now calibrate their saved
  Opus, AAC, or MP3 bitrate using three repeatable 15-second excerpts and the same marked-reference,
  anonymous-candidate classification model. Optimisarr creates a lossless FLAC reference for browser
  playback, measures the full lineup with EBU R128 integrated loudness, and attenuates every version
  to the quietest measured level so volume cannot reveal the answer or introduce clipping. The result applies
  only the library's audio bitrate after an explicit confirmation; every candidate remains
  disposable and outside replacement, history, notifications, and the normal queue.
- **A zoom-synchronised blind image check.** Photo and mixed libraries can now compare a lossless
  PNG view of one still image against five hidden output-quality levels. The reference and A–E share exactly one
  viewport, so zoom and pan stay fixed when switching and spatial position cannot give the answer
  away. The PNG reference carries the source colour metadata when the configured metadata tool can
  copy it, animated images are excluded, browser loading fails closed, and Apply changes only the
  library's saved image quality without queueing work.
- **Gold-standard first-run recommendations and final review.** Setup now turns proved encoder and
  VMAF capabilities into visible, reversible encoder, hardware-decode, VMAF, and overnight-window
  recommendations instead of silently choosing for the operator. When a probed candidate exists,
  the wizard can run the established disposable original-versus-output preview without touching the
  source. The final check-answers page now covers network security, storage, libraries, encoder,
  quality, scheduling, optional connections, and replacement, with accessible Change actions that
  preserve the draft. One validated database transaction applies the reviewed settings and opted-in
  per-library recommendations, records completion, and returns a no-work-started receipt linking to
  candidate review or the dashboard; duplicate submissions return that completed state without
  changing the applied plan. Draft safety choices survive refresh in local browser storage, inline
  validation is tied to its field and focused error summary, and the new browser acceptance suite
  exercises keyboard flows, re-test announcements, final apply, dark/reduced-motion presentation,
  320px reflow, landscape, 390px mobile, and all nine locales in CI.

## 0.2.4 — 2026-07-16

### Added

- **A maintained hardware validation matrix.** CPU, NVIDIA NVENC, Intel QSV, Intel/AMD VA-API,
  hardware decode, VMAF acceleration, and live metrics now have one public evidence table that
  distinguishes implemented/unit-tested paths from real-host validation. It records known gaps and
  an exact, non-secret evidence checklist; AMD VA-API and current CUDA VMAF remain honestly pending
  instead of being implied by device detection or command coverage.
- **Resumable, safety-first first-run setup.** A genuinely new database now opens a five-step
  setup workspace for deployment/tool checks, creation and full configuration of any number of
  conservatively configured libraries,
  dry-run/concurrency choices, and a final no-work-started review. Progress is versioned and
  persisted after each completed step, duplicate submissions are idempotent, Back retains applied
  choices, and completion is possible only from the review step. Upgraded installations are marked
  complete and never forced through onboarding; the Settings header offers an explicit **Run setup
  again** action that keeps every existing library and setting. The library step reuses the complete
  per-library rules editor, rechecks every configured path before Continue, and keeps Add/Configure
  actions available until the operator is ready. Fresh installs start in dry-run,
  automatic enqueue/replacement and VMAF remain off, and the readiness ledger reports database,
  config/work/quarantine/library path access, filesystem and container-mount identities, free and
  total capacity, the configured work-space reserve, required tools, and detected hardware encoders.
  It identifies whether each library can use atomic moves to work and quarantine, states when the
  verified cross-filesystem fallback is disabled, and gives selectable, exact recovery steps for
  local, Docker Compose, Unraid, and TrueNAS deployments. **Re-test system** reruns the real probes,
  announces completion, and removes resolved guidance without pretending a container can repair its
  own host mounts or permissions. The focused
  responsive layout ships in all nine locales with text status alongside colour and keyboard focus
  moved to actionable errors. Shared form sizing and unit-bearing fields now remain inside General
  Settings cards at phone widths, with long toggle labels wrapping instead of being truncated.
- **Dedicated library configuration pages and library-owned VMAF policies.** Configure now
  opens a full-page Rules workspace instead of expanding an increasingly dense library card, while
  Candidates and Excluded remain adjacent tabs on the same canonical library URL. Each video
  library can disable its VMAF gate, select a Space-saver through Archival quality tier, or enter
  custom harmonic-mean, fifth-percentile, and catastrophic-frame floors. Clip/full-file scoring and
  the 1st–10th-frame sampling interval are also library-owned. The upgrade migration materialises
  the former global policy onto existing libraries and removes its obsolete settings; older config
  backups receive the same conversion during import. API/import validation enforces safe ranges and
  ordered floors. The responsive editor keeps primary choices visible, uses a compact borderless
  summary for effective floors, progressively discloses advanced encoding controls, warns before
  discarding changes, and exposes non-overlapping Save/Cancel actions on desktop and mobile.
- **Encoder-aware VMAF recovery and safer temporal pooling.** Hardware quality controls now receive
  conservative encoder-family calibration (QSV ICQ, NVENC CQ, VA-API QP) instead of treating their
  numeric values as interchangeable with software CRF. Verification pools harmonic mean, fifth
  percentile, and a catastrophic single-frame floor; optional fast scoring uses deterministic
  early/middle/late windows. If VMAF is the only failed gate, Optimisarr automatically retries once
  at a higher encoder-specific quality. The Queue persists and displays the requested/effective
  quality and sampling context, offers explicit same-settings and higher-quality recovery actions,
  and container shutdown now drains active jobs for up to two hours before safe cancellation.
- **Hardware-assisted and subsampled VMAF with automatic software fallback.** SDR verification now
  follows the job's selected NVIDIA/QSV/VA-API hardware path when Hardware decoding is enabled:
  NVIDIA can keep both inputs on the GPU through NVDEC, bicubic `scale_cuda`, and `libvmaf_cuda`,
  while QSV/VA-API can offload both decodes before downloading frames for CPU VMAF. CUDA is enabled
  only when the configured binary exposes the exact filter (`OPTIMISARR_FFMPEG_VMAF_CUDA` can point
  at a purpose-built binary), and every accelerated runtime failure is discarded and retried through
  the established software graph. HDR deliberately stays on the colour-accurate software path. A new
  Frame sampling control scores every 1st–10th frame (default 1/every frame) with a warning that
  skipped frames weaken the worst-frame floor. Incidental PSNR/SSIM features are no longer computed
  during the VMAF gate, avoiding work that does not affect replacement safety.
- **Optional sampled VMAF (faster quality gate on modest hardware).** A per-library control under
  the quality policy measures VMAF across three 40-second windows near the beginning, middle and end
  instead of the whole runtime, which cuts VMAF time dramatically on low-power hosts (e.g. an Intel
  N100) where full-file scoring is impractical. Both the output and original are sought to each
  matching window, and progress is reported across all three samples. The other
  gates (decode health, duration, structure, size) still check the whole output. Off by default, and
  skipped for short files where full scoring is comparable. VMAF's scoring acceleration remains NVIDIA-only;
  Intel/AMD hardware can offload decode but not the metric itself.
- **Per-library "Keep audio languages" removes unwanted audio tracks.** A library can list the
  ISO 639 codes to keep (e.g. `eng, jpn`); when a video is optimised or remuxed, audio tracks in
  any other language are dropped from the output via explicit `-map -0:a:N` exclusions. The
  behaviour is conservative by design: unknown, malformed, uncoded, and private-use language tags
  are never removed, and when no track matches a kept language nothing is removed. The complete
  ISO 639-1/-2 aliases match each other (`de`/`deu`/`ger`). Verification requires exactly the
  planned nonzero retention and judges channel/sample-rate fidelity against the kept tracks. The
  upgrade queues existing videos with audio for background re-probing, and every destructive job
  obtains a fresh successful source probe before FFmpeg,
  so already-clean remuxes become eligible for fast stream-copy cleanup. The accessible library
  control validates and normalises input before save in every locale, and config backup/restore
  uses the same validator. Migration `AddKeepAudioLanguages`.

### Fixed

- **Queue capacity now follows the real work filesystem.** Free-space checks select the deepest
  mounted filesystem containing the configured work path, so a bind-mounted `/work` no longer
  reports the container overlay's much smaller capacity or pauses a healthy queue prematurely.
- **Abandoned work output is reclaimed safely.** Clearing, deleting, or retrying a failed job now
  removes its owned scratch output before dropping the database reference and retains the job if
  cleanup cannot be completed. Startup also removes output left by cancelled jobs and numeric,
  unreferenced work directories older than seven days, while retaining recent or referenced data.
- **Credential-bearing integration URLs are excluded from normal logs.** Default logging now keeps
  `System.Net.Http.HttpClient` request messages at Warning or above, preventing Discord webhook URLs
  and similar outbound credentials from appearing in ordinary Information-level container logs.
- **First-run review copy uses locale-aware singular and plural forms.** The setup summary now reads
  naturally for one or many concurrent jobs in every supported language.

## 0.2.3 — 2026-07-14

### Added

- **Live verification progress in the queue.** The perceptual-quality (VMAF) pass is the long part
  of verification, so it now reports real 0–100% progress on the same job-progress and live-update
  channel the transcode uses (ffmpeg `-stats` parsed against the source runtime). The Queue hero and
  job rows show a determinate bar and percentage during verification and name the stage explicitly
  ("Scoring perceptual quality (VMAF)…"), so a job scoring for several minutes reads as active work
  rather than a stall. The hero also shows a live CPU-usage graph during verification — VMAF is
  CPU-only, so it surfaces just the CPU load (no GPU), making the heavy load visible at a glance.
- **Internationalisation (i18n) foundation.** The web UI now resolves user-facing text through a
  typed locale system (`web/src/lib/i18n/`): English is the source of truth, and every other locale
  is typed against it, so a missing or misspelled key fails `npm run check` — making translation
  completeness a compile-time CI gate rather than something that can silently rot. A lightweight
  runes store exposes the active locale's messages and a `t()` interpolation helper, the choice is
  persisted (and falls back to the browser language, then English), and a language selector lives in
  the sidebar. German ships as the first translated locale. **Every page is migrated** — the app
  shell and navigation, plus Dashboard, Schedule, Quarantine, Inventory, Queue, Settings, and
  Libraries — each with complete German (including confirm dialogs, InfoTip help text, preset
  summaries, and count-aware/pluralised strings). Shared components and the complete YA-WAMF
  language set now use the same contract.
- **Backend-originated messages now participate in i18n.** User-facing JSON errors expose stable
  machine codes while retaining English fallback text for older clients. The typed web client
  translates settings/query validation and filesystem, library, media, queue, exclusion,
  replacement, and integration failures. Queue rows and grouped failure diagnostics use persisted
  failure categories for localized summaries; raw process errors remain available as collapsed,
  explicitly labelled technical detail.
- **Spanish translation.** Spanish joins English and German as a complete selectable locale across
  all 832 message leaves, with YA-WAMF's existing Spanish vocabulary used for shared product terms.
  The frontend completeness check now also compares interpolation placeholders in every locale, so
  a translation that drops or changes runtime tokens such as `{count}`, `{path}`, or `{error}` fails
  `npm run check`.
- **French translation.** French is now selectable and complete across the same 832-message
  contract, including backend error summaries, validation feedback, operational panels, and
  accessibility labels.
- **Italian translation.** Italian is now selectable and complete across the full typed message
  contract, including media terminology, safety guidance, validation, and diagnostics.
- **Japanese, Portuguese, Russian, and Simplified Chinese translations.** The final four YA-WAMF
  languages are selectable and complete across all 832 message leaves. Ambiguous software terms
  such as media library, processing queue, job, container, candidate, and space saving receive
  context-specific translations rather than literal dictionary meanings. The placeholder audit now
  checks all eight non-English translations, completing parity across `en`, `de`, `es`, `fr`, `it`,
  `ja`, `pt`, `ru`, and `zh` without adding any language to the initial application chunk.
- **Shared-component i18n migration started.** Folder selection, detail-sheet controls, and the
  original/encoded media comparison now use the typed locale contract, including accessible labels,
  loading/empty guidance, playback controls, downloads, and browser compatibility guidance.
  Candidate tables, grouped failure diagnostics/log controls, and the tools/hardware capability
  panel now use the same contract for filters, paging, status, empty states, and accessible actions.
  The preview-transcode comparison completes the shared-component pass: progress/failure states,
  safety guidance, minimised controls, original/encoded labels, sample explanation, technical stats,
  and verification summary all switch live with the selected locale.

- **Unraid Community Applications template.** Added `unraid/optimisarr.xml` (Docker template with
  config/media/work/quarantine volume mappings, the `8787` web port, optional
  `OPTIMISARR_ADMIN_TOKEN`, PUID/PGID/UMASK, and an optional `/dev/dri` device for Intel/AMD
  hardware transcoding), a repository-root `ca_profile.xml` (the profile Community Applications
  requires for a repository submission), and [`docs/setup/unraid.md`](docs/setup/unraid.md) covering
  volume layout, atomic-move guidance for `/trash`, and CPU/Intel/AMD/NVIDIA transcoding on Unraid.

### Fixed

- **Sampled VMAF no longer mistakes seek/decode startup frames for catastrophic quality loss.**
  Each early/middle/late window now seeks both independently encoded inputs five seconds early and
  trims identical decoded pre-roll before resetting timestamps and scoring the requested interval.
  When a hardware-decoded measurement falls below any configured VMAF floor, Optimisarr confirms
  that window through the authoritative software path before rejecting the output or spending time
  on a higher-quality re-encode.
- **Replacement and rollback are crash-safe end to end.** Optimisarr now commits a `Pending`
  rollback record before moving the original, then finalizes it only after the verified output is
  in place. Startup reconciles interrupted records idempotently: it restores a quarantined original
  when the verified output remains in `/work`, or completes the database transition when both file
  moves had already finished. Rollback stages the current optimised file and restores it if moving
  the quarantined original fails, so a failed rollback never leaves the library path empty.
  Quarantine folders also include the job id, preventing concurrent same-named replacements from
  colliding within the same millisecond.
- **Clip-VMAF configuration and translations are complete.** Config export/import now preserves
  the clip-scoring preference. All VMAF settings and presets are translated across the eight
  non-English locales, stale claims that VMAF is enabled by default are corrected, and the locale
  audit now rejects long English prose copied unchanged into a translation.
- **Admin tokens no longer leak through ordinary API query strings.** The `access_token` query
  fallback is limited to SignalR hub paths, where WebSocket clients require it; REST API calls must
  use the bearer header so reverse-proxy logs and browser history do not capture the token. A
  successful bearer request establishes a derived HttpOnly, same-site cookie for native browser
  media elements, keeping authenticated previews working without exposing the configured secret.
- **A retried job no longer keeps a stale failure category.** Starting a new attempt now clears the
  previous attempt's error message and failure classification, so a job that failed once (e.g. a
  verification-gate rejection) and then succeeded on retry is reported as the success it is, instead
  of lingering in the "why jobs fail" diagnostics as a verification failure.
- **The language selector can open in either direction.** Its accessible custom menu measures the
  available viewport space and opens upward near the bottom of the sidebar, while retaining a
  downward menu where there is room. It supports outside-click/Escape dismissal and arrow-key,
  Home, and End navigation. Shared toggle checkboxes now also expose their visible labels to
  assistive technology.
- **VMAF is an opt-in perceptual-quality gate, chosen with a quality slider.** It is off by default
  because it fully decodes both files and scores every frame, which roughly doubles verification
  time and can dominate a run on modest hardware. Each library's policy selector turns it on and
  prefills all floors from five tiers — Space-saver (80/60/30), Balanced (85/70/40), High
  (90/75/45), Visually lossless (93/80/50), and Archival (96/90/70) — with "Off" as the default;
  existing installations retain their effective choice. While it is off the structural, duration and
  size gates plus quarantine rollback still guard every replacement. Remux, audio, and image work
  skip VMAF because it is either redundant or inapplicable, and the final-container smoke test still
  performs a real synthetic `libvmaf` comparison rather than trusting the filter list. Model choice
  and stream preparation are automatic: HDTV/4K selection, reference-resolution bicubic scaling,
  timestamp/timebase and colour-range alignment, and like-for-like HDR-to-SDR reference tone-mapping
  require no libvmaf expertise; reports record the selected policy.
- **Still-image conversion now fails closed around structural loss.** Animated GIF/WebP inputs
  require a proven single frame, TIFF is left untouched while multi-page status cannot be proved,
  and the JPEG target rejects alpha or high-bit-depth inputs. Lossless-to-lossy conversion now
  requires the existing explicit lossy-image opt-in, while PNG/BMP/GIF to WebP uses the encoder's
  genuinely lossless mode. Pixel format and raw bit depth are persisted via a schema migration so
  rescans and queue dispatch make the same decision. AVIF has been withdrawn as an output choice
  because the shipped Jellyfin FFmpeg contains no AVIF encoder (`libaom-av1`); existing AVIF library
  overrides migrate to the proven WebP target, and API/UI choices expose only JPEG and WebP.
- **Music conversion now treats artwork, tags, and lyrics as replacement-critical data.** AAC in
  M4A is the compatibility-first default for art-free music and can retain timed-text lyrics. The
  shipped Jellyfin FFmpeg silently drops inherited M4A cover streams, while Opus needs a picture-
  comment translation it cannot safely perform, so both targets reject attached-art candidates
  before queueing with guidance to choose MP3. MP3/Opus likewise reject timed-lyrics streams they
  cannot contain. Verification now compares
  source/output format tags and embedded-picture counts, and probing persists artwork counts for
  deterministic candidate decisions. Mapped MP3 covers are normalised to broadly supported JPEG
  APIC artwork and explicitly marked `attached_pic`; the final-container smoke test proves this
  against the exact FFmpeg build that performs production transcodes. The UI explains the default
  consistently in every language.
- **Audio bitrate policy is now channel-aware.** The configured value is a stereo baseline;
  retained 5.1/7.1 layouts automatically receive the same budget for each channel pair, while an
  explicit stereo downmix keeps the configured bitrate. Candidate size-saving decisions use that
  effective bitrate too, preventing a lossy surround source from being queued when it cannot save
  space. MP3 rejects more than two retained channels and AAC/Opus reject layouts above their
  supported eight-channel ceiling before FFmpeg runs. Channel counts are persisted by migration.
- **Container CI now exercises the actual media pipelines.** The final image creates and converts
  real synthetic H.264/AAC video, FLAC music with tags and attached JPEG artwork, and opaque/alpha
  PNG fixtures across the production JPEG and WebP targets.
  It runs the production stream-map/codec argument shapes with the shipped Jellyfin FFmpeg, fully
  decodes the video, probes codec/art/tag retention, and byte-compares decoded RGBA frame hashes for
  lossless WebP, and probes and decodes every selectable still format. This complements pure
  command-builder tests with real encoder/muxer coverage.
- **Image verification is now default-on and uses a current SSIM graph.** New installations require
  0.95 SSIM plus EXIF/ICC retention before replacing a still; saved opt-outs remain unchanged. Both
  inputs are independently aligned to explicit reference dimensions, timestamps, full colour range,
  and planar RGB/RGBA without deprecated `scale2ref`, so alpha participates when applicable. ExifTool
  copies source EXIF/ICC before verification while excluding orientation, previews, and stale raster
  dimensions that would misdescribe the encoded pixels. WebP now also rejects high-bit-depth sources
  because its wired encoder path is eight-bit.
- **CRF direction guidance and VMAF availability.** The library video-quality slider now correctly
  labels low CRF values as sharper and high CRF values as smaller. The container no longer assumes
  Jellyfin FFmpeg provides `libvmaf`: it keeps that binary for hardware-aware transcoding and adds a
  digest-pinned, multi-architecture static FFmpeg for perceptual measurements. The image build and
  container smoke test assert the exact `libvmaf` filter (without mistaking `vmafmotion` for it), and
  Settings → Tools reports the measurement binary as an optional capability without making core
  readiness depend on an opt-in quality gate.
- **A/V sync verification no longer false-fails sources with an inherent audio-start offset.** The
  gate previously checked the *output's* absolute video-vs-audio start divergence (>0.5 s = fail),
  so a source that legitimately carries a baked-in audio delay — e.g. a Bluray rip whose AC3 track
  starts ~1 s after video — failed verification on every episode even though the transcode preserved
  the timing exactly, blocking optimisation of whole seasons (observed live on a Stargate SG-1 S2
  release). The check is now **relative**: when the original's stream start times are known it
  compares the *change* the transcode made to the A/V offset and fails only when that change exceeds
  the 0.5 s tolerance — so a faithfully-preserved offset passes, while a transcode that shifts sync
  (or drops an inherent delay) still fails. Falls back to the previous absolute check when the
  original's start times can't be read. `VerificationInput` gains `OriginalVideoStartSeconds` /
  `OriginalAudioStartSeconds`, populated from the original probe.
- **Preview VMAF now compares the same source window frame-for-frame.** The encoded preview used an
  accurate decode seek, while its stream-copied reference could retain frames from the preceding
  keyframe; libvmaf then compared different moments and reported near-zero, CRF-insensitive scores.
  Preview measurement now seeks and decodes the full original as libvmaf's reference input, while
  `shortest` bounds it to the encoded sample. Final-container CI starts deliberately between
  keyframes and requires the resulting ordinary CRF encode to score at least 90.
- **Video timing now follows probe evidence instead of forcing MP4 to CFR.** The earlier blanket
  `-fps_mode cfr` response to live job 3334 could duplicate/drop frames and alter motion cadence.
  Probing now compares nominal and average frame rate (with a rounding tolerance) and persists a
  nullable VFR decision. Positively identified VFR re-encodes use `-fps_mode vfr` plus the demuxer
  encoder timebase in every container; CFR and unknown sources receive no frame-rate override, and
  remuxes are never re-timed. Timestamp-integrity, tail, duration, and relative A/V-sync verification
  remain the fail-closed backstops. Migration `TrackVariableFrameRate`.
- **Video verification now checks the encoded signal structure.** Replacement requires the resolved
  target codec (or unchanged codec/profile for a remux), exact source resolution, no reduction in
  pixel bit depth or chroma sampling, and a reported profile consistent with high-bit-depth output.
  These checks catch silent 10→8-bit, 4:4:4→4:2:0, resize, wrong-codec, and missing-profile outcomes
  that a clean decode or good VMAF score cannot prove. ffprobe profiles are persisted via
  `TrackVideoProfile`, unit tests cover each failure class, and container CI probes the real HEVC
  output's dimensions, format, and profile.
- **Compatibility video profiles now include compatible audio by default.** Balanced HEVC/MP4 and
  Compatibility H.264/MP4 re-encode audio to channel-aware AAC at a 160 kbps stereo baseline, so
  “plays everywhere” describes the whole file rather than only its video stream. Surround layout is
  retained with scaled bitrate; there is no implicit downmix. Advanced now distinguishes “Profile
  default” from an explicit “Copy” override, which remains available for operators who require
  bit-exact source audio. AV1/MKV and remux profiles continue to copy by default.
- **Every production media pass now uses one configured toolchain.** Probing, timestamp inspection,
  decode verification, hardware detection, and transcoding share the configured Jellyfin
  FFmpeg/ffprobe pair instead of mixing it with Debian's PATH binaries. Custom installations can
  set `OPTIMISARR_FFPROBE`; otherwise an absolute `OPTIMISARR_FFMPEG` path automatically resolves
  its sibling probe. The final-container smoke suite exercises that exact configured pair.

## 0.2.2 — 2026-07-05

### Safety — edge-case hardening

- **Dolby Vision sources are now left untouched by default.** DV carries a dynamic-metadata RPU that
  cannot survive a re-encode; without it the file degrades to HDR10/SDR, and a Profile 5 source (no
  HDR10 base layer) comes out green/pink. With the perceptual (VMAF) gate off by default there was no
  backstop, so a DV source could be transcoded to a colour-shifted output and still pass verification,
  replacing the original. The probe now detects Dolby Vision distinctly (DOVI side-data or a
  `dvhe`/`dvh1`/`dav1` codec tag), and the candidate evaluator skips DV sources regardless of the HDR
  setting unless a per-library **Optimise Dolby Vision** opt-in is enabled (off by default; settable
  in the library form and preserved across config backup/restore). Migration `AddDolbyVisionHandling`.

- **MP4 falls back to MKV when the audio can't be muxed.** Copying a Blu-ray audio format MP4 has no
  tag for (Dolby TrueHD, Blu-ray/DVD LPCM) into an MP4 target aborts the encode. The resolver now
  falls back to MKV in that case — the same proven pattern as image-based subtitles — but only when the
  audio is being copied; a library that re-encodes audio to a compatible codec keeps its MP4 target.

- **Timestamp and hardware-stream robustness.** Every video job now regenerates presentation
  timestamps (`-fflags +genpts`) so a source with missing or non-monotonic DTS muxes cleanly instead
  of warning or aborting (a no-op when timestamps are valid). And a hardware encode (QSV/VAAPI/NVENC)
  now drops data streams (camera timecode, GoPro GPMF) even for a Matroska output — previously dropped
  only for MP4 — since a hardware encoder can abort on one whatever the container.

### UI

- **The compare preview starts at the middle of the file.** In the Quarantine compare-to-approve
  panel (and the Settings preview it shares), both the original and encoded video previews now seek to
  their midpoint once metadata loads, so the operator lands on a representative frame instead of the
  black leader at the start.

- **A "Play both" button starts the two compare viewers together.** In the same compare panel
  (Quarantine and the shared Settings preview), one control now plays or pauses the original and
  encoded viewers at once, so they can be compared in motion without juggling two sets of controls.
  The toggle reflects the real element state, so using a viewer's own controls keeps the label honest.

### Build

- **Pinned `Microsoft.OpenApi` to 2.9.0 to clear a security advisory.** The 2.0.0 pulled in
  transitively by `Microsoft.AspNetCore.OpenApi` is flagged by NuGet audit (GHSA-v5pm-xwqc-g5wc,
  `NU1903`: a circular schema reference can trigger a stack overflow), which the CI backend build
  promotes to an error via `-warnaserror` — failing the build on a clean restore. Added a direct
  reference at 2.9.0 (latest on the patched 2.x line; the fix first shipped in 2.7.5).

## 0.2.1 — 2026-06-28

### Security

- **Admin-token auth is now covered by end-to-end tests.** A real HTTP host (with the token configured)
  asserts that every destructive or secret-bearing endpoint — settings read/save/export/import, library
  create/delete/enqueue, job clear/cancel/retry/remove/replace, replacement rollback/approve, and the
  diagnostics bundle — is rejected with `401` when the token is missing or wrong, that `/api/health`,
  `/api/ready`, and `/api/auth/status` stay open, and that a valid token passes. The auth enforcement is
  now proven at the pipeline level, not just in the matcher unit tests.

### API contract

- **The generated OpenAPI document is now self-describing.** A titled, versioned, described info
  block; every operation is grouped under an area tag (`System`, `Settings`, `Libraries`, `Inventory`,
  `Queue`, `Replacements`, `Integrations`, `Realtime`) so API browsers and client generators render a
  navigable contract; and every admin-token-protected operation now documents its `401` response while
  the open endpoints (`/api/health`, `/api/ready`, `/api/auth/status`) correctly do not. Generated by a
  single document transformer; `docs/openapi.json` regenerated.

### Pipeline robustness

- **A queued job is re-checked for eligibility just before it transcodes.** A job can sit in a long
  backlog while the library's rules tighten (e.g. the already-efficient-source floor is added) or the
  file gains an optimised sibling — previously such a job still ran and was only caught by the
  size-saving gate after wasting an encode. The dispatcher now re-evaluates the file against the
  current rules first (the shared `CandidateService` logic: rule decision, optimised-sibling skip, and
  explicit exclusions — but not the job-history overlay, so a retry still runs) and, when the file is
  no longer a candidate, marks the job `Cancelled` with the reason ("Skipped before encoding: …")
  instead of transcoding. Previews always run. This closes the gap that let pre-floor Breaking Bad
  episodes re-encode to a larger file and fail.

- **Rollback now fails safely when the quarantined original is gone.** A regression test proves that
  if the quarantined original has been purged or lost, a rollback returns a clear failure and leaves
  the in-place optimised file untouched (instead of deleting it and losing both copies) — completing
  the pipeline robustness pass, whose FFmpeg stream/container permutations, replacement state
  transitions, and candidate decisions are now all covered by adversarial tests.

### UI

- **The candidate table now pages a large library one screen at a time.** The shared candidate table
  (the fleet-wide Candidates page and the per-library Candidates tab) renders 100 rows per page with a
  range readout and prev/next controls, and resets to page one when the all/eligible/skipped filter
  changes — the same client-side paging the Queue table uses, so a library with thousands of probed
  files stays responsive instead of rendering every row.

- **Detail-sheet layout polish.** In the Queue detail sheet the box art now sits to the right of the
  technical detail and verification report (a clean left-info / right-art body) instead of being wedged
  above the progress bar. In the Inventory detail sheet the faded poster backdrop now spans the whole
  sheet — `BottomSheet` gained an optional ambient `backdrop` layer rendered behind the header and the
  full content, clipped to the panel — instead of being trapped inside the scrolling details panel.
  The backdrop is tuned to the Queue hero's intensity (a directional fade that keeps the labels
  readable on the left while the poster shows through on the right) so it is actually visible rather
  than washed out.

### Diagnostics & observability

- **The media/inventory API now exposes the optimisation marker.** `GET /api/media` and `/api/inventory`
  rows carry `optimisedMarker` — the Optimisarr version stamped into a file when Optimisarr produced it,
  or null for a source. This lets a client tell an optimised output apart from an original (and which
  version made it) without inspecting the file, closing a gap that previously required filesystem access
  to diagnose a replacement collision.

### UI

- **Inventory shows the filename, with the full path in the detail sheet.** The Inventory list now
  shows each file's name instead of its full library path (the path is still the hover title); opening
  a row shows the filename and the full relative path in the detail header, over a faded, blurred
  poster backdrop (like the Queue hero) that stays silent when no artwork resolves.
- **The Queue detail sheet shows larger box art.** Clicking a queue row now shows a larger poster of
  the title beside the job's progress and details, as a recognition aid.
- **The sidebar shows the running version alongside the build's git hash** (e.g. `v0.2.0 · a7283c3`).
  The version comes from `/api/health` — the same assembly version stamped into the `optimisarr=<version>`
  marker on optimised files — so what the UI reports always matches what the backend writes. It falls
  back to just the hash if the version can't be read.

## 0.2.0 — 2026-06-27

### Eligibility

- **A per-library "Skip already-efficient sources" toggle.** The efficiency floor that skips sources
  already too compressed to shrink is on by default per the library's preset, and can now be turned
  off per library (Libraries → video settings) to send every eligible file to the encoder anyway — the
  size-saving gate still protects the original. Settings backups carry the toggle. Migration
  `AddLibrarySkipEfficientSources` (existing libraries keep the floor on).

### Maintainability

- **`Program.cs` is now a composition root, not a 2,000-line endpoint file.** All 72 endpoints live in
  nine focused `Endpoints/*.cs` extension methods (settings, integration, exclusion, health, system,
  library, stats, replacement, and media/queue), mapped from a `Program.cs` trimmed from 1,960 to 418
  lines that now holds only service registration, startup, middleware, and the endpoint-group calls.
  The few endpoints that need startup locals (the admin token, the config directory) take them as
  method parameters, and the range-aware file server is a shared `FileServing` helper. A pure move —
  no route, name, or behaviour change — verified by the byte-identical generated OpenAPI document and
  the full test suite.

### Scalability

- **The Queue table renders one page at a time.** A large queue (thousands of jobs) previously
  rendered every row into the DOM at once. The table now pages 100 rows at a time with prev/next
  controls and a "showing X–Y of Z" line; the filter chips still count the whole set, and the
  live current-work hero is unaffected.

- **The Inventory page paginates on the server instead of downloading the whole library.** A new
  `GET /api/inventory` returns one page of files already paired with their rule verdict, plus the
  filtered total and the per-filter tallies (all/eligible/skipped/unprobed). The Inventory page now
  fetches a single page (50 rows) and drives its filter chips, counts, and pager from the server, so a
  library with thousands of files renders a page at a time rather than loading every row and every
  candidate into the browser. The rule evaluation stays pure logic over the probed inventory, so it is
  cheap even for a large library.

- **The inventory list paginates and filters in the database.** `GET /api/media` now accepts
  `status`, `search` (a case-insensitive path substring), and `page`/`pageSize`; the body stays a
  media array (existing callers are unaffected) and the pre-paging total is returned in the
  `X-Total-Count` header. Filtering, counting, ordering, and paging all run in SQL, and a new
  `(LibraryId, RelativePath)` index lets a large library page without a table sort — so Inventory stays
  responsive with tens of thousands of files. Migration `AddMediaFileLibraryPathIndex`.

### Security

- **Optional built-in admin token.** Set `OPTIMISARR_ADMIN_TOKEN` to require a bearer token for
  protected API calls and the SignalR jobs hub. The SPA shell stays loadable so it can show a token
  prompt; `/api/health`, `/api/ready`, and `/api/auth/status` remain open for health checks and
  discovery. Browser media previews append the token as `access_token` because native media requests
  cannot send custom authorization headers. Reverse-proxy authentication remains the recommended
  boundary for internet-exposed instances.

### API contract

- **The OpenAPI document is now generated and checked in CI.** `docs/openapi.json` is generated from
  the running API, and CI fails if the checked-in contract drifts. The documentation checker also
  verifies every method/path listed in `docs/api.md` exists in the generated spec, so the human API
  reference can no longer silently document missing endpoints.

### Test coverage

- **Pipeline robustness coverage now includes more MP4-family edge cases.** FFmpeg command tests cover
  `.mp4`, `.m4v`, and `.mov` subtitle conversion plus attachment/data stream exclusion, and `.m4a`/
  `.m4b` metadata handling for audio outputs. Transcode-spec tests also confirm image-based subtitles
  fall back from every MP4-family video container to MKV while MKV targets stay unchanged.
- **Replacement now has coverage for a vanished original.** When the original is deleted or renamed
  between verification and replacement (e.g. a Sonarr/Radarr upgrade), replacement fails permanently
  without creating anything, retains the verified output, records nothing, and saves no lifetime
  bytes — so the reconcile sweep fails the job once instead of retrying it forever. This closes the
  one untested branch in the destructive replacement path.

### Diagnostics & observability

- **Admin diagnostics snapshot at `GET /api/diagnostics`.** One authenticated call returns the version,
  environment (OS, framework, config path and writability), global settings, tool (FFmpeg/ffprobe) and
  hardware-encoder capability, per-library summaries (with file counts), integration summaries,
  dashboard stats, the failure summary, and the most recent captured ffmpeg logs — enough to file a
  support issue from API evidence alone. It is assembled only from non-secret data: provider tokens,
  API keys, and webhook URLs (which can embed a secret) are never included, enforced by a single pure
  redaction step and verified against real data. The endpoint is under `/api`, so the admin token
  protects it when one is set.

- **The job and failure queries take filters and paging.** `GET /api/jobs` now accepts `libraryId`,
  `category` (a failure category), and `since`/`until` (bounding a job's finished/enqueued time), plus
  `page` and `pageSize`; the body stays a `JobDto` array and the pre-paging total is returned in the
  `X-Total-Count` header, so existing callers are unaffected. `GET /api/jobs/failures` accepts a
  `libraryId` to scope the summary to one library. SQL-translatable filters run in the database; the
  date filter and ordering run in memory (SQLite can't order or compare a `DateTimeOffset`).

- **The Queue page has a Failures tab.** Rather than a new sidebar entry, failed jobs are grouped by
  reason on a tab beside the live queue, so job views stay in one place and the sidebar stays lean.
  Each group shows its plain-language reason, a count, and recent jobs; "View log" expands the
  captured ffmpeg log inline. Reads the diagnostics endpoints below, so "why did it fail?" is
  answerable in the UI without container access.

- **Failed transcodes are now answerable from the API.** `GET /api/jobs` accepts a `status` filter
  (e.g. `?status=Failed`) so callers no longer fetch every job and filter client-side, and a new
  `GET /api/jobs/failures` groups failed jobs by a classified reason — size-saving gate, container
  incompatibility, image-based subtitles, replacement collision, source/output missing, verification,
  or other — with a count and recent samples (job id, path, error) per group, largest first. The
  classification is a pure, shared `FailureClassifier`, so the same buckets drive the API and, in
  future, the UI. This makes "why are jobs failing?" answerable without reading container logs.

- **A failed transcode now keeps its ffmpeg log, served at `GET /api/jobs/{id}/log`.** When ffmpeg
  exits non-zero, its substantive stderr — stream mapping, warnings, and the error that ended the run
  — is captured (the thousands of progress frames are filtered out, and a very long log keeps its head
  and tail with the middle elided) and stored on the job. The endpoint returns it as plain text, or
  404 when a job has none. The rich "Could not find tag for codec none…" detail that previously lived
  only in container logs is now one request away.

- **A job's failure category is stored when it fails, not re-derived on every read.** The classified
  reason is written to a new `FailureCategory` column the moment a job fails, so the failure summary
  groups in the database and the class stays stable even if the message is later edited (older rows
  fall back to classifying the message). The category is also surfaced per job on `GET /api/jobs`, so
  the queue can label a failure by its kind. Migration `AddJobFailureCategory`.

### Eligibility

- **Sources already too efficiently encoded to shrink are skipped before transcoding.** A video whose
  bitrate is already very low for its resolution (e.g. a ~1.6 Mbps 1080p h264 episode) cannot be made
  meaningfully smaller by re-encoding, so it used to transcode and then fail the size-saving
  verification gate — burning GPU/CPU time for nothing. Profiles now carry an efficiency floor
  (bits per pixel-second, so it holds across resolutions and frame rates) and skip such a source up
  front with the reason "Already efficiently encoded (~X.X Mbps at 1080p) — re-encoding is unlikely to
  save space". The floor is conservative and uses the total-file bitrate (which overstates the video
  bitrate), so it only skips clear cases; the size-saving gate remains the backstop. HEVC and H.264
  profiles set a floor; AV1 sets none, as it can shrink even low-bitrate sources.

- **A file whose optimised copy already sits beside it is no longer re-transcoded.** When an
  Optimisarr-produced output (e.g. an hevc `.mp4`) remains next to its original (e.g. the h264
  `.mkv`) — left by a cleared replacement history, a move-on-complete, or a separate re-import — the
  original was still rule-eligible and would be transcoded again, only to be refused at replacement
  time because the destination is occupied. The candidate and enqueue paths now detect the marked
  sibling (same library, same path stem) and skip the original up front with the reason "An optimised
  copy already exists alongside this file", so no GPU/CPU time is spent on an encode that can never be
  applied. Only the unmarked original is held back; the optimised copy itself is unaffected.

### Replacement reliability

- **A permanently blocked auto-replace now fails the job once instead of retrying forever.** When a
  verified `ReadyToReplace` job can never be applied — the destination is already occupied by a
  different optimised file, the verified output has vanished from `/work`, or the original is gone —
  the reconcile sweep marked it with a warning and left it `ReadyToReplace`, so it was re-attempted on
  every cycle and flooded the logs. These unrecoverable outcomes are now classified as permanent and
  the job is failed once. The original is still never touched. Because the job becomes terminally
  failed, the file is also skipped by the "previously failed" overlay and counts toward auto-exclusion,
  so it is no longer re-queued and re-transcoded on each scan.

### Transcode reliability

- **MP4 outputs no longer abort on Matroska attachment or data streams.** A re-encode or remux to an
  MP4-family container (`.mp4`/`.m4v`/`.mov`) now excludes attachment (`-0:t`) and data (`-0:d`)
  streams, which MP4 cannot mux. Previously a source carrying an embedded font or cover-art
  attachment — reported by ffmpeg as `Could not find tag for codec none in stream #N` — failed the
  whole job before a frame was written. The original was always left untouched, but the file could
  never be optimised. Matroska outputs still keep these streams via the blanket stream copy.

### Preview clip mode

- **Video previews now verify the same middle sample they encode.** Long video previews still encode
  a 60-second segment from the middle of the source for fast turnaround, but verification now creates
  a temporary clipped reference from that same window before running duration, stream, size, VMAF,
  loudness, timestamp, and tail checks. The compare panel labels clipped verification as
  segment-only and keeps the saving estimate based on the sample bitrate.

### Release hardening

- **Dry-run mode is now available from Settings → General → Replacement.** Optimisarr can still scan,
  queue, transcode, verify, and preview normally, but manual replacement, auto-replace, and
  quarantine purge are blocked while dry-run mode is on. Verified outputs stop at
  `ReadyToReplace` for review, and rollback remains available for existing replacements because it
  restores protected originals.
- **Configuration import is stricter and transactional.** Backup imports now reject malformed image
  downscale modes/values and auto-enqueue windows instead of silently coercing them to defaults, and
  the whole import runs inside one database transaction so settings and definitions are applied
  together or not at all.
- **Backup restore feedback now includes Sonarr/Radarr connections.** The Settings UI reports imported
  download-manager connections and reloads that section after import instead of leaving stale data on
  screen.
- **The test suite now includes an EF migration smoke test.** It applies all migrations to an empty
  SQLite database and asserts no pending migrations remain, covering the real migration chain
  separately from `EnsureCreated`-based unit tests.
- **Release docs are safer for first-run users.** The quickstart now walks through compose selection,
  writable mounts, readiness checks, dry-run-first operation, and authenticated reverse-proxy
  exposure. Troubleshooting now covers dry-run replacement blocks, readiness failures, config import
  validation, and stale UI after image updates; the security policy spells out that the UI is an
  administrative surface and exports contain secrets.
- **Synthetic-media integration coverage has started.** A hermetic test now creates synthetic media
  files, scans them through the real inventory service, applies synthetic ffprobe JSON through the
  parser, and verifies candidate decisions through the real candidate service.
- **The roadmap now reflects existing GHCR publishing.** CI already builds the production container,
  runs the container readiness smoke test, and publishes GHCR images on non-PR branch/tag builds.
- **CI avoids duplicate Docker builds.** The Docker job now builds the production image once,
  smoke-tests that exact image, and pushes the same local tags on non-PR runs, keeping release
  hardening inside standard GitHub-hosted public-repo CI.

### Custom mode for a library's video preset

- **Setting your own codec/container is now a first-class "Custom" choice, not a warning.** The
  optimisation slider gains a **Custom** stop at the end (kept on the same control for consistency).
  Selecting it — or changing the codec/container in Advanced, which moves the slider to Custom
  automatically — is treated as a deliberate configuration: the amber "Overridden" badge and caution
  box are replaced by a calm, neutral note explaining that the slider now just sets the baseline for
  anything you leave on "Profile default." Dragging back to a preset stop clears the overrides. No
  data change — "Custom" is derived from the existing override fields, so nothing new is stored.

### Library Advanced panel spacing

- **The first section in a library's Advanced options no longer hugs the top divider.** The leading
  section (e.g. "Video" for Film/TV) had its top padding stripped, so its heading sat tight against
  the drawer border; it now gets the same breathing room as the other sections.

### Media thumbnails in the Inventory and Candidates lists

- **Every row now shows a thumbnail** (on the Inventory page and the per-library Candidates tab) so you
  can recognise an item at a glance instead of parsing a filename. The thumbnail is chosen by kind:
  - **Film/TV → poster** from a connected **Radarr/Sonarr first** (an exact, local match keyed to the
    imported file; TV rows show the series poster), falling back to a media server (Plex/Jellyfin/Emby).
  - **Music → the file's embedded cover art**, extracted with ffmpeg — no external service needed.
  - **Images → a down-scaled still** of the image itself, rendered with ffmpeg.
- All bytes are produced/proxied by the backend, so no server/API token ever reaches the browser;
  thumbnails lazy-load into a fixed box (no layout shift) and fall back silently to a plain
  placeholder. ffmpeg extraction runs with a timeout and a short negative cache so a list scroll never
  respawns it for a cover-less file. New reusable `<Thumbnail>` component and `GET
  /api/media/{id}/thumbnail` endpoint (replacing the film-only poster endpoint).
- Artwork is a **recognition aid, not decoration**: it lazy-loads into a fixed 2:3 box (no layout
  shift), degrades silently to a plain placeholder when nothing resolves, and never implies state.
  Audio/image candidates show no poster. New reusable `<Poster>` component and `GET
  /api/media/{id}/poster` endpoint; the existing Queue-hero backdrop is unchanged.

### Upgraded/renamed source files no longer leave phantom candidates and failing jobs

- **A scan now retires inventory rows whose file has vanished.** When Radarr/Sonarr upgrade a
  release and rename the file (e.g. `… WEBDL-1080p.mkv` → `… Bluray-1080p.mkv`), the new file is
  discovered but the old path used to linger forever as a phantom candidate — and any job already
  queued against it failed with a bare ffmpeg `No such file or directory`. The scan now removes a row
  whose file is genuinely gone from disk (a settled file merely skipped this pass is kept), cascading
  away its now-meaningless jobs. Rows with replacement history are never pruned, so rollback records
  survive. The reconcile stays idempotent: scanning an unchanged library removes nothing.
- **A job whose source has disappeared now fails fast with a clear reason.** Before transcoding, the
  worker checks the source still exists; if it was moved/upgraded it stops immediately with
  "Source file no longer exists … re-scan the library" instead of spinning up the hardware encoder
  only to hit a raw ffmpeg error.

### Concurrent auto-replace no longer strands a job and loses its output

- **Fixed a race where two replacements ran on the same job at once, destroying the verified
  output.** A job becomes replaceable the instant it reaches `ReadyToReplace`, and two callers
  competed for it — the worker's post-verify auto-replace and the background auto-replace reconcile
  sweep (a manual replace could join too). Because a same-container replacement puts the output back
  at the original's own path, the overlapping move sequences corrupted each other: one moved the
  output into place while the other quarantined what it found there, the final-path integrity check
  tripped, and both runs restored the original from quarantine. The safety model held — **the
  original was always preserved** — but the verified output in `/work` was deleted, leaving the job
  stuck in `ReadyToReplace` forever while the reconcile retried every few seconds. Replacement is now
  serialised per job by a process-wide claim (`ReplacementCoordinator`): the first caller wins and
  the rest back off, so only one replacement ever touches a job.
- **A `ReadyToReplace` job whose verified output has vanished from `/work` is now failed, not retried
  forever.** Such a job can never be replaced (the output is gone), so the reconcile sweep now marks
  it `Failed` with a "re-run the optimisation" message instead of looping and burying real warnings.
  The original is untouched — replacement bails before quarantining when the output is missing.
- **Quieter logs:** Entity Framework's per-query `Database.Command` logging is now at `Warning`, so a
  steady background sweep no longer floods the container log with full SQL on every cycle.

### Queue detail sheet shows the FFmpeg command

- **Clicking the job that's encoding no longer just repeats the hero panel.** The live CPU/GPU usage
  graph now appears only in the "now encoding" hero; the detail sheet instead shows the exact FFmpeg
  command for the job (and points to the hero for live usage), which is also handy for diagnosing a
  failed job.

### Exclude files from optimisation

- **You can now exclude individual files so they are never optimised again.** From a failed/stuck
  job in the Queue, hit **Exclude** — the file is added to a durable, path-keyed exclusion list and
  the failed attempt is cleared. Excluded files are skipped by scans, the candidate list, and
  auto-optimise. Each library has an **Excluded** tab listing its exclusions, where you can remove
  one to make the file eligible again. Unlike the soft "previously failed" skip (which hangs off a
  job row and disappears when the queue history is cleared), an exclusion survives clearing the
  queue, re-scanning, and re-adding the library. Your original files are never touched.
- **Files that keep failing are now excluded automatically.** After three terminal failures of a
  file's current version it is auto-excluded, so it stops burning encode time and instead surfaces
  on the Excluded tab for review (a successful encode resets the streak). The tab distinguishes
  automatic exclusions (amber, "repeated failures") from manual ones with an icon, and removing one
  resets the file's failure count so it gets a genuine fresh start.

### Decode-health no longer fails on hardware-encoder timestamp noise

- **A verified hardware-encoded output is no longer rejected for muxer DTS warnings.** The decode-
  health check decodes to the null muxer, which is stricter about timestamps than any player; a
  hardware encoder (e.g. `hevc_qsv`, NVENC) can emit equal/duplicate DTS that the muxer reports as
  "non monotonically increasing dts to muxer" — once per packet. These were being counted as decode
  errors (tens of thousands of them), failing verification on an otherwise-perfect encode. They are a
  muxing remark about the throwaway decode output, not picture corruption — genuine decode-order
  regressions are still judged by the separate timestamp-integrity gate — so they are now excluded
  from the decode-error tally. Real decode errors (corrupt frames, packet read errors) still count.

### "Scott's Settings" optimisation preset

- **A new "Scott's Settings" preset** on the library optimisation slider (Film/TV): conservative
  HEVC (H.265) in MP4 at CRF 24, HDR preserved, and audio re-encoded to AAC 96 kbps downmixed to
  stereo — a compatibility-first, space-saving bundle. It preserves HDR rather than defaulting to
  HDR-to-SDR tone mapping, avoiding the CPU-heavy software tone-map path while retaining the source
  signal. Selecting it fills the matching Advanced fields so the panel honestly shows what it does
  (the stereo downmix in particular is an explicit per-library switch). A music library set to this
  profile gets the same AAC 96 kbps stereo target.

### Re-encode oversized files already in the target codec

- **A new per-library option to re-encode large files that already match the target codec** (e.g. a
  huge HEVC remux under an HEVC preset). Off by default; when enabled, same-codec files at or above a
  configurable size (default 20 GB) become eligible so they can be shrunk, while smaller ones are
  still left untouched. The size-saving verification gate still rejects any output that doesn't get
  smaller, so the original is never lost.

### Libraries settings: cleaner, more polished form

- **Advanced options are now a distinct tinted "drawer"** — a bordered, rounded card with its own
  header band, clearly set apart from the simple controls above. Sub-section titles (Video, Audio,
  Eligibility…) are crisp uppercase labels, and the dense per-field helper text has been moved into
  hover tooltips across every section (matching the Settings page) for an easier-to-navigate form.
- **Library cards show a friendly preset name** ("Scott's Settings", "Conservative HEVC") instead of
  the raw PascalCase profile id.

### Dashboard: durable lifetime savings + live system usage

- **"Total space saved" is now a persistent lifetime total, not a figure derived from current rows.**
  Previously the headline summed the surviving `Replacement` rows, so it silently shrank whenever
  those rows went away — a quarantine purge, clearing queue/replacement history, or removing a
  library — and read as zero against a fresh database. It now accrues in durable `AppSetting`
  counters as each replacement is put in place (and is reversed when one is rolled back), so it
  survives restarts and history changes and reflects realised lifetime savings. A **Reset** control
  on the card (two-step confirm) zeroes it via `POST /api/stats/clear`; this only clears the headline
  figure and touches no files, quarantine, or rollback history.
- **The Dashboard now shows the live CPU/GPU usage graph** while a job is encoding, reusing the same
  unprivileged SignalR telemetry as the Queue view (idle hint when nothing is encoding).

### Safer replacement (hardening)

- **A cross-filesystem replacement copy is now verified by SHA-256 content, not just byte length.**
  The copy-plus-delete fallback used when an atomic rename isn't possible across mounts previously
  accepted any same-length copy; it now confirms the destination is a bit-for-bit duplicate of the
  source before the original is removed, so a truncated or corrupted copy can never stand in for it.
- **A failed replacement can no longer strand the original in quarantine.** If an output move fails
  partway and leaves a remnant at the destination — including the original's own path when the
  container is unchanged — restore now clears that disposable remnant before moving the protected
  original back, instead of skipping the restore because the path looked occupied.

## 0.1.0 — 2026-06-22

First tagged release.

### Dashboard reworked around outcomes

- **The Dashboard now leads with what Optimisarr has achieved**, replacing the stale placeholder
  pipeline card (which wrongly claimed only Discover/Probe were active). A new `GET /api/stats`
  endpoint aggregates the figures server-side:
  - **Total space saved** (headline) — realised savings across every file whose optimised version is
    in place, with original→optimised totals and the size-weighted average reduction. Rolled-back
    replacements correctly count as zero saving.
  - **Queue** (running / queued / failed), **Awaiting review** (in quarantine + reclaimable space),
    **Ready to replace**, and **Libraries** (enabled + files discovered) — each a shortcut to its
    page.
  - A **system-health** strip (service + media tools + jobs in flight).
  Aggregation is unit-tested; the page refreshes every 15s.

### Stale UI after a deploy fixed

- **`index.html` is now served with `no-cache`, and the content-hashed assets with a one-year
  immutable cache.** Previously the SPA entry point could be cached by the browser, so after pulling
  a new image users kept loading the previous deploy's asset hashes and never saw the new UI without
  a manual hard-refresh. New deploys are now picked up automatically.

### Preview can be minimised

- **The optimisation preview (Inventory → Preview) can now be minimised** to a small floating widget
  while its throwaway transcode runs, so the rest of the UI stays usable instead of being blocked by
  the full-screen panel. The widget shows live status ("Encoding 42%", "Ready", etc.); Expand
  restores the full comparison. Clicking away or pressing Escape now minimises rather than discards —
  only the explicit Close stops the preview and deletes its output.

### Quarantine: shared detail sheet + clear finished

- **The compare-to-approve review now opens in the shared bottom sheet** (with the table shrinking to
  keep rows reachable), instead of expanding a row in place — matching Inventory and the Queue.
  Clicking a "Replaced" row opens the original-vs-replacement comparison, verification report, and
  the approve / roll-back actions.
- **New "Clear finished" button** removes spent quarantine entries (rolled back + purged) from the
  list via `POST /api/replacements/clear`. These are terminal history with no rollback left, so
  clearing them only declutters — active "Replaced" entries are always kept and never touched.

### Verification page folded into the Queue

- **Removed the standalone Verification page** (and its sidebar entry). It was a filtered view of the
  same job data the Queue already shows, with the same detail sheet and gate report. The Queue gains
  **"Verified" and "Verification failed" filter chips** so you can still slice by verification
  outcome (which cuts across job status — a ready-to-replace job has passed; a job can fail a gate
  without being a hard failure). One less page to navigate for the same information.

### Cover art no longer fails the encode

- **Files with embedded cover art / poster thumbnails now optimise instead of failing.** A remux
  often carries several mjpeg/png "video" streams (attached pictures). The old `-map 0 -c:v <enc>`
  routed those tiny stills through the hardware encoder, which rejects them with `-22 (Invalid
  argument)` and aborts the whole job ("Could not open encoder before EOF" → nothing written). The
  builder now copies every stream by default and re-encodes only the primary video (`-c copy` +
  `-c:v:0`/`-filter:v:0`), so cover art, attachments and data are preserved untouched. Verified
  live against a Remux with four embedded covers.

### Queue hero & dispatch tidy-up

- The "now processing" hero shows the **file name as the title with the folder path as a small
  subtitle**, instead of the whole path as the heading.
- The steady-state "Dispatch ready · N running · work free" line is **removed** — dispatch state is
  now surfaced only when it needs attention (paused, or a backlog waiting on a closed library
  window).

### Discord notifications

- **Discord is now a first-class notification type** in Settings → Notifications, posting a native
  embed (title + description) so messages render properly. The type was supported by the backend but
  missing from the UI dropdown, so it couldn't be selected.
- **A "Webhook" target pointed at a Discord webhook URL now works automatically.** Discord rejects
  the generic `{ event, title, body }` webhook body with HTTP 400; the builder now detects a Discord
  webhook URL (discord.com / discordapp.com `/api/webhooks/`) under either the Discord or the generic
  Webhook type and sends an embed instead — so existing misconfigured targets start working without
  being recreated. Detection is pure and unit-tested.

### Auto-replace now applies retrospectively

- **Turning on a library's "Replace automatically when verified" now also applies to jobs already
  waiting in ReadyToReplace** — not just jobs that verify afterwards. Previously auto-replace was
  evaluated only inline at the moment a job finished verifying, so a job that became ready before the
  toggle was enabled (or was left ready by a transient replace failure) sat there forever needing a
  manual Replace. A periodic reconciliation pass in the dispatch loop now picks up any verified
  ReadyToReplace job whose library auto-replaces and replaces it (bounded per cycle). The original is
  still quarantined with a recorded rollback first, so the safety model is unchanged; a failure
  leaves the job ready to retry next cycle. The rule is a pure, unit-tested `AutoReplacePolicy`.

### Queue: reset button + "waiting for window" reason

- **New "Clear queue" action** resets the pending backlog in one click — removes every Queued and
  ReadyToReplace job and stops anything in flight (`POST /api/jobs/clear-pending`). Useful after a
  rules change. **No original is ever touched:** ReadyToReplace jobs hold only a verified,
  not-yet-applied output (no replacement, no rollback), so the reset discards recomputable work,
  never data; their `/work` outputs are cleaned up to reclaim space. Distinct from "Clear errored"
  and "Clear completed", which only remove terminal history.
- **The Queue now explains an idle backlog.** When dispatch is ready but nothing starts because
  every queued job's library auto-optimise window is shut, the Queue shows e.g. *"1605 job(s)
  waiting for the TV optimise window (00:00–05:00)"* instead of appearing stuck. The new
  `queue/status.waitingReason` is computed by a pure, unit-tested `QueueWaitReason`.

### Queue hero artwork backdrop

- The "now processing" hero shows a faint **backdrop image** of the title being encoded, pulled from
  the first connected media server (Plex `art`, Jellyfin/Emby backdrop) and **proxied** server-side
  so the browser never sees the token. The file path is parsed to a title/year
  (`MediaTitleParser`), the server is searched (`ArtworkSearchParser`), and the resolved URL is
  cached. Fully optional and graceful: no connected server, no match, or a music/photo job simply
  shows the plain hero. All matching/parsing logic is pure and unit-tested.
- **Fix: Plex backdrops never resolved.** Plex's plain `/search?query=` returns only search
  *providers* (no `Metadata`) on current server versions, so the lookup always came back empty. The
  service now queries `/hubs/search`, and `ArtworkSearchParser` reads results from both the
  top-level `Metadata` and the `Hub[].Metadata` shapes.

### Queue hero + Verification detail sheet

- **"Now processing" hero on the Queue page.** A card above the table shows what's being worked on
  right now — file, GPU/CPU encoder, a live progress bar with fps/speed/ETA, and the live CPU/GPU
  usage graph — with a calm idle state ("nothing processing · N queued") when the worker is between
  jobs.
- **Verification page normalised to the shared detail sheet.** Clicking a result now opens the same
  slide-up `BottomSheet` (with the table shrinking to keep rows reachable) used by Inventory and
  Queue, instead of expanding a row in place — so the gate report and per-job details are consistent
  with the rest of the app.

### Queue: separate "Clear errored" and "Clear finished"

- The single "Clear finished" button is now two: **Clear errored** (failed + cancelled jobs) and
  **Clear finished** (completed jobs), each shown only when it has something to clear and labelled
  with its count. The `/api/jobs/clear` endpoint takes an optional `scope` (`errored` / `finished` /
  all) and keeps the existing safety check, so a job still holding a live rollback is never removed.

### Scheduling rework: separate scan / auto-optimise / auto-replace

The old model welded library scanning to the once-a-night auto-enqueue window and added a separate
*global* processing window on top, which was confusing. Global settings now control **one** thing —
how often libraries are scanned — and everything about *when work happens* lives per-library:

- **Library scan = global, on an interval.** A `LibraryScanWorker` rescans every enabled library
  every *N* hours (Settings → General → **Library scan interval**, default 1h, configurable) for file
  updates and re-probes. Scanning is idempotent and probing stays continuous, so the inventory is
  always current. This is the **only** scheduling setting in global settings.
- **The global processing window is removed.** Jobs you queue manually run whenever the queue can
  start one (concurrency limit + disk/activity gates); there is no global time gate anymore.
- **Per-library "Optimise automatically" + window = when files are enqueued *and* run.** Inside a
  library's window its eligible files are auto-enqueued **and** dispatched continuously; outside the
  window that library's jobs don't start (running jobs are never interrupted). Libraries without
  auto-optimise have no window — their manually-queued jobs run any time. The window inputs are shown
  only when the toggle is on.
- **New per-library "Replace automatically when verified" toggle.** When on, a job that passes every
  verification gate is replaced without the manual "Replace" click. The original is still quarantined
  first and is fully rollback-able (kept for the quarantine-retention period), so the safety model is
  unchanged; **default off**. A failed auto-replace (e.g. an unwritable folder) leaves the job
  ReadyToReplace for a manual retry. Round-trips through config export/import.
### Image-based subtitles into MP4: auto-MKV + clearer errors

- **Sources with image-based subtitles (Blu-ray PGS / DVD VobSub) now optimise instead of
  failing.** MP4 can only carry text subtitles (mov_text), so a remux with PGS used to fail with a
  cryptic "Subtitle encoding currently only possible from text to text…". When a video job targets
  MP4 and the source has bitmap subtitles, Optimisarr now muxes to **MKV** instead, preserving the
  subtitles via a stream copy. Detection is a quick probe gated to MP4-target jobs that actually
  have subtitle tracks; the container swap is in the pure, unit-tested `TranscodeSpecResolver`, and
  `SubtitleClassifier` (also tested) decides which codecs are image-based.
- **Clearer ffmpeg failure reasons generally.** A new pure, unit-tested `FfmpegErrorInterpreter`
  translates known ffmpeg errors into actionable messages (falling back to the raw output for
  anything unrecognised), so the Queue explains *why* a job failed and what to do.

### Per-library access check + graceful replace on permission errors

- **Replace no longer 500s on a permissions problem.** A media folder the container can't write
  to made the atomic-move probe throw `UnauthorizedAccessException` (not an `IOException`, so it
  escaped the catch) and surfaced as a raw 500. The probe now treats a permission-denied write as
  "not movable", and `ReplaceAsync` does an up-front writability check that fails with a clear,
  actionable message (and leaves the original untouched) instead of a 500.
- **New per-library "Test access" check.** `GET /api/libraries/{id}/access` reports whether the
  library path exists, is readable, and is writable; the Libraries page runs it on load and shows
  a badge (`access ok` / `not writable — replace will fail` / `can't read` / `path missing`) plus
  an inline fix hint, and a per-library **Test access** button re-runs it. This surfaces a
  misconfigured mount/permission *before* a replacement fails. Verdict/message logic is a pure,
  unit-tested `LibraryAccessEvaluator`.

### Settings: full-width, simpler tabs, clearer copy

- **Fills the page.** Every Settings tab now uses the full content width with responsive multi-column
  layouts (previously all but Tools were a narrow `max-w-2xl` column leaving most of the page empty).
- **Fewer, clearer tabs (6 → 5).** "Activity" and "Connections" merged into a single **Connections**
  tab with two sections — **Media servers** (Plex/Jellyfin/Emby) and **Download managers**
  (Sonarr/Radarr) — so all server connections live in one place. Pause-while-streaming is now a
  per-server "Pause while streaming" toggle rather than a separate tab/concept.
- **Verification gates** are presented as a tidy two-column grid of self-contained gate cards, each
  with plain-language help and a note that gates fail closed.
- Consistent section intros, toggle alignment, and headings throughout; the General "Save settings"
  button now notes that connections/notifications save on their own.
- **Rollover tooltips replace the wall of help text.** A new accessible `InfoTip` (hover/focus info
  icon) carries the per-setting explanations, and every `Toggle`'s hint now appears as a tooltip on
  an info icon next to its label rather than a sub-paragraph — so each row is a single dense line
  with its definition available on demand. Applied across Settings (and every toggle app-wide).

### Media-server connections: find Plex servers + test any connection

- **Test connection** for every media-server connection (Plex/Jellyfin/Emby): a button confirms the
  URL is reachable and the token is accepted, showing the server's name and version (or a clear
  reason it failed). When editing, a blank token tests the stored one. Plex tests `GET /`;
  Jellyfin/Emby test `GET /System/Info`.
- **Plex server discovery:** after signing in with Plex, Optimisarr lists the servers on the account
  (from `plex.tv/api/v2/resources`) and fills the connection in one click — preferring the **local**
  non-relay address and using that server's own access token. No more finding a host/port or token.
- Response parsing (Plex resources + connection preference, Plex/Jellyfin identity) is pure and
  unit-tested. Jellyfin/Emby keep manual URL entry + Test (LAN UDP discovery is unreliable from a
  bridge-networked container, so it is intentionally not used).

### Queue detail view, live CPU/GPU graph, and a sidebar activity indicator

- **Queue rows open a detail bottom sheet** (the same slide-up pattern as Inventory, now extracted
  into a shared `BottomSheet` component): a large progress bar, live fps/speed/ETA telemetry, the
  resolved encoder (GPU/CPU), output size, verification report, and the replace/retry/cancel
  actions in one place. The jobs table now scrolls internally with a sticky header and **shrinks
  when the sheet opens** (its height measured live) so rows stay reachable above the panel — like
  Inventory.
- **Live CPU/GPU usage graph** while a job is encoding, pushed over SignalR (`systemMetrics`). All
  sampling is **unprivileged** — `/proc/stat` for CPU and, for the GPU, the per-process DRM fdinfo
  of our own ffmpeg child, falling back to the AMD `gpu_busy_percent` sysfs node or an `nvidia-smi`
  query. No root, CAP_PERFMON, or container privilege is required; when no source applies the graph
  shows "GPU stats unavailable". The GPU path is vendor-neutral (Intel/AMD via DRM fdinfo, AMD via
  sysfs, NVIDIA via nvidia-smi) — nothing is hardcoded to one vendor.
- **Sidebar activity indicator:** the Queue nav item shows a throbbing **GPU chip** when work is
  hardware-accelerated, or a throbbing **snail** when it's grinding on the CPU, with a running-job
  count (a small pulsing dot when the rail is collapsed). Backed by one app-wide connection so it
  stays live on any page; queue status now reports whether the active work is hardware-accelerated.

### Hardware decoding (GPU decode) with automatic CPU fallback

- **The source is now decoded on the GPU when a hardware encoder is in use.** Previously a
  QSV/VAAPI job hardware-*encoded* but software-*decoded* the input, then uploaded frames to the
  GPU — so a large (e.g. 4K) source still pinned a CPU core or more just to decode. The ffmpeg
  command now adds `-hwaccel` with a matching `-hwaccel_output_format` before the input and drops
  the now-redundant `hwupload` filter, keeping frames on the GPU end to end.
- **New setting `queue.hardwareDecode` (Settings → Encoder mode), default on.** It only takes
  effect with a hardware encoder; CPU encoding is unaffected. An HDR→SDR tone-map job keeps
  software decode, because that filter runs in software and needs frames in system memory.
- **Automatic software-decode fallback.** Not every source codec/profile can be hardware-decoded;
  if a hardware-decode attempt fails at decoder/hwaccel setup, the job is retried once with the
  software-decode command instead of failing. The fallback is scoped to decode-setup failures, so
  an unrelated late failure (e.g. the disk filling mid-encode) is not retried needlessly.

### Schedule and Verification pages

- **Schedule page** (`/schedule`): a new dedicated page showing the current dispatch status
  (ready / paused + reason, running job count, work-disk free space), the configured processing
  window and whether the current time falls inside it (overnight windows handled correctly), and
  a per-library auto-enqueue table with the configured window and the last time each library ran.
  No settings are edited here; links point to Settings where changes are made.
- **Verification page** (`/verification`): a fleet-wide audit of every job that has passed
  through the Verifying step. Shows aggregate stats (total verified, pass rate, most-common
  failing check) and a filterable table (All / Passed / Failed). Each row expands to the full
  `VerificationChecks` gate report, reusing the same component as the Queue page. Both pages
  use only existing API endpoints — no backend changes required.
- Both routes are now **enabled** in the sidebar (previously "coming soon").

### Inventory bottom-sheet: gap fix, expand/collapse, and spinner direction

- **Closed the dead gap below the table.** The table container's `max-height` deduction was
  6 rem too large, leaving a constant gap between the table and the sheet (and the viewport
  bottom when the sheet was closed). Corrected to match the actual chrome above the table plus
  the main element's bottom padding.
- **Detail sheet can now collapse to a header strip.** A chevron control toggles between the
  full detail panel and a header-only strip. Collapsing shrinks the sheet, and the existing
  `ResizeObserver` automatically gives the table back the reclaimed height. Opening a new row
  always starts expanded.
- **Fixed the working spinner spinning the wrong way.** The `rotate` icon was a
  counter-clockwise arrow paired with the clockwise `animate-spin`, so it read as reversed.
  The icon path is now mirrored so the arrow follows the rotation direction.

### Inventory bottom-sheet: sidebar respect and table shrink

- **Sheet no longer overlays the sidebar.** The bottom sheet's left edge is now offset by the
  sidebar width (15 rem expanded / 4 rem collapsed) on desktop, with a smooth 200 ms transition
  that tracks sidebar collapse. On mobile, where the sidebar is an off-canvas overlay, the sheet
  stays full-width.
- **Table shrinks when the sheet opens.** The table container's `max-height` is dynamically
  reduced by the sheet's actual rendered height (measured via `ResizeObserver`), so all rows
  above the panel remain reachable by scrolling the table, not hidden behind the sheet.

### Inventory: viewport-filling table with bottom detail sheet

- **The table now fills the viewport and scrolls internally.** The page itself no longer scrolls;
  the table body scrolls within a fixed-height container so no rows are ever off-screen without
  being reachable by scrolling the table. The `thead` is sticky so column headers stay visible.
- **File details slide up from the bottom.** Clicking a row opens a fixed bottom sheet that slides
  in from below the viewport edge — no more scrolling past 50 rows to reach the detail card. The
  sheet persists while browsing; clicking a different row updates its content. Click the row again,
  press Escape, or click ✕ to dismiss.
- **Pagination moved above the table.** The page-counter and Previous/Next controls now live on the
  same bar as the filter tabs, always visible, so pagination never requires scrolling to the bottom.

### Fix: Intel QSV / VA-API hardware encoding not used despite GPU being present

- **Hardware encoding now actually reaches the GPU.** The entrypoint used `gosu user:group`,
  which drops all supplementary groups — including the `render` group that Docker's `group_add`
  adds for `/dev/dri` access. The ffmpeg hardware-capability probe (and the encode itself)
  therefore could not open `/dev/dri/renderD128`, the confirmation probe failed, and the encoder
  fell back to CPU `libx265`. The entrypoint now detects the GID(s) of every device under
  `/dev/dri`, adds them to the app user before switching context, and calls `gosu` with just the
  username so supplementary groups are preserved.

### Preview playback fallback

- **Side-by-side previews now offer direct downloads of both exact streams.** Browser playback
  remains native—Optimisarr never creates a second, browser-friendly re-encode that could distort
  the quality comparison. When a browser cannot decode the source container, video, or audio codec,
  the operator can download either the original or real encoded preview for local inspection.

### Security

- **Updated the native SQLite library to 3.50.3.** EF Core 10.0.8 otherwise transitively selected
  `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which carries the high-severity SQLite CVE-2025-6965 advisory.

### Inventory is now bounded and inspectable

- **The Inventory no longer renders one unbounded file table.** It now displays 50 files at a
  time with explicit Previous/Next controls and a clear filtered/total count. The table is limited
  to scan-oriented fields; selecting a row opens a detail card beneath it with stream data, rule
  verdict, probe error, and the Probe/Re-probe and Preview actions.

### Fix: MP4 previews with SubRip subtitles

- **MP4/MOV video output now transcodes text subtitles to `mov_text`.** Preview jobs mapped every
  source stream and copied subtitle streams unchanged. A common Matroska SubRip subtitle therefore
  made the MP4 muxer reject the output before any video frame was encoded. Matroska outputs continue
  to preserve subtitle codecs by stream copy; MP4/MOV uses its native text-subtitle codec.

### Previews are fast now (no more sitting on "Verifying")

- **Video previews encode a short sample, not the whole file.** A preview used to run a full
  transcode *and* the full verification (decode-health + VMAF over the entire file), so a long video
  sat on "Verifying" for ages. A video preview now encodes a **60-second sample taken from the middle
  of the file** (representative content, not the intro). This originally skipped gating verification
  for speed; the current preview path now verifies against a temporary clipped reference from the same
  middle segment. Audio/image previews already finished quickly and run in full. The size saving for
  a sampled video is shown bitrate-based (`≈`) with a note that it is a sample; a full optimise still
  encodes and verifies the whole file.

### Settings preview — compare original vs encoded before committing (Phase 11)

- **Try a library's settings on a real file and see the result.** A **Preview** action on each
  eligible candidate (Inventory and the Libraries workspace) runs a throwaway transcode of that one
  file with the library's resolved settings, then shows the **original next to the encoded result**:
  a per-media-type viewer (image ↔ image, video ↔ video, audio ↔ audio, streamed with range support
  so you can seek), a size/codec/container/resolution/audio stats table with the **% size saving**,
  and the full verification report (VMAF/SSIM and the other gates).
- **Safe by construction.** A preview is a dedicated job type that runs the real
  probe→transcode→verify pipeline but **never moves or replaces** anything, writes to its own
  `/work/preview/<id>` scratch area, is hidden from the queue, is deleted when you close the panel,
  and never survives a restart (purged on startup). The original is guaranteed untouched.
- **The same visual compare now lives in Quarantine too.** The compare-to-approve panel adds the
  side-by-side viewers (the quarantined original vs the in-place replacement, both streamed from
  disk) alongside the existing size/saving and verification report — so you can see/hear the
  difference before approving or rolling back, not just read the numbers. The shared viewer
  (`MediaCompare`) is used by both the preview and the quarantine panels.
- Deferred for now: in-browser playback of some encoded codecs (HEVC/AV1) depends on the browser;
  the stats and verification still apply. Clip-mode has since shipped: sampled video previews verify
  against the same middle segment of the original and label scores as segment-only.

### The Queue shows which encoder each job used (GPU vs CPU)

- **Hardware-vs-software encoding is now visible at a glance.** Each video job records its resolved
  encoder (e.g. `hevc_nvenc`, `hevc_qsv`, `libx265`) and the Queue shows a **GPU**/**CPU** badge with
  the encoder name under the file. No more reading the ffmpeg command line to tell whether a transcode
  ran on the GPU — useful when bringing up a new GPU (e.g. an Intel N100). New nullable `Job.VideoEncoder`
  column (additive migration) surfaced through `/api/jobs`.

### Hardware encoders are confirmed by a real test encode

- **Encoder availability is now proven, not inferred.** Detection previously reported a hardware
  encoder available whenever ffmpeg listed it and a device node existed (`/dev/dri` for QSV/VAAPI,
  a working `nvidia-smi` for NVENC). That cheap check still runs as a pre-filter, but any hardware
  encoder that clears it is then **confirmed with a tiny throwaway encode** (a few 320x240 frames to
  the null muxer, using the same device-init/upload arguments a real transcode would). A
  present-but-broken driver, or a codec the GPU does not actually support (e.g. `av1_qsv` on older
  Intel), is now reported unavailable instead of being assumed to work — and selection won't pick an
  encoder that would fail at job start. CPU encoders are trusted from the listing.
- **Detection is cached** (hardware doesn't change while the process runs), so the per-job encoder
  resolution no longer re-spawns ffmpeg every time. The Tools page **Refresh** button forces a fresh
  probe (`GET /api/system/hardware?refresh=true`) for when a GPU is added or a driver is fixed.

### Fix: GPU encoding silently fell back to CPU

- **A video re-encode now always uses the selected hardware encoder.** The worker resolved a
  GPU/CPU encoder only when a file's detected `MediaKind` was exactly `Video`, but the FFmpeg
  command builder treats anything that isn't audio/image as a video re-encode. A video file
  classified `Unknown` (e.g. a row probed before media-kind detection existed) therefore skipped
  encoder selection and silently transcoded on the CPU library encoder even when NVENC/QSV/VAAPI
  was available and selected. Encoder resolution is now gated on the transcode spec actually
  re-encoding video (a non-null target video codec), so the worker and the command builder always
  agree. The chosen encoder is logged per job so CPU-vs-GPU is visible.
- **Correct per-encoder rate control and hardware device setup.** The command builder previously
  emitted `-crf` and an x264-style `-preset` for *every* video re-encode, which NVENC ignores
  (so it ran at default quality, not the configured CRF) and which QSV/VAAPI reject outright. The
  builder now branches on the resolved encoder: software x264/x265/SVT-AV1 keep `-crf`; **NVENC**
  uses constant-quality `-cq` (VBR, no bitrate cap); **QSV** uses `-global_quality` with
  `-init_hw_device qsv=hw`; **VAAPI** uses `-rc_mode CQP -qp` with `-vaapi_device` declared before
  the input and a `format=nv12,hwupload` step appended after any tone-map. (NVENC is validated on
  an RTX 4070; QSV/VAAPI argument shape is unit-tested and pending on-hardware validation — see
  KNOWN_ISSUES.)
- **Intel iGPU (N100) and AMD GPU transcoding via jellyfin-ffmpeg.** Transcoding and hardware
  detection now run through jellyfin-ffmpeg (already bundled for VMAF), which ships the Intel iHD
  driver + oneVPL (libvpl) runtime and NVENC support — so one binary covers NVIDIA, Intel QSV/VA-API,
  and AMD VA-API without chasing distro driver packages. The transcode/detection binary is
  configurable via the new `OPTIMISARR_FFMPEG` env (defaults to jellyfin-ffmpeg in the image,
  `ffmpeg` on PATH otherwise); detection and transcode share it so the reported encoder list always
  matches what runs. The compose example now documents mapping `/dev/dri` **and** adding the
  container user to the host `render` group (required to open the device) for Intel/AMD, alongside
  the existing NVIDIA reservation. The NVIDIA block now also documents the required
  `NVIDIA_DRIVER_CAPABILITIES=compute,video,utility` — without the `video` capability the NVENC
  library is not injected and hardware encoding fails with "Cannot load libnvidia-encode.so.1"
  even though `nvidia-smi` works (the plain `--gpus all` default grants only `compute,utility`).
  NVENC was validated end-to-end on an RTX 4070 with this configuration (encoder utilisation
  52–81% during a real transcode).
- **Re-classify legacy `Unknown` media.** Files probed before media-kind detection existed were left
  as `Unknown` and the idempotent scan never revisited them, so an actual video/audio/image stayed
  misclassified (and an audio/image file would have been sent down the video pipeline). A one-time
  startup backfill resets such already-probed `Unknown` files to `Discovered` so the normal probe
  worker re-probes and re-classifies them; it is guarded by a settings flag so it runs exactly once.

### Inventory and Candidates merged into one page

- **One media view instead of two.** The Inventory page now shows each file's stream detail *and*
  its eligibility — an **Optimise?** column (Eligible / Skipped / Not probed) with the reason — so
  "what do I have?" and "will it be optimised, and why?" are answered in one place rather than across
  two screens. An eligibility filter (All / Eligible / Skipped / Not probed) sits alongside the
  library filter, and the Probe/Re-probe action is unchanged (a freshly probed file gets its verdict
  immediately).
- The standalone **Candidates page and its sidebar entry are removed**; the old `#/candidates` route
  now lands on Inventory. (The Libraries workspace keeps its per-library Candidates tab.) Reuses the
  existing `/api/media` and `/api/candidates` endpoints — no API or eligibility-logic changes.

### Preset slider says exactly what every position selects

- **The per-library video preset slider is no longer a black box.** Each position now shows the
  codec it resolves to (Compatibility → H.264, Balanced → HEVC, Efficiency → AV1), with the active
  position's full codec/container/CRF still spelled out in the "Selects: …" row.
- **Accurate by construction.** Those specs are now served from the backend's `RuleProfileDefaults`
  via `/api/library-options` (new `ruleProfileSpecs`) instead of a hard-coded UI table, so the
  slider can never drift from what the server actually does when an option changes.

### Quarantine compare-to-approve

- **Review a replacement before deciding.** Each replacement on the Quarantine page now expands into
  a compare panel showing the quarantined original vs the in-place replacement — size and saving %,
  plus the full verification report (the measured VMAF/SSIM, duration, audio-retention and other
  gates, the same ✓/✗ list the Queue shows) — so the decision isn't made on a size number alone.
- **Approve or reject from one screen.** **Approve & free space** deletes the quarantined original
  now (reclaiming space immediately instead of waiting for the retention window; the replacement is
  kept and can no longer be rolled back), and **Reject (roll back)** restores the original. Both
  reuse the existing purge and rollback services — no new destructive path, safety model unchanged.
- New `GET /api/replacements/{id}` (replacement + its job's verification report) and
  `POST /api/replacements/{id}/approve` (on-demand single purge). The report renders through a shared
  `VerificationChecks` component now used by both the Queue and Quarantine pages. Visual media
  preview (players/thumbnails) is deferred.

### Unified Library & Candidates workspace (Phase 12)

- **Tune a library's rules and see what they select in one place.** Opening a library is now a
  tabbed workspace: a **Rules** tab (the existing preset + Advanced form) and a **Candidates** tab
  listing the eligible/skipped decisions for *that* library, with the same reasons the Candidates
  page shows. No more hopping to a separate screen and re-selecting the library to see a change's
  effect.
- **Re-resolve on Save.** The Candidates tab reflects the library's *saved* rules; after you Save
  (and after Scan/Enqueue) it re-fetches, and the workspace stays open so the change → effect loop
  is immediate. The tab shows the live eligible count.
- **Eligible/skipped tallies on the Libraries list.** Each library card now shows its candidate
  tally, backed by a lightweight `GET /api/candidates/summary` that reuses the pure
  `CandidateEvaluator` (counts only — the list never fetches every probed file row).
- The standalone all-libraries **Candidates page is kept** for the cross-library view; both it and
  the new tab share one `CandidateTable` component. **No domain logic changed** and the safety
  model is unchanged — enqueue still only queues; nothing here replaces or deletes.

### Opt-in image EXIF/ICC-retention verification gate

- **New verification gate for photo/image jobs.** When enabled (Settings → Verification → "Preserve
  image EXIF/ICC metadata"), an image whose re-encode silently **drops the original's embedded ICC
  colour profile or EXIF metadata** fails verification, so the original is never replaced by a copy
  that lost its colour profile or capture data. Some encoders/containers discard these by default,
  which can shift colours, so a colour-sensitive library can now demand they survive. The gate only
  flags **loss** — an output may *gain* metadata (Optimisarr stamps its own `Software` marker)
  without failing — and **fails closed**: if the metadata can't be read it blocks rather than
  assumes retention. Off by default. Metadata is read with `exiftool` (pure, unit-tested
  `ImageMetadataParser`; `ImageMetadataService` runs the process), since ffprobe does not surface
  ICC/EXIF reliably across the still formats. Wired through the verification policy, settings
  persistence, and the API like the existing SSIM gate.

### Output filename collision fixed (work path + replacement)

Two source files that differ only by extension (e.g. `photo.bmp` and `photo.tif`, both targeting
`photo.webp`) used to resolve to the **same** output path, so the second job could clobber the
first's output. Closed on both layers, with no change to the safety model (originals were already
recoverable; this prevents wasted/incorrect work):

- **Unique work output per source.** Each file's transcode now lands under a per-media-file work
  root (`/work/<mediaFileId>/…`), so two sources sharing a stem can never write to the same work
  path and overwrite each other's verified output before it is moved or replaced. Pure, unit-tested
  `WorkOutputRoot`; the move-to-target destination still mirrors the library's natural structure
  (the id segment never leaks into the target folder).
- **Safe-fail on replacement collision.** If a verified output's final destination is already
  occupied by a *different* file (another source that optimised to the same name), the replacement
  now **fails with a clear "would collide with an existing file" reason and leaves the original
  untouched** — instead of quarantining and then erroring on the move. An unchanged-container
  replacement landing back on the original's own path is still the normal case, not a collision.
- **Work scratch directories are pruned.** Because each job's output now lives under
  `/work/<mediaFileId>/…`, a finished job (deleted, moved to a target folder, or replaced) now
  removes the empty per-media scratch directory it leaves behind, so `/work` no longer accumulates
  one empty tree per file ever processed. Pruning only ever deletes *empty* directories and never
  the work root, so it cannot touch real output (pure, unit-tested `WorkPaths.PruneEmptyAncestors`).

### Per-library move-overwrite control + explicit preset sliders

- **Overwrite tickbox for "move output to a target folder".** When a library moves its completed
  output into a target folder, a new per-library **Overwrite an existing converted file** option
  controls what happens if a converted file is already there: on → replace it; **off (default) →
  the job fails with a clear reason** and leaves the new output in the work directory, rather than
  silently clobbering the existing file. Originals are never affected either way. Schema column
  `Library.MoveOverwrite` via migration `AddLibraryMoveOverwrite`, wired through the request
  parser, DTO, and config import/export. (The trash/quarantine directory is configurable via
  `OPTIMISARR_TRASH_DIR`, and quarantine retention via Settings → General → Replacement.)
- **Sliders now say exactly what they select.** The per-library video and image preset sliders show
  a "Selects: …" badge row with the concrete codec/container/CRF (video) or format/quality (image)
  the current position resolves to — accounting for any Advanced overrides — so the slider is no
  longer a black box. (Richer slider options are tracked on the roadmap.)

### Image optimisation: JPEG/WebP/AVIF, downscaling, and a portable marker

A big expansion of the image pipeline so it fits real media stacks rather than assuming WebP
everywhere.

- **Three output formats on a compatibility→efficiency slider.** WebP-only was the wrong default —
  **Plex does not display WebP photos**, and AVIF support is newer-clients-only. Photo libraries now
  get their own one-choice slider (the image counterpart of the video preset): **JPEG** (max
  compatibility — every server/client incl. Plex), **WebP** (smaller; Jellyfin/modern), **AVIF**
  (smallest; newer clients only). All three are genuinely wired in the command builder — JPEG via
  `mjpeg -q:v`, AVIF via `libaom-av1` constant-quality CRF — with a single
  0–100 quality mapped onto each encoder's native scale. JXL is detected as a *source* but is no
  longer an encode target (no media server displays it).
- **New default for Photo libraries is JPEG**, not WebP — safety/compatibility beats savings, and a
  fresh photo library now displays on every server out of the box. (Existing libraries keep whatever
  they had.)
- **Downscaling.** A per-library option fits images within a named cap (**4K** / **1080p**), a
  **custom max long-edge**, or a **percentage** of the original — always keeping aspect ratio and
  **never upscaling** (pure `ImageScale` filter builder, unit tested). The verification "Dimensions"
  gate is now downscale-aware: an operator-requested downscale passes (judged for no-enlarge and a
  preserved aspect ratio), while an *unrequested* shrink still fails as a corrupt encode — mirroring
  how an intentional audio downmix is handled.
- **Portable optimisation marker for every image format (resolves `KNOWN_ISSUES.md` #1).** ffmpeg's
  still encoders silently drop `-metadata`, so an image's "already optimised" fingerprint is now
  written and read with **exiftool** in the standard EXIF/XMP `Software` field
  (`optimisarr/<version>`) — a new `exiftool` dependency added to the image. The marker now travels
  *with the file* for JPEG/WebP/AVIF, surviving a database wipe or a move to another machine, exactly
  like the container marker on video/audio. Writing is best-effort: if exiftool is unavailable,
  re-optimisation is still prevented by the database history and the "already in the target format"
  check.
- Schema: two new `Library` columns (`ImageDownscaleMode`, `ImageDownscaleValue`) via migration
  `AddLibraryImageDownscale`; wired through the request parser/validation, library DTO, rule
  resolver, and config import/export.

### Image quality (SSIM) verification gate

- **Opt-in image structural-quality gate.** Image jobs can now be held to a perceptual
  bar, the still-image counterpart of the VMAF gate for video. When enabled, the output
  still is scored against the original with FFmpeg's `ssim` filter (the distorted picture
  `scale2ref`-scaled to the reference so dimensions match), and replacement is blocked when
  the all-channel SSIM falls below a configurable floor (conservative default 0.95). Like the
  other quality gates it **fails closed**: if SSIM can't be measured, the job fails rather than
  replacing on unproven quality. All gate logic is pure and unit tested
  (`ImageSsimParser`, `VerificationEvaluator`), with the measurement isolated in
  `ImageQualityService` (no live FFmpeg in tests). Off by default; configured from Settings →
  Verification, persisted via two new settings keys and round-tripped by config import/export.
  This satisfies the Phase 10 exit criterion that image verification can block replacement on
  quality loss. (EXIF/ICC-profile retention remains deferred — it is coupled to the
  exiftool metadata-writing work tracked in `KNOWN_ISSUES.md` #1, since `libwebp` drops
  metadata today.)

### Image optimisation: skip animated images; Candidates profile column; KNOWN_ISSUES

- **Animated images are left untouched.** An animated GIF (or animated WebP) is really a short
  video; treating it as a still flattened it into a broken, larger single-frame output (which
  verification correctly failed, leaving the original safe — but wastefully). The probe now records
  the picture stream's **frame count** (`MediaFile.FrameCount`, migration `AddMediaFileFrameCount`),
  and a multi-frame image is skipped as a candidate with a clear "Animated image (N frames)" reason.
- **Candidates page no longer shows a video profile for audio/image rows.** The "Profile" column
  (a video preset) now shows "—" for audio and image files, which are governed by their own
  audio/image rules — matching the same fix already applied to the Libraries page.
- Added **`KNOWN_ISSUES.md`** documenting the open items found during live testing: the WebP marker
  not round-tripping (ffmpeg limitation; re-optimisation still prevented by the DB history), and the
  output-filename collision when two sources share a stem (originals never lost; only an optimised
  output can be overwritten in move-on-complete mode).

### Fix: discovered files are now probed automatically (queue was empty after a scan)

- Scanning only records that a file *exists*; candidate evaluation needs its codec, media kind, and
  dimensions, which come from a probe — but **nothing probed discovered files automatically**.
  Probing was a manual, per-file button on the Inventory page, so a freshly scanned library sat at
  "Discovered", produced **zero candidates, and could never be enqueued** (for any media type —
  it surfaced first on new Music/Photo libraries).
- Added a **background prober** (`MediaProbeWorker`) that probes discovered files in batches
  shortly after a scan, so a library becomes a set of candidates without hand-probing each file.
  Probe failures are recorded as `ProbeFailed` and not retried, so the sweep converges.
- **Auto-enqueue** now probes a library's newly discovered files between scanning and enqueuing,
  so files found in a run can actually be queued in that same run (previously its scan-then-enqueue
  enqueued nothing because the just-discovered files had no probe data yet). Shared via a new
  `LibraryInventoryService.ProbePendingAsync`.

### Fix: media-type handling — non-video libraries now work properly

A sweep of places that assumed every library is video:

- **Scans now discover the right files for the library's type.** The scanner only knew video
  extensions, so a **Music or Photo library discovered nothing** (and a re-scan of an "Other"
  library missed its audio/images). `LibraryScanner` now has separate video/audio/image extension
  sets and an `ExtensionsFor(MediaType)` helper; the inventory scan picks the set matching the
  library — audio for Music, images for Photo, video for Film/TV, all three for Other — so a Film
  library still ignores stray poster images while a Music/Photo library finds its content. The
  image set is shared with the kind classifier so discovery and classification can't drift.
- **New `Photo` media type.** Still-image libraries no longer have to masquerade as "Other": a
  `Photo` type discovers images and exposes the image rules. The Advanced sections are now scoped
  via `isVideoType`/`isAudioType`/`isImageType` (images show for Photo *and* Other; the Audio
  channels control is hidden for a Photo library, which has no audio).
- **The rule-profile badge no longer shows on non-video libraries.** A Music or Photo library was
  labelled e.g. "ConservativeHevc" — a meaningless video preset. The profile badge on the library
  card, the preset slider, and the preset summary are all video-only now; Music and Photo show a
  type-appropriate note (audio → Opus, images → WebP) instead of a video preset.
- **Candidates page is kind-aware.** Its "Video" column (which only ever showed the video codec)
  is now a **Codec** column showing the codec that matters for each file — audio codec for music,
  image codec for stills — and audio/image rows carry a small **Kind** badge. The candidate API
  gained `mediaKind` and a unified `codec` field to back this.

### Library form: surface preset overrides, and unsaved-changes tracking

- **Preset override visibility.** When a library sets its **Target codec** or **Container**
  manually in Advanced, the compatibility↔efficiency preset slider can imply a codec that isn't
  actually used. The slider now stays editable (it still sets the baseline the non-overridden
  values follow) but shows an **"Overridden"** badge and a short note saying which setting was
  taken over (e.g. "codec (AV1) and container (.mkv)"), with a **"Reset to preset"** action that
  clears those two overrides. Chosen over disabling the slider so the operator is never trapped
  and the state is honest. Only the codec/container overrides trigger this — unrelated Advanced
  settings (audio, eligibility, etc.) don't.
- **Unsaved-changes (dirty) tracking.** The library editor now tracks whether the form differs
  from when it was opened: **Save** is disabled until something actually changes; an **"Unsaved
  changes"** indicator appears next to it; and discarding edits is guarded — cancelling, opening a
  different library, or starting a new one while there are unsaved changes prompts for
  confirmation, as does a full page reload/close.
- **Navigation guard.** Leaving the page entirely with unsaved edits — clicking another sidebar
  item or using the browser back/forward — now prompts the same confirmation instead of silently
  discarding the changes. Implemented as a single leave-guard in the hash router that any page can
  register while it has unsaved work (the library editor is the first to use it); a declined prompt
  cancels the navigation and keeps you on the page.

### Image optimisation: per-library overrides (Phase 10)

- A library can now **override the image rules**, the same way it overrides audio/video: a **target
  format**, an **image quality** (1–100), and a **"re-encode lossy images too"** toggle (which makes
  already-compressed sources like JPEG eligible, not just lossless PNG/BMP/TIFF/GIF). Stored as three
  new nullable `Library` columns via migration `AddLibraryImageOverrides`; `null` means "use the
  default" (WebP, quality 80, lossless-only). Wired end to end: `RuleOverrides`/`RuleResolver`, the
  library request parser (with validation), the library DTO and `library-options`, and the
  secret-free config export/import snapshot.
- The override controls appear in a new **Images** section of a library's Advanced options, scoped to
  mixed **Other** libraries (where still images live — there is no dedicated photo type). The
  **format picker only offers WebP** for now: `ImageTarget.EncodableFormats` gates the choice to
  formats whose encode is actually wired, so an operator can't select AVIF/JXL and have the job fail
  later — they appear automatically once their encode lands.

### Fix: scope the optimisation-preset slider to the library's media type

- The top-of-form **compatibility↔efficiency preset slider** is a *video* decision (it picks
  H.264/HEVC/AV1), but it was shown for every media type — so switching a library to **Music**
  left the video slider and its video-centric summary in place, unchanged. It is now scoped like
  the Advanced sections: Film/TV and Other show the slider (and remux toggle + preset summary),
  while a Music library shows a short audio-appropriate note instead (lossless → Opus 128 kbps by
  default; codec/bitrate in Advanced). No behaviour change to saved values — purely which control
  is presented.

### Image optimisation: kind-aware verification gates (Phase 10)

- Verification is now **image-aware**, so an image job can pass through to replacement instead
  of falsely failing. Previously every non-audio job was treated as video, so a still — which has
  no duration — would fail the duration gate outright. An image is now judged as a still: it must
  **decode cleanly**, be **readable**, contain a **picture stream**, **keep its dimensions** (no
  downscaling is performed yet, so any shrink is a degenerate/corrupt encode and fails), and be
  **smaller** than the original. The time-based and stream gates that don't apply to a still
  (duration, audio/subtitle retention, A/V sync, timestamps, tail, HDR, VMAF, loudness/clipping)
  are skipped rather than evaluated.
- `VerificationInput` gains original/output width+height (populated from the existing output and
  original probes), feeding the new **Dimensions** gate. All new gate logic is pure and unit
  tested. Per-kind quality scoring (SSIM) and EXIF/ICC-retention gates for images are still to
  come.

### Image optimisation: transcode command building + marker (Phase 10)

- The image pipeline now **produces an output**, not just a candidate decision. `TranscodeSpec`
  carries an image encoder + quality, `TranscodeSpecResolver` resolves an image job to a
  `libwebp` encode with the target extension (e.g. `Photo.png` → `/work/.../Photo.webp`), and
  `FfmpegCommandBuilder` emits the encode: `-map_metadata 0` (so the source **EXIF/ICC profile
  is preserved** — a Phase 10 verification deliverable), the primary picture stream mapped, the
  encoder, and `-quality <n>`.
- **Optimisation marker on image outputs.** Like video/audio, an image output is stamped with the
  `optimisarr` marker so a re-optimised image is recognised and skipped — closing the
  re-optimisation loop the candidate rules opened. (The marker is written into the output
  metadata; its persistence across the WebP container is verified against the bundled ffmpeg
  in-container, with the candidate "already in target format" check as the active guard in the
  meantime.)
- The queue dispatcher no longer resolves a video hardware/software encoder for image jobs (only
  video re-encodes need one); the image encode uses the encoder from the spec.
- **WebP is the only reachable target today** (per-library image overrides aren't wired yet, so
  the resolved format is always the WebP default). AVIF (`libaom-av1`) and JXL (`libjxl`) are
  selectable in `ImageTarget` but their quality mapping is not wired: the builder **throws a clear
  `NotSupportedException`** rather than emit a wrongly-scaled encode, until those parameters are
  validated against the bundled encoders. Image verification gates and per-library overrides/UI
  follow in later slices.

### Image optimisation: candidate rules (Phase 10)

- First slice of **image optimisation**: an image file is no longer skipped as "not available
  yet" — `CandidateEvaluator` now decides image eligibility like it does audio. A new pure,
  unit-tested `ImageTarget` defines the supported modern target formats (**WebP** — the
  compatible default — **AVIF**, and **JXL**), the conservative defaults (WebP, quality 80, a
  200 KB minimum size), and which source formats are worth re-encoding.
- **Lossless sources (PNG/BMP/TIFF/GIF) are eligible** to re-encode to the target format — a
  large saving with no quality loss. An **already-lossy image (e.g. a JPEG) is left untouched by
  default**; a per-library **"re-encode lossy images"** opt-in makes it eligible too. A still
  already in the target format is skipped (mapping ffprobe's codec names — an `.avif` probes as
  `av1`, a `.jxl` as `jpegxl` — back to the chosen format), as is anything below the minimum
  size. New `RuleSettings` fields `TargetImageFormat`, `ImageQuality`, and `ReencodeLossyImages`
  carry the rules; per-library overrides, command building, and verification follow in later
  slices.

### UI polish: mobile layout and a tidier library settings form

- **Mobile layout.** On small screens the sidebar is now an off-canvas drawer (opened from a
  hamburger in a new mobile top bar, dismissed by tapping the backdrop) and returns to the
  static, collapsible rail at `md+`. Fixed the **horizontal overflow** where page content ran
  off-screen to the right on phones — the main content column was sizing to its widest child;
  it now has `min-w-0` so wide tables/grids stay within the viewport (and scroll within their
  own card). Library card actions wrap instead of overflowing, and page padding tightens on
  mobile. On the **Settings** page the tab bar now scrolls horizontally instead of wrapping,
  and the activity/notification/connection list rows wrap their badges and actions onto a
  second line rather than overflowing. The **Inventory, Candidates, Queue, and Quarantine**
  tables now hide secondary columns on small screens (revealing them progressively at `sm`/`md`/
  `lg`) so the essential columns and actions fit a phone without horizontal scrolling.
- Fixed two follow-ups from the above: the mobile sidebar drawer now always shows nav **labels**
  (the collapse-to-icons state is desktop-only, so a previously-collapsed session no longer left
  the full-width drawer icon-only), and the library **Advanced options** panel always opens
  collapsed instead of auto-expanding for libraries that had non-default settings.
- **Library settings form.** The expanded per-library settings are reorganised into clearly
  titled sections — **Video**, **Audio**, **Audio channels**, **Eligibility & queue**,
  **Completed output** — each with a one-line description and separated by dividers, instead of
  one long interleaved list. Sections are **scoped to the library's media type** (Video only for
  Film/TV, Audio only for Music, both for Other), so an operator only sees the controls that
  apply. All settings remain behind the collapsed **Advanced options** panel; the simple choice
  (name, path, type, and the compatibility↔efficiency preset) stays up front.

### Optimisation preset is a simple compatibility↔efficiency slider (Phase 10)

- The per-library encode preset is now a single **compatibility → efficiency slider**
  (Compatibility / Balanced / Efficiency → H.264 / HEVC / AV1) instead of a codec-named
  dropdown, so the common case is one self-explanatory choice. A separate **"Just clean up
  containers — no re-encode"** toggle above the slider selects the Remux/Cleanup profile (which
  isn't on the quality axis) and disables the slider while it's on. The live preset description
  still explains the chosen tradeoff. Purely presentation over the existing `ruleProfile` value
  — no API change — and every exact knob (codec, container, CRF, audio codec/bitrate, downmix,
  HDR, resolution cap) stays under **Advanced options**.

### Researched, sane default profiles per container/use-case (Phase 10)

- Each rule profile now ships an **opinionated, matched container + quality default** instead of
  leaving every knob to the operator, based on current (2026) device/codec compatibility and
  visually-transparent quality research:
  - **Conservative HEVC** → **MP4** container (HEVC + AAC plays on virtually all phones, smart
    TVs, and Apple devices), default **CRF 24** (visually transparent at 1080p).
  - **Compatibility H.264** → **MP4** container (H.264 + AAC plays everywhere), default **CRF 20**.
  - **Experimental AV1** → **MKV** container (AV1 + Opus for maximum efficiency; MP4 + Opus has
    poor player support), default **CRF 30**.
  - **Remux/Cleanup** → MKV, unchanged (no re-encode, no CRF).
- **Default CRF** is new: previously a re-encode with no per-library quality override fell back
  to the encoder's arbitrary built-in default (e.g. libx265's 28). Each re-encode profile now
  has a researched, visually-transparent `DefaultCrf`; a per-library `QualityCrf` override still
  takes precedence (`RuleSettings.DefaultCrf`, applied in the queue dispatcher).
- **Audio still defaults to copy** on every profile — a video re-encode never silently downgrades
  the original audio (including lossless surround). The profile descriptions document the
  recommended audio codec (AAC for the compatibility profiles, Opus for AV1) to opt into in
  Advanced options.
- **Behaviour change for libraries using profile defaults:** the HEVC and H.264 profiles now
  remux into **MP4** rather than MKV, and re-encodes target the researched CRF. Libraries with an
  explicit container or `QualityCrf` override are unaffected. Pure `RuleProfileDefaults` /
  `RuleResolver` / `TranscodeSpecResolver` changes are unit tested.

### Re-encode lossy audio when it genuinely saves space (Phase 10)

- Audio optimisation previously only considered **lossless** sources (FLAC, ALAC, PCM, …),
  leaving already-lossy files untouched to avoid generational quality loss. Each library can
  now opt in to **re-encoding lossy audio too** (e.g. a 320 kbps MP3 → 128 kbps Opus) via a new
  "Re-encode lossy audio too" toggle in the audio Advanced options. It defaults to **off**, so
  the conservative lossless-only behaviour is unchanged unless the operator opts in.
- The opt-in only makes a lossy file eligible when re-encoding it would **genuinely save
  space**: the source bitrate must exceed the target by at least 25%
  (`AudioTarget.LossyReencodeSaves`). A file already at/near the target, or one whose source
  bitrate ffprobe could not report, is left untouched with a clear reason — so the queue never
  churns a file for a marginal saving or a likely size *increase*. Re-encoding a file already
  in the target codec is still skipped as before.
- Probing now records the **source audio bitrate** (the highest audio stream's `bit_rate`, or
  the container bitrate for an audio-only file; never the container bitrate of a video file,
  which is dominated by video). New nullable `MediaFile.AudioBitrateKbps` and non-nullable
  `Library.ReencodeLossyAudio` columns (one additive migration, defaulting existing rows to
  null/false; re-running it is a no-op), carried in config export/import and surfaced as a Kind-
  appropriate toggle. The pure `CandidateEvaluator`, `RuleResolver`, `AudioTarget`, and
  `MediaProbeService.Parse` changes are unit tested.

### Library Advanced options are scoped to the media type (Phase 10)

- The library Advanced-options form now shows **only the controls that apply to the library's
  media type**: video knobs (target video codec/container, HDR handling, encoder preset, CRF,
  resolution limit, the video-audio codec/bitrate, and the VMAF quality gate) for **Film/TV**
  libraries, and the audio target codec/bitrate for **Music** libraries. A mixed **Other**
  library still shows everything, since it may hold more than one kind. The stereo-downmix
  toggle stays visible for every type because it applies wherever audio is re-encoded.
- This removes the previous clutter where, e.g., a Music library showed video codec, CRF,
  preset, resolution, HDR, and VMAF (all irrelevant to audio) and a Film/TV library showed the
  audio-only "Audio target codec". Purely a UI refinement — the underlying per-library
  overrides are unchanged and any previously stored values are preserved.

### Stereo downmix (Phase 10)

- Each library can now **downmix multichannel audio to 2.0 stereo** on re-encode, in Advanced
  options. It applies to audio-only jobs and to the re-encoded audio of a video transcode
  (only where the audio is actually re-encoded — a copied track keeps its layout), emitting
  `-ac 2`. Defaults to **off**, so surround is preserved unless the operator opts in. New
  non-nullable `Library.DownmixToStereo` column (additive migration defaulting existing rows
  to false; re-running it is a no-op), carried in config export/import.
- The verification audio-fidelity gate understands the **intentional reduction**: a requested
  downmix (e.g. 5.1 → 2.0) passes instead of being flagged as a silent channel loss, while an
  *unrequested* downmix still fails and a downmix that dropped audio entirely still fails. The
  pure `FfmpegCommandBuilder`, `TranscodeSpecResolver`, `RuleResolver`, and
  `VerificationEvaluator` changes are unit tested.

### Audio codec selection for video transcodes (Phase 10)

- A video re-encode previously always copied its audio (`-c:a copy`). Each library can now
  opt to **transcode the audio of a video** to a chosen codec and bitrate in Advanced options
  — **AAC** (the broadly compatible default), **Opus**, or **MP3** — reusing the same encoder
  mapping as audio-only jobs. The control defaults to **Copy (leave audio untouched)**, so
  nothing changes unless the operator opts in. Works whether the video is re-encoded or just
  remuxed; subtitles and video are still preserved. New nullable `Library.VideoAudioCodec`/
  `VideoAudioBitrateKbps` columns (additive migration; re-running it is a no-op), validated on
  save and carried in config export/import.
- Verification understands the intentional re-encode: a video job that transcoded its audio
  may **normalise the sample rate** (e.g. to 48 kHz) without tripping the audio-fidelity gate,
  exactly like an audio-only job. A copied audio track (the default) still must keep its
  original sample rate, and a silent channel downmix still fails for both. The pure
  `FfmpegCommandBuilder`, `TranscodeSpecResolver`, `RuleResolver`, and `VerificationEvaluator`
  changes are all unit tested.

### Per-library audio rules

- Each library can now **override the audio target codec (Opus, AAC, or MP3) and bitrate**
  in Advanced options, layered on the profile defaults exactly like the video overrides; the
  resolver maps each codec to its encoder and container (`.opus`/`.m4a`/`.mp3`). Unset
  libraries keep the conservative Opus 128 kbps default. The settings are validated on save
  and carried in config export/import. New nullable `Library.AudioTargetCodec`/
  `AudioBitrateKbps` columns (additive migration; re-running it is a no-op).
- Fixed the optimisation marker not surviving in `.m4a`/`.m4b` (AAC) outputs — they now get
  the same MP4 metadata flag as `.mp4`, so an AAC-target audio file is recognised and never
  re-optimised.

### Audio optimisation (Phase 10)

- **Lossless audio files can now be optimised** through the same safe pipeline as video.
  A lossless source (FLAC, ALAC, PCM/WAV, APE, WavPack, TrueHD, …) is offered as a candidate
  to re-encode to **Opus 128 kbps**; already-lossy audio (MP3, AAC, …) is left untouched to
  avoid generational quality loss, and audio already in Opus or below a small size threshold
  is skipped with a clear reason.
- The whole pipeline is honoured: the transcode copies embedded cover art and all metadata
  and stamps the optimisation marker; verification runs the audio-appropriate gates (decode
  health, duration, audio-track retention, channel-layout fidelity, size, and the opt-in
  loudness/clipping gates) while skipping video-only ones; the verified output only replaces
  the original on the usual explicit, reversible Replace action. The audio re-encode is
  allowed to normalise the sample rate (Opus is always 48 kHz) without tripping the fidelity
  gate. The target is a conservative fixed default for now; per-library audio rules come next.

### Media-kind detection (Phase 10 groundwork)

- Every probed file is now classified as **video, audio, or image** (or unknown) by a pure,
  unit-tested `MediaKindClassifier`, and the kind is stored on the file and shown as a new
  **Kind** column in the Inventory. This is the first step toward optimising audio and
  images through the same safe pipeline as video.
- Detection is robust about **embedded cover art**: an audio file's album art is an
  attached-picture stream, not real video, so such files are correctly classified as audio
  (and the cover no longer leaks in as the file's "video codec"). Still images are
  recognised by extension since they probe as a one-frame video stream. New nullable-safe
  `MediaFile.MediaKind` column (additive migration defaulting existing rows to `Unknown`
  until re-probed; re-running it is a no-op).
- Candidate evaluation is now **media-kind aware**: audio and image files report an honest
  "not available yet" skip reason instead of looking like a probe failure, and the dispatch
  is structured so the upcoming audio/image rules slot straight in. Video is unchanged.
- Groundwork for the audio pipeline: a pure, unit-tested audio branch in the ffmpeg command
  builder and transcode-spec resolver re-encodes audio to an efficient default (Opus 128 kbps)
  while copying embedded cover art and metadata untouched and carrying the optimisation
  marker.
- Verification is now **media-kind aware**: an audio job runs the decode, duration,
  audio-retention, size, and (opt-in) loudness/clipping gates, and skips the video-only ones
  (video-stream-present, HDR, colour, A/V sync, timestamp/tail integrity, VMAF). Audio is
  still gated at the candidate layer, so no audio job runs yet — the safety gates are in
  place for when it is switched on.

### Settings is tabbed, and Tools lives inside it

- The Settings page is now organised into tabs — **General** (the core queue, verification,
  and replacement options the single Save button persists together), **Activity**,
  **Notifications**, **Connections** (Sonarr/Radarr), **Tools**, and **Backup** — so each
  concern is one click away instead of a long scroll.
- **Tools (FFmpeg/ffprobe status, hardware acceleration, encoders) has moved out of its own
  sidebar entry into a Settings tab.** The hardware panel is now a reusable `ToolsPanel`
  component. The old `/tools` link still works — it lands on Settings with the Tools tab
  open — so nothing breaks.

### Clear finished jobs from the Queue

- The Queue page has a **Clear finished** button that removes completed, failed, and
  cancelled jobs so the list doesn't grow without bound. Re-optimisation of the cleared
  files stays blocked by the embedded optimisation marker, so losing the history rows is
  safe.
- **A job whose original is still in quarantine is never cleared** — it is the live
  rollback path, and the safety standard requires keeping it. Such a job becomes clearable
  only once its replacement has been rolled back or purged; the pure, unit-tested
  `JobClearing` rule enforces this, backed by the restrict foreign key.

### Files carry proof they were optimised (durable re-optimisation guard)

- Every optimised output is now **stamped with an `optimisarr` tag in its container
  metadata** (a re-encode and a remux alike). The probe reads that tag back into the
  inventory, and the candidate evaluator skips any file that carries it — first and
  unconditionally, ahead of every other rule.
- Unlike the database job-history guard (which is local and can be cleared), the mark
  **travels with the file**: move the media to another machine, reinstall Optimisarr, or
  clear the queue, and the file is still recognised and never transcoded a second time.
  The two guards are complementary — history catches files this instance handled; the
  embedded mark catches everything, everywhere.
- MP4/MOV outputs get the muxer flag needed for custom tags to round-trip; Matroska and
  others preserve them by default. New nullable `MediaFile.OptimisedMarker` column (additive
  migration; re-running it is a no-op). Marker key and all gate logic are pure and unit
  tested.

### Verification: decode-timestamp monotonicity and truncated-tail gates

- Verification now checks the converted output's **video decode timestamps are
  monotonic**. A new pure, unit-tested `PacketTimestampParser` reads ffprobe's
  per-packet timestamp stream and tallies any decode timestamp that steps backward; a
  `TimestampIntegrityCheck` gathers it with a cheap metadata-only probe (no decode).
  Out-of-order packets can stall or desync playback even on a file that otherwise
  decodes, so any regression fails verification and blocks replacement.
- The same single packet probe now also gates **tail integrity (truncated/partial last
  GOP)**: it tracks the output's latest presentation time — where the video genuinely
  ends — and fails the job when that falls materially short of the source runtime (over a
  second and over 2%, tolerances chosen to absorb last-frame and B-frame reorder slack).
  This catches the dangerous case the duration gate cannot: a truncated encode whose
  container header still claims the full length. Both gates are always on when the
  output's packet timestamps are readable and simply abstain when they are not, never
  blocking on missing evidence. **This completes Phase 9's container/stream-integrity
  checks.**

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
