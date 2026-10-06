# Configuration and scheduling

Settings are stored in `/config/optimisarr.db`; idempotent EF Core migrations
run at startup.

Screenshots in this page use fabricated dummy media created for documentation.
No copyrighted material is used.

## First-run setup

A genuinely new database opens a five-step setup workspace before the normal dashboard. It verifies
database access, the config/work/quarantine paths, required media tools, and detected hardware. The
storage ledger shows effective read/write access, free and total capacity, filesystem and mount
identity, the work-space reserve, and whether each existing library can move atomically to work and
quarantine. A failed row explains the cause and offers local, Docker Compose, Unraid, or TrueNAS
recovery steps. **Re-test system** reruns the actual probes and announces the refreshed result; it
never claims to create a host mount or change host permissions. Setup then
lets you add and fully configure as many libraries as needed before reviewing the starting safety
posture. The same complete per-library rules editor is used inside and outside setup, and every
configured path is rechecked before Continue. Progress is saved after each step, so refreshing or
restarting resumes at the first incomplete step. Finishing setup does not scan, enqueue, encode,
replace, or delete a file.

Fresh installations start in dry-run with one concurrent job. Every new library has automatic
enqueue and automatic replacement disabled. New video re-encode libraries start on Adaptive
per-title VMAF with the Visually lossless target; non-video and remux-only libraries keep VMAF off. Existing
installations upgraded from an older release never see the wizard automatically. To revisit it
without deleting or resetting any configuration, use **Settings → System → First-run setup → Run
setup again**.

![Encoding settings showing primary media slots and advanced workload lanes](../images/optimisarr-settings-general-dark.png)

**Settings → Encoding → Queue** sets the primary media slots for video encoding and full
container verification. Open **Advanced workload lanes** to choose **Automatic** or **Manual**.
Automatic conservatively reserves an extra audio/image slot on servers with sufficient CPU and
memory, and one or two slots for validating strict sidecar evidence. Manual lets you set 0–4 extra
audio/image slots and 1–4 evidence slots; the effective capacity preview shows the result before
you save. At zero extra audio/image slots, those jobs can still use a free primary slot. Worker
capacity is independent of these local slots. The Queue page shows each lane's active capacity,
waiting work, and the current reason for a wait.

## Admin token

Optimisarr is intended to run on a trusted network or behind an authenticated
reverse proxy. For a built-in backstop, set `OPTIMISARR_ADMIN_TOKEN` to a long
random value before starting the container:

```yaml
environment:
  OPTIMISARR_ADMIN_TOKEN: "change-this-long-random-token"
```

When the token is set, the web UI asks for it before loading operational data.
API clients must send it as a bearer token:

```bash
curl -H "Authorization: Bearer change-this-long-random-token" \
  http://localhost:8787/api/settings
```

`/api/health`, `/api/ready`, and `/api/auth/status` remain open for health
checks and startup detection. If the token is not set, Optimisarr behaves as it
did before and logs a warning at startup.

## Remote workers

Windows, macOS and Linux sidecars can encode video, measure VMAF, and perform the full
verification workload. Availability and the saved worker switch default on for fresh installations;
existing settings are preserved. Pair and manage machines in **Settings → Remote workers**.
Use **Settings → Files & safety → Remote workers** to disable activity without deleting pairings.
The existing `OPTIMISARR_EXPERIMENTAL_REMOTE_WORKERS=false` environment override disables
the feature for the deployment; an unset variable leaves it available.

Work placement is per library: **Libraries → Configure → Choose files → Advanced eligibility →
Where this library's work may run**. Choose **Here or on a worker**, **Only on this server**,
**Prefer a worker**, or **Only on workers**. **Verify entirely on the sidecar** defaults on for
new installations; existing settings are preserved. You can change it in **Settings → Files & safety → Remote workers**.

See [Remote workers and sidecars](remote-workers.md) for installation, the ten-minute preference
window, strict verification, and the work that remains on the container. For a Linux worker,
use the [Linux container setup guide](linux-sidecar.md).

