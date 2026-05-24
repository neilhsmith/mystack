# mystack

A personal full-stack boilerplate. **Currently in v0** — this is a bare skeleton plus the agent/PR workflow. Most features are not yet implemented and will be added via subsequent PRs.

## Current state

- ASP.NET 10 API at `apps/api/` with `/hello`, `/health` (live/ready/aggregate), and `/posts` (CRUD) endpoints. Feature-folder layout: `src/Api/Features/<feature>/`. Cross-cutting infrastructure (DbContext, migrations) lives under `src/Api/Data/`.
- EF Core 10 + Npgsql provider; `AppDbContext` with `Posts` DbSet. Migrations live in `src/Api/Data/Migrations/` and apply on startup in `Development` env.
- Framework-managed entity concerns via marker interfaces in `src/Api/Data/`: `ITimestamped` (auto `CreatedAt`/`UpdatedAt`), `ISoftDeletable` (auto-converts `Remove` into `DeletedAt = now()` + global query filter hides deleted rows). Both handled by `AuditInterceptor` on SaveChanges. DB safety net: `now()` column defaults + UPDATE trigger for timestamps; raw SQL is the escape hatch for hard delete.
- Postgres via `infra/docker-compose.yml`. Default `docker compose up` (run from `infra/`) brings up only Postgres so the API can run natively. `docker compose --profile full up` brings up the full stack including a built API container.
- Production-style Dockerfile at `apps/api/src/Api/Dockerfile` (multi-stage, .NET 10 SDK → ASP.NET 10 runtime).
- Test projects at `apps/api/tests/Api.Tests.Unit` and `apps/api/tests/Api.Tests.Integration` (xUnit v3, Microsoft Testing Platform). Integration tests use `WebApplicationFactory<Program>` + a shared `Testcontainers.PostgreSql` container (`ApiTestFactory`).
- Folder skeleton in place for `apps/web`, `packages/`, `tests/`, `scripts/`
- GitHub Actions PR validation workflow (build + test). CI runner has Docker available, so Testcontainers works there too.
- Shared VS Code config in `.vscode/` (extensions, settings, tasks, launch)
- This file (CLAUDE.md), README, .editorconfig, .gitignore

## Planned (not yet built)

The full architecture vision lives in [docs/features/bootstrap/initial.md](docs/features/bootstrap/initial.md) and will be implemented via PRs. High-level: TanStack Start BFF + ASP.NET API, Postgres + EF Core, OpenIddict auth, Hangfire, Docker compose dev env, comprehensive testing.

Agents working in this repo: **do not invent architecture decisions.** When something isn't documented yet, ask first. The user has strong opinions about how this should be built.

## Workflow rules (CRITICAL)

These rules govern how agents interact with this repo:

1. **Never push to main.** All changes go through pull requests.
2. **Create a feature branch** for every task, named `agent/<short-description>` (e.g., `agent/add-user-endpoint`).
3. **Commit messages** follow Conventional Commits: `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`, `test:`, `ci:`.
4. **Open a PR** when work is complete (or at a logical stopping point for human review).
5. **PR description** must include:
   - What changed and why
   - Anything the human should pay attention to during review
   - Any decisions made that weren't pre-specified (call these out explicitly)
6. **Wait for human review.** Don't merge your own PRs.
7. **Respond to review feedback** by pushing additional commits to the same branch, not by force-pushing or rebasing (unless explicitly asked).
8. **When unsure, ask** rather than guess. The cost of asking is low; the cost of doing the wrong thing and creating cleanup work is high.
9. **Touching `apps/api/`? Use the [`backend-dev`](.claude/skills/backend-dev/SKILL.md) skill.** It covers app launch (with collision-free ports for parallel worktrees), test discipline (*if you change source, you change tests*), and the **mandatory** pre-PR validation checklist. Paste the skill's validation report into the PR body — the human reviewer relies on it. Skipping any step in that checklist is not an option for backend PRs.

## Current commands

