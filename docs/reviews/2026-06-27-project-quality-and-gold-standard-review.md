# Optimisarr project quality and gold-standard review

Date: 2026-06-27  
Reviewer: Codex  
Scope: Repository-level engineering review based on local source, tests, docs,
and live UI documentation work. This is not a formal security audit or
performance benchmark.

## Executive summary

Optimisarr is a serious, unusually safety-conscious early-stage self-hosted
media application. It is not just a web UI wrapped around FFmpeg. The strongest
part of the project is that the safety model is explicit in the engineering
standards and visible in the implementation: originals are not replaced until
verification passes, replacement preserves a rollback path, and destructive
actions are treated as first-class domain events.

The project is already above the normal quality bar for homelab media tooling.
The remaining gap to "gold standard" is less about correctness of the core idea
and more about product hardening:

- Modularize the API composition layer.
- Add a generated, CI-checked OpenAPI contract.
- Add optional built-in authentication for direct deployments.
- Continue real-hardware validation, especially AMD VA-API.
- Split dense engineering roadmap content into clearer user-facing roadmap and
  engineering notes.
- Keep turning operational docs into testable, screenshot-backed workflows.

## Review basis

Local project references:

- Engineering contract: [`CLAUDE.md`](../../CLAUDE.md)
- API composition: [`src/Optimisarr.Api/Program.cs`](../../src/Optimisarr.Api/Program.cs)
- Verification core: [`src/Optimisarr.Core/Verification/VerificationEvaluator.cs`](../../src/Optimisarr.Core/Verification/VerificationEvaluator.cs)
- Verification tests: [`tests/Optimisarr.Tests/VerificationEvaluatorTests.cs`](../../tests/Optimisarr.Tests/VerificationEvaluatorTests.cs)
- Documentation standard: [`../documentation-standard.md`](../documentation-standard.md)
- API reference: [`../api.md`](../api.md)
- User workflow: [`../usage/workflow.md`](../usage/workflow.md)

External standards and reference points:

- Diátaxis documentation framework: https://diataxis.fr/
- Google developer documentation style guide: https://developers.google.com/style/highlights
- Microsoft procedure guidance: https://learn.microsoft.com/en-us/style-guide/procedures-instructions/
- OpenAPI Specification: https://spec.openapis.org/oas/latest.html
- Microsoft REST API design best practices: https://learn.microsoft.com/en-us/azure/architecture/best-practices/api-design
- OWASP Application Security Verification Standard: https://owasp.org/www-project-application-security-verification-standard/
- OWASP API Security Project: https://owasp.org/www-project-api-security/
- Twelve-Factor App methodology: https://12factor.net/

## Current strengths

### 1. Safety model is explicit and product-defining

The engineering standards open with the right non-negotiable:

- No original file is deleted or overwritten until a verified replacement exists.
- Every destructive action needs a recorded rollback path before it runs.
- FFmpeg/ffprobe are invoked through explicit argument arrays, not shell strings.
- External processes require cancellation and captured output.

That is the correct foundation for a media-library optimizer. A product in this
space can be forgiven for missing a convenience feature; it cannot be forgiven
for corrupting or deleting originals.

Gold-standard target: keep this as the first architectural invariant. Any
feature that weakens it should be rejected or redesigned.

### 2. Pure core logic is a strong architectural choice

The project separates core media decisions from API and persistence better than
many early applications. Verification is a good example:

```csharp
public static VerificationReport Evaluate(VerificationInput input, VerificationPolicy policy)
```

`VerificationEvaluator` is pure: no database, no filesystem, no FFmpeg process.
It turns already-gathered evidence into a deterministic report. That makes
behavior testable and reviewable.

This pattern should remain the standard for:

- eligibility decisions,
- replacement planning,
- queue scheduling,
- failure classification,
- verification gates,
- config validation.

Gold-standard target: any business rule that can be pure should live in
`Optimisarr.Core` and have direct unit coverage.

### 3. Defensive verification is unusually strong

The verification pipeline is not a single "FFmpeg exited zero" check. It covers
decode health, output readability, media-kind-specific gates, size reduction,
duration, stream retention, HDR/colour metadata, A/V sync, timestamp monotonicity,
tail integrity, VMAF, loudness, true peak, image SSIM, and image metadata where
applicable.

The fail-closed posture is the right one. Examples:

- enabled loudness gates fail if loudness cannot be measured;
- enabled true-peak gates fail if true peak cannot be measured;
- enabled image metadata gates fail if metadata cannot be measured;
- video-only gates do not incorrectly penalize audio or image outputs.

Gold-standard target: keep a clear difference between:

- always-on safety checks,
- optional quality gates,
- informational metrics.

