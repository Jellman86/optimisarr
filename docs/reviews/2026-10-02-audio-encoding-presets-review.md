# Audio encoding presets review, 2 October 2026

## Delivered behaviour

Audio encoding offers codec-specific bitrate starting points for AAC, Opus and MP3, including
explicitly re-encoded video soundtracks. Default follows the server's profile value; Custom
opens Advanced audio. Existing saved values remain unchanged until edited. A tier chosen during
an edit follows codec changes during that edit. The saved policy remains codec and bitrate.

The experimental audio difference limit is in Advanced verification, with a direct link from
Verify. Encoding presets neither enable nor change that gate. Sample search, soundtrack
perceptual assessment and surround assessment remain planned. Originals still require every
configured check before replacement, with the existing quarantine rollback path.

## Independent reviews

The operator requested Astra and Opus 5.5 after each slice. Astra used a fresh review agent;
Opus used the signed-in Claude CLI with the explicit `claude-opus-5-5` model. Its result metadata
confirmed that model. Both received the same neutral brief and code snapshot, with test logs.
Neither received an author verdict or suggested findings. Reviews were read-only.

The shared brief was:

> Review this Optimisarr change independently. Read AGENTS.md, CLAUDE.md and
> docs/documentation-standard.md first. Inspect the patch and affected and related source files
> as needed. Assess correctness, integration with existing settings and encoding behaviour,
> safety, regressions, accessibility, clarity, maintainability and test coverage. Report
> actionable findings with severity, exact file and line, a reproduction or concrete example,
> the consequence and a suggested correction. Distinguish confirmed defects from unverified
> concerns. Give your independent opinion on readiness and any further tests needed. Do not
> edit files, make network writes, deploy, or operate production hosts. Independently check
> any supplied evidence you rely on. Return the review as Markdown.

### Findings and corrections

| Finding | Reviewers | Correction and regression coverage |
|---|---|---|
| Standalone Default showed 128 kbps while Scott's profile resolves to 96 | Astra, Opus | API exposes standalone profile defaults; UI reads them. Real API tests cover every profile, and Music/Other browser tests cover Scott's default. |
| Selecting a video profile made its soundtrack baseline appear Custom | Opus | Mode detection accounts for the profile baseline. Browser tests cover 160 and 96 kbps profiles. |
| Custom with no value displayed a custom choice but saved an inherited default | Opus | Custom seeds the effective bitrate. Clearing the value displays Default. |
| Verify asked for a limit in a hidden input | Opus | A direct button opens Advanced verification; the browser test follows that action. |
| Ordinary preset budget did not explain retained surround scaling | Astra | The control states that the budget is a mono/stereo baseline and is applied per channel pair for retained surround. |
| An explicit saved bitrate matching the default had different inheritance behaviour | Astra re-review | The control explains the saved value and how Default clears it, including an accessible description. Two browser tests cover profile changes with null and explicit budgets. |

Astra's final independent re-review found no remaining actionable findings and considered the
slice ready for merge subject to required CI. It reran the preset unit tests, documentation
checks and diff checks, inspected the screenshot, and checked all 96 hardware evidence records.

Opus completed its initial review. Its requested final re-review could not run because the
signed-in CLI session reached its usage limit. Its initial confirmed findings are addressed
above and covered by regression tests; there is no final Opus approval claim.

## Verification

| Check | Result |
|---|---|
| Release backend build | Zero warnings and errors |
| Backend tests | 2,662 passed |
| Svelte/TypeScript and nine-locale checks | Zero errors and warnings |
| Frontend unit tests | 80 passed |
| Full Playwright suite | 249 passed |
| Python tooling tests | 74 passed |
| Runtime OpenAPI check | Current; the anonymous options response adds fields without generated schema drift |
| Documentation, Unraid metadata and diff checks | Passed |
| Documentation capture | Current fabricated UI, visually inspected |

Playwright uses fabricated API responses. Its development-server log includes SignalR
connection attempts against the absent backend; these are not failed assertions. The production
frontend build succeeds with the existing advisory about bundle size.

### Installed media tools

The [raw acceptance evidence](evidence/2026-10-02-audio-encoding-presets.json) records 24
codec/bitrate/channel combinations in each environment:

| Environment | Cases | Result |
|---|---:|---|
| This Mac's installed sidecar tools | 24 | Encoded, probed and decoded |
| PICARD's MSI-installed Windows tools | 24 | Encoded, probed and decoded |
| Quark's running Linux sidecar tools | 24 | Encoded, probed and decoded |
| Riker's running server-container tools | 24 | Encoded, probed and decoded |

Tests used eight seconds of freely reusable Samplelib mono speech, plus a stereo version with
the channel duplicated. Each codec's four budgets were exercised for both channel counts.
Codec, channel count and duration were checked; outputs decoded successfully. Fixture hashes
were unchanged, and remote task directories were removed. No production library settings,
queue jobs, worker pairing or container lifecycle were changed.

This proves the recorded codec settings work with installed tools. It does not calibrate
listening grades, qualify surround, or constitute new full application pipeline acceptance.
The previous gate slice's worker/server and replacement evidence is recorded in
[PR #345](https://github.com/Jellman86/optimisarr/pull/345).

## Remaining pipeline work

Use bounded sample search only with an explicit acceptance policy, cancellation and resource
limits. For video soundtracks, match retained source/output tracks and languages, construct
the intended reference for downmixes, retain independent lip-sync checks and assess each
re-encoded track. Copied tracks need preservation checks. Strict worker mode must keep media
analysis on the worker. These are future changes, not capabilities delivered by this UI slice.