## Library workflow

Each library has its own root, media type, rule profile, and processing policy.
The Inventory explains why every file is eligible or skipped.

**Configure** opens a dedicated page for that library. A video re-encode library first chooses one
of two mutually exclusive quality paths:

- **Adaptive per-title VMAF** (the default for new video re-encode libraries) first encodes deterministic early, middle, and late
  video-only samples at no more than four encoder-specific values and selects the smallest measured
  candidate that clears the library target. Missing or contradictory evidence falls back to Fixed;
  the final file still runs every normal verification gate.
- **Fixed library quality** uses the preset/custom quality directly, then runs the normal
  full-output verification and one bounded VMAF-only recovery retry when its VMAF gate is enabled.

Adaptive mode adds up to twelve 40-second sample encodes before each full encode and requires VMAF
to be enabled. Choose Fixed to avoid that preparation cost; video libraries can then disable VMAF
or select a named quality tier. **Custom** exposes the
harmonic-mean, fifth-percentile, and catastrophic-frame floors plus full/clip scoring and the frame
sampling interval. VMAF has no global setting: every video library owns its policy. Upgrades copy
the former global policy into each existing library so behaviour does not change unexpectedly.

| Control | Behaviour |
|---|---|
| Library scan interval | Rescans every enabled library at the configured interval (one hour by default), the only scheduling control in global settings. Scanning also runs once at startup. |
| Primary media slots | Bounds video encodes and full container media verification. Audio/image work can use a free slot. Advanced workload lanes provide independent extra audio/image and strict sidecar-evidence capacity. |
| CPU threads | Limits FFmpeg CPU usage where applicable. |
| Work-disk threshold | Prevents new starts when `/work` is too full. |
| Encoder mode | Auto, CPU, NVIDIA NVENC, Intel QSV, or VA-API. |
| Hardware decoding | Uses GPU decode with hardware encoders when possible, including eligible SDR VMAF passes. Runtime failures fall back to CPU decode, and a below-floor accelerated VMAF result is confirmed in software before rejection. |
| HDR tone-map engine | Software is the compatible default. Hardware uses Intel QSV or VA-API for a freshly confirmed non-Dolby-Vision HDR10/PQ source under an existing **Tone-map to SDR** library rule when hardware decoding is active, and retries once with software if that path fails. HLG, Dolby Vision, unknown transfer metadata, VMAF-gated work, and disposable comparisons retain the software transform. |

There is no global processing window: *when* work runs is set per library (see
below). Manually queued jobs in a library with auto-optimise enabled also obey its
window; libraries without auto-optimise have no window.

![Library Advanced encoding page with breadcrumbs, codec and container overrides, encoder effort, and bitrate controls](../images/optimisarr-library-advanced-encoding-dark.png)

## Media toolchain overrides

The published container configures a matched Jellyfin FFmpeg/ffprobe pair automatically. Custom
installations can select the production transcoder with `OPTIMISARR_FFMPEG`; Optimisarr derives a
sibling `ffprobe` from an absolute FFmpeg path so probing and verification interpret streams with
the same build. Set `OPTIMISARR_FFPROBE` only when the paired probe lives elsewhere. The independent
`OPTIMISARR_FFMPEG_VMAF` command supplies libvmaf, loudness, and image-SSIM measurement.
`OPTIMISARR_FFMPEG_VMAF_CUDA` may point at a purpose-built NVIDIA binary that exposes
`libvmaf_cuda`; when unset, the normal VMAF binary is checked for that filter. The CUDA binary is
optional and every unsupported build, GPU, driver, or source falls back to the normal software
measurement. `OPTIMISARR_EXIFTOOL` can select a non-PATH ExifTool binary.

