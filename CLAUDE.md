# Optimisarr — Engineering Standards

This file is the contract for everyone working in this repository, human or AI
agent. Read it before writing code. These standards are not aspirational; they
are the bar a change must clear before it is committed. When a request conflicts
with a standard here, surface the conflict instead of silently breaking the
standard.

For *what* the product is and *where* it is going, see
[`docs/product-and-architecture.md`](docs/product-and-architecture.md) and
[`docs/roadmap.md`](docs/roadmap.md). This file is about *how* we build it.

---

## 1. Safety first — the non-negotiable

Optimisarr's entire reason to exist is that it is safer than the alternatives.

- **No original file is ever deleted or overwritten until a verified replacement
  exists and every configured verification gate has passed.** This is the core
  promise. Any change that could break it must be rejected.
- Every destructive action (replace, delete, move-to-trash) must have a recorded
  rollback path before it runs, not after.
- Defaults are conservative. When in doubt, do nothing and report why.
- FFmpeg/ffprobe are invoked through explicit argument arrays
  (`ProcessStartInfo.ArgumentList`), **never** a shell string and never string
  interpolation of a path. Treat every file path as untrusted input.
- External processes always run with a `CancellationToken` and captured
  stdout/stderr.

If you are unsure whether a change is safe, it is not safe yet. Add a test that
proves the safe behaviour.

## 2. Test-driven development

We work test-first.

- Write a failing test that describes the behaviour, then write the code that
  makes it pass, then refactor. Commit only with the suite green.
- Pure logic (scanning, eligibility rules, ffprobe parsing, verification,
  replacement decisions) lives in `Optimisarr.Core` and must be unit tested
  **without** a database, filesystem mocking framework, or live FFmpeg. Keep
  these functions pure and pass inputs in (e.g. `nowUtc`, parsed JSON) so they
  are deterministic — see `MediaProbeService.Parse` and `LibraryScanner.Scan`.
- Tests own their fixtures: create temp directories, clean them up (`IDisposable`),
  and never depend on machine-specific paths or on FFmpeg being installed.
- A bug fix starts with a test that reproduces the bug.
- Test names state the behaviour: `Scan_skips_files_modified_within_the_settling_period`.

The full suite must pass before any commit. "It builds" is not "it works".

## 3. Database excellence

- **Migrations only. `EnsureCreated` is banned in committed code.** Schema is
  versioned through EF Core migrations under `src/Optimisarr.Data/Migrations`.
  Startup applies migrations with `Database.MigrateAsync()`.
- **Idempotency is required end to end.** Applying migrations to an up-to-date
  database is a no-op. Re-running a scan against an unchanged library produces
  zero new rows and zero updates (`LibraryInventoryService` matches on the unique
  `Path`). Any operation that can run more than once must be safe to run more
  than once.
- Every schema change ships with a migration in the same commit. Never edit an
  already-released migration; add a new one.
- Constraints belong in the schema: unique indexes, max lengths, required
  columns, enum-to-string conversions. Don't rely on application code to enforce
  what the database can enforce.
- SQLite lives under `/config` in the container (or `./config` locally). It is
  user data — migrations must be backwards-safe and never destructive without an
  explicit, documented reason.
- All EF calls are async and take a `CancellationToken`. Read-only queries use
  `AsNoTracking()`.

To add a migration (the env from §7 must be set so `dotnet`/`dotnet ef` resolve):

```bash
dotnet ef migrations add <Name> \
  --project src/Optimisarr.Data --startup-project src/Optimisarr.Api \
  --output-dir Migrations
```

## 4. Self-documenting code

- Names carry the meaning. A reader should understand intent without comments.
- Comments explain **why**, not **what**. The only *what* worth a comment is a
  non-obvious one (e.g. "clear stale probe results because the file changed").
- Keep functions small and single-purpose. Domain logic in `Core`, persistence
  in `Data`, composition/HTTP in `Api`. `Core` does not depend on EF.
- Prefer immutable records for results and DTOs. Use the type system to make
  invalid states unrepresentable.
- Match the style of the surrounding code: file-scoped namespaces, primary
  constructors for services, `sealed` by default, nullable reference types on.
- No dead code, no commented-out blocks, no `TODO` without a linked issue.

## 5. Clean, simple, self-explanatory UI

- The UI is **operational, not marketing**. Dense, calm, and honest about state.
- A first-time user should understand what a screen does and what will happen
  when they click, without a manual. Label actions by their effect ("Scan",
  "Probe", "Re-probe"), and show *why* something is disabled or skipped.
- Never imply a destructive action is reversible when it isn't, or irreversible
  when it isn't. The UI must reflect the safety model truthfully.
- Svelte 5 runes (`$state`, `$effect`, `$derived`) and modern event syntax
  (`onclick={...}`, not `on:click`). TypeScript everywhere; `npm run check` is
  clean (zero errors, zero warnings) before commit.
