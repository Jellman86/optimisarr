# Engineering excellence review — 2026-06-22

## Scope

This review covers Optimisarr `dev` at `f85d1d9`, excluding unrelated
uncommitted worktree changes. It compares the repository with [OWASP
ASVS](https://owasp.org/www-project-application-security-verification-standard/),
[OpenSSF Best Practices](https://bestpractices.coreinfrastructure.org/criteria),
[ASP.NET Core production guidance](https://learn.microsoft.com/aspnet/core/host-and-deploy/),
and [GitHub Actions hardening guidance](https://docs.github.com/actions/security-for-github-actions/security-guides/security-hardening-your-deployments).

This is a source/repository review, not a penetration test or live operational audit.

## Executive assessment

Optimisarr has a strong safety core for an early self-hosted project. Original
files are verified before replacement, replacements have quarantine/rollback
records, external tools use argument arrays instead of shell strings, migrations
are idempotent, and CI treats backend warnings as errors. The largest maturity
gap is release engineering around that core: exposure, recovery, supply chain,
end-to-end proof, and operations.

## Existing strengths

| Area | Evidence | Assessment |
|---|---|---|
| Original-file safety | Verification, quarantine, rollback, and conservative standards in `CLAUDE.md` | Strong. |
| Process safety | `ProcessStartInfo.ArgumentList` for FFmpeg/ffprobe/exiftool | Strong injection resistance. |
| Daemon design | Hosted workers, cancellation, restart recovery, SQLite migrations | Sound foundation. |
| Testability | Pure `Core` logic and 61 focused test files | Strong unit-test culture. |
| CI quality gates | Release `-warnaserror`, test suite, frontend check, Docker build | Good baseline. |
| Documentation | README plus task-based setup, safety, GPU, integration, and diagnostics docs | Good foundation. |

## Findings

### P0 — explicitly secure or restrict API exposure

`Program.cs` exposes mutable library, queue, replacement, configuration, and
integration endpoints without application authentication/authorisation. The
Compose example publishes port 8787. Public or semi-trusted-network exposure
would allow unauthorised scheduling and destructive workflow control.

**Required direction:** either add owner authentication/authorisation and CSRF
controls, or bind loopback by default and document an authenticated reverse
proxy as the only supported remote access path. Add tests proving unauthenticated
destructive requests are rejected.

### P0 — make state recovery operationally complete

`/config/optimisarr.db` holds queue, replacement, and rollback state, but there
is no complete backup/restore runbook or tested active-database backup flow.
Secret-free config export is correct but is not recovery.

**Required direction:** provide an atomic database backup/export and tested
restore procedure; document backup scope for `/config` and quarantine; state
that secrets must be re-entered. Test recovery into a fresh instance.

### P1 — harden software supply chain

CI uses mutable action tags (`@v4`/`@v5`/`@v6`) and has no Dependabot/Renovate,
CodeQL, dependency review, SBOM, image vulnerability scan, or provenance
attestation.

**Recommendation:** pin Actions to SHAs; enable Dependabot for NuGet, npm,
Docker, and Actions; add CodeQL and dependency review; generate an SBOM and scan
release images before GHCR publishing. Preserve the existing job-scoped
`packages: write` permission.

### P1 — prove the destructive workflow end to end

The unit suite is broad, but CI has no production-image integration test for
scan → probe → queue → verify → replace → rollback.

**Recommendation:** add a hermetic Docker test with temporary config/data/work/
trash mounts and a small media fixture. Prove failed verification leaves the
original untouched, successful replacement creates rollback state, rollback
restores the original, and restart/cross-filesystem behaviour is safe.

### P1 — distinguish liveness from readiness

`/api/health` returns process metadata but does not indicate migration completion,
tool availability, critical path access, or worker readiness. The Dockerfile has
no `HEALTHCHECK`.

**Recommendation:** add liveness and readiness endpoints, a Docker health check,
and deployment documentation for readiness failures. Readiness should cover
database migration, FFmpeg/ffprobe, and required writable paths.

### P1 — improve observability

There is no documented structured-log contract, metrics endpoint, alerting
guidance, or retention policy. Queue, verification, and replacement incidents
will otherwise depend on manual log inspection.

**Recommendation:** emit structured job/library/replacement identifiers (never
tokens or full sensitive paths), publish queue/failure/verification/disk-pause
metrics, and document minimal alerts and log retention.

### P2 — formalise API contracts

OpenAPI is enabled only in Development while the SPA maintains API types by hand.

**Recommendation:** publish a build-time OpenAPI artifact, generate or validate
TypeScript types, and add endpoint contract tests. Version external API changes,
or clearly designate the API internal.

### P2 — add public repository governance

The project has excellent internal standards in `CLAUDE.md`, but no visible
licence, `SECURITY.md`, contribution guide, release policy, or issue/PR templates.

**Recommendation:** add `LICENSE`, `SECURITY.md`, `CONTRIBUTING.md`, issue
templates, supported-version policy, and a private vulnerability-reporting path.

### P2 — automate documentation checks

The documentation suite is useful but CI does not check Markdown links, Compose
syntax, command snippets, or key documentation/code consistency.

**Recommendation:** add a docs-quality job with link checking, `docker compose
config`, and scripted assertions for ports, mounts, environment variables, and
documented endpoints.

## Recommended 30-day plan

1. Decide the exposure model; add security policy, backup/restore runbook,
   readiness endpoint, and container health check.
2. Add required Docker integration coverage for replacement, rollback, and
   restart recovery.
3. Add action pinning, dependency automation, CodeQL, SBOM, and image scanning.
4. Add logs/metrics, API-contract validation, docs-quality automation, and a
   release checklist.

## Future-change checklist

- Is an original preserved until verification and a rollback record exist?
- Does cross-process/filesystem behaviour have integration coverage?
- Are migrations additive, idempotent, and tested against existing state?
- Is new configuration validated, safely exported, and documented?
- Is remote API access authenticated or deliberately limited to trusted local use?
- Are CI dependencies pinned and release images scanned/traceable?
- Do README, operational docs, and changelog describe the shipped behaviour?

## Conclusion

The media-safety design is credible. The next maturity step is an auditable
operating system around that design: secure exposure, recoverable state,
end-to-end proof, supply-chain controls, and production observability.
