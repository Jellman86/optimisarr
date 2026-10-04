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

For worker hardening or fleet testing, read the current
[sidecar handoff](docs/development/sidecar-hardening-handoff.md). Its dated evidence is
context; current code, issue state and exact-build verification remain authoritative.