```yaml
environment:
  OPTIMISARR_FFMPEG: /opt/media/ffmpeg
  OPTIMISARR_FFPROBE: /opt/media/ffprobe
  OPTIMISARR_FFMPEG_VMAF: /opt/media/ffmpeg-vmaf
  OPTIMISARR_FFMPEG_VMAF_CUDA: /opt/media/ffmpeg-vmaf-cuda
  OPTIMISARR_EXIFTOOL: /opt/media/exiftool
```

The standard image already provides the normal FFmpeg, ffprobe, VMAF, and ExifTool values. The CUDA
VMAF override is optional; leave it unset unless supplying a compatible NVIDIA build. Do not
override the standard values unless supplying a complete, tested replacement toolchain.

## Per-library verification gates

`OPTIMISARR_VMAF_SHADOW_SERVER=1` opts the main container into bounded paired v0/v1 research
measurements. It adds server CPU work even for sidecar-verified candidates, with up to two minutes
of extra finalisation per sampled job. Existing VMAF gates still decide replacements. It is off by
default; see the [decision and evidence guide](../development/vmaf-shadow-decision.md) before enabling.

The configuration page has four linked stages for every media type:
**Choose files → Encode → Verify → Schedule & replace**. Open any stage directly from the overview
or return with its breadcrumb; navigation keeps the current draft and **Save** saves all stages
together. Music exposes its output codec and bitrate in **Encode**. Specialist controls have
dedicated **Advanced eligibility**, **Advanced encoding**, and **Advanced verification** pages.
Completed-output routing stays in **Schedule & replace**, beside automatic replacement.

Every job must pass decode health, output readability, and the media-kind checks
that apply to it. Video jobs also have an always-on structural comparison: the output codec must
match the resolved target (or the source for a remux), resolution must not change without a resize
policy, bit depth and chroma sampling may not be reduced, and ffprobe must report a coherent output
profile. These checks are independent of VMAF because perceptual quality alone cannot prove the
requested codec or signal structure was retained. Open **Libraries**, edit a library, and use
**Verify** to tune its applicable checks. Optimisarr shows only controls that can affect
the selected media type:

| Gate | Applies to | Default |
|---|---|---|
| Duration tolerance | Video and audio | On, 1% |
| Require audio tracks retained | Video and audio | On |
| Require subtitle tracks retained | Video | Off |
| Require output smaller than original | Video, audio, image | On |
| Perceptual quality (VMAF) | Video re-encodes | Visually lossless for new video re-encode libraries; existing saved policies are unchanged |
| Audio loudness drift (EBU R128) | Video and audio | Off |
| Audio clipping (true peak) | Video and audio | Off |
| Image SSIM | Images | On, 0.95 |
| Image metadata | Images | On |

Enabled measurement gates fail closed. If Optimisarr cannot measure an enabled
VMAF, loudness, true-peak, SSIM, or metadata gate, the job fails instead of
becoming replaceable. VMAF is skipped for remux-only work because those jobs copy
the encoded video frames unchanged. New video re-encode libraries enable the Visually lossless VMAF
tier and score three representative windows by default. This still adds bounded encode-and-score
work and can dominate a run on modest hardware; choose Fixed and turn the gate off when that cost is
not appropriate. Each library configuration page offers named tiers (Space-saver through Archival)
and custom floors. While the gate is off, the structural, duration, and size gates plus quarantine rollback
still guard every replacement. Existing installations copy their former global verification values
to every library during migration, so updating does not silently weaken or strengthen an existing
policy. When enabled, **Score three representative samples** measures deterministic 40-second
windows near the beginning, middle and end of long files. The weakest window controls the tail
floors. **Frame sampling** can score every Nth frame from 1–10; 1 is the conservative default,
because skipped frames cannot participate in the percentile or catastrophic floor. Image SSIM and EXIF/ICC
retention are enabled for new installations; existing saved
opt-outs remain unchanged. SSIM uses
explicit reference dimensions, aligned timebases, full-range planar RGB/RGBA, and includes alpha
when the source may carry it. Before verification, ExifTool copies EXIF and ICC while deliberately
excluding orientation, embedded previews, and stale raster dimensions from the old image.

