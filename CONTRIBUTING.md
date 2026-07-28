# Contributing

Optimisarr is independently maintained. Small, focused changes with a clear
problem statement are the easiest to review. Read [CLAUDE.md](CLAUDE.md) and
[AGENTS.md](AGENTS.md) before changing code. By participating, you agree to
follow the [Code of Conduct](CODE_OF_CONDUCT.md).

No original may be replaced or deleted until a verified replacement and rollback
path exist.

Before submitting a change, run:

```bash
dotnet build Optimisarr.slnx
dotnet test Optimisarr.slnx
cd web && npm run check
```

Add focused tests for changed behaviour, add an EF migration for every schema
change, update `CHANGELOG.md`, and update documentation where behaviour or
configuration changes.

## Branches and pull requests

Start from current `dev` and create a short-lived branch for one reviewable
behaviour change or tightly related maintenance slice. Open the pull request
into `dev`; release pull requests alone target `main`.

Use the repository pull request template to record:

- the useful outcome and exact included/excluded scope;
- focused and full verification commands with their real results;
- original-file, replacement, migration, security, or operational risks;
- recovery or deployment evidence when the change needs it.

Keep required checks green and resolve blocking review conversations before
merge. A pull request is delivery evidence, not a substitute for tests or a
place to accumulate unrelated roadmap work.

## Documentation

Follow the [documentation standard](docs/documentation-standard.md). In short,
write for a person setting up one server at home:

- Use plain English, active voice, and `you` for instructions.
- Start with the working command or action. Put background detail after it.
- Use real paths, settings, and screenshots from the current build. Do not add
  speculative features or marketing language.
- Keep sentences and paragraphs short. Explain acronyms the first time they
  matter.
- Avoid collective language such as “we”, release promises, and generic filler.
- Run `python3 scripts/check_docs.py` after changing Markdown links.
