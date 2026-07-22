# API reference

Optimisarr's web UI uses the same HTTP API documented here. The API is useful
for local automation, health checks, and read-only dashboards, but it is not a
stable public contract yet. Check this page against the running version before
building long-lived integrations.

The generated OpenAPI 3.1 document is checked in at [`openapi.json`](openapi.json)
and regenerated in CI from the running API. This page is the readable companion
to that machine-readable contract.

Examples below assume Optimisarr is reachable at `http://localhost:8787`.

```bash
curl http://localhost:8787/api/health
```

## Basics

- Requests and responses are JSON unless the endpoint returns media content.
- Write requests use `POST`, `PUT`, or `DELETE`.
- Validation errors return `400` with an `error` field when the endpoint can
  describe the problem.
- Readiness failures return `503` from `/api/ready` with a `detail` string.
- If `OPTIMISARR_ADMIN_TOKEN` is set, protected endpoints require
  `Authorization: Bearer <token>`. Put Optimisarr behind an authenticated reverse
  proxy before exposing it outside a trusted network; the token is a built-in
  backstop, not a full public-access security model.
- Live queue updates are also published through the SignalR hub at `/hubs/jobs`.

Common status codes:

| Code | Meaning |
|---|---|
| `200` | Request succeeded and usually returned JSON. |
| `204` | Delete-style request succeeded with no response body. |
| `400` | Request body or query value was invalid. |
| `401` | `OPTIMISARR_ADMIN_TOKEN` is set and the request did not include the correct token. |
| `404` | Requested library, media file, job, preview, exclusion, or replacement was not found. |
| `409` | The requested action is not allowed in the current state. |
| `503` | Readiness failed; check the response detail before starting work. |

## Authentication

`GET /api/health`, `GET /api/ready`, and `GET /api/auth/status` are always open
so health checks and clients can detect whether a token is required.

```bash
curl -fsS http://localhost:8787/api/auth/status
```

Response:

```json
{ "required": true }
```

When `required` is `true`, send the configured token on protected requests:

```bash
curl -fsS -H "Authorization: Bearer change-this-long-random-token" \
  http://localhost:8787/api/settings
```

The SignalR hub at `/hubs/jobs` accepts the same token through its WebSocket
`access_token` query parameter. Ordinary `/api` routes reject query tokens. A
successful bearer-authenticated API request also establishes a derived HttpOnly,
same-site session cookie so native browser media requests can authenticate
without placing the admin token in their URLs.

## Common Recipes

### Check Whether Optimisarr Is Ready

```bash
curl -fsS http://localhost:8787/api/ready
```

Ready response:

```json
{ "status": "ready" }
```

If the service is not ready, the endpoint returns `503` with a detail string such
as an unwritable path or unavailable media tool.

### Scan One Library

```bash
curl -fsS -X POST http://localhost:8787/api/libraries/1/scan
```

Response:

```json
{
  "discovered": 5426,
  "added": 0,
  "updated": 0,
  "skippedUnsettled": 0
}
```

### See What Would Be Queued

```bash
curl -fsS "http://localhost:8787/api/candidates?libraryId=1"
```

Each candidate includes `eligible` and `reason`. Use this before enqueueing a
large library.

### Enqueue Eligible Files

```bash
curl -fsS -X POST http://localhost:8787/api/libraries/1/enqueue
```

Response:

```json
{
  "enqueued": 10,
  "alreadyQueued": 2,
  "ineligible": 50,
  "importing": 0
}
```

### Watch Queue State

```bash
curl -fsS http://localhost:8787/api/queue/status
curl -fsS http://localhost:8787/api/jobs
```

Use `waitingReason` and `blockedReason` from `/api/queue/status` to understand
why jobs are not starting.

### Pause and Resume Queue Work

Manual pause is operational state, not portable configuration. It persists across restarts but is
not included in a settings export. Pausing prevents new jobs and automatic replacements from
starting. On Linux and macOS, Optimisarr also suspends running transcodes in place; verification
already underway finishes. Other platforms use a dispatch-only pause and report that limitation.

```bash
curl -fsS -X POST http://localhost:8787/api/queue/pause
curl -fsS -X POST http://localhost:8787/api/queue/resume
```

