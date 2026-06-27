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
