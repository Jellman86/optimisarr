# Optimisarr macOS sidecar

A menu-bar app that pairs a Mac with an Optimisarr server so it can contribute spare encoding
capacity.

## What this version does, and does not

**It does not transcode anything yet.** It pairs, stores its credential, and checks in. That is the
whole of version 0.1.0.

That is deliberate rather than unfinished. The server cannot dispatch work to a sidecar yet — job
leasing, media delivery, and the result path are all still to build — so an app that claimed to
transcode would be claiming something neither end can do. Pairing exists now so the connection can
be set up and tested while the rest is built, and so this contract has a second implementation
holding it honest.

The app reports **no encoders and no VMAF support**, because it has none to prove: no ffmpeg is
bundled with it yet. The probing that will report them is written and tested — it parses
`ffmpeg -encoders` and then confirms each VideoToolbox encoder with a real throwaway encode, because
every Apple build lists VideoToolbox whether or not a given machine can actually open it. With no
ffmpeg present it reports nothing, which is the honest answer. Optimisarr's capability matcher fails closed, so a worker advertising nothing is
never offered work. Honesty here is the safety mechanism — a sidecar that overstated itself would
have jobs scheduled onto it that could only fail.

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

21 tests covering the protocol client, the pairing and check-in lifecycle, and address handling.

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