The response is the current queue status. `manualPauseMode` is `inactive`, `suspended`, `partial`,
or `dispatchOnly`. `runningEncodesSuspended`, `suspendedEncodeCount`, and
`pauseFailedEncodeCount` distinguish a complete process suspension from a partial or unsupported
one. A resume that cannot continue every still-running suspended process returns `409` and keeps
dispatch paused so the operation can be retried safely.

### Replace a Verified Job

Replacement is state-changing. It is refused while dry-run mode is enabled and
should only be automated after you trust the library rules.

```bash
curl -fsS -X POST http://localhost:8787/api/jobs/42/replace
```

The response is a replacement/quarantine record. Keep the returned `id` if you
want to approve or roll back later.

### Roll Back or Approve a Replacement

```bash
# Restore the quarantined original and remove the replacement.
curl -fsS -X POST http://localhost:8787/api/replacements/7/rollback

# Permanently remove the quarantined original.
curl -fsS -X POST http://localhost:8787/api/replacements/7/approve
```

Approval is permanent. Rollback is available only while the original still
exists in quarantine.

## Health and System

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/api/health` | Liveness check: the web process is responding. |
| `GET` | `/api/ready` | Readiness check: database, writable paths, FFmpeg, and ffprobe are usable. |
| `GET` | `/api/auth/status` | Authentication discovery: whether `OPTIMISARR_ADMIN_TOKEN` is configured. |
| `GET` | `/api/diagnostics` | Admin support snapshot: version, environment, settings, library and integration summaries, stats, and the failure summary. Assembled from non-secret data only (no tokens, API keys, or webhook URLs). Protected by the admin token when one is set. |
| `GET` | `/api/system/tools` | Required FFmpeg/ffprobe checks plus optional CPU/CUDA VMAF-FFmpeg capabilities; each result includes `required`. |
| `GET` | `/api/system/hardware` | Hardware accelerator and encoder detection. Use `?refresh=true` to retest. |
| `GET` | `/api/fs/browse?path=/data` | Folder browser for directories visible inside the container. |

## First-run setup

Setup progress is admin-token protected and intentionally separate from configuration backup. It
does not contain secrets and is not exported between installations.

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/api/setup` | Read the versioned completed/current step and completion state. |
| `GET` | `/api/setup/readiness` | Non-destructively check database, config/work/quarantine paths and media tools, then return visible encoder, VMAF and schedule recommendations from proved capabilities. |
| `PUT` | `/api/setup/progress` | Persist one completed step in order; repeated writes are idempotent. |
| `POST` | `/api/setup/complete` | Complete setup from the final review step. Does not start work. |
| `POST` | `/api/setup/apply` | Validate and atomically apply the reviewed settings and opted-in recommendations, then complete setup. A duplicate submission returns the existing receipt without changing the applied plan. |
| `POST` | `/api/setup/restart` | Return to step one while preserving libraries and settings. |

Health response:

```json
{
  "status": "healthy",
  "service": "optimisarr",
  "version": "1.0.0.0",
  "checkedAt": "2026-06-27T08:00:00+00:00"
}
```

