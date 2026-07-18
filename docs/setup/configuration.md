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
enqueue, automatic replacement, and VMAF disabled unless changed explicitly in its editor. Existing
installations upgraded from an older release never see the wizard automatically. To revisit it
without deleting or resetting any configuration, use **Run setup again** in the Settings header.

![Settings General tab showing queue, encoder, scan interval, disk threshold, and hardware decode controls](../images/optimisarr-settings-general-dark.png)

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

Each library has its own root, media type, rule profile, and processing policy.
The Inventory explains why every file is eligible or skipped.

**Configure** opens a dedicated page for that library. Video libraries can disable VMAF or select a
named quality tier. **Custom** exposes the
harmonic-mean, fifth-percentile, and catastrophic-frame floors plus full/clip scoring and the frame
sampling interval. VMAF has no global setting: every video library owns its policy. Upgrades copy
the former global policy into each existing library so behaviour does not change unexpectedly.

| Control | Behaviour |
|---|---|
| Library scan interval | Rescans every enabled library at the configured interval (one hour by default), the only scheduling control in global settings. Scanning also runs once at startup. |
| Concurrent jobs | Bounds parallel encodes. |
| CPU threads | Limits FFmpeg CPU usage where applicable. |
| Work-disk threshold | Prevents new starts when `/work` is too full. |
| Encoder mode | Auto, CPU, NVIDIA NVENC, Intel QSV, or VA-API. |
| Hardware decoding | Uses GPU decode with hardware encoders when possible, including eligible SDR VMAF passes. Runtime failures fall back to CPU decode, and a below-floor accelerated VMAF result is confirmed in software before rejection. |

There is no global processing window: *when* work runs is set per library (see
below). Jobs you queue manually run whenever the queue can start one.

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

## Verification gates

Every job must pass decode health, output readability, and the media-kind checks
that apply to it. Video jobs also have an always-on structural comparison: the output codec must
match the resolved target (or the source for a remux), resolution must not change without a resize
policy, bit depth and chroma sampling may not be reduced, and ffprobe must report a coherent output
profile. These checks are independent of VMAF because perceptual quality alone cannot prove the
requested codec or signal structure was retained. The configurable gates make replacement stricter:

| Gate | Applies to | Default |
|---|---|---|
| Duration tolerance | Video and audio | On, 1% |
| Require audio tracks retained | Video and audio | On |
| Require subtitle tracks retained | Video | Off |
| Require output smaller than original | Video, audio, image | On |
| Perceptual quality (VMAF) | Video re-encodes | Off per library by default; named or custom tiers pick harmonic / fifth-percentile / catastrophic-frame floors |
| Audio loudness drift (EBU R128) | Video and audio | Off |
| Audio clipping (true peak) | Video and audio | Off |
| Image SSIM | Images | On, 0.95 |
| Image metadata | Images | On |

Enabled measurement gates fail closed. If Optimisarr cannot measure an enabled
VMAF, loudness, true-peak, SSIM, or metadata gate, the job fails instead of
becoming replaceable. VMAF is skipped for remux-only work because those jobs copy
the encoded video frames unchanged. The perceptual-quality (VMAF) gate is off by default because it
fully decodes both files and scores every frame, roughly doubling verification time and dominating a
run on modest hardware; each library configuration page can turn it on and prefill all three floors from
named tiers (Space-saver through Archival). Existing installations retain their effective policy, and while the
gate is off the structural, duration and size gates plus quarantine rollback still guard every
replacement. When enabled, **Score three representative samples** measures deterministic 40-second
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
timebases and starting timestamps, normalises colour range and pixel format, and
uses bounded automatic threading. It selects Netflix's `vmaf_v0.6.1` HDTV model
for HD material and `vmaf_4k_v0.6.1` when either source axis reaches UHD. If a job
intentionally converts HDR to SDR, the reference receives the same production
tone-map before comparison; HDR-preserving jobs keep both streams in the matching
HDR transfer domain. SDR jobs follow the selected encoder's hardware decode path when Hardware
decoding is enabled: QSV/VA-API download decoded frames for CPU VMAF, while a compatible NVIDIA
build can use NVDEC, `scale_cuda`, and `libvmaf_cuda` end to end. Hardware attempts always retry in
software on failure, and HDR always uses the established software colour pipeline. Only VMAF is
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
The requested and effective values are stored with each job. When VMAF is the only failed gate,
Optimisarr makes one automatic higher-quality retry. If that recovery still fails VMAF, the file is
automatically excluded from future optimisation and remains reversible from the library's **Excluded**
tab. A size-saving failure excludes immediately instead of silently lowering the configured quality;
the same applies when size and VMAF both fail because higher quality would worsen size while lower
quality would worsen VMAF. Other technical or transient failures retain the three-terminal-failure
threshold. Cancelled work and jobs interrupted by a worker restart do not count toward exclusion.

## Rule profiles (presets)

Each library picks an **optimisation preset** that sets its codec, container, and a
researched quality target; anything can be fine-tuned under **Advanced options**.

| Preset | Targets |
|---|---|
| Compatibility (H.264) | H.264 / MP4 with channel-aware AAC — plays everywhere, larger files. |
| Balanced (HEVC) | HEVC (H.265) / MP4 at CRF 24 with channel-aware AAC — a good default. |
| Efficiency (AV1) | AV1 / MKV — smallest files, slower to encode. |
| **Scott's Settings** | HEVC / MP4 at CRF 24, **HDR preserved**, audio re-encoded to **AAC 96 kbps downmixed to stereo**. A compatibility-first, space-saving bundle; the same AAC 96 kbps stereo target applies to a music library. |
| Remux / cleanup | No re-encode — repackage into a clean container only. |

A file already in the target codec is normally skipped. Enable **"Re-encode large
files already in the target codec"** (Advanced options) to also re-encode oversized
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

**Keep audio languages** (Advanced options) removes unwanted audio tracks while a
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

**Keep subtitle languages** (Advanced options) works the same way for subtitle
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

**Auto-replace** is disabled by default. When enabled for a library, a job that
passes every verification gate is replaced automatically. The original is still
quarantined first and remains rollback-able through **Quarantine**. Enable it
only after validating a small manual batch for that library.

**Dry-run mode** is a global replacement safety switch. It leaves scanning,
queueing, transcoding, verification, previews, and rollback available, but blocks
manual replacement, auto-replace, and quarantine purge. Use it for first passes
over a real library when you want evidence without any original-file changes.

Quarantine retention is not a backup policy; retain independent backups of
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

The **Settings** page can export and import a JSON configuration snapshot. It
includes libraries, activity watchers, notification targets, Arr connections,
and provider credentials in plain text. Store it as sensitive material: do not
commit, share, or leave it in an unprotected download directory.

![Backup tab showing export and import controls plus the sensitive-data warning](../images/optimisarr-settings-backup-dark.png)

Import validates the complete file before writing, then merges configuration
without deleting existing entries. It intentionally does not include media,
queued jobs, replacements, quarantined originals, or rollback history. Keep a
separate backup of `/config/optimisarr.db` and `/trash` when that operational
state must be recoverable.