No libvmaf model or filter configuration is required in the UI. Optimisarr prepares
both streams at the original's resolution with bicubic scaling, aligns their
timebases and starting timestamps, resamples both onto the source's measured picture cadence before
selecting sampled windows, normalises colour range and pixel format, and
uses bounded automatic threading. It selects Netflix's `vmaf_v0.6.1` HDTV model
for HD material and `vmaf_4k_v0.6.1` when either source axis reaches UHD. If a job
intentionally converts HDR to SDR, the reference receives the same production
tone-map before comparison; HDR-preserving jobs keep both streams in the matching
HDR transfer domain. SDR jobs follow the selected encoder's hardware decode path when Hardware
decoding is enabled: QSV/VA-API download decoded frames for CPU VMAF, while a compatible NVIDIA
build can use NVDEC, `scale_cuda`, and `libvmaf_cuda` end to end. Hardware attempts always retry in
software on failure. A VMAF-gated HDR→SDR job always uses the established software production
tone-map so its reference receives the identical transform. Only VMAF is
requested during this gate; the older incidental PSNR/SSIM report fields remain nullable. The model,
sampling interval, and preparation used are recorded in the result.

The 93 harmonic-mean, 80 fifth-percentile and 50 catastrophic-frame floors are Optimisarr's conservative
replacement guardrails, not universal scores promised by Netflix. VMAF is most
useful for compression and scaling damage; the independent decode, duration,
stream, HDR-signal, colour, timestamp, and A/V-sync checks remain equally important.
Netflix does not publish a general HDR VMAF model: for HDR-preserving work Optimisarr
compares both streams in the same HDR transfer domain, which remains a useful
full-reference compression check, but its absolute threshold is less formally
calibrated than the SDR viewing models. The default general-purpose profiles exclude
HDR; preserving or tone-mapping it is an explicit library-profile choice.

Encoder quality values are not assumed to be portable between implementations. Software uses the
profile CRF directly; QSV ICQ, NVENC CQ and VA-API QP receive conservative family-specific headroom.
The requested and effective values are stored with each job. Adaptive mode persists its selected
encoder-specific value on the job so a crash, ordinary retry, or higher-quality recovery cannot
silently revert to the library baseline; the selection is not shared with another title. Candidate
comparison uses actual encoded video bytes, not a cross-encoder assumption about quality-number
size. When VMAF is the only failed gate,
Optimisarr makes one automatic higher-quality retry only after a real score was measured. If that
recovery still produces a measured score below the VMAF gate, the file is
automatically excluded from future optimisation and remains reversible from the library's **Excluded**
tab. Missing or unusable VMAF evidence fails closed but cannot trigger that quality retry or immediate
VMAF exclusion. A size-saving failure excludes immediately instead of silently lowering the configured quality;
the same applies when size and VMAF both fail because higher quality would worsen size while lower
quality would worsen VMAF. Other technical or transient failures retain the three-terminal-failure
threshold. Cancelled work and jobs interrupted by a worker restart do not count toward exclusion.

## Audio quality reports and gates (development)

### Choose the encoding settings

Open **Configure → Encode → Audio & subtitles**. Choose the **Target codec** for standalone
audio, or an explicit **Re-encode to** choice under **Audio track** for video. The horizontal
**Audio encoding quality** control changes the bitrate for the selected codec:

| Preset | AAC | Opus | MP3 |
|---|---:|---:|---:|
| Space saver | 96 kbps | 96 kbps | 128 kbps |
| Balanced | 128 kbps | 128 kbps | 192 kbps |
| High | 192 kbps | 160 kbps | 256 kbps |
| Very high | 256 kbps | 192 kbps | 320 kbps |

These are encoding starting points. They are not calibrated listening grades, equal-quality
claims across codecs, or audio gate thresholds. Higher budgets usually use more space. AAC
standalone output uses `.m4a`, Opus uses `.opus`, and MP3 uses `.mp3`. Video keeps its separately
chosen video container. Retained surround receives this budget per channel pair; downmixing
to stereo keeps the selected budget.

