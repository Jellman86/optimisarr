# Soundtrack quality validation

Validation dates: 2 and 3 October 2026. This records the optional video soundtrack quality
report and gate introduced on the development branch.

## Scope and limits

The report measures every retained, re-encoded mono or stereo soundtrack, up to
eight tracks and 90 seconds per track. Language removal uses the frozen stream
mapping. Copied audio bypasses this additional measurement. Surround and downmix
assessment remain unavailable; an enabled gate stops replacement when required
evidence is unavailable. Reporting and gating default to off, and an enabled gate
requires an explicit maximum difference. No listening calibration or universal
quality threshold is claimed.

Selected soundtrack windows leave the final 100 ms outside the assessment to avoid
encoder padding. Decode preparation requests a short timestamp allowance, then
trims to the assigned sample count without adding samples. The report records actual
coverage. The existing complete-file decode and retention checks still apply.

## Automated checks

- Application: 2,705 tests passed, with a Release build containing zero warnings.
- Web: type checks were clean, 80 unit tests and 257 Playwright tests passed, and
  the production build succeeded. Settings and report screenshots use fabricated
  media and were inspected for clipping and theme consistency.
- Acceptance scripts: 77 Python tests passed, including independent report
  validation and the idempotent FFmpeg patch test.
- Shared worker core: 285 tests passed. Linux worker: 30 tests passed.
- macOS: the full Swift suite and real RAM-volume capacity and cleanup checks
  passed (267 Swift tests); the release app was built with its bundled tools.
- The additive migration was tested against fresh and existing databases, including
  repeat application. Older configuration exports preserve existing soundtrack
  settings when the new fields are absent.

## Real media and machines

Generated video, seeded noise and sine fixtures avoid third-party media rights and
private media dependencies. The complete application acceptance run passed AAC,
Opus and MP3 soundtracks, removal of the first audio stream, explicit gate rejection
and copied-audio bypass. Two further cases cover a soundtrack shorter than its
video and a 120-second file whose tracks each require three sample windows. Successful cases exercised replacement, repeat-call
idempotency and rollback to the original bytes.

The server container, Linux sidecar, Windows sidecar's installed MSI tools and native
macOS tools were exercised. The follow-up fixes were exercised with 120-second fixtures on the server container
and Linux worker. Windows follow-up testing and its MSI update are deferred until
the Windows machine is available again. Local and strict worker verification accepted the good
tracks, rejected silenced commentary and missing evidence, and preserved the
language removal mapping. Original fixtures remained unchanged and scratch files
were cleaned up. Actual native Swift reports were also consumed by the server with
local media tools unavailable, proving that strict worker verification did not fall
back to server processing.

The macOS bundled FFmpeg build includes the upstream
[Opus end-of-file parser fix](https://github.com/FFmpeg/FFmpeg/commit/618fc15e65).
This prevents a valid Opus file from producing a false decode error while retaining
the full corruption check. Its source patch and packaging record are included in
the repository.

## Independent review

Astra reviewed the implementation and subsequent fixes. Its findings about
relative audio/video timestamps, silent video and incomplete nested evidence were
fixed with regression tests. Opus 5.5 subsequently completed a review through the
Claude CLI. Its findings prompted per-track duration handling, one before/after
identity check per batch, fixed-point Mac seek arguments, a native Swift evidence
fixture and longer acceptance cases. Follow-up reviews by Astra and Opus found no
replacement-safety blocker. Astra identified conflicting Matroska duration writers;
the parsers now refuse ambiguous nonzero-start tags instead of guessing.

Tracks without usable duration metadata are unavailable. MKVToolNix elapsed-length
tags and FFmpeg end-timestamp tags are handled separately. Files combining FFmpeg
container metadata with MKVToolNix statistics and a nonzero track start cannot prove
which tool last wrote the duration; an enabled gate stops replacement. This follows
the [MKVToolNix maintainer’s explanation](https://help.mkvtoolnix.download/t/duration-discrepancy-for-audio-streams-in-ffmpeg-written-matroska-files/1553)
and the pinned FFmpeg Matroska muxer implementation.

The saved native Swift fixture verifies server compatibility without depending on
installed media tools. Batch regressions cover changes to either media file, changes
to a metric executable, and a failed final hash read. All batch results become
unavailable when identity checks fail; originals and owned scratch cleanup remain
protected.

This evidence covers the tested formats and fixtures. It does not establish a
perceptual calibration study, surround support or a guarantee for every codec and
container combination.

## Predicted size failures

Sample forecasts above the configured output limit now fail the attempt before a
full encode. Regression tests cover local transitions, worker lease release, an
explicit retry, cancellation followed by reassignment, and idempotent conversion
of older review holds. The UI labels the prediction and says that the full encode
did not run; a mobile browser test checks the failure detail and available actions.
These predictions remain estimates. No replacement is made from sample evidence.