## Settings

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/api/settings` | Read global queue, verification, and replacement settings. |
| `PUT` | `/api/settings` | Save global settings. Body is the full settings object. |
| `GET` | `/api/settings/cleanup` | Preview files and bytes currently eligible under the saved cleanup policy without changing anything. |
| `POST` | `/api/settings/cleanup` | Run the saved cleanup policy now. Body is the preview returned by `GET`; a changed preview returns `409`. Success returns the execution-time preview, processed count, and actual reclaimed bytes. |
| `GET` | `/api/settings/export` | Export configuration snapshot. Contains provider secrets. |
| `POST` | `/api/settings/import` | Validate and merge a configuration snapshot. |

Settings fields include:

```json
{
  "maxConcurrentJobs": 1,
  "minFreeDiskBytes": 10737418240,
  "cpuThreadLimit": 0,
  "libraryScanIntervalHours": 1,
  "encoderMode": "Auto",
  "hardwareDecode": true,
  "verificationDurationTolerancePercent": 1,
  "verificationRequireAudioRetained": true,
  "verificationRequireSubtitlesRetained": false,
  "verificationRequireSizeReduction": true,
  "verificationQualityGateEnabled": false,
  "verificationMinimumVmafHarmonicMean": 93,
  "verificationMinimumVmafMin": 80,
  "verificationMinimumVmafCatastrophicMin": 50,
  "verificationAudioLoudnessGateEnabled": false,
  "verificationMaxLoudnessDriftLufs": 1,
  "verificationAudioClippingGateEnabled": false,
  "verificationMaxTruePeakDbtp": 0,
  "verificationImageQualityGateEnabled": true,
  "verificationMinimumImageSsim": 0.95,
  "verificationImageMetadataGateEnabled": true,
  "verificationClipVmafEnabled": false,
  "verificationVmafFrameSubsample": 1,
  "replacementAllowCrossFilesystem": false,
  "dryRunMode": false,
  "replacementQuarantineRetentionDays": 0
}
```

`replacementQuarantineRetentionDays` retains its historical wire name for API and
configuration-backup compatibility. It is the general cleanup-retention window:
the startup/six-hour sweep applies it to quarantined originals and failed outputs
under `/work`. A value of `0` retains both indefinitely. Expiring a failed output
clears only its scratch file and path; its job diagnostics and measured output size
remain available through the jobs and failures APIs.

The cleanup preview counts only records the shared timed-cleanup policy can act on
and measures files that currently exist on disk. Dry-run excludes quarantined
originals but still includes eligible failed scratch outputs. The `POST` endpoint
re-evaluates eligibility and requires it to match the submitted preview. If a retry
or background sweep changed the policy, counts, or bytes, it returns `409` without
deleting anything so the operator can review and confirm the new preview.

## Libraries and Inventory

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/api/library-options` | Available media types, presets, codecs, containers, HDR modes, encoders, and image formats. |
| `GET` | `/api/libraries` | List configured libraries. |
| `GET` | `/api/libraries/{id}/access` | Check whether the configured path exists and is readable/writable. |
| `POST` | `/api/libraries` | Create a library. |
| `PUT` | `/api/libraries/{id}` | Update a library. |
| `DELETE` | `/api/libraries/{id}` | Delete a library record. |
| `POST` | `/api/libraries/{id}/scan` | Scan one library. |
| `POST` | `/api/libraries/scan` | Scan all enabled libraries. |
| `GET` | `/api/media?libraryId={id}` | List discovered files. Omit `libraryId` for all libraries. Optional `status`, `search` (path substring), and `page`/`pageSize`; the pre-paging total is returned in `X-Total-Count`. |
| `POST` | `/api/media/{id}/probe` | Probe one media file. |
| `GET` | `/api/candidates?libraryId={id}` | Show rule decisions for discovered files. |
| `GET` | `/api/candidates/summary` | Eligible/skipped counts per library. |
| `GET` | `/api/inventory` | Inventory page: files paired with their rule verdict, filtered (`show`=all/eligible/skipped/unprobed), searched (`search`), and paged (`page`/`pageSize`). Returns the page, the filtered total, and per-filter counts. |

Create and update library bodies use the same shape. Common fields:

```json
{
  "name": "TV",
  "path": "/data/media/tv",
  "mediaType": "TV",
  "ruleProfile": "ScottsSettings",
  "enabled": true,
  "priority": 0,
  "minFileSizeBytes": null,
  "maxHeight": null,
  "targetVideoCodec": null,
  "targetContainer": null,
  "hdrHandling": null,
  "qualityCrf": null,
  "audioTargetCodec": null,
  "audioBitrateKbps": null,
  "downmixToStereo": false,
  "targetImageFormat": null,
  "imageQuality": null,
  "autoEnqueueEnabled": false,
  "autoEnqueueWindowStart": "00:00",
  "autoEnqueueWindowEnd": "00:00",
  "autoReplace": false
}
```

Use `/api/library-options` for valid enum values. Unknown or invalid values are
rejected.

## Preview