**Default** clears the bitrate override and follows the selected profile: standalone audio is
normally 128 kbps, or 96 kbps under Scott's Settings; video uses that profile's soundtrack bitrate. **Custom** opens
Advanced audio, where you can enter your own bitrate. Opening or saving a library does not
change existing values. A preset you choose during the edit follows a subsequent codec change;
a loaded or custom bitrate is preserved. The saved policy stores codec and bitrate, so there
is no persistent link to a preset after saving.
When an explicit saved bitrate matches the profile default, the control explains that it
remains saved. Choose **Default** to clear that override and follow future profile changes.

Presets do not enable or change the audio quality gate. They use the existing fixed-bitrate
encode path, with no automatic sample search or quality retry. Copied video audio is unchanged.
For explicitly re-encoded video audio, configure the separate soundtrack report and gate below.

![Audio encoding settings showing named presets, the actual Opus bitrate and output format](../images/optimisarr-audio-encoding-presets-dark.png)

### Collect reports or require a result

In a music library, open **Configure → Verify** and enable **Audio quality report** to collect
experimental Zimtohrli observations. It starts off. Single-track mono/stereo files are supported;
standalone files with multiple tracks and surround are not assessed. Video uses the separate
soundtrack controls below. Up to 90 seconds are
compared per channel. This adds CPU work, temporary PCM files and reads of both complete files
for their hashes on the verifying host.

Queue job details and Quarantine display the largest sample distance for each channel,
coverage and measurement location. Smaller distances indicate closer audio, without a
calibrated listening score or pass threshold. An **Unavailable** report explains missing tools,
unsupported files or incomplete evidence. Strict worker jobs stay on the worker with no server
fallback. In server verification mode, the server measures.