Users should be able to tell whether a failure means "unsafe output", "policy
not met", or "measurement unavailable".

### 4. Test coverage appears broad and aligned with risk

The test suite is extensive, especially around pure logic. File names indicate
coverage for parser behavior, queue behavior, candidate rules, replacement
planning, path handling, migration smoke, verification, hardware selection, and
failure classification.

The testing approach matches the standards document: pure logic gets direct unit
tests, while live FFmpeg and machine-specific hardware are kept out of ordinary
unit tests where possible.

Gold-standard target: add more end-to-end smoke tests around API contracts and
config import/export, not just pure logic. Keep hardware tests separate and
clearly labeled.

### 5. Operational UI philosophy is sound

The UI is dense, operational, and state-first. It does not look like marketing
software. The screens map cleanly to user jobs:

- Dashboard: health and outcomes.
- Libraries: configure rules and automation.
- Inventory: inspect files and candidate reasons.
- Queue: operate jobs.
- Quarantine: review rollback/approval state.
- Settings: global policy, integrations, tools, backup.

Gold-standard target: make "why is this disabled/skipped/waiting?" answerable
directly in the UI everywhere. The Queue waiting reason and Candidates reasons
are already good examples.

### 6. Documentation has been brought into a good shape

The docs now follow a recognizable structure:

- README as orientation.
- Getting started and workflow as tutorials.
- Setup/operations/integrations/troubleshooting as how-to pages.
- API and glossary as references.
- Roadmap/product architecture as explanation.

This follows Diátaxis: tutorials, how-to guides, reference, and explanation are
separate user needs. The new documentation standard codifies this so agents and
contributors can preserve it.

Gold-standard target: keep documentation updates in the definition of done for
behavior, UI, API, and deployment changes.

## Main gaps to gold standard

### 1. `Program.cs` is doing too much

`src/Optimisarr.Api/Program.cs` currently owns:

- service registration,
- startup migration,
- static asset caching policy,
- health/readiness,
- settings validation,
- integration endpoints,
- library endpoints,
- inventory endpoints,
- queue endpoints,
- replacement endpoints,
- stats endpoints,
- SignalR mapping.

This is a pragmatic minimal-API start, but it is now too dense. It raises the
cost of review because unrelated endpoint groups sit in one file.

Gold-standard target:

```text
src/Optimisarr.Api/
  Program.cs
  Endpoints/
    HealthEndpoints.cs
    SettingsEndpoints.cs
    LibraryEndpoints.cs
    InventoryEndpoints.cs
    QueueEndpoints.cs
    ReplacementEndpoints.cs
    IntegrationEndpoints.cs
    StatsEndpoints.cs
```

Example shape:

```csharp
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");

        group.MapGet("/", GetSettings).WithName("GetSettings");
        group.MapPut("/", UpdateSettings).WithName("UpdateSettings");
        group.MapGet("/export", ExportSettings).WithName("ExportSettings");
        group.MapPost("/import", ImportSettings).WithName("ImportSettings");

        return app;
    }
}
```

Why this matters:

- endpoint review becomes scoped;
- OpenAPI tags become natural;
- validation helpers can be shared without bloating `Program.cs`;
- future auth/policy can be applied per group;
- agents are less likely to accidentally affect unrelated behavior.

### 2. API contract is documented manually, not enforced

The API docs are now useful, but they can drift. The project already registers
OpenAPI services in development, which is a good start, but a gold-standard API
needs a contract that can be generated, versioned, and checked.

Gold-standard target:

- generate OpenAPI in CI;
- commit or publish the OpenAPI artifact;
- lint it;
- fail CI when endpoint metadata is incomplete for public/internal-documented
  endpoints;
- use the spec to drive docs examples or at least compare paths/methods.

Example acceptance criteria:

```bash
dotnet run --project src/Optimisarr.Api -- --dump-openapi ./artifacts/openapi.json
npx @redocly/cli lint ./artifacts/openapi.json
```

The specific tooling can vary. The important point is that the API reference is
not hand-maintained alone.

### 3. Optional built-in auth would materially improve deploy safety

Optimisarr is intended for trusted networks and authenticated reverse proxies.
That is acceptable for early homelab software, but the UI/API expose admin
operations:

- replacement,
- rollback,
- approval/purge,
- config export with secrets,
- provider credentials,
- queue control.

Gold-standard target: add optional app-level authentication that is simple and
hard to misconfigure.

Recommended first step:

- environment variable: `OPTIMISARR_ADMIN_TOKEN`;
- if set, require `Authorization: Bearer <token>` for all `/api/*` and app
  routes except `/api/health` and maybe `/api/ready`;
- show a clear startup warning when no token is configured and the app is bound
  beyond loopback;