| Method | Endpoint | Purpose |
|---|---|---|
| `POST` | `/api/media/{id}/preview` | Start a throwaway preview encode for one media file. |
| `GET` | `/api/preview/{jobId}` | Read preview status, comparison stats, and verification report. |
| `DELETE` | `/api/preview/{jobId}` | Remove preview output. |
| `GET` | `/api/media/{id}/content` | Stream original media content for comparison. |
| `GET` | `/api/preview/{jobId}/content` | Stream preview output for comparison. |

Long video previews may be segment-only. The response includes `clipped: true`
when the verification report is for a sample rather than the whole file.

## Personal blind quality calibration

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/api/libraries/{id}/calibration/sources` | List probed video, audio, and still-image sources suitable for a personal quality check. |
| `POST` | `/api/libraries/{id}/calibration` | Start a short-lived session and its disposable candidate clips. Body: `{ "mediaFileId": 123, "diagnosticsEnabled": false, "ignoreActiveStreams": false }`. Diagnostics reveal candidate details. The default-off stream exception applies only to the session's calibration jobs; normal jobs remain activity-paused. |
| `GET` | `/api/calibration` | List every active quality-check session, including any revealed result. Listing does not extend an abandoned session's lifetime. |
| `GET` | `/api/calibration/{id}` | Read preparation progress, the marked original reference plus anonymous media-specific candidates, or a revealed result. |
| `POST` | `/api/calibration/{id}/classifications` | Classify every anonymous candidate and reveal its settings. Body: `{ "classifications": { "A": "Acceptable", "B": "VisiblyWorse", "…": "…" } }`. |
| `POST` | `/api/calibration/{id}/apply` | Explicitly apply a recommended video preset or media quality to the library, if its relevant settings have not changed. |
| `GET` | `/api/calibration/{id}/variants/{variant}/samples/{sampleIndex}/content` | Stream one scene or excerpt for `ORIGINAL` or anonymous candidate `A`–`E`. |
| `DELETE` | `/api/calibration/{id}` | Cancel the session and remove its scratch media and non-failed disposable jobs. A failed job keeps only its diagnostic row until **Clear errored** removes it. |

Video calibration creates three 12-second scenes for the four shuffled library-slider presets plus one marked original reference.
Its reference is the unchanged video bitstream; when a mid-file stream copy needs packets from the
preceding keyframe, its sample `startSeconds` identifies the matching presentation window. Video
samples retain the complete preset output, including its container and audio contract. Each scene is
also measured with VMAF. Scores remain absent from the API until classifications reveal the lineup;
then every non-original `result.variants[]` entry includes a `vmaf` summary and its three underlying
`samples`. `harmonicMean` is frame-weighted across measured scenes, `fifthPercentile` is the lowest
scene fifth percentile, and `minimum` is the lowest individual-frame score. `measuredSamples` and
per-scene `error` fields make partial or unavailable measurement explicit. VMAF is objective evidence
only in this personal check: it neither rejects a structurally valid sample nor changes the user's
preference-led recommendation. Music uses three 15-second excerpts with a lossless
FLAC reference and returns a per-sample `gainDb` that brings every anonymous version to the same
quietest measured level. Still images use a lossless PNG reference and return `startSeconds: 0` and
`gainDb: 0`.

All candidate and reference media lives under `/work/calibration`. These jobs are excluded from the
normal queue and can never enter replacement. Active requests extend the session; an abandoned
session expires after two hours, and all session state is discarded on restart. Failed job rows
retain only their error, verification report, and process diagnostics after the scratch media and
session are removed, so the failure API remains useful after the lab closes. The original file
is only read. A completed result does not alter settings: only the separate `apply` request may
change the relevant library preset or quality, and it queues no media work.

Variant labels and URLs are opaque until reveal when `diagnosticsEnabled` is false. With diagnostics
enabled, each variant includes its profile, codec, actual container, requested/effective quality,
and encoder for troubleshooting; clients must clearly state that the session is no longer blind.
Browser verification clients should replace the `src` of one video element, report that element's
resolved `currentSrc`, and offer a direct link to that exact resource rather than presenting an
application-supplied active-file label as proof of a switch.
Normal blind clients must not display encoder, quality, bitrate, estimated size, or raw media
duration during this phase. Drive each variant from its sample `durationSeconds` and `startSeconds`,
preserve one relative playback position, and wait for `seeked` before showing a newly selected video;
otherwise timing can become a side channel. Submit exactly one `Indistinguishable`, `Acceptable`, or
`VisiblyWorse` classification for every variant. If any stream cannot be decoded, fail closed.

## Queue

| Method | Endpoint | Purpose |
|---|---|---|
| `POST` | `/api/libraries/{id}/enqueue` | Enqueue eligible files for one library. |
| `GET` | `/api/queue/status` | Read dispatch blockers and the exact manual-pause/suspension state. |
| `POST` | `/api/queue/pause` | Pause new dispatch and automatic replacement; suspend running transcodes where supported. |
| `POST` | `/api/queue/resume` | Resume suspended transcodes, then reopen dispatch and automatic replacement. |
| `GET` | `/api/jobs` | List queue jobs. Optional filters `status`, `libraryId`, `category`, `since`, `until`, and paging `page`/`pageSize`. |
| `GET` | `/api/jobs?status=Failed` | List jobs filtered by status. |
| `GET` | `/api/jobs/failures` | Failure summary for normal work and failed preview/personal-quality comparisons. Optional `libraryId` scopes it to one library. Samples identify `jobType` and include structured failed `verificationChecks` (`name`, `outcome`, and measured `detail`). |
| `GET` | `/api/jobs/{id}/log` | FFmpeg/process log for a failed job (plain text; `404` when none was captured). |
| `GET` | `/api/jobs/{id}/artwork` | Proxied artwork for a job when a provider can resolve it. |
| `POST` | `/api/jobs/{id}/cancel` | Cancel an active job. |
| `DELETE` | `/api/jobs/{id}` | Remove a clearable job. |
| `POST` | `/api/jobs/{id}/retry` | Retry a failed or cancelled job. |
| `POST` | `/api/jobs/clear?scope=errored` | Clear failed jobs. Scope can be `errored`, `finished`, or `all`. |
| `POST` | `/api/jobs/clear-pending` | Clear queued and ready-to-replace jobs and stop running work. |

Job responses include status, progress, priority, FFmpeg arguments, selected
encoder, output size, verification result, verification report JSON, the
classified failure category (when failed), and timestamps. When paging is used,
the total number of matches before paging is returned in the `X-Total-Count`
response header.

Failed preview and personal-quality jobs never appear in the normal queue feed. Their scratch media
is still deleted, but the small failed row remains available to `/api/jobs/failures`,
`/api/jobs/{id}/log`, and the diagnostics bundle until `POST /api/jobs/clear?scope=errored` removes
it. This makes an interactive failure diagnosable without retaining candidate media or reading the
application database directly.

Common job states include `Queued`, `Probing`, `Transcoding`, `Verifying`,
`ReadyToReplace`, `Completed`, `Failed`, and `Cancelled`. A job is re-checked
against its library's current rules immediately before it transcodes; one that
is no longer a candidate (e.g. an already-efficient source enqueued before the
efficiency floor existed) is marked `Cancelled` with an `errorMessage` of
`Skipped before encoding: …` rather than being transcoded and failed.

Verification reports are stored as JSON in `verificationReportJson`:

```json
{
  "checks": [
    {
      "name": "Duration",
      "outcome": "Passed",
      "detail": "Output duration is within tolerance."
    }
  ]
}
```

## Exclusions

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/api/exclusions?libraryId={id}` | List path-keyed exclusions. Omit `libraryId` for all. |
| `POST` | `/api/exclusions` | Exclude a media file from future optimisation. |
| `DELETE` | `/api/exclusions/{id}` | Remove an exclusion. |