With **Require audio quality** off, reports do not change replacement decisions. Existing decode, duration, stream, metadata and
size checks still apply, and verified replacement quarantines the original before moving the
candidate into place. See the [development evidence and limits](../development/perceptual-audio-image-quality-plan.md#integrated-report-controls-in-development).

![Audio quality report showing separate channel distances, assessed duration, worker location and the report-only safety note](../images/optimisarr-audio-report-dark.png)

To make audio quality a replacement requirement, enable **Require audio quality** in the same
view, then open **Advanced verification** and choose **Maximum audio difference**. Both switches
start off, and the limit starts blank. Verify shows the current limit; Advanced verification holds its control.
The horizontal control has clickable numeric points, with lower values on the left. The points
are shortcuts, not calibrated quality levels. Choose **Custom** to enter any value from 0 to 1;
lower values are stricter. There is no calibrated default or conversion
from VMAF. Use your reports and listening examples to choose a limit for that library.

The largest distance in every assessed channel and sample must be at or below your limit.
A missing tool, unsupported input or incomplete worker measurement blocks replacement when the
gate is enabled. Enabling the gate requests measurement even if **Audio quality report** is off.
Strict sidecar verification keeps those measurements on the worker; the server compares their
validated results with the selected limit. The gate applies to standalone audio in music and
mixed libraries, with one mono or stereo track. Video soundtracks use their own gate; surround
assessment is unavailable.
Files up to 90 seconds are fully assessed; longer files use three 30-second samples, so the gate
cannot prove the quality of unassessed sections.

Job details show **Passed** or **Blocked**, the limit used for that job and each channel’s largest
distance. A failed gate leaves the original unchanged. This does not add an automatic audio
quality retry or change the other safety checks.

![Audio quality gate controls with an explicit maximum difference](../images/optimisarr-audio-quality-settings-dark.png)

![Blocked audio quality report showing the selected limit](../images/optimisarr-audio-gate-dark.png)

### Assess re-encoded video soundtracks

Soundtrack samples leave a 100 ms margin at the file end to avoid encoder padding. Each prepared sample must still contain its assigned frames within the 64-frame resampling allowance; missing audio is never padded. Coverage in the report shows what was actually assessed.

![Soundtrack quality controls with a separate optional gate and explicit maximum difference](../images/optimisarr-soundtrack-quality-settings-dark.png)

![Per-track soundtrack results showing a passed main track and blocked commentary](../images/optimisarr-soundtrack-report-dark.png)


For a Film, TV or Other library, open **Configure → Verify**. **Soundtrack quality report**
and **Require soundtrack quality** are separate opt-in controls; both start off. The standalone
**Audio quality report** and **Require audio quality** controls do not enable them. Choose an
explicit **Re-encode to** audio choice under **Encode → Audio & subtitles** to make soundtrack
assessment applicable. Copied audio, remuxes and disposable previews skip this assessment and
its gate; existing stream-retention and timing checks still apply.

Enable **Soundtrack quality report** to see experimental per-track observations in Queue job
details and Quarantine. Each track shows its output position, language/title when present,
largest distance per channel, coverage and whether measurement ran on the server or worker.
An **Unavailable** result explains unsupported tracks, missing tools or incomplete evidence.
Reporting alone does not block replacement.

To require a result, enable **Require soundtrack quality**, then choose
**Maximum soundtrack difference** in **Advanced verification**. Its numeric points and **Custom** input work like the
standalone audio limit: enter a finite value from 0 to 1, with lower values stricter and no
calibrated default. The gate requests measurement even when reporting is off. Every channel
and sample of every retained track must meet the inclusive limit; a missing or unsupported
measurement blocks replacement. Encoding presets do not select or change this limit.

Assessment supports one to eight retained mono/stereo tracks with matching channel layouts.
The frozen job records intentional source-track removals. Remaining source tracks map in order
to output audio tracks; matching language, title and commentary identity is required. It does
not guess track matches from language alone. A missing, extra or reordered track makes assessment
unavailable. Surround and surround-to-stereo downmix assessment are unavailable, including a
retained surround track alongside otherwise supported stereo tracks. Track duration differences
over 100 ms, start-time differences over 50 ms, and unsupported picture/container offsets are
also refused; a perceptual score cannot approve a timing shift or establish lip-sync quality.

Each track up to 90 seconds is fully assessed; longer tracks use three 30-second samples at
the start, middle and end. This cannot guarantee quality outside the assessed windows or
inaudibility within them. Cost grows with retained tracks: serial decode/scoring, temporary
PCM files, and repeated reads of both complete video files for identity hashes on the verifying
host. Strict worker verification requires an updated protocol 8 sidecar and verification
contract 3; measurement stays on the worker with no server fallback. Server verification mode
measures on the server. See [evidence and resource limits](../development/perceptual-audio-image-quality-plan.md#video-soundtrack-reports-and-gate-in-development).

All other configured video, timing, stream, metadata, loudness/clipping and size gates still
apply. A failed soundtrack gate leaves the original unchanged and adds no automatic quality
search or retry. Replacement still quarantines the original before installing the verified
candidate; quarantine is not a backup, and approval or retention purge removes rollback ability.
Dry-run blocks replacement and purge while allowing encoding and verification.

## Rule profiles (presets)

Each library picks an **optimisation preset** that sets its codec, container, and a
researched quality target; specialist controls are available from the applicable workflow stage.

| Preset | Targets |
|---|---|
| Compatibility (H.264) | H.264 / MP4 with channel-aware AAC — broad compatibility for proven 8-bit sources, larger files. Higher or unknown bit depths are skipped with guidance to use HEVC or AV1. |
| Balanced (HEVC) | HEVC (H.265) / MP4 at CRF 24 with channel-aware AAC — a good default. |
| Efficiency (AV1) | AV1 / MKV — smallest files, slower to encode. |
| **Scott's Settings** | HEVC / MP4 at CRF 24, **HDR tone-mapped to SDR**, audio re-encoded to **AAC 96 kbps downmixed to stereo**. A compatibility-first, space-saving bundle; Settings → Encoding chooses compatible software or supported hardware tone mapping, and the same AAC 96 kbps stereo target applies to a music library. |
| Remux / cleanup | No re-encode — repackage into a clean container only. |

A file already in the target codec is normally skipped. Enable **"Re-encode large
files already in the target codec"** (**Choose files → Advanced eligibility**) to also re-encode oversized
same-codec files above a size you set (default 20 GB) — useful for shrinking a huge
HEVC remux under an HEVC preset. The size-saving verification gate still rejects an
output that does not get smaller, so the original is never lost.

### Audio channel and bitrate policy

For music and any opted-in video-audio re-encode, the configured bitrate is the budget for a
mono/stereo programme. When Optimisarr retains surround audio it applies that budget per channel
pair: for example, a 128 kbps baseline becomes 384 kbps for 5.1 and 512 kbps for 7.1. Enabling the
explicit stereo downmix keeps the configured value. This conservative scaling prevents a setting
chosen for stereo from starving retained surround channels, and the candidate saving calculation
uses the same effective value. MP3 requires stereo downmix for sources above two channels; AAC and
Opus accept up to eight retained channels. Post-encode verification independently rejects any
unrequested channel loss.

**Keep audio languages** (**Encode → Audio & subtitles**) removes unwanted audio tracks while a
video is optimised or remuxed. Enter comma-separated ISO 639 codes (e.g. `eng, jpn`);
the field validates the syntax before Save, then lower-cases and de-duplicates the
codes. Complete ISO 639-1/-2 aliases match (`de`, `deu`, and `ger` are equivalent).
Tracks in any other known language are dropped from the output. The behaviour is
deliberately conservative: missing, malformed, uncoded, and private-use language tags
are never removed, and when no track matches a kept language nothing is removed — so
the output always keeps at least one audio track. Verification then holds the output
to exactly the planned removal (never more or fewer tracks than planned, never zero),
and the original is untouched until every gate passes. Under the **Remux / cleanup**
preset, a file already in the right container but carrying removable foreign-language
tracks becomes eligible for a fast stream-copy cleanup; re-encode presets strip tracks
as part of the jobs they already run.

**Keep subtitle languages** (**Encode → Audio & subtitles**) works the same way for subtitle
tracks, with one deliberate difference: subtitles are optional streams, so there is
no keep-at-least-one guard. A track with no language tag is never removed, but if a
file's subtitles are all in non-kept languages they are all removed and the file ends
with none. Verification expects exactly the planned subtitle retention, so an encode
that drops a stream beyond the plan still fails.

Language removal is fail-closed. Optimisarr accepts only registered, individual ISO
639 languages and stores their canonical ISO 639-2/T form (`en` → `eng`, `fre` →
`fra`). Unknown, malformed, collective, special-purpose, untagged, and private-use
values never authorise removal. If a legacy stored rule contains even one
unrecognised entry, the whole rule becomes a no-op rather than silently becoming
broader. Every governed job freshly probes the source before FFmpeg; if that proof
fails, no stream-removal command is run.

**Track cleanup** is a preset for libraries that should only lose unwanted tracks:
it never re-encodes and never changes the container type (an `.mkv` stays `.mkv`, an
`.mp4` stays `.mp4`). A file is eligible only when it has audio or subtitle tracks
outside the library's kept languages; with neither kept-language field set, every
file is skipped with a clear reason. Removing a track always rewrites the file —
FFmpeg stream-copies every kept stream bit-identically into a new file, which then
passes the usual verify-and-replace gates (including container, retained-language,
and retained-audio-codec checks)
before the original is touched.

## Per-library automation

**Auto-optimise** uses a per-library local-time window. Inside that window the
library's eligible files are continuously queued **and** dispatched; outside it,
that library's jobs do not start (a running job is never interrupted). Libraries
without auto-optimise have no window, so their manually queued jobs run at any
time. Scanning/probing is independent and global (see the scan interval above),
and Queue dispatch still obeys concurrency, activity-pause, and disk-safety
controls. A start time equal to the end time means the window is open all day.

The **Schedule** view shows each library's window, whether it is currently open,
and why new work is waiting. It distinguishes an operator pause from other dispatch
gates and links each library to its configuration.

![Schedule view with queue dispatch reason and per-library automation windows](../images/optimisarr-schedule-dark.png)

**Auto-accept passed jobs** is off by default for every library type. Find it in
**Libraries → Configure → Schedule & replace**. Enabling it opens **Do you really,
really mean it?**: acknowledge the risk, confirm, then **Save** the library. Existing
saved choices stay unchanged.

Every job that passes all enabled checks replaces its library file automatically,
including jobs already ready to replace, using the checks from their completed
attempt. Changing verification settings does not recheck those existing outputs.
The original goes to **Quarantine** first.
Approval or retention cleanup permanently deletes that copy and removes rollback
ability. Quarantine is not a backup.

Incorrect settings can damage media, reduce quality, or remove tracks and metadata.
A passing result covers only the checks you enabled. Review a small manual batch
and keep a separate backup before enabling this option. Switching it off and saving
leaves verified jobs ready for manual replacement; a replacement already moving files
finishes safely. **Move output to a target folder instead of replacing** leaves originals in place
and takes precedence over auto-accept.

![Auto-accept confirmation with the risk warning, Quarantine retention notice, acknowledgement, and disabled enable button](../images/optimisarr-auto-accept-confirmation-dark.png)

**Dry-run mode** is a global replacement safety switch. It leaves scanning,
queueing, transcoding, verification, previews, and rollback available, but blocks
manual replacement, auto-replace, and quarantine-original purge. Expired failed
outputs under `/work` are still cleaned because that never touches an original.
Use dry-run for first passes over a real library when you want evidence without
any original-file changes.

**Cleanup retention** applies one simple retention window to quarantined originals
and failed outputs under `/work`. The timed sweep runs at startup and every six
hours. When a failed output expires, its job row, verification report, FFmpeg log,
failure classification, and measured output size remain available for diagnosis;
only the reproducible scratch file is removed. A value of `0` keeps both kinds of
file indefinitely. The reclaimable-space preview beside the setting uses the saved
policy and current file sizes. **Clean up now** applies that same policy immediately
after a confirmation; it does not bypass the retention window or dry-run protection.

Cleanup retention is not a backup policy; retain independent backups of
irreplaceable media and `/config`.

## Excluded files

You can exclude individual files so they are never optimised. From a failed or
stuck job on the **Queue** page, choose **Exclude**; the file is added to a durable
exclusion list and its failed attempt is cleared. A file that fails three times is
**excluded automatically**. Excluded files are skipped by scans, the candidate
list, and auto-optimise.

Each library has an **Excluded** tab listing its exclusions — automatic ones (from
repeated failures) and manual ones are shown distinctly. Remove an exclusion there
to make the file eligible again (which also resets its failure count). Exclusions
are keyed by file path, so they survive clearing the queue, re-scanning, and
re-adding the library. Originals are never touched either way.

## Configuration backup and import

**Settings → System → Backup & restore** can export and import a JSON configuration snapshot. It
includes libraries, activity watchers, notification targets, Arr connections,
and provider credentials in plain text. Store it as sensitive material: do not
commit, share, or leave it in an unprotected download directory.

![Backup and restore card explaining export contents and providing Export config and Import config controls](../images/optimisarr-settings-backup-dark.png)

New exports use configuration format version 3 so older builds reject them rather than silently
lose soundtrack reporting or its gate. Version 1 and 2 backups remain importable on this build;
omitted soundtrack controls preserve existing saved values, and new libraries keep them off.
Omitted standalone audio gate settings also preserve an existing gate.

Import validates the complete file before writing, then merges configuration
without deleting existing entries. It intentionally does not include media,
queued jobs, replacements, quarantined originals, or rollback history. Keep a
separate backup of `/config/optimisarr.db` and `/trash` when that operational
state must be recoverable.
