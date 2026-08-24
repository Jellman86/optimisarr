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

The app reports **no encoders and no VMAF support**, because it has none to prove: nothing is
bundled with it. Optimisarr's capability matcher fails closed, so a worker advertising nothing is
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

macOS fills the menu bar right-to-left, and on a Mac with a notch the items that run out of space
end up behind it rather than being pushed off the edge. A newly launched app is last in the queue,
so it is the first to disappear. This was hit on the first real launch: the status item existed at
x=735 on a 1470-point menu bar — dead centre, behind the notch on a 13-inch Air — with nine other
status items to its right.

Check whether it is actually running before assuming it crashed:

```bash
pgrep -lf OptimisarrSidecar
```

If it is running but invisible, make room in the menu bar — quit a menu bar app, or hold ⌘ and drag
icons to reorder them. Menu bar items can be repositioned by ⌘-dragging, so once it is visible it
can be moved somewhere convenient.

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
