# Optimisarr macOS sidecar

A menu-bar app that pairs a Mac with an Optimisarr server so it can contribute spare encoding
capacity.

## What this version does, and does not

It pairs, stores its credential, reports what this Mac can actually do, checks in, and — once the
server has verified it can finish a job that came back — asks for work. A job runs like this:

1. **Claim.** On each healthy check-in while idle, the app asks for one job. The server answers
   with the exact FFmpeg command it would have run itself, resolved for an encoder this Mac
   proved, with two tokens standing in for paths.
2. **Validate.** The command is checked before a byte is fetched: every option is one the server's
   builder is known to emit, the only input is the `{{input}}` token, the only output is the
   `{{output}}` token in last position carrying the promised extension, and no other value looks
   like a path. Anything else is refused whole and the job handed back with the offending token
   named. The server decides *what* to encode; it never names files on this machine.
3. **Fetch and prove.** The source is downloaded by lease into the app's own scratch in 64 MB byte
   ranges. A dropped connection resumes from the last complete range rather than restarting a
   multi-gigabyte file. Every range is checked against the server's response and the assembled
   source is hashed; a transfer that does not match the server's hash is never encoded. Immediately
   before the first byte, the sidecar also rechecks that its real work volume can still hold the
   source plus the candidate allowance and hands the lease back if it cannot.
4. **Encode, renewing.** The bundled ffmpeg runs the command against this Mac's paths. The lease
   is renewed throughout; losing it stops the encode rather than finishing work the server has
   already given to someone else.
5. **Measure.** When the library has a quality gate, the assignment also carries the server's own
   libvmaf command for each measurement window, validated the same way. The app runs them against
   the source and the candidate and posts the raw JSON logs with both hashes. Nothing is computed
   here; the server parses, pools and judges the logs, and only believes them if the candidate it
   then receives carries the same hash. If a measurement cannot be made, the candidate is still
   delivered and the server measures for itself.
6. **Deliver.** The candidate is hashed and uploaded with both hashes, so the server can bind it to
   this exact source — in 64 MB chunks at offsets the server confirms, resuming from whatever it
   holds after a dropped connection, or in one request against a server that predates that. The server then verifies it against the original exactly as it would a local
   encode. Nothing is replaced from here, ever.

Scratch lives under `~/Library/Application Support/OptimisarrSidecar/work` and is removed on every
exit path. **Jobs at once** in the menu chooses how many jobs run in parallel (one to four); the
number is reported on every check-in and the server holds the worker to it. The machine is probed
again on every launch, not only at pairing, so a relaunched app reports what it can do today.

While a job runs the app holds a system activity assertion, so macOS neither naps it nor idles the
machine to sleep under an encode. When the Mac does sleep — a closed lid, a chosen sleep — the job
is handed back to the server first, so it is reassigned at once rather than after the lease lapses,
and check-ins resume the moment the Mac wakes. Quitting the app mid-job hands the job back the same
way before the app exits. **Start at login** in the menu registers the app as a login item through
the system's own service, so it also appears under System Settings › General › Login Items; it needs
the app to run from the built bundle rather than a bare build directory.

This loop has run end to end on real hardware: `LiveWorkLoopTests` pairs with a running server,
claims a queued job, encodes it with the bundled ffmpeg and delivers it, and the server's own
verification — every gate, VMAF included — then judges the candidate. Run it against a server that
has a job queued this Mac can take:

```bash
OPTIMISARR_LIVE_URL=localhost:8787 OPTIMISARR_LIVE_PIN="1234 5678" \
OPTIMISARR_FFMPEG=$(pwd)/vendor/ffmpeg swift test --filter LiveWorkLoop
```

