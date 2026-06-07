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

To add a migration (note the local SDK lives in `/tmp/dotnet`, so `DOTNET_ROOT`
must be set — see §7):

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

This WSL environment has the .NET 10 SDK under `/tmp/dotnet` (not on the global
PATH) and the `dotnet-ef` tool under `~/.dotnet/tools`. EF tooling needs
`DOTNET_ROOT`. Export these for any backend command:

```bash
export PATH="/tmp/dotnet:$PATH:$HOME/.dotnet/tools"
export DOTNET_ROOT=/tmp/dotnet
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
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
```

Dependency direction is strict: `Api → Data → Core`, and `Api → Core`. Nothing
flows back toward `Core`.
