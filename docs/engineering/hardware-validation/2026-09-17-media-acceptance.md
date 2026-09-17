# Media acceptance harness — 2026-09-17

## Scope

Implemented and exercised from an isolated worktree based on `81b5a6f`, with fresh native
application instances and a disposable macOS worker. No production library, installed worker
pairing, or other agent's checkout was changed. See the
[execution guide](../../development/media-acceptance.md) for reproducible commands and the
remaining coverage boundaries.

The local FFmpeg/ffprobe/reference measurement bundle reports `n8.0.3`. The main matrix used
local `libx265` and the actual macOS sidecar core with `hevc_videotoolbox`, generated SDR/VFR/
timestamp-offset fixtures, and six checksum-pinned Big Buck Bunny/Tears of Steel excerpts.
This is a selected Mac matrix, not certification of all worker hosts or all advertised codecs.

The subsequent [Windows/container run](2026-09-17-windows-container-acceptance.md) passed all
146 fleet cases, all 17 container smoke cases and all 170 Windows core tests on the real PC.
It resolves the Windows/container access gap recorded below. The subsequent
[installed Mac/Intel container run](2026-09-17-installed-mac-container.md) added 52 passes and three
failures on the deployed build, plus a passing longer Mac run.

## Results

| Check | Result |
|---|---|
| Native real-media smoke | 17 passed; no failures or blocked cases |
| Selected local + Mac worker matrix | 37 passed, 1 failed: calibration decode health |
| Backend tests | 2,028 passed |
| Browser tests on an isolated preview port | 141 passed |
| Frontend unit tests | 28 passed |
| macOS sidecar tests | 188 passed |
| Windows core tests, executed on Mac | 170 passed; this is not Windows hardware evidence |
| Python harness/documentation tests | 14 passed |
| Release build | No warnings or errors |
| OpenAPI, documentation links and frontend checks | Passed |
| Container execution on this development host | Blocked: Docker CLI unavailable |

The successful media cases independently measured raw VMAF frames and checked model, frame count,
harmonic mean, fifth percentile and minimum against application evidence. They also checked
decode/structure, preserved audio samples, actual worker attribution, replacement hashes and exact
rollback. Audio/image, preview, concurrent isolation, cancellation, upload faults and abrupt native
server restart cases passed. The intentionally impossible VMAF gate and damaged candidates were
rejected as expected.

## Defects found and fixed

- Readiness ignored configured work/quarantine directories.
- Finished remote jobs lost their worker attribution.
- Short previews were checked against the requested duration even when the source was shorter.
- Remote VMAF plans omitted freshly probed source cadence for variable-frame-rate media.
- Releasing a cancelled worker lease requeued the cancelled job.
- The later live run exposed missing output directories on software-decode retries; the fix
  centralizes directory reservation/creation for every FFmpeg attempt. Three new regression tests
  passed, taking the backend total to 2,031, with a zero-warning release build.

The first, second, third and fifth fixes have focused backend regression tests; the cadence fix
was exercised against actual VFR local/worker outputs and independent measurements.

## Outstanding evidence

Calibration fails decode health with the installed Mac FFmpeg bundle. An earlier broad codec run
also found AV1 decode failures and rejected a 10-bit HEVC candidate that became 8-bit. These remain
failures; no gate was weakened to make the matrix green. They need investigation with the exact
packaged media tools used for each platform.

The initial Mac session could not access a container runtime. After the PC became available,
Windows/NVIDIA and the Linux container passed the linked follow-up run. Authenticated access to
the existing Linux deployment subsequently allowed fixture-only Intel QSV and installed Mac
checks, documented in the linked live record. VAAPI remains outside these new runs.
No production container was changed. CI is wired to run the new final-image smoke gate before
publication, but that workflow has not been executed for these unpushed changes.

HDR/Dolby Vision, richer metadata/stream corpora, sampled-window audits, real worker disconnects,
mid-replacement disk exhaustion and resource-leak thresholds are documented follow-up coverage;
neither the smoke result nor this selected Mac matrix certifies those paths.