What it reports is real. It bundles its own ffmpeg, built from pinned source by
[`scripts/build-ffmpeg.sh`](scripts/build-ffmpeg.sh), and probes this machine in two stages: parse
`ffmpeg -encoders`, then confirm each VideoToolbox encoder with a real throwaway encode. Every Apple
build lists VideoToolbox whether or not a given machine can open it, so listing alone would have the
sidecar advertise encoders that fail on first use. Hardware *decode* is proved the same way — encode
a clip, decode it back with VideoToolbox engaged, and both halves must succeed.

When the server has seen that proof, the command it sends decodes with VideoToolbox for a
VideoToolbox encode, with the frames left in system memory so its filters still apply; the
validator accepts `-hwaccel videotoolbox` and nothing else under that option. If a candidate decoded
that way comes back with the signature of decoder corruption, the server requeues the job to decode
in software and says so on this worker's card.

A machine that proves nothing reports nothing, and Optimisarr's capability matcher fails closed, so
such a worker is never offered work. Honesty here is the safety mechanism: a sidecar that overstated
itself would have jobs scheduled onto it that could only fail.

Optimisarr remains the only thing that replaces, quarantines, moves, or deletes a file. A sidecar
never can, by design, and nothing in this app is capable of touching media.

## Codecs

Encoding, all proved with a real test encode at launch rather than taken from FFmpeg's listing:

| Target | On this Mac |
| --- | --- |
| H.264 | `h264_videotoolbox` (hardware) and `libx264` |
| HEVC | `hevc_videotoolbox` (hardware) and `libx265` |
| AV1 | `libsvtav1` (software only) |
| Audio | `aac`. **Not** `libopus` or `libmp3lame` — the build links no external audio libraries, so a library set to Opus or MP3 is never offered to this worker |

**There is no hardware AV1 encoder on Apple Silicon.** VideoToolbox on an M5 advertises 27 encoders
and not one of them is AV1, so AV1 here is SVT-AV1 on the CPU. Decoding AV1 *is* a hardware path
the chip has, and FFmpeg 8.0 is the first release with the VideoToolbox AV1 hwaccel, which is why
the build is pinned there.

Decoding covers H.264, HEVC, VP9, AV1, MPEG-2, VC-1 and ProRes, with VideoToolbox acceleration
where the server asks for it.

## Options

**Options…** in the menu opens a panel for how the Mac does the work, kept apart from the menu so
watching a job and configuring one stay separate.

**Where work happens.** A job downloads its source and writes its candidate before sending it back,
which together come to roughly one and a half times the size of the original. Three choices:

- *The app's own folder*, inside Application Support on the startup disk. The default.
- *A folder I choose* — an external SSD, or simply somewhere off the startup disk. If the drive is
  not mounted at launch this falls back to the default rather than failing every job on a path that
  no longer exists.
- *Memory*, a RAM disk created for each job and destroyed when it ends.

**About memory.** A job needing more working space than the budget runs on disk instead. It is
never refused over this setting — losing work to a preference would be worse than ignoring the
preference, and a refused job goes straight back on the queue to be offered again. The budget
defaults to a quarter of installed memory and is adjustable between a twentieth and a half; a RAM
disk holds real pages for as long as the job runs, and filling most of a Mac's memory with one
leaves it swapping, which is slower than the SSD the setting was meant to avoid.

It is also rarely faster. A download is limited by the network and an encode by the encoder, not by
an Apple SSD. Most films will not fit any sensible budget.

Stray volumes from a crash are swept at launch, since one left behind holds memory until the Mac
reboots with nothing on screen to say so.

## Requirements

- macOS 14 or later
- Xcode 26 (or a Swift 6 toolchain) to build
- An Optimisarr server with **Settings → General → Remote workers** switched on

## Build and run

```bash
cd sidecars/macos
./scripts/make-app.sh          # or: ./scripts/make-app.sh release
open build/OptimisarrSidecar.app
```

It appears in the menu bar with no Dock icon. Click it, enter your server address and the pairing
code from **Settings → Workers** in Optimisarr, and press Pair.