- document reverse-proxy auth as still recommended.

Example policy shape:

```csharp
var adminToken = Environment.GetEnvironmentVariable("OPTIMISARR_ADMIN_TOKEN");
if (!string.IsNullOrWhiteSpace(adminToken))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path == "/api/health")
        {
            await next();
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization != $"Bearer {adminToken}")
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next();
    });
}
```

That is not the final polished implementation, but it shows the level of
complexity needed to remove the biggest deployment foot-gun.

### 4. API authorization and object access assumptions should be explicit

OWASP API Security calls out broken object-level authorization, broken
authentication, resource consumption, and function-level authorization as common
API risks. Optimisarr currently has no user model, so "authorization" is mostly
deployment boundary. That should be explicit in code, docs, and tests.

Gold-standard target:

- every destructive endpoint has a named policy, even if the initial policy is
  "admin token required";
- every object lookup returns `404` for missing records and refuses invalid
  state transitions cleanly;
- every unbounded list endpoint has pagination or documented limits;
- expensive operations have resource checks or rate/queue controls.

Examples:

- `GET /api/jobs` should eventually support pagination.
- `GET /api/media` should eventually support pagination/filtering for very large
  libraries.
- `POST /api/libraries/{id}/enqueue` should remain idempotent and report counts.
- `POST /api/jobs/{id}/replace` should remain state-gated and dry-run-aware.

### 5. Roadmap is too dense for outside users

The roadmap is valuable as an engineering log. It is also very long and
implementation-heavy. That makes it less useful for someone asking "what is
coming next?"

Gold-standard target:

```text
docs/roadmap.md
  concise user-facing now/next/later

docs/engineering/
  2026-06-gpu-validation-notes.md
  2026-06-verification-hardening.md
  2026-06-diagnostics-observability.md
```

Keep the implementation details, but move them out of the main roadmap.

### 6. Real-hardware validation should become a tracked matrix

The project has meaningful hardware support: CPU, NVIDIA NVENC, Intel QSV,
VA-API, hardware decode, and GPU metrics. Some validation is complete, some is
pending.

Gold-standard target: maintain a hardware validation matrix.

Example:

| Platform | Encode | Decode | Metrics | Last validated | Evidence |
|---|---|---|---|---|---|
| CPU/libx264 | Pass | n/a | CPU | 2026-06 | CI smoke |
| NVIDIA NVENC RTX 4070 | Pass | Pending CUDA decode | `nvidia-smi` | 2026-06 | manual run |
| Intel QSV N100 | Pass | Pass | DRM fdinfo | 2026-06 | manual run |
| AMD VA-API | Implemented | Implemented | DRM fdinfo/sysfs | Pending | unit tests only |

This would make the state of hardware support honest and actionable.

## Gold-standard implementation plan

### Phase 1: Stabilize architecture seams

Goal: reduce cognitive load and make future changes safer.

Tasks:

1. Split minimal API endpoint groups out of `Program.cs`.
2. Move request validation into focused parsers/validators where endpoint logic
   is currently long.
3. Add endpoint-level tags and names consistently.
4. Keep `Program.cs` responsible for composition only: services, middleware,
   static files, migrations, and endpoint group mapping.

Acceptance criteria:

- `Program.cs` is short enough to review in one screenful per concern.
- Each endpoint group has focused tests for important validation and state
  transitions.
- Existing API behavior remains compatible.

### Phase 2: Make the API contract machine-checkable

Goal: prevent docs/API drift and make automation safer.

Tasks:

1. Generate OpenAPI from the running app.
2. Add schemas for key request/response DTOs.
3. Add descriptions for destructive endpoints.
4. Add CI validation for OpenAPI generation.
5. Link `docs/api.md` to the generated artifact.

Acceptance criteria:

- CI fails if OpenAPI generation fails.
- Publicly documented paths in `docs/api.md` exist in the generated spec.
- Destructive endpoints are labeled clearly in the spec.

### Phase 3: Add optional built-in auth

Goal: reduce the risk of accidental unauthenticated exposure.

Tasks:

1. Add `OPTIMISARR_ADMIN_TOKEN`.
2. Require the token when configured.
3. Exempt only liveness/readiness endpoints if needed.
4. Add docs for reverse proxy and bearer-token usage.
5. Add tests for protected endpoints.

Acceptance criteria:

- Existing trusted-network deployments keep working when no token is set.
- Token-enabled deployments reject unauthenticated API and UI access.
- Config export and destructive endpoints are covered by auth tests.

### Phase 4: Improve large-library scalability

Goal: keep the app responsive with very large libraries.

Tasks:

1. Add pagination to media and jobs endpoints.
2. Add server-side filters for library, status, failure category, eligibility,
   and date where relevant.
