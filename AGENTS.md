# Agent instructions

The engineering standards for this repository live in [`CLAUDE.md`](CLAUDE.md)
and apply to every contributor and every AI agent, regardless of tool.

Read `CLAUDE.md` before making changes. In short: **safety first, test-driven,
migrations-only databases that are idempotent, self-documenting code, and a
clean operational UI.** A change is not done until the §6 "Definition of done"
checklist passes.

Everyday work starts from current `dev` on a short-lived branch and reaches
`dev` through a pull request. Release pull requests alone target `main`.

Release notes follow the human-first standard in
[`docs/development/releasing.md`](docs/development/releasing.md) and use
[`.github/RELEASE_NOTES_TEMPLATE.md`](.github/RELEASE_NOTES_TEMPLATE.md).

For the current sidecar verification follow-up, start with the dated
[hardening handoff](docs/development/sidecar-hardening-handoff.md) and the roadmap's
**Up next** section. They distinguish completed work, unresolved failures and private evidence.
Recheck live state before operational work; the documented retry batch is finished.
