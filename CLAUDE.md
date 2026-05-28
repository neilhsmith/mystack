# mystack

A personal full-stack boilerplate. **Currently in v0** — this is a bare skeleton plus the agent/PR workflow. Most features are not yet implemented and will be added via subsequent PRs.

## Current state

- ASP.NET 10 API at `apps/api/` with `/v1/hello`, `/health` (live/ready/aggregate), and `/v1/posts` (CRUD) endpoints. API surface is versioned under `/v1` via a top-level `MapGroup("/v1")` in `Program.cs` — new features hang off that group, breaking changes go behind a sibling `v2`. Health endpoints stay unversioned (ops probes don't track API contract version). Feature-folder layout: `src/Api/Features/<feature>/`. Cross-cutting infrastructure split by concern: data layer (`AppDbContext`, marker interfaces, interceptors, migrations) under `src/Api/Data/`; HTTP layer (response-shaping middleware, exception handlers) under `src/Api/Http/`; validation infrastructure under `src/Api/Validation/`.
- EF Core 10 + Npgsql provider; `AppDbContext` with `Posts` DbSet. Migrations live in `src/Api/Data/Migrations/` and apply on startup in `Development` env.
- Framework-managed entity concerns via marker interfaces in `src/Api/Data/`: `ITimestamped` (auto `CreatedAt`/`UpdatedAt`), `ISoftDeletable` (auto-converts `Remove` into `DeletedAt = now()` + global query filter hides deleted rows). Both handled by `AuditInterceptor` on SaveChanges. DB safety net: `now()` column defaults + UPDATE trigger for timestamps; raw SQL is the escape hatch for hard delete.
- **Body-hash `ETag` + 304 on every GET via [`EtagMiddleware`](apps/api/src/Api/Http/EtagMiddleware.cs).** Strong tag is SHA1 of the response body, computed and stamped by the middleware after the handler runs; matching `If-None-Match` short-circuits to `304 Not Modified` with an empty body. Endpoints stay ignorant — no per-handler ETag wiring, no per-route opt-in. The tradeoff is intentional: a body-hash tag has no relationship to the DB row version, so it can't back `If-Match` preconditions for safe writes. Lost-update protection is instead handled at the DB layer — every entity gets Postgres's `xmin` column as a shadow concurrency token (applied by the convention loop in `AppDbContext.OnModelCreating`, zero per-entity / per-migration cost), EF throws `DbUpdateConcurrencyException` on stale writes, and [`DbUpdateConcurrencyExceptionHandler`](apps/api/src/Api/Http/DbUpdateConcurrencyExceptionHandler.cs) maps that to `409 Conflict` problem+json. Health endpoints (`/health*`) are exempt from the ETag middleware for the same reason they're exempt from rate-limiting (ops infra shouldn't tangle with response-shaping).
- Request DTO validation via **FluentValidation** — one validator class per DTO in the feature folder (`CreatePostRequestValidator`, `UpdatePostRequestValidator`). The generic `ValidationEndpointFilter<T>` in `src/Api/Validation/` runs the validator before the handler and short-circuits with RFC 9457 `application/problem+json` (via `Results.ValidationProblem`). Field constraints live in a nested `public static class Constraints` on the entity (`Post.Constraints.MaxTitleLength`, `Post.Constraints.MaxContentLength`) and are referenced by EF fluent config, FluentValidation, and the OpenAPI spec — one source of truth per entity.
- **Authn + RBAC authz via OpenIddict 7 in-process** ([details](docs/features/auth/)). OAuth 2.0 + OIDC authorization server hosted in the same project as the API (deliberate v0 choice — splitting auth into its own service is a few file moves away). ASP.NET Identity backs users + roles (tables in the same `AppDbContext`); OpenIddict EF Core stores keep applications / scopes / authorizations / tokens in the same DB. Three flows enabled: Authorization Code + PKCE (interactive `mystack-web` client), Client Credentials (service-to-service `mystack-service` client), Refresh Token (rotation, no reuse leeway). Implicit / password / device-code flows are off. JWT access tokens (15 min) signed with RS256, encryption disabled so resource servers read them directly; refresh tokens 14 days, rotating. Discovery doc lives at `/.well-known/openid-configuration`, JWKS at `/.well-known/jwks`. Login UI in [`src/Api/Pages/Account/`](apps/api/src/Api/Pages/Account/), minimal Razor Pages — auth UI is cross-cutting so it stays at the `Pages/` root rather than under a feature folder.
- **Two-layer authorization on every API endpoint.** Layer 1: **scope** (broad gate, on the token). Custom scopes `mystack.read` + `mystack.write` declared in [`Features/Auth/Scopes.cs`](apps/api/src/Api/Features/Auth/Scopes.cs); standard OIDC scopes (`openid`, `profile`, `email`, `roles`, `offline_access`) come from `OpenIddictConstants.Scopes`. Layer 2: **permission** (fine-grained, derived from role). Permission constants are code-defined in [`Rbac/Permissions.cs`](apps/api/src/Api/Rbac/Permissions.cs) (`posts.read`, `posts.create`, …); roles in [`Rbac/Roles.cs`](apps/api/src/Api/Rbac/Roles.cs) (`GlobalAdmin`, `Admin`, `User`). The seeder upserts permissions + roles + default `(role, permission)` assignments on startup; admin edits to `role_permissions` survive subsequent seeds (the seeder NEVER deletes extra rows). Endpoints declare requirements with `.RequireScope(Scopes.Read).RequirePermission(Permissions.Posts.Read)` — dynamic policy provider in [`Authorization/DynamicAuthorizationPolicyProvider.cs`](apps/api/src/Api/Authorization/DynamicAuthorizationPolicyProvider.cs) synthesises policies on the fly from the `scope:` / `perm:` prefix.
- **Role-only tokens; permission lookup via in-process cache.** Access tokens carry the user's role(s) — not their permissions. The API resolves the permission set per-request by hashing into [`RolePermissionCatalog`](apps/api/src/Api/Rbac/RolePermissionCatalog.cs), a singleton `FrozenDictionary<role, FrozenSet<permission>>` rebuilt by [`RolePermissionCatalogRefresher`](apps/api/src/Api/Rbac/RolePermissionCatalogRefresher.cs) — synchronous load at startup, then every 5 minutes. Whole-snapshot atomic replacement (`Interlocked.Exchange`) means no lock in the hot path. Admin edits to `role_permissions` take effect within ~5 min without re-issuing tokens. Service clients (client_credentials grant) get their role from the `mystack:roles` property on the OpenIddict application (see [`ClientRoles`](apps/api/src/Api/Features/Auth/ClientRoles.cs)) — `mystack-service` is seeded with `GlobalAdmin`; deployers can change this per-client.
- **Fallback authorization policy denies everything unauthenticated.** `AddAuthorization(opts => opts.FallbackPolicy = ...)` requires an authenticated principal under either the OpenIddict validation scheme (bearer tokens) or the Identity application cookie. Forgetting to add `.RequirePermission(...)` to a new endpoint still 401s rather than serving anonymously. Anonymous endpoints (`/v1/hello`, `/health*`, `/.well-known/*`, `/openapi/v1.json`, `/Account/Login`, `/`) opt out explicitly via `.AllowAnonymous()` or `[AllowAnonymous]`. Token audience is `mystack-api`; the validation pipeline rejects tokens missing that `aud` claim (registered as a scope resource by the seeder).
- **RFC 7807 / 9457 `problem+json` for every error response.** `AddProblemDetails` + `UseExceptionHandler` + `UseStatusCodePages` in `Program.cs` ensure unhandled exceptions (default → 500, `DbUpdateConcurrencyException` → 409 via the registered `IExceptionHandler`), bare 4xx/5xx status results, validation failures, and any handler-built `Results.Problem` calls all come back in the same shape — no stack trace leaks. A customizer attaches `traceId` (the per-request `HttpContext.TraceIdentifier`) to every problem response so a client report can be matched against server logs. Verified end-to-end by `ProblemDetailsTests` against dev-only `/v1/diagnostics/throw` and `/v1/diagnostics/throw-concurrency` probes.
- **Per-IP fixed-window rate limiter** (`AddRateLimiter` / `UseRateLimiter`) applied globally. Defaults in `appsettings.json` are strict (100 req / 60 s) so the boilerplate is safe out of the box; `appsettings.Development.json` overrides to 10000/60s so the test suite and local dev aren't throttled. Limit and window are config-driven (`RateLimiting:PermitLimit`, `RateLimiting:WindowSeconds`) so deployers tune per environment. Health endpoints (`/health*`) are exempt — k8s / load balancer probes shouldn't be throttled. Rejections emit `429 Too Many Requests` as RFC 7807 problem+json (via the same customizer as everything else) plus a `Retry-After` header. `RateLimitingTests` exercises the rejection path against a separate `RateLimitedTestFactory` that drops the limit to 3 — keeps the main test suite running under lenient defaults while still proving the wiring.
- DTO ↔ entity mapping via **Mapperly** — one `[Mapper] partial class <Feature>Mapper` per feature folder (source-generated, no runtime reflection). `PostMapper` exposes `ToResponse`, `ToEntity`, `Apply`, registered as a singleton.
- **OpenAPI spec at `/openapi/v1.json`** (`Microsoft.AspNetCore.OpenApi`, ASP.NET 10 native). [`FluentValidationSchemaTransformer`](apps/api/src/Api/Validation/FluentValidationSchemaTransformer.cs) reflects validator rules (`required`, `maxLength`, `minLength`, `pattern`) into the schema so the published contract advertises the same constraints the runtime enforces. ETag is emitted by middleware on every GET response and not currently advertised in the spec — add a transformer if/when a typed client needs to know about `If-None-Match`. [`OAuthSecuritySchemeTransformer`](apps/api/src/Api/OpenApi/OAuthSecuritySchemeTransformer.cs) + [`OAuthOperationSecurityTransformer`](apps/api/src/Api/OpenApi/OAuthOperationSecurityTransformer.cs) declare the `oauth2` security scheme once and stamp per-operation security requirements (with the exact scopes the endpoint needs) onto every protected route — so spec consumers (Swagger UI, generated clients) know how to authenticate and which scopes to request. `OpenApiSpecTests` is the regression guard.
- **Swagger UI at `/swagger`** (`Swashbuckle.AspNetCore.SwaggerUI` — UI only, the spec still comes from the native generator). **Dev-only** by design — prod APIs typically don't expose Swagger. The page is wired with OAuth Authorization Code + PKCE against the seeded [`mystack-swagger`](apps/api/src/Api/Features/Auth/Seeding/OpenIddictSeeder.cs) public client (redirect URI `http://localhost:5084/swagger/oauth2-redirect.html` — Swashbuckle's bundled callback page). Click "Authorize", the popup runs the full OIDC dance through `/connect/authorize` (which kicks the cookie auth challenge to `/Account/Login` if needed) and `/connect/token`, then every "Try it out" request below carries the bearer token automatically.
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

