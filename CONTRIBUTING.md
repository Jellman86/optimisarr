# Contributing

Read [CLAUDE.md](CLAUDE.md) and [AGENTS.md](AGENTS.md) before changing code.
Optimisarr's non-negotiable rule is that no original is replaced or deleted until
a verified replacement and rollback path exist.

Before submitting a change, run:

```bash
dotnet build Optimisarr.slnx
dotnet test Optimisarr.slnx
cd web && npm run check
```

Add focused tests for changed behaviour, add an EF migration for every schema
change, update `CHANGELOG.md`, and update documentation where behaviour or
configuration changes.