3. Add indexes for common filters.
4. Add UI pagination/virtualization where tables grow large.

Acceptance criteria:

- Inventory and Queue remain responsive with tens of thousands of rows.
- API callers can inspect failures without fetching the world.
- Query plans are covered by tests or migration/index review.

### Phase 5: Mature observability

Goal: make failures diagnosable without SSH.

Tasks:

1. Continue failure classification work.
2. Add structured event records for major lifecycle transitions.
3. Add downloadable diagnostics bundle with config summary, versions, tools,
   selected logs, and redacted secrets.
4. Add health details endpoint for authenticated admins.

Acceptance criteria:

- A support issue can be filed from UI/API evidence alone.
- Diagnostics redact provider tokens and filesystem secrets.
- Failed-job root causes are grouped and searchable.

### Phase 6: Keep docs as a first-class artifact

Goal: make documentation stay correct as the app changes.

Tasks:

1. Keep `docs/documentation-standard.md` as the contributor standard.
2. Keep screenshots current and focused.
3. Add API spec links once generated.
4. Split roadmap into user roadmap and engineering notes.
5. Add a release checklist item for screenshot/API/doc review.

Acceptance criteria:

- `python3 scripts/check_docs.py` passes in CI.
- New settings/API/UI changes update docs in the same PR/commit.
- User docs remain task-first and do not become a feature dump.

## Examples of gold-standard patterns to preserve

### Pure decision logic

Good:

```csharp
public static VerificationReport Evaluate(VerificationInput input, VerificationPolicy policy)
```

This is testable, deterministic, and safe to reason about.

Avoid:

```csharp
public async Task<bool> VerifyAndReplaceAsync(string path)
```

That would mix media inspection, policy, filesystem mutation, and replacement in
one operation.

### Explicit state transitions

Good:

```text
Queued -> Probing -> Transcoding -> Verifying -> ReadyToReplace -> Completed
```

This makes UI, API, and recovery behavior inspectable.

Avoid hidden flags that imply state indirectly.

### Fail-closed optional gates

Good:

```text
VMAF gate enabled + VMAF unavailable = failed verification
```

Avoid:

```text
VMAF gate enabled + VMAF unavailable = assume pass
```

If the user asked for a gate, inability to measure it must block replacement.

### Documentation as safety surface

Good:

```text
Dry-run blocks replacement and purge. It does not block scanning, probing,
preview, transcoding, or verification.
```

Avoid vague wording like:

```text
Dry-run makes everything safe.
```

The exact boundary matters.

## Gold-standard scorecard

| Area | Current state | Target |
|---|---|---|
| Safety model | Strong | Preserve as non-negotiable invariant |
| Core domain design | Strong | Keep pure and heavily tested |
| Verification | Strong | Keep expanding evidence and failure clarity |
| API structure | Functional but too centralized | Endpoint modules + generated OpenAPI |
| Security boundary | Acceptable for trusted LAN | Optional built-in auth + OWASP-informed endpoint review |
| Testing | Broad unit coverage | Add API contract and larger workflow smoke tests |
| UI | Operational and honest | Continue improving empty/error/waiting explanations |
| Hardware support | Strong but uneven validation | Public validation matrix |
| Docs | Good after recent pass | Keep standard enforced in CI and release process |
| Roadmap | Honest but too dense | Split user roadmap from engineering notes |

## Suggested agent prompt

Use this when pointing an agent at the project:

```text
Read docs/reviews/2026-06-27-project-quality-and-gold-standard-review.md,
CLAUDE.md, docs/documentation-standard.md, and AGENTS.md before changing code.

Improve Optimisarr toward gold-standard quality without weakening the safety
model. Keep domain rules pure and tested, preserve verified replacement +
quarantine semantics, avoid unrelated refactors, and update documentation when
behavior, settings, API, or UI labels change.

Prioritize endpoint modularization, generated OpenAPI, optional admin-token auth,
large-library pagination, hardware validation matrix, and clearer roadmap
structure. Before finishing, run the relevant backend tests, frontend checks if
UI changed, python3 scripts/check_docs.py for docs changes, and git diff --check.
```

## Bottom line

Optimisarr is already a high-quality foundation. It has the right instincts:
safety first, pure core logic, conservative defaults, visible verification, and
rollback-aware replacement. The work needed to reach gold standard is not a
rewrite. It is hardening:

- make the API surface more modular and machine-described;
- add a simple built-in auth option;
- scale list endpoints and UI tables;
- formalize hardware validation;
- keep docs and screenshots current;
- split dense engineering history into maintainable notes.

If those are done without compromising the safety model, Optimisarr will be in
the top tier of self-hosted media operations tools.
