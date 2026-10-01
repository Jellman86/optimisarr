# Standalone sidecar audio — 2026-10-01

## Contract and safety

Protocol 6 introduces standalone AAC, Opus and MP3 assignments with an explicit audio
kind and proved audio encoder. Verification contract 2 carries complete audio probe,
decode and packet evidence, bound to the same frozen lease and transferred hashes as
video. VMAF is inapplicable to standalone audio. The server evaluates every configured
policy gate; missing evidence fails without local fallback. Replacement and rollback
remain server-owned. This change uses existing persisted JSON and adds no DB schema.

Tests failed before implementation for audio capability matching, claim/contract shape,
strict verification, spectral command construction, preview deadline, frozen codec
identity, unknown Swift wire kinds and native Windows acceptance cleanup. Additional
negative coverage checks decode errors, timestamp regression, short duration, channel
loss, empty packet spans, wrong codec, loudness drift, clipping, missing probes and non-finite evidence.

## Physical test results

| Platform | Evidence |
| --- | --- |
| Apple Silicon Mac | Fresh pinned FFmpeg n8.0.3 with Opus 1.5.2 and LAME 3.100; all audio matrix cases passed |
| PICARD / native Windows | Installed FFmpeg n8.1.2; all audio matrix cases passed; zero-warning sidecar solution build; 264 core tests; native monitor rendering and anchor checks passed |
| Quark / native Linux | Retained actual-container Jellyfin FFmpeg 7.1.4 tools; all audio matrix cases passed; zero-warning API/worker builds; 264 core and 30 dashboard tests |

Each final matrix has local AAC/Opus/MP3/MP3-art and worker AAC/Opus/MP3/downmix/MP3-art
cases. These independently inspect codec, stereo layout and artist/title metadata,
fully decode the candidate, measure EBU R128 and true peak, require correct worker
attribution and worker verification location, replace, and roll back to the exact source
hash. Fixture paths include spaces, quotes and Unicode. Seeded pink noise and generated
cover art contain no private or copyrighted media.

Final reports are retained in the owned test roots:

- Mac: `/tmp/optimisarr-audio-mac-acceptance-covers-20261001/report`
- PICARD: `C:\Users\scott\optimisarr-audio-windows-acceptance-20261001\report`
- Quark: `/tmp/optimisarr-audio-linux-acceptance-final-20261001/report`

The first Linux run failed safely because loudness used the separately configured video
VMAF tool. A live regression now supplies a deliberately absent VMAF binary and requires
complete audio evidence using the audio FFmpeg. The corrected Linux matrix passes.
An early Opus run found a harness candidate-search omission; `.opus` discovery now has
its own regression. Failed exploratory runs are retained, not counted as passes.

## Second-order and visual review

- Old protocol workers retain video work but cannot claim audio; missing audio encoders
  and local-only placement also refuse audio claims.
- Audio-only capability sets need no video encoder or VMAF backend. Audio assignments
  reject a video contract/search before processing files.
- Spectrograms measure the primary source audio over at most three seconds near the
  encoding cursor. One sample may run per job, at most every 1.5 seconds while viewed.
  Output is bounded to 8 KiB, with a three-second deadline and cancellation. Preview
  failure never changes a quality verdict. The Swift runner now performs this work off
  the UI thread and stops oversized/unresponsive producers.
- Native light/dark and Linux desktop/phone captures use a generated chirp. Review
  removed video-shaped empty thumbnails and duplicate Mac spectra, preserved the
  established card/hover identity, and fixed the Linux caption’s grid placement and
  misleading audio/VMAF/server workflow text.
- Mac redistributable source manifests now include the exact Opus Git revision and
  checksum-locked LAME archive, with extracted license notices. All three audio
  encoders pass capability probes in the rebuilt bundle.

Local validation: 2,534 backend tests, 264 shared-sidecar tests, 30 Linux dashboard tests,
244 Swift tests (including real audio previews and cancellation/output bounds), live
RAM lifecycle tests, 72 Python tests, 75 frontend unit tests and 196 browser tests.
Dotnet builds and Svelte checks are warning-free. Full native hardware matrices and
final-image CI complement deterministic tests; this is coverage of the supported
codec/fixture paths, not certification of every possible recording or toolchain.

## Container and deployment boundaries

Final server-image CI now runs the local audio matrix. Paired Linux-image CI runs the
strict worker matrix; Windows installer CI exercises its bundled spectrogram/verifier.
CI reports are required before merge and retained as artifacts. Physical runs used fresh
native test servers and disposable worker identities. They did not alter production
libraries, installed pairing, running services or container deployment. New sidecars
and the server must be released/upgraded together before production audio offload.
