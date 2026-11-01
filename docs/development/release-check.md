# Release regression check

Run [`scripts/release_check.py`](../../scripts/release_check.py) from the reviewed release
checkout. It creates a new disposable server for each step, never connects to an existing
Optimisarr instance, and never deploys an application. Use a local Docker daemon and keep all
inputs, clips, scratch and full evidence under an operator-owned root outside the repository.

Copy [`release-check.config.json`](../../scripts/examples/release-check.config.json) outside
the checkout and replace every placeholder. Configure the required worker identities explicitly;
an absent worker, package, native-only runtime or required metadata is **blocked**, never passed.
Do not put credentials in this config. Use existing SSH host aliases and key/agent authentication.

```bash
python3 scripts/release_check.py \
  --image ghcr.io/jellman86/optimisarr@sha256:<exact-published-digest> \
  --config /operator/private/release-check.config.json \
  --corpus /operator/private/house/corpus.json \
  --root /operator/private/release-runs/new-run
```

Omit `--image` to build the current checkout into a unique local image. Accepted images must use
an immutable digest; the runner resolves that image to its immutable local ID for every subsequent
step. A locally built image is development evidence; repeat against the published digest for the
release artifact. Each invocation and every nested acceptance run needs a new root. No production
library is mounted. This command can take hours; a step has a bounded timeout (default four hours).

## Artifact receipts

The private config records `artifacts.image`, `artifacts.macos` and `artifacts.windows`. Each needs
`sourceCommit`, `version`, `ffmpegRevision` and `nativeHelperRevision`. Revisions are full upstream
Git hashes; obtain them from the exact package build/source records, not a new upstream lookup.
`sourceCommit` must match the release checkout. Mac and Windows records also need a package
`path` or a SHA-256 `sha256` receipt. With a path, the runner hashes the actual ZIP/DMG/MSI and
checks any supplied hash; with only a hash, the operator attests to an artifact retained elsewhere.
A missing path or missing metadata blocks the release check. These are **operator receipts**, not
cryptographic proof of which binary a remote process loaded.

A Mac app ZIP's `CFBundleShortVersionString` is checked against its receipt. DMG/MSI versions
come from the operator's package receipts; the runner does not mount a DMG or install an MSI.
It observes the container FFmpeg version and native audio-helper revision directly, checks the
helper against its receipt, and compares the running server/worker versions with the receipts.
Each worker config also supplies `ffmpegRevision` and `nativeHelperRevision`; protocol and
sidecar versions come from the paired worker's actual check-in. Missing worker version/protocol
or tool revisions block that coverage. Keep package installation, signing, upgrade/recovery and
source-distribution qualification alongside this report; this command does not perform them.

## Coverage and remote workers

The command runs smoke with diagnostics and VMAF shadow evidence, all 18 focused regressions,
the house corpus (a run without one is blocked), and a fleet run with **every** `requiredWorkers` identity paired
beside the same isolated server (mixed Docker, local and SSH launchers).
The regression inventory is tested against both the harness choices and every CI workflow.
`quality-first-ownership` uses `native.path` and its configured tools; without a native POSIX
instance it is blocked. `interactive-lifecycle` and `quality-first-workers` run on each required
worker. Soundtrack selection and the house corpus also repeat with each worker. Successful
preflight alone, an empty/partial report, a missing worker or a child that exits nonzero cannot pass.

Each worker selects one encoder and gets its exact configured name. The release command writes
a private `--worker-config` list for the harness; standalone runs can use `--worker-name`.
Use separate entries for additional encoders. A worker entry uses a disposable Linux `image`, a
local `command` argument array, or `ssh`. Devices/GPUs are opt-in via `devices`/`gpus`; the default
6 GiB worker memory allows native image-quality jobs. Normal full fleet fixtures include SDR,
VFR, offsets and 10-bit. A selected matrix does not certify every driver or full-length source.

For SSH, build the existing macOS `AcceptanceWorker` or Windows
`Optimisarr.Sidecar.Acceptance` at the same commit and configure its command plus paths to the
packaged media tools/helpers. The operator must verify those paths refer to the qualified package.
The remote host needs Python (`ssh.python`, a simple executable path without spaces), the
acceptance launcher and its dependencies. On Windows use Python on PATH and the standard
OpenSSH command shell. No host aliases are built into the scripts.