```powershell
# --- Infra (Postgres) ---
# Bring Postgres up (run from infra/). Persists in volume `mystack_postgres-data`.
cd infra; docker compose up -d

# Bring the FULL stack up (API container + Postgres)
cd infra; docker compose --profile full up -d --build

# Tear down (keeps volume)
cd infra; docker compose down

# Tear down + drop the volume (DESTROYS DB STATE)
cd infra; docker compose down -v

# --- API ---
# Run the API natively (Development env reads connection string from appsettings.Development.json)
dotnet run --project apps/api/src/Api

# Build everything
dotnet build apps/api/Api.slnx

# --- Tests ---
# Run all tests (unit + integration). Integration tests need Docker running.
dotnet test --solution apps/api/Api.slnx

# Just unit tests (fast — no Docker needed)
dotnet test apps/api/tests/Api.Tests.Unit

# Just integration tests
dotnet test apps/api/tests/Api.Tests.Integration

# With coverage (cobertura XML in TestResults/)
dotnet test --solution apps/api/Api.slnx --collect:"XPlat Code Coverage"

# --- EF Core migrations ---
# Add a migration (after model changes). Always pass --output-dir so files land under Data/Migrations/.
dotnet ef migrations add <Name> --project apps/api/src/Api --output-dir Data/Migrations

# Apply migrations against the running dev Postgres (Program also does this on startup in Dev)
dotnet ef database update --project apps/api/src/Api

# Remove the last unapplied migration
dotnet ef migrations remove --project apps/api/src/Api
```

The `dotnet test --solution …` form is required for multi-project solutions on the Microsoft Testing Platform runner (configured in `global.json`). For a single project path, omit `--solution`.

More commands will be added as the stack grows.

## Testing conventions

High-level summary — the authoritative recipe (with examples, decision tables, and the pre-PR checklist) lives in the [`backend-dev`](.claude/skills/backend-dev/SKILL.md) skill.

- **Unit tests** (`apps/api/tests/Api.Tests.Unit/`): pure in-process. No I/O, no DI container, no HTTP.
- **Integration tests** (`apps/api/tests/Api.Tests.Integration/`): real ASP.NET host via `WebApplicationFactory<Program>` over `HttpClient`. For real dependencies (DB, queues), prefer [Testcontainers](https://dotnet.testcontainers.org/) over mocks/in-memory fakes.
- Behavior changes require test changes. Refactors should pass existing tests unchanged.
- xUnit v3 on Microsoft Testing Platform — pass `TestContext.Current.CancellationToken` when calling anything that accepts a token (the `xUnit1051` analyzer warns otherwise).

## Things never to do (current list — will grow)

- Don't push directly to main
- Don't force-push to PR branches without being asked
- Don't add architecture pieces (auth, DB, etc.) unless the task explicitly asks for them
- Don't run destructive git commands (`git reset --hard origin/main`, `git push --force`, etc.) without explicit confirmation
- Don't commit secrets, API keys, or anything from `.env*` files

## Skill files

Skills live under `.claude/skills/`, one folder per skill, each with a `SKILL.md` inside. They auto-trigger on keyword matches in their description — name and describe them in action terms, not role terms, so triggers are unambiguous.

Currently available:

- [`backend-dev`](.claude/skills/backend-dev/SKILL.md) — workflow for tasks touching `apps/api/`. **Required for any backend PR** (see workflow rule #9 above). Project-specific: where tests live, how to launch the app on a free port, the mandatory pre-PR checklist.
- [`tdd`](.claude/skills/tdd/SKILL.md) — general TDD philosophy and workflow (red-green-refactor, vertical slices, what makes a good test, when to mock). Cross-language guidance; examples are C#/xUnit. Use whenever you're writing tests, regardless of stack.
- [`zoom-out`](.claude/skills/zoom-out/SKILL.md) — user-invoked only (`disable-model-invocation: true`). Use when you (the human) want the agent to step back and map an unfamiliar area at a higher level of abstraction before diving in.
