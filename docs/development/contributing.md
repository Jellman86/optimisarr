# Development

Optimisarr contains ASP.NET Core API, Core domain logic, EF Core/SQLite data,
and a Svelte frontend. Run the standard checks from the repository root:

```bash
dotnet build Optimisarr.slnx --configuration Release -warnaserror
dotnet test Optimisarr.slnx --configuration Release --no-build
python3 -m unittest discover -s scripts/tests -p 'test_*.py'
python3 scripts/check_release_metadata.py
python3 scripts/check_docs.py
python3 scripts/check_openapi.py --configuration Release --no-build
npm --prefix web run check
npm --prefix web run test:e2e
npm --prefix web run build
```

Read [`CLAUDE.md`](../../CLAUDE.md) and [`AGENTS.md`](../../AGENTS.md) before
editing. Keep safety decisions pure and tested, use migrations for schema changes,
and never weaken replacement verification to make a job complete.

Install the frontend's locked dependencies with `npm --prefix web ci` and
Playwright's Chromium browser before running its suite. `npm --prefix web run dev`
starts the interactive development server; it is not a substitute for the
browser regression suite.

For visual layout regressions, run the [UI layout audit](ui-layout-audit.md).
It checks every route at desktop, tablet, phone and enlarged text sizes, then
inspects card width, clipping, modal fit, translations and interaction states.

Native sidecars have separate platform checks. On macOS, run `swift test` and
`swift build --configuration release` from `sidecars/macos`. On Windows, build
`sidecars/windows/Optimisarr.Sidecar.slnx` in Release with `-warnaserror`, run
`sidecars/windows/tests/Optimisarr.Sidecar.Core.Tests`, and run the tray's
`--verify-popover` check. CI also renders the Windows native monitor states and
retains those screenshots. Installer changes need the clean-host MSI test in
[the installer guide](../../sidecars/windows/installer/README.md).

The backend tests also run on native Windows to check portable rules, media
verification, protocols, migrations and file safety. This does not add support
for deploying the server directly on Windows. Its supported deployment remains
the Linux container.

Eight backend tests use **PosixFact**: the Linux mount-path fixture, four native
hard-link probe tests and three inventory link-count tests. They run on Linux
and macOS and show an explicit skip reason on Windows. Unknown hard-link counts
still block unsafe replacement. Other path fixtures use the host's separators,
API tests read decoded JSON values, and queue-pause tests assert Windows'
existing `dispatchOnly` behavior. The full Linux suite keeps all POSIX checks.

The Windows worker suite has one privileged integration test that creates a temporary
Event Log source. It runs in an administrator PowerShell window; ordinary Windows runs
show an explicit skip for that test. Windows CI requires an administrator token before
running the suite, so the native write check runs there. It removes only its own generated
source and does not change the production worker, pairing or Event Log permissions.

Unit tests do not certify live encoders or the packaged application. Run the
[real-media acceptance harness](media-acceptance.md) for container and physical
worker evidence, and follow [the release guide](releasing.md) when publishing
downloadable artifacts.
