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
3. **Fetch and prove.** The source is downloaded by lease into the app's own scratch and hashed;
   a transfer that does not match the server's hash is never encoded.
4. **Encode, renewing.** The bundled ffmpeg runs the command against this Mac's paths. The lease
   is renewed throughout; losing it stops the encode rather than finishing work the server has
   already given to someone else.
5. **Deliver.** The candidate is hashed and uploaded with both hashes, so the server can bind it to
   this exact source. The server then verifies it against the original exactly as it would a local
   encode. Nothing is replaced from here, ever.

Scratch lives under `~/Library/Application Support/OptimisarrSidecar/work` and is removed on every
exit path. One job at a time. Quality evidence (VMAF) is not yet measured here; the server measures
it itself for now.

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

A machine that proves nothing reports nothing, and Optimisarr's capability matcher fails closed, so
such a worker is never offered work. Honesty here is the safety mechanism: a sidecar that overstated
itself would have jobs scheduled onto it that could only fail.

Optimisarr remains the only thing that replaces, quarantines, moves, or deletes a file. A sidecar
never can, by design, and nothing in this app is capable of touching media.

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
```

43 tests covering the protocol client, the pairing and check-in lifecycle, address handling,
and capability probing — including live probes against the bundled ffmpeg.

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

## Signing

`make-app.sh` applies an ad-hoc signature, which is enough to run locally. Distribution needs a real
Developer ID and notarisation, and neither is set up yet.