### Auth endpoints (quick reference)

```powershell
# OIDC discovery — what flows / scopes / endpoints are supported.
Invoke-RestMethod http://localhost:5084/.well-known/openid-configuration

# JWKS — public keys used to sign access + identity tokens.
Invoke-RestMethod http://localhost:5084/.well-known/jwks

# Client credentials grant — get a service-to-service token for mystack-service.
# Default dev secret is in appsettings.Development.json; replace in real deployments.
curl -X POST http://localhost:5084/connect/token `
  -d "grant_type=client_credentials" `
  -d "client_id=mystack-service" `
  -d "client_secret=dev-service-secret-change-me" `
  -d "scope=mystack.read mystack.write"

# Call a protected endpoint with the token.
curl -H "Authorization: Bearer <access_token>" http://localhost:5084/v1/posts

# Authorization Code + PKCE flow — driven from a browser at:
#   GET /connect/authorize?client_id=mystack-web&response_type=code&...
# After /Account/Login, the browser is redirected back to the registered redirect URI
# with the auth code. Exchange the code at /connect/token with the same code_verifier.

# Sign-in page (interactive flow). Dev users seeded with password from appsettings.Development.json:
#   globaladmin@mystack.test / admin@mystack.test / user@mystack.test
http://localhost:5084/Account/Login

# Swagger UI (dev only). Click Authorize, sign in with a dev user, then "Try it out" on
# any protected endpoint — the bearer token is attached automatically.
http://localhost:5084/swagger
```

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
