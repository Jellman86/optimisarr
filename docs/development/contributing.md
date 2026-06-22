# Development

Optimisarr contains ASP.NET Core API, Core domain logic, EF Core/SQLite data,
and a Svelte frontend. Run the standard checks from the repository root:

```bash
dotnet build Optimisarr.slnx
dotnet test Optimisarr.slnx
cd web && npm run check
cd web && npm run dev
```

Read [`CLAUDE.md`](../../CLAUDE.md) and [`AGENTS.md`](../../AGENTS.md) before
editing. Keep safety decisions pure and tested, use migrations for schema changes,
and never weaken replacement verification to make a job complete.
