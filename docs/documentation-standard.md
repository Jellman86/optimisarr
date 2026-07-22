# Documentation standard

Optimisarr documentation is part of the safety model. It must help a Docker
media-stack user understand what the app will do before they let it touch a real
library.

## Audience

Write for a person running one self-hosted server with Docker Compose, mounted
media folders, and Plex/Jellyfin/Emby/Sonarr/Radarr-style tooling. Assume they
understand containers and file permissions, but do not assume they know
Optimisarr's vocabulary yet.

## Source of truth

Ground every claim in the current repository:

- Code and tests are the source of truth for behavior.
- Compose files are the source of truth for deployment examples.
- Svelte UI text and routes are the source of truth for screen names and control
  labels.
- Existing docs are context, not proof. Correct them when code has moved on.

Do not invent capabilities, settings, guarantees, support promises, or future
dates. Mark unverified assumptions explicitly or remove them.

## Information architecture

Use this structure:

| Kind | Purpose | Current examples |
|---|---|---|
| README | Project orientation, quick start, and links onward. | [`../README.md`](../README.md) |
| Tutorial | First successful run, safest path, expected results. | [`setup/getting-started.md`](setup/getting-started.md), [`usage/workflow.md`](usage/workflow.md) |
| How-to | Task-focused operational guidance. | [`setup/hardware-acceleration.md`](setup/hardware-acceleration.md), [`setup/reverse-proxy.md`](setup/reverse-proxy.md), [`operations/safe-replacement.md`](operations/safe-replacement.md), [`integrations/media-servers.md`](integrations/media-servers.md), [`troubleshooting/diagnostics.md`](troubleshooting/diagnostics.md) |
| Reference | Complete lookup information. | [`setup/configuration.md`](setup/configuration.md), [`api.md`](api.md), [`glossary.md`](glossary.md) |
| Explanation | Product direction, tradeoffs, architecture. | [`product-and-architecture.md`](product-and-architecture.md), [`roadmap.md`](roadmap.md) |

Keep detail out of the README when a docs page can carry it better. The README
should orient and link; the docs index should route; individual pages should
solve one user need.

## Page pattern

For task pages, prefer this order:

1. User outcome.
2. Prerequisites or safety warning.
3. Smallest working path.
4. Expected result.
5. Optional details and variants.
6. Troubleshooting or next link.

For procedures, use short numbered steps. Add a short "You should see" or
"If it fails" paragraph when the result is not obvious.

For reference pages, use consistent tables, valid enum values, request/response
examples, and links to related concepts.

## Safety requirements

Every page that touches replacement, automation, quarantine, purge, backup,
credentials, external access, or API automation must state the relevant safety
boundary:

- Originals are not replaced until verification passes.
- Replacement quarantines the original before moving the verified output into
  place.
- Approval and retention purge remove rollback ability.
- Quarantine is not a backup.
- Dry-run blocks replacement and purge, not scan/probe/transcode/verify.
- Configuration exports contain provider secrets.
- The UI/API are administrative surfaces and need an authenticated reverse
  proxy if exposed remotely.

Never imply a destructive action is reversible after purge.

## Screenshots

Screenshots must be current, specific, and useful:

- Capture from the current UI or a local build matching the documented behavior.
- Prefer focused section screenshots over full-page images when explaining one
  control or workflow.
- Use stable filenames under `docs/images/`.
- Include meaningful alt text that describes the UI information, not just the
  page name.
- Every screenshot-bearing page must state that screenshots use fabricated dummy
  media created for documentation and no copyrighted material is used.
- Do not use real user library data, provider tokens, hostnames with secrets, or
  copyrighted media artwork.

## Style

- Use plain English, active voice, and `you` for instructions.
- Use exact UI labels in bold, for example **Settings → General → Replacement and cleanup**.
- Use monospace for paths, commands, endpoints, environment variables, enum
  values, and file names.
- Prefer "Optimisarr does X" over vague passive phrasing.
- Keep paragraphs short.
- Explain acronyms the first time they matter.
- Avoid marketing language, release promises, and generic filler.
- Avoid "we"; use "Optimisarr" or "you".
- Use consistent terms from [`glossary.md`](glossary.md).

## Commands and examples

Commands must be copyable and match the repository:

- Use `docker compose`, not legacy `docker-compose`.
- Use real container paths: `/config`, `/data`, `/work`, `/trash`.
- Use current image names and compose examples.
- Prefer `curl -fsS` in API recipes when failure should stop a shell script.
- Redact secrets in examples.
- Include the expected response when it helps the user confirm success.

## API docs

The API reference must be separate from user workflow docs. It should include:

- Stability/authentication boundary.
- Common status codes.
- Common recipes for health, scan, candidates, enqueue, queue status,
  replacement, rollback, and approval.
- Endpoint groups with method, path, and purpose.
- Request bodies for writes.
- Response examples for important reads.
- Safety notes for automation.

Do not document an endpoint unless it exists in `src/Optimisarr.Api/Program.cs`
or a committed API module.

## Release notes

GitHub Release notes are a short, friendly guide to an update, not a second
changelog. Write for the person deciding whether to update a working server.

- Open with one or two sentences that explain who will notice the release and why.
- Group bullets by user outcome, not by code area. Prefer **What's new**,
  **Smoother and safer**, and **Before you update** over Backend/Frontend/Other.
- Start each bullet with a short bold benefit, then explain the practical change
  in one or two sentences.
- Use `you` and plain English. Explain an unavoidable technical term the first
  time it matters.
- State new defaults, opt-in behaviour, migration steps, downtime, and meaningful
  tradeoffs honestly. If no special action is needed, say so.
- Keep implementation detail in `CHANGELOG.md`; link it for readers who want the
  complete record.
- Omit empty sections. Never pad the notes with commit titles, issue numbers,
  generic praise, or promises the shipped code does not prove.

Use [`development/releasing.md`](development/releasing.md) for the release workflow
and [the copy-ready template](../.github/RELEASE_NOTES_TEMPLATE.md) for every
GitHub Release.

## Validation checklist

Before finishing a documentation change:

1. Read the changed page from top to bottom.
2. Check UI labels against `web/src`.
3. Check commands and paths against compose files and code.
4. Check safety claims against implementation.
5. Run `python3 scripts/check_docs.py`.
6. Run `git diff --check`.
7. If screenshots changed, visually inspect them.
8. Confirm the docs index links to any new user-facing page.

If the change documents new behavior, update `CHANGELOG.md` unless the change is
purely documentation-only.
