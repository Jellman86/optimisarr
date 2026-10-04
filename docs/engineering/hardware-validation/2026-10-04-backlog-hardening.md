# Backlog hardening: 4 October 2026

These results distinguish physical-machine testing from deployment verification.
The fleet still used `836231f253370cfaf794d96c115dea2e64b07cda` when these tests ran.
Tests of proposed fixes used isolated source copies; they do not claim the proposed
Windows updater or diagnostic export was already installed on every host.

## Actual media paths

An isolated runner used the application command builder, local verification service
and strict worker evidence evaluator on a generated 16-second repeated-timestamp
source. It contained 400 pictures, audio and subtitles. Its SHA-256 was
`bf0e9789d94acb380c715f077b127bd925fcd456677cede31114ff3271c10886`.

| Host | Actual encoder/runtime | Result |
| --- | --- | --- |
| Mac | Bundled FFmpeg, HEVC VideoToolbox | Local and strict worker verification passed |
| PICARD | Native Windows bundle, HEVC NVENC on RTX 4070 | Local and strict worker verification passed |
| Riker | Deployed server container, HEVC QSV | Local and strict worker verification passed |
| Quark | Deployed sidecar container, software x265 | Local and strict worker verification passed |

Each result had 400 source and 400 candidate decoded pictures, passed configured
quality/structural/size checks and preserved both files' hashes during verification.
Source end was 16.04 seconds and candidate end 16.00 seconds in this fixture. These
count/quality checks complement the earlier numbered-picture identity/order acceptance;
they do not prove normal playback of the private real source in #353 or certify all media.
A first Quark staging attempt used an incomplete archive. It was discarded and repeated
from a complete verified archive in a fresh owned directory; only the complete run counts.
No managed container was restarted for these helper runs.

## Fix-specific testing

| Slice | Physical results | Review/limits |
| --- | --- | --- |
| [#361](https://github.com/Jellman86/optimisarr/pull/361), backend platform contracts | Mac and Quark: 2,764 passed, zero skipped. PICARD: 2,756 passed, eight explicit POSIX-only skips. Zero-warning builds. | Astra review passed. This does not add native Windows server support. |
| [#359](https://github.com/Jellman86/optimisarr/pull/359), regional heartbeat dates | PICARD reproduced the incorrect typed-date conversion before the fix. Regression checks pass for typed dates, UTC offsets and en-GB/en-US/de-DE formats. | The original live update stopped before installation; the test does not claim that upgrade succeeded. |
| [#362](https://github.com/Jellman86/optimisarr/pull/362), Event Log reliability | PICARD shared suite: 303 passed, zero skipped, including real native log writes and concurrent categories. Mac: 299 passed with three Windows-only skips; Quark: 300 with three skips. | The host copies included the concurrency fixture from #360, explaining different totals. Astra review passed. Availability notices omit message text, paths and credentials. |
| [#363](https://github.com/Jellman86/optimisarr/pull/363), diagnostics | Mac/Quark: 2,781 backend tests passed. PICARD: 2,773 passed with eight POSIX-only skips. Zero-warning builds. | Astra review passed. Coverage includes serialized current-contract counts/audio timing and privacy assertions. |

The native Event Log fixture also exposed Windows source-registration latency. It waits
within a bounded deadline using the production retry interval, then verifies the written
entries. The production retry and exception boundaries were not relaxed to pass the test.
For every PR, require its complete CI, including final-image media acceptance and applicable
MSI/paired-worker checks, before merge. The Windows MSI remains unsigned.