Example body:

```json
{
  "mediaFileId": 123,
  "reason": "Already tuned manually"
}
```

## Replacement and Quarantine

| Method | Endpoint | Purpose |
|---|---|---|
| `POST` | `/api/jobs/{id}/replace` | Replace the original for a verified job. Refused in dry-run mode. |
| `GET` | `/api/replacements` | List quarantine/replacement records. |
| `GET` | `/api/replacements/{id}` | Read one replacement with verification details. |
| `GET` | `/api/replacements/{id}/original/content` | Stream the quarantined original for comparison. |
| `GET` | `/api/replacements/{id}/replacement/content` | Stream the replacement for comparison. |
| `POST` | `/api/replacements/{id}/rollback` | Restore the original and remove the replacement. |
| `POST` | `/api/replacements/{id}/approve` | Permanently remove the quarantined original. |
| `POST` | `/api/replacements/clear` | Clear finished replacement history rows. |

Rollback is available only while the original still exists in quarantine.

Replacement statuses:

| Status | Meaning |
|---|---|
| `Replaced` | The original is still in quarantine and can be rolled back or approved. |
| `RolledBack` | The original was restored and the replacement removed. |
| `Purged` | The quarantined original was deleted by approval or retention. Rollback is no longer available. |

## Stats

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/api/stats` | Dashboard totals: saved bytes, queue counts, quarantine totals, and library counts. |
| `POST` | `/api/stats/clear` | Reset persistent lifetime space-saved totals. |

## Connections

### Activity Watchers

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/api/activity-watchers` | List Plex/Jellyfin/Emby watchers. |
| `POST` | `/api/activity-watchers` | Create watcher. |
| `PUT` | `/api/activity-watchers/{id}` | Update watcher. |
| `DELETE` | `/api/activity-watchers/{id}` | Delete watcher. |