The server address is whatever you use to reach Optimisarr in a browser — `optimisarr.local:8787`,
an IP and port, or a full `https://` URL behind a reverse proxy. A missing scheme is assumed to be
`http://`, since this is usually a LAN tool.

## If no icon appears

The app has no window and no Dock icon, so a menu bar with no room left for it looks identical to
an app that failed to launch.

macOS fills the menu bar right-to-left, and on a Mac with a notch the items that run out of room go
behind it rather than being pushed off the edge. A newly launched app is last in the queue, so it is
the first to disappear.

This was hit on the first real launch. Measuring the screen with `NSScreen` rather than guessing:

| Region | Range |
| --- | --- |
| Usable left of notch (`auxiliaryTopLeftArea`) | x 0 – 646 |
| **Notch** | **x 646 – 825** |
| Usable right of notch (`auxiliaryTopRightArea`) | x 825 – 1470 |
| This app's status item | **x 735 – 769** |

Entirely inside the notch, with nine other status items to its right. Note that the empty space to
the *left* of the notch is not available: macOS reserves it for the application menu and never
places status items there, so a menu bar that looks half empty can still have no room.

**Launch it again.** `open` on an already-running app raises it rather than starting a second copy,
and this app responds by opening a normal window with the same pairing screen. That is the way back
in when the icon cannot be seen — macOS has no overflow menu for status items the way Windows does
for the system tray, so a hidden icon is otherwise unreachable. The window also opens by itself on
first launch while nothing is paired.

Check whether it is actually running before assuming it crashed:

```bash
pgrep -lf OptimisarrSidecar
```

If it is running but invisible, make room: ⌘-drag any visible status icon leftward past the notch,
which reorders the row and pushes this one out the far side, or quit a menu bar app to free the
width. Once visible it can be ⌘-dragged wherever suits.

## Tests

```bash
swift test
swift build --configuration release
```

The suite covers the protocol client, resumable transfers, scratch-space refusal, lease loss during
transfers, the pairing and check-in lifecycle, address handling, and capability probing — including
live probes against the bundled ffmpeg. CI runs the ordinary suite and release build on an Apple
Silicon macOS runner; the live suites remain explicit acceptance tests because they need the pinned
FFmpeg and, for the work loop, a paired server with a suitable queued job.

There is also a live suite that runs against a real server, skipped unless you point it at one:

```bash
OPTIMISARR_LIVE_URL=localhost:8787 OPTIMISARR_LIVE_PIN="1234 5678" swift test
```

Worth running when the contract changes. The stubbed tests prove this client behaves the way its
author believes the contract works; only a live run proves the belief itself.

## Where the credential lives

In the login Keychain, under `uk.optimisarr.sidecar` — never in `UserDefaults`, a plist, or a log.
It is issued once at pairing and cannot be reissued by the server, so it is written to the Keychain
before anything else can go wrong.

"Forget this pairing" clears it locally. That does **not** revoke it server-side; only an operator
can do that, from the Workers tab in Optimisarr. If a worker is revoked there, this app notices on
its next check-in, discards the dead credential, and says so.

An item written by an earlier build whose signature this one no longer matches cannot be read —
an ad-hoc build's signature changes every time it is rebuilt, so to the Keychain it is a different
application each time. The app detects that without letting a dialog appear, removes the item, and
reports itself unpaired so you pair once more rather than being asked for a password for ever.
Builds signed with the same Developer ID certificate read each other's items, so an upgrade keeps
its pairing.

## Pairing without a screen

```
echo "<pin>" | /Applications/OptimisarrSidecar.app/Contents/MacOS/OptimisarrSidecar \
  --pair https://optimisarr.example.com
```

Pairs and exits, printing the worker id on success and the reason on failure. Nothing appears on
screen, so a Mac can be paired over SSH, scripted onto several machines at once, or recovered
remotely when a pairing is lost.

The PIN is read from standard input rather than taken as an argument, so it never lands in `ps`
output or a shell history. Get one from the Workers tab, or from
`POST /api/workers/pairing-code`.