- Show loading, empty, and error states explicitly. Empty states tell the user
  what to do next.
- **Media artwork is a recognition aid, not decoration.** Posters/backdrops (from
  Radarr/Sonarr first, then a connected media server, proxied so no token reaches
  the browser) may be used to help a user recognise a title at a glance. They must
  degrade silently to a plain placeholder, never imply state, and never shift
  layout (fixed-aspect, lazy-loaded). This stays within "operational, not
  marketing": artwork orients the user, it does not sell.

## 6. Definition of done

A change is done when **all** of these hold:

1. `dotnet build` succeeds with **zero warnings**.
2. `dotnet test` is fully green, and new behaviour has new tests.
3. `npm run check` is clean if the frontend changed.
4. Schema changes have a migration and re-running them is a no-op.
5. The safety model is intact (or strengthened) — never weakened.
6. `CHANGELOG.md` (Unreleased section) records the change.
7. Code is self-documenting and matches surrounding style.

## 7. Commands & local environment

This WSL environment has the .NET 10 SDK installed under `~/.dotnet` (a
persistent location — `/tmp` is wiped on the nightly reboot) and the `dotnet-ef`
tool under `~/.dotnet/tools`. `~/.bashrc` exports the variables below, so an
interactive shell already has `dotnet` on its PATH. For non-interactive shells,
export them first:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
```

If the SDK is ever missing (e.g. a clean machine), reinstall it to the same
persistent path:

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- \
  --version 10.0.300 --install-dir "$HOME/.dotnet"
```

Then:

```bash
dotnet build Optimisarr.slnx          # build everything
dotnet test  Optimisarr.slnx          # run the suite
cd web && npm run check               # frontend type/lint check
cd web && npm run build               # emits static assets into Optimisarr.Api/wwwroot
```

## 8. Project layout

```
src/Optimisarr.Api    ASP.NET Core host: minimal APIs, SignalR, DI, endpoints. Composition layer.
src/Optimisarr.Core   Pure domain logic: scanning, probing, rules, verification. No EF. Heavily unit tested.
src/Optimisarr.Data   EF Core: DbContext, entities, migrations. References Core.
web                   Svelte 5 + TypeScript SPA, built to Api/wwwroot.
tests/Optimisarr.Tests xUnit. References Core and Api.
docs                  Product, architecture, and roadmap.
.github/workflows     CI: build/test gates and GHCR image publishing (see §9).
Dockerfile            Multi-stage build (Node UI -> .NET publish -> aspnet runtime + ffmpeg).
```

Dependency direction is strict: `Api → Data → Core`, and `Api → Core`. Nothing
flows back toward `Core`.

## 9. Continuous integration & images

CI is defined in [`.github/workflows/ci.yml`](.github/workflows/ci.yml) and is
the enforcement point for the Definition of Done. Three jobs run on every push
to `dev`/`main`, every tag `v*`, and every pull request targeting `dev`/`main`:

- **backend** — `dotnet restore` → `dotnet build … -warnaserror` → `dotnet test`.
  The `-warnaserror` flag makes the §6 zero-warnings rule a hard gate; a warning
  fails the build. Do not silence it.
- **frontend** — `npm ci` → `npm run check`. Must be clean.
- **docker** — builds the image (after backend + frontend pass) and **publishes
  to GHCR** as `ghcr.io/jellman86/optimisarr`.

### Image tags (publishing rules)

Images are pushed only on `push` and tag events — **never on pull requests**
(PRs build the image to catch Dockerfile breakage but have no registry
credentials, especially from forks). Tagging is automatic via
`docker/metadata-action`:

- push to `dev` → `:dev`
- push to `main` (default branch) → `:main` **and** `:latest`
- tag `vX.Y.Z` → `:X.Y.Z` and `:X.Y`

Publishing uses the built-in `GITHUB_TOKEN` with `packages: write`; no personal
access token or manually managed secret is required. The first published image
may create a private GHCR package — make it public (or grant pull access) from
the repo's package settings if anonymous `docker pull` is expected.

### Rules for changing CI

- Keep CI and local commands in lock-step with §7. If you change how the app is
  built or tested locally, update the workflow in the same commit.
- Anything in the Definition of Done that *can* be machine-checked *should* be a
  CI gate. CI is allowed to be stricter than a local run, never looser.
- The `Dockerfile` is part of the build contract: it must reference real files
  (e.g. `Optimisarr.slnx`, not a stale `.sln`) and stay in sync with the project
  layout. A green `docker` job is required before merge.

## 10. Workflow

- **Develop and commit directly on `dev`.** Do not create feature branches for this
  repo — make changes, commit, and push on `dev`. (Release tags/`main` are handled
  separately; everyday work lives on `dev`.)
- **No sub-agents.** Do the work inline. Do not spawn sub-agents (e.g. the Agent/Task
  "Explore"/"Plan"/general-purpose agents) to carry out tasks in this repo.
