# mystack

Personal full-stack boilerplate. Work in progress.

## Status

**v0** — bare skeleton with agent workflow scaffolding. See [docs/features/bootstrap/initial.md](docs/features/bootstrap/initial.md) for what exists and what's planned.

## Quick start

```powershell
# 1. Start Postgres (default profile is just infra — fast dev loop, API runs natively).
cd infra; docker compose up -d; cd ..

# 2. Run the API. Applies EF migrations on startup in Development.
dotnet run --project apps/api/src/Api

# Endpoints (API surface is versioned under /v1; health checks stay unversioned):
#   GET    http://localhost:5084/v1/hello
#   GET    http://localhost:5084/health (live | ready | aggregate)
#   GET    http://localhost:5084/v1/posts
#   GET    http://localhost:5084/v1/posts/{id}
#   POST   http://localhost:5084/v1/posts
#   PUT    http://localhost:5084/v1/posts/{id}
#   DELETE http://localhost:5084/v1/posts/{id}
```

To bring up the **full stack inside Docker** (API container + Postgres), use the `full` compose profile:

```powershell
cd infra; docker compose --profile full up -d --build
# API at http://localhost:8080
```

## Tests

Two test projects under `apps/api/tests/`: `Api.Tests.Unit` (fast, in-process) and `Api.Tests.Integration` (boots the real ASP.NET host via `WebApplicationFactory<Program>` against a throwaway Postgres container spun up by **Testcontainers**). Both use **xUnit v3** on the **Microsoft Testing Platform**.

**Integration tests require Docker to be running locally.** CI provides Docker as part of the GitHub Actions Ubuntu runner.

```powershell
# All tests (Docker required for integration)
dotnet test --solution apps/api/Api.slnx

# Just unit tests (no Docker needed)
dotnet test apps/api/tests/Api.Tests.Unit

# Just integration tests
dotnet test apps/api/tests/Api.Tests.Integration

# With coverage (cobertura XML in TestResults/)
dotnet test --solution apps/api/Api.slnx --collect:"XPlat Code Coverage"
```

In **VS Code**, install the recommended extensions (you'll be prompted on first open — or run **Extensions: Show Recommended Extensions**). The C# Dev Kit's **Test Explorer** then auto-discovers both projects; you can run / debug individual tests from the gutter or the Testing sidebar. `Ctrl+Shift+B` runs the default build task, and the **Tasks: Run Test Task** command exposes `test`, `test:unit`, `test:integration`, and `test:coverage`.

CI runs the same `dotnet test` invocation on every PR — see [.github/workflows/pr.yml](.github/workflows/pr.yml). See [apps/api/tests/README.md](apps/api/tests/README.md) for what belongs in each project.

## Project structure

```
apps/      Application code (api, web) — `apps/api/tests/` holds per-app unit + integration tests
packages/  Shared TypeScript packages
infra/     Docker, compose files
tests/     Cross-cutting tests (E2E)
scripts/   Dev scripts
docs/      Documentation
.claude/   Claude Code skills + hooks + settings
.github/   GitHub Actions + PR template
.vscode/   Shared editor config (extensions, tasks, launch)
```

## Working with agents

This repo is set up to be developed primarily via Claude Code agents creating pull requests for human review. See [CLAUDE.md](CLAUDE.md) for the workflow.