Give the address with its scheme. A bare host is reached over `http://`, which is right for a
server on a home network and wrong for one behind a TLS proxy; the failure message says so when the
address had no scheme.

## Signing and release

`make-app.sh` applies an ad-hoc signature by default, which is enough to run locally. Set
`SIGNING_IDENTITY` to sign with a real certificate:

```bash
security find-identity -v -p codesigning          # what this Mac holds
SIGNING_IDENTITY="Developer ID Application: You (TEAMID)" ./scripts/make-app.sh release
```

A real certificate is worth using even for local work. An ad-hoc signature is derived from the
binary, so it changes on **every build**, and the Keychain — which decides access by signature —
sees each rebuilt copy as a different application. The pairing then cannot be read, and on the
legacy keychain macOS asks for a password to reach it, over and over. With a certificate the
signature is stable and a pairing survives rebuilds.

### What you need once

1. **A Developer ID Application certificate.** In Xcode: Settings → Accounts → your Apple ID →
   Manage Certificates → **+** → *Developer ID Application*. Only the Account Holder of the team
   can create one. An *Apple Development* certificate is not a substitute: it signs and runs
   locally, but Apple will not notarise anything signed with it.
2. **An App Store Connect API key** for notarisation, from
   [App Store Connect → Users and Access → Integrations → App Store Connect API](https://appstoreconnect.apple.com/access/integrations/api),
   with the **Developer** role. Download the `.p8` once — it cannot be downloaded again — and note
   the Key ID and the Issuer ID. Then store it under a name the release script can use:

   ```bash
   xcrun notarytool store-credentials optimisarr-notary \
     --key ~/private_keys/AuthKey_XXXXXXXX.p8 --key-id XXXXXXXX --issuer <issuer-uuid>
   ```

### Cutting a release

```bash
SIGNING_IDENTITY="Developer ID Application: You (TEAMID)" \
NOTARY_PROFILE=optimisarr-notary \
./scripts/release-app.sh 0.1.0
```

That builds, signs with the hardened runtime and a secure timestamp (signing the bundled `ffmpeg`
and `ffprobe` first, as notarisation requires), archives with `ditto`, submits to Apple, waits,
staples the ticket to the bundle, re-archives, and checks the result the way Gatekeeper will. It
then builds a disk image from the stapled app and notarises and staples that too. Attach both the
`.dmg` and the `.zip` to the GitHub Release.

The disk image is the one to point people at: it opens with the app beside a shortcut to
Applications, so it installs by dragging. Install [`dmgbuild`](https://pypi.org/project/dmgbuild/)
for that layout — `pip install dmgbuild`. Without it the image is still built and still works, but
with Finder's default arrangement. Scripting Finder to do the layout was tried and abandoned: it
times out under automation, so the result would be a coin toss.

The app's icon is generated from `Resources/AppIcon.png`, the same mark the web app uses. That
source is 192px, so sizes above 128 are upscaled; replace it with a larger master if one appears.

Stapling matters: without the ticket attached, anyone who downloads the app on a machine that
cannot reach Apple is told it "cannot be checked for malicious software".

CI can do the same on a `sidecar-v*` tag — see
[`.github/workflows/sidecar-release.yml`](../../.github/workflows/sidecar-release.yml), which needs
these repository secrets:

| Secret | What it is |
| --- | --- |
| `SIDECAR_CERTIFICATE_P12` | The Developer ID certificate and key, exported from Keychain Access as `.p12`, base64 encoded |
| `SIDECAR_CERTIFICATE_PASSWORD` | The password set on that export |
| `SIDECAR_NOTARY_KEY_P8` | The App Store Connect `.p8`, base64 encoded |
| `SIDECAR_NOTARY_KEY_ID` | Its Key ID |
| `SIDECAR_NOTARY_ISSUER` | The Issuer ID |

Base64 a file for pasting with `base64 -i <file> | pbcopy`.