Body:

```json
{
  "name": "Plex",
  "type": "Plex",
  "baseUrl": "http://plex:32400",
  "apiToken": "token",
  "enabled": true,
  "refreshOnReplace": true
}
```

When updating, send an empty `apiToken` to keep the stored token.

### Sonarr and Radarr

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/api/arr-connections` | List Sonarr/Radarr connections. |
| `POST` | `/api/arr-connections` | Create connection. |
| `PUT` | `/api/arr-connections/{id}` | Update connection. |
| `DELETE` | `/api/arr-connections/{id}` | Delete connection. |

Body:

```json
{
  "name": "Sonarr",
  "type": "Sonarr",
  "baseUrl": "http://sonarr:8989",
  "apiKey": "key",
  "enabled": true
}
```

When updating, send an empty `apiKey` to keep the stored key.

### Notifications

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/api/notification-targets` | List notification targets. |
| `POST` | `/api/notification-targets` | Create target. |
| `PUT` | `/api/notification-targets/{id}` | Update target. |
| `DELETE` | `/api/notification-targets/{id}` | Delete target. |

Body:

```json
{
  "name": "Ops",
  "type": "Discord",
  "url": "https://discord.com/api/webhooks/...",
  "token": "",
  "enabled": true,
  "notifyOnReplacement": true,
  "notifyOnFailure": true
}
```

Supported target types are `Webhook`, `Discord`, `Ntfy`, and `Apprise`.

## Interactive Connect Flows

| Method | Endpoint | Purpose |
|---|---|---|
| `POST` | `/api/connect/plex/start` | Start Plex PIN/OAuth flow. |
| `GET` | `/api/connect/plex/poll?id={id}` | Poll Plex authorization result. |
| `POST` | `/api/connect/plex/servers` | Discover Plex servers for a token. Body: `{ "token": "..." }`. |
| `POST` | `/api/connect/jellyfin/start` | Start Jellyfin Quick Connect. Body: `{ "baseUrl": "..." }`. |
| `POST` | `/api/connect/jellyfin/poll` | Poll Jellyfin Quick Connect. Body: `{ "baseUrl": "...", "secret": "..." }`. |
| `POST` | `/api/connect/test` | Test a Plex/Jellyfin/Emby connection. |

Connection test body:

```json
{
  "type": "Jellyfin",
  "baseUrl": "http://jellyfin:8096",
  "token": "token"
}
```

## Live Updates

The UI connects to SignalR at `/hubs/jobs`. It receives job-list change events
and live progress updates while FFmpeg is running. Use the HTTP endpoints above
for durable state; treat hub messages as a convenience stream.

## Safety Notes for Automation

- Prefer dry-run mode while testing scripts.
- Read candidates before enqueueing.
- Read verification reports before replacing.
- Never call approve automatically unless you have an independent backup or a
  deliberate retention policy.
- Do not expose these endpoints directly to the internet; use an authenticated
  reverse proxy.