`ssh.serverUrl` must contain `{port}`. The adapter substitutes the **new isolated server's** port;
it cannot take a fixed existing application URL. A trusted test interface must reach the bound
`listen` address. Configure firewall access outside this command. Pairing PINs travel only in
SSH stdin, never command arguments or config files, and are removed from relayed launcher logs.
Only tool paths are allowed in the forwarded config environment. The remote supervisor owns its
child tree: stdin EOF, missing keepalives, termination or its four-hour deadline stops that tree.
It does not stop installed services, reuse pairing or delete existing worker directories. Scratch
and accounting paths receive random suffixes. SSH orchestration is unit-tested with fakes;
physical platform execution remains an explicit operator qualification.

## Private house corpus

Copy [`house-corpus.entries.json`](../../scripts/examples/house-corpus.entries.json) outside the
repository and replace its placeholder paths. This is the input list, not a checksum manifest.
Every entry supplies `source`, `start` (seconds), `seconds` (at most 600), `reason` and `expected`.
Optional `property` is a fixed public category, `targetContainer` selects MKV/MP4, and `cut` is
`stream-copy` (default) or `whole-file` for an already-short source. Example expected outcomes
are starting hypotheses: qualify the actual clip and record the exact safe outcome, never change
an expectation merely to hide a failure.

```bash
python3 scripts/acceptance_house_corpus.py \
  --entries /operator/private/house.entries.json \
  --root /operator/private/house \
  --ffmpeg /operator/tools/ffmpeg --ffprobe /operator/tools/ffprobe

python3 scripts/media_acceptance.py --image optimisarr:rc \
  --regression house-corpus --corpus /operator/private/house/corpus.json \
  --root /operator/private/house-runs/new-run
```

The cutter reads originals and copies every stream, metadata and attachment where the source
container permits. It never falls back to a re-encode. Stream copy can add keyframe pre-roll,
change duration statistics or normalize timing; inspect `sourceProbe` and `probe` in the private
manifest to confirm the chosen property survived. Remuxing cannot reproduce every historical
mkvpropedit/Lavf quirk. Use `whole-file` for existing short clips whose exact bytes matter; it
requires start zero and a probed duration within the requested bound. SHA-256, codecs, channel
layouts, tags and the reason/expectation are retained. Manifest import checks checksums and
confines every clip path to the manifest directory, preserving the original extension.

The house regression creates a video re-encode library with the soundtrack gate, stereo downmix
and adaptive soundtrack selection enabled. The explicit distance limit is 0.01. `passes` needs a
measured search without fallback and a passing final gate; `search-fallback` requires an actual
fallback and a passing final gate. `{"outcome":"blocked","reason":"substring"}` needs a failed
job with that specific refusal, an unchanged source and no replacement. An expected blocked
entry **passes its assertion**; unavailable infrastructure still blocks qualification. Successful
cases decode the candidate and exercise replacement/rollback only on disposable fixture copies.
Use long enough clips (normally 40–90 seconds) to exercise search windows.

Negative-start Opus remains the known open [#474](https://github.com/Jellman86/optimisarr/issues/474)
first-window bug; the example records it as expected blocked. Legacy MKVs without per-track
duration and with a video lead over 100 ms remain refused by design, also expected blocked.
Subtitle-heavy short clips exercise stream preservation and worker claims, but do not certify
multi-gigabyte demux latency. The first claim for a large uncached source can still time out;
the cached retry is the #466 safeguard.

**Never commit or publish clips, input lists, corpus manifests or full reports.** The default
in-repository convenience paths `.acceptance-house/` and `.release-check/` are ignored; prefer
external roots. Developer state and these private corpora are outside ordinary application backup.

## Shareable release artifact

Only `release-check.json` and `release-check.md` may leave the operator's root. Attach those
redacted summaries to the release. They contain fixed test properties, opaque step/clip/worker
IDs, outcomes, digests/hashes, versions and tool revisions. Export uses an allowlist, not string
replacement: case.json, job.json, arbitrary failure messages, source paths, titles, library names,
SSH aliases, probe metadata and full manifests are never copied to the summary. Free-form corpus
`reason` stays private; select a documented `property` for public attribution.

`private-report.json`, nested harness reports, case.json, logs and the original corpus manifest
remain private. The summary is blocked until orchestration finishes. Exit codes are 0 for pass,
1 for failure, 2 for blocked coverage. Any failed/blocked summary blocks publication, as does a
missing summary. Inspect private evidence locally to resolve the affected opaque step, then rerun
under a new root; never attach the full report to explain it.
