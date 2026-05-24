# mystack

Personal full-stack boilerplate. Work in progress.

## Status

**v0** — bare skeleton with agent workflow scaffolding. See [docs/features/bootstrap/initial.md](docs/features/bootstrap/initial.md) for what exists and what's planned.

## Quick start

```bash
# Run the API
dotnet run --project apps/api/src/Api

# Should respond at http://localhost:5xxx/hello
```

## Tests

Two test projects live under `apps/api/tests/`: `Api.Tests.Unit` (fast, in-process) and `Api.Tests.Integration` (boots the real ASP.NET host via `WebApplicationFactory<Program>`). Both use **xUnit v3** on the **Microsoft Testing Platform**.

```bash
# All tests
dotnet test --solution apps/api/Api.slnx

# Just unit tests
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
