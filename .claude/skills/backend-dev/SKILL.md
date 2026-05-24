---
name: backend-dev
description: Backend workflow for the ASP.NET API at `apps/api/`. Use when starting any task that touches `apps/api/` (source or tests) — adding endpoints or entities, writing EF Core migrations, wiring ETags / RFC 7232 conditional requests, adding/editing/removing tests, running the API locally, or validating changes before opening a PR. Covers feature-folder layout, entity conventions (timestamps, soft delete, ETags), test organisation rules, the random-port launch trick, and the mandatory pre-PR validation checklist.
---

# backend-dev

The recipe for working on `apps/api/` without breaking things. Three sections — read each before doing the corresponding action.

> **Mandatory:** Section 3 (the pre-PR checklist) must be completed before you call `gh pr create` on any PR touching `apps/api/`. Paste the validation report from Section 3 into the PR body so the human reviewer can see what you actually ran. No exceptions for "small" or "obvious" changes.

---

## 1 — Starting work on the API

### Layout

```
apps/api/
├── Api.slnx                              # solution (multi-project)
├── Directory.Build.props                 # solution-wide MSBuild props
├── src/Api/                              # the API itself
│   ├── Program.cs                        # entry point + endpoint mapping
│   ├── Api.csproj
│   ├── Dockerfile                        # production image
│   ├── Properties/launchSettings.json    # local debug ports (5084 / 7199)
│   ├── Features/                         # one folder per feature (Posts/, …)
│   │   └── Posts/                        # Post entity, DTOs, MapPostsEndpoints
│   ├── Data/                             # cross-cutting data layer
│   │   ├── AppDbContext.cs
│   │   ├── DesignTimeDbContextFactory.cs
│   │   ├── ITimestamped.cs               # marker: opts entity into auto CreatedAt/UpdatedAt
│   │   ├── ISoftDeletable.cs             # marker: opts entity into soft delete (DeletedAt)
│   │   ├── AuditInterceptor.cs           # SaveChanges hook: timestamps + soft-delete intercept
│   │   └── Migrations/                   # EF Core generated
│   ├── Http/                             # cross-cutting HTTP layer
│   │   ├── ETag.cs                       # strong RFC 7232 tag from ITimestamped.UpdatedAt
│   │   ├── ConditionalRequest.cs         # If-None-Match → 304; If-Match → 412/428
│   │   └── ConditionalRequestOpenApi.cs  # .WithConditionalRead/Write/EtagResponseHeader markers + transformer
│   └── Validation/                       # cross-cutting validation layer
│       ├── ValidationEndpointFilter.cs   # runs IValidator<T>, short-circuits with problem+json
│       └── FluentValidationSchemaTransformer.cs  # reflects validator rules into the OpenAPI schema
└── tests/
    ├── Api.Tests.Unit/                   # pure in-process tests
    └── Api.Tests.Integration/            # WebApplicationFactory<Program>-based
        └── Fixtures/                     # ApiTestFactory (Testcontainers)
```

**New features go under `src/Api/Features/<FeatureName>/`.** Keep cross-cutting concerns (DbContext, migrations, future Auth/Telemetry/etc.) at `src/Api/` root, not under `Features/`.

### Conventions for new entities

- **Timestamps:** implement `ITimestamped` from `Api.Data` to get `CreatedAt` / `UpdatedAt` set automatically by `AuditInterceptor` on every SaveChanges. **Do not** initialize the properties on the entity or assign them in endpoint handlers — the interceptor is the single source of truth, and it truncates to microsecond precision so API responses match what Postgres persists (ETags / conditional GETs depend on this exact match).
- **Soft delete:** implement `ISoftDeletable` from `Api.Data` to get a `DeletedAt` (nullable `DateTimeOffset`) column the `AuditInterceptor` populates whenever you call `db.<Entity>.Remove(...)`. The row is never hard-deleted by EF — a `DELETE` becomes an `UPDATE` setting `DeletedAt = now()`. A global query filter (applied automatically in `AppDbContext.OnModelCreating`) hides soft-deleted rows from every query; use `.IgnoreQueryFilters()` to opt out for admin/audit views. Add a convenience `public bool IsDeleted => DeletedAt is not null;` on the entity. Hard delete is still possible via raw SQL when truly needed (GDPR erasure, maintenance).
- **Primary keys:** `Guid` initialized with `Guid.CreateVersion7()` — sortable, distributed-safe.
- **The current time:** inject `TimeProvider` (registered as `TimeProvider.System`) and call `GetUtcNow()`. Don't call `DateTimeOffset.UtcNow` directly — that ties the code to the wall clock and makes deterministic tests painful.
- **ETags & conditional requests:** any endpoint that returns or mutates a single `ITimestamped` resource should surface an ETag and honour RFC 7232 preconditions via [`ETag.From(...)`](../../../apps/api/src/Api/Http/ETag.cs) and [`ConditionalRequest`](../../../apps/api/src/Api/Http/ConditionalRequest.cs). See the section below.
- **Field constraints:** define max lengths, regex patterns, and any other field-level constraints as `public const` (or `public static readonly`) fields on a **nested `public static class Constraints`** on the entity. Reference them from EF fluent config, the FluentValidation validators, and any other layer that needs to know. One change, three layers (DB schema, runtime validation, OpenAPI spec). [`Post.Constraints`](../../../apps/api/src/Api/Features/Posts/Post.cs) is the worked example — callers read e.g. `Post.Constraints.MaxTitleLength`. The nesting is the convention: do NOT inline these constants directly on the entity, and do NOT split them into a sibling class — that breaks the "one place to look per entity" rule.
- **Request DTO validation:** one FluentValidation `AbstractValidator<T>` per request DTO, lives flat in the feature folder (e.g. [`CreatePostRequestValidator`](../../../apps/api/src/Api/Features/Posts/CreatePostRequestValidator.cs)). Set `RuleLevelCascadeMode = CascadeMode.Stop` so each property reports one failure at a time. Validators are auto-discovered by `AddValidatorsFromAssemblyContaining<Program>()` in `Program.cs`. Apply per-endpoint with `.AddEndpointFilter<ValidationEndpointFilter<TRequest>>()` — see [`PostsEndpoints`](../../../apps/api/src/Api/Features/Posts/PostsEndpoints.cs) for the wiring. Failure → 400 with RFC 9457 `application/problem+json` (built by `Results.ValidationProblem`). The filter short-circuits **before** any handler logic, including 404/ETag checks.
- **DTO ↔ entity mapping:** one `[Mapper] partial class <Feature>Mapper` per feature folder (e.g. [`PostMapper`](../../../apps/api/src/Api/Features/Posts/PostMapper.cs)) using Mapperly. Source-generated — the mapping code is plain C# you can step through, no runtime reflection. Register as a singleton in `Program.cs`. Use `RequiredMappingStrategy.Target` on the `[Mapper]` attribute and `[MapperIgnoreTarget(nameof(Post.Id))]` etc. to silence warnings for framework-managed fields (Id, CreatedAt, UpdatedAt, DeletedAt) that the mapper must not touch.

### Adding a new entity — checklist

When you create a new DB-backed entity, walk this list:

- [ ] **Entity** in `src/Api/Features/<Feature>/<Entity>.cs`. Implements `ITimestamped` (timestamps), `ISoftDeletable` (soft delete), or both. `Id` is `Guid` with `Guid.CreateVersion7()` default. Do not initialize `CreatedAt` / `UpdatedAt` / `DeletedAt`. If you implement `ISoftDeletable`, add `public bool IsDeleted => DeletedAt is not null;`. Declare every field-level constraint inside a **nested `public static class Constraints`** on the entity (max lengths, regex patterns, value ranges) so EF config, validators, and the OpenAPI spec read from one source of truth — e.g. `Post.Constraints.MaxTitleLength`.
- [ ] **DbSet** added to `AppDbContext`: `public DbSet<MyEntity> MyEntities => Set<MyEntity>();`.
- [ ] **OnModelCreating** in `AppDbContext`: configure keys, max lengths (reference the nested constants — e.g. `HasMaxLength(MyEntity.Constraints.MaxTitleLength)`), unique indexes, FKs. Column defaults for `ITimestamped` timestamps AND the global query filter for `ISoftDeletable` are applied automatically by the convention loops — don't repeat them per entity.
- [ ] **DTOs** in the feature folder (`<Entity>Dtos.cs`): one request record per write operation (`Create…Request`, `Update…Request`), one response record (`<Entity>Response`).
- [ ] **Mapper** in the feature folder: `[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)] public partial class <Entity>Mapper`. Declare `partial PostResponse ToResponse(Post source)`, `partial Post ToEntity(CreatePostRequest source)`, `partial void Apply(UpdatePostRequest source, Post target)`. Use `[MapperIgnoreTarget(...)]` on `ToEntity`/`Apply` for framework-managed fields (`Id`, `CreatedAt`, `UpdatedAt`, `DeletedAt`). Register as singleton in `Program.cs`. [`PostMapper`](../../../apps/api/src/Api/Features/Posts/PostMapper.cs) is the reference.
- [ ] **Validators** in the feature folder: one `AbstractValidator<TRequest>` per request DTO. `RuleLevelCascadeMode = CascadeMode.Stop`. Reference the entity's nested `Constraints` for length limits and patterns (e.g. `MaximumLength(Post.Constraints.MaxTitleLength)`). Auto-discovered via `AddValidatorsFromAssemblyContaining<Program>()` — no manual registration. [`CreatePostRequestValidator`](../../../apps/api/src/Api/Features/Posts/CreatePostRequestValidator.cs) is the reference.
- [ ] **Endpoint filter**: every POST / PUT / PATCH handler with a request body must be decorated `.AddEndpointFilter<ValidationEndpointFilter<TRequest>>()`. The filter runs **before** any handler logic — including 404 lookups and ETag preconditions — so a malformed body never burns a DB roundtrip.
- [ ] **Generate the migration**: `dotnet ef migrations add Add<Entity> --project apps/api/src/Api --output-dir Data/Migrations`.
- [ ] **Hand-add the UPDATE trigger to the migration's `Up()`** if the entity implements `ITimestamped`. The trigger function `set_timestamps_on_update()` already exists from `InitialCreate`; new tables just need their own `CREATE TRIGGER`:

  ```csharp
  migrationBuilder.Sql(@"
      CREATE TRIGGER <tablename>_set_timestamps_on_update
      BEFORE UPDATE ON ""<TableName>""
      FOR EACH ROW EXECUTE FUNCTION set_timestamps_on_update();
  ");
  ```

  And the matching drop in `Down()`:
  ```csharp
  migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS <tablename>_set_timestamps_on_update ON ""<TableName>"";");
  ```

  Why hand-add it? Triggers protect against non-EF writers (Hangfire jobs, serverless functions, raw SQL maintenance) leaving `UpdatedAt` stale or accidentally mutating `CreatedAt`. EF Core doesn't auto-generate trigger SQL, so the agent adding the entity owns it. Skipping it isn't a build failure — it's a future-confusing-bug.

- [ ] **Tests** in `Api.Tests.Integration/` exercising the endpoints. **One file per resource** (e.g. `PostsEndpointsTests`), grouped internally by HTTP verb. Everything that's observable at the API boundary — CRUD, ETag conditional requests, soft-delete behaviour — lives in that single file, next to the verb it concerns. Don't split out per-concern files (no `PostsEtagTests`, no `PostsSoftDeleteTests`); see [`PostsEndpointsTests`](../../../apps/api/tests/Api.Tests.Integration/PostsEndpointsTests.cs) for the layout. The exception is concerns that *bypass* the API — e.g. DB-level safety nets that simulate non-EF writers — which belong in their own file like [`TimestampsDbSafetyNetTests`](../../../apps/api/tests/Api.Tests.Integration/TimestampsDbSafetyNetTests.cs) (different boundary, different file). For `ISoftDeletable` entities, prove from the API that the row survives + the query filter hides it (peek with `IgnoreQueryFilters()`) — `PostsEndpointsTests.Delete_KeepsRowInDb_WithDeletedAtPopulated` is the pattern. **Per-test cleanup must use `IgnoreQueryFilters()`** when calling `ExecuteDeleteAsync()` on soft-deletable tables, otherwise soft-deleted rows from previous tests accumulate.

`Program` is a `public partial class` solely so `WebApplicationFactory<Program>` can reach it from the integration test project. **Do not delete that declaration** at the bottom of `Program.cs`.

### ETags & conditional requests

Single-resource endpoints over `ITimestamped` entities follow RFC 7232. The pattern (see [`PostsEndpoints`](../../../apps/api/src/Api/Features/Posts/PostsEndpoints.cs) for a worked example):

- **GET `/<resource>/{id}`** — always set the `ETag` response header. Honour `If-None-Match` and return `304 Not Modified` (empty body) on match.
- **POST `/<resource>`** — set `ETag` on the `201 Created` response so the client can use it for subsequent writes without a round trip.
- **PUT / DELETE `/<resource>/{id}`** — require `If-Match`. Return `428 Precondition Required` when the header is absent, `412 Precondition Failed` (with the current ETag on the response) when it doesn't match. On success, set the new `ETag` on the response.

The two helpers do all the work:

```csharp
// GET /<resource>/{id}
var etag = ETag.From(entity);
if (ConditionalRequest.EvaluateRead(http, etag) is { } notModified)
{
    return notModified;
}
return TypedResults.Ok(entity.ToResponse());

// PUT / DELETE /<resource>/{id}
if (ConditionalRequest.EvaluateWrite(http, ETag.From(entity)) is { } preconditionFailure)
{
    return preconditionFailure;
}
// ... apply the change, then:
ConditionalRequest.SetETagHeader(http, ETag.From(entity));
```

**Always pair the runtime helper with the OpenAPI marker** so the published contract matches what the runtime does. Three extensions in [`ConditionalRequestOpenApi`](../../../apps/api/src/Api/Http/ConditionalRequestOpenApi.cs):

```csharp
group.MapGet("/{id:guid}",   GetById).Produces(304).WithConditionalRead();
group.MapPost("/",           Create).WithEtagResponseHeader();
group.MapPut("/{id:guid}",   Update).WithConditionalWrite();
group.MapDelete("/{id:guid}", Delete).WithConditionalWrite();
```

The operation transformer reads the marker and stamps the spec with `ETag` response headers on the right status codes (200/201/304/412 depending on kind) and the `If-Match` / `If-None-Match` request parameters. Forgetting the marker means a future TS client won't know it can send those headers — `OpenApiSpecTests` is the regression guard.

**Order of checks** (mirror this in your endpoint):

1. Request body validation (400) — handled by `ValidationEndpointFilter<TRequest>` via `.AddEndpointFilter<...>()`. Runs before the handler body, so a malformed request never reaches the resource lookup.
2. Resource lookup (404) — no point checking preconditions on a non-existent resource.
3. `EvaluateWrite` (428 / 412) — precondition check.
4. Apply the change, refresh the ETag header on the response.

Why a strong tag from `UpdatedAt`? `AuditInterceptor` truncates `UpdatedAt` to microsecond precision so the in-memory value matches what Postgres stores in `timestamptz`. That stability is what makes the tag round-trip cleanly — without it, `If-Match` would always fail on the next request. Weak tags are explicitly forbidden for `If-Match` (RFC 7232 §3.1) so strong is the only option for the write path.

Collection endpoints (`GET /<resource>`) intentionally don't emit ETags — collection state is much harder to summarise cheaply, and the boilerplate doesn't need it yet.

When you add a new single-resource endpoint, **integration tests must cover** at minimum: `ETag` present on GET/POST/PUT, `304` on matching `If-None-Match`, `428` on PUT/DELETE without `If-Match`, `412` on PUT/DELETE with a stale `If-Match`. These tests live alongside the resource's CRUD tests in one file per resource — [`PostsEndpointsTests`](../../../apps/api/tests/Api.Tests.Integration/PostsEndpointsTests.cs) is the reference layout (tests grouped by verb, ETag scenarios next to their CRUD counterparts).

### OpenAPI spec

The native ASP.NET 10 OpenAPI generator publishes `/openapi/v1.json` in all environments. The spec is the contract for any downstream client (current or future TS code), so it must reflect what the runtime actually enforces.

[`FluentValidationSchemaTransformer`](../../../apps/api/src/Api/Validation/FluentValidationSchemaTransformer.cs) reflects validator rules into the generated schema. Today it propagates:

- `required` — from `NotEmpty()` / `NotNull()`
- `maxLength` / `minLength` — from any `ILengthValidator` (`MaximumLength`, `MinimumLength`, `Length(min, max)`)
- `pattern` — from `Matches(...)` (`IRegularExpressionValidator`)

If you add a new rule type to a validator, **confirm it shows up in the spec** — either by extending the transformer and adding a case to [`OpenApiSpecTests`](../../../apps/api/tests/Api.Tests.Integration/OpenApiSpecTests.cs), or by accepting that the rule won't be advertised to clients (rare; document it in the PR if so). Property-name mapping is camelCase-of-the-CLR-name; revisit the transformer if you ever introduce `[JsonPropertyName]` on a DTO.

### Running the API locally

```powershell
# Default profile — uses ports from launchSettings.json (5084 http / 7199 https).
# Fine for solo work; will clash if another agent is already running the API.
dotnet run --project apps/api/src/Api
```

**If you're running in a worktree (`.claude/worktrees/...`), assume another agent might also be running the API.** Override the ports so the kernel picks free ones:

```powershell
# Bind to a random free port. ASP.NET prints the bound URL to stdout.
# Use 127.0.0.1, NOT localhost — Kestrel rejects `localhost:0` with
# "Dynamic port binding is not supported when binding to localhost".
dotnet run --project apps/api/src/Api --urls "http://127.0.0.1:0"
```

After launch you'll see something like:

```
Now listening on: http://127.0.0.1:54321
```

Grab that port and use it for the rest of the validation steps. Kill the server with Ctrl+C when done.

### Hitting the running API

```powershell
# Replace 54321 with the port the server actually bound to.
Invoke-RestMethod http://localhost:54321/hello
Invoke-RestMethod http://localhost:54321/health
Invoke-RestMethod http://localhost:54321/health/live
Invoke-RestMethod http://localhost:54321/health/ready
```

Or with curl (works on Windows 10+ and in CI):

```bash
curl -s http://localhost:54321/hello
```

For new endpoints you add, hit them with realistic input and confirm the response body and status code match what you intended.

---

## 2 — Test discipline

> **How** to write good tests (red-green-refactor loop, behavior vs implementation, when to mock) lives in the [`tdd`](../tdd/SKILL.md) skill. This section covers **where** tests live and **when** they must be added in this repo.

**The rule: behavior changes require test changes.** Concretely:

- Added a new endpoint? → integration test for it in `apps/api/tests/Api.Tests.Integration/`.
- Added a new service / handler / validator / pure function? → unit test for it in `apps/api/tests/Api.Tests.Unit/`.
- Changed the *behavior* of existing code? → update the corresponding test(s) so they assert the new behavior.
- Removed code? → remove the now-orphan tests for it.
- Refactor with no behavior change? → tests should still pass without modification. If they don't, the refactor changed behavior — re-classify and either fix the regression or update tests with justification.

You can skip writing a test only if you can explain *why* the change is genuinely untestable (rare). If you skip, call it out explicitly in the PR description.

### What goes where

| Test type | Lives in | Touches | Examples |
|---|---|---|---|
| **Unit** | `Api.Tests.Unit/` | One class/function, no I/O, no DI container | Pure functions, validators, mappers, in-memory rules |
| **Integration** | `Api.Tests.Integration/` | Real ASP.NET host via `WebApplicationFactory<Program>` over `HttpClient` | Endpoint contracts, middleware, DI wiring, eventually DB-backed flows |

When real dependencies land (Postgres, etc.), spin them up via [Testcontainers](https://dotnet.testcontainers.org/) inside the integration test project. Do **not** mock the database with `EF Core In-Memory` or hand-rolled fakes — mocks drift from production behavior. Reference existing integration tests in `Api.Tests.Integration/` for the established patterns.

### Test files: one per boundary, not per concern

Integration tests are organised by the boundary they exercise, not by the cross-cutting concern they cover:

- **One file per API resource.** `PostsEndpointsTests` owns every behaviour that's observable at the `/posts` boundary — CRUD, ETag preconditions, soft-delete visibility, validation, the lot. Group tests inside the file by HTTP verb (`GET /posts`, `GET /posts/{id}`, `POST`, `PUT`, `DELETE`) so each endpoint's full contract reads top-to-bottom. Resist the urge to split out `PostsEtagTests`, `PostsValidationTests`, `PostsSoftDeleteTests` — those would scatter related behaviour across files and force a reviewer to grep across multiple files to understand a single endpoint.
- **Separate file when the boundary is different.** [`TimestampsDbSafetyNetTests`](../../../apps/api/tests/Api.Tests.Integration/TimestampsDbSafetyNetTests.cs) is its own file because it explicitly bypasses the API (and the EF interceptor) to verify guarantees at the *database* boundary — column defaults and UPDATE triggers that protect against non-EF writers. Different boundary → different file. The same applies to anything that tests cross-resource flows, infrastructure setup, etc.

The rule of thumb: ask "what's the boundary I'm asserting against?" If it's the same boundary as an existing file, the test goes in that file.

### Test code conventions (this repo)

- xUnit v3 on the Microsoft Testing Platform runner.
- Use `[Theory]` + `[InlineData]` for table-driven cases (see [HealthEndpointTests.cs](../../../apps/api/tests/Api.Tests.Integration/HealthEndpointTests.cs)).
- When awaiting anything that takes a `CancellationToken`, pass `TestContext.Current.CancellationToken`. The xUnit analyzer (`xUnit1051`) flags missing tokens — treat its warnings as errors.
- One class per unit under test, one class per endpoint or feature for integration.
- Tests assert behavior, not implementation. Name them after what they prove (`Get_Hello_ReturnsExpectedMessage`), not the method they call.

### Running tests

```powershell
# Everything (the canonical command)
dotnet test --solution apps/api/Api.slnx

# Just unit (fast feedback loop — sub-second)
dotnet test apps/api/tests/Api.Tests.Unit

# Just integration
dotnet test apps/api/tests/Api.Tests.Integration

# With coverage (cobertura XML in TestResults/)
dotnet test --solution apps/api/Api.slnx --collect:"XPlat Code Coverage"
```

⚠️ `dotnet test apps/api/Api.slnx` (without `--solution`) errors out in MTP mode. Use `--solution` for the whole solution; project paths work fine without it.

---

## 3 — Pre-PR validation checklist (MANDATORY)

Run all of these in order before `gh pr create`. If any step fails, **fix the issue and re-run from the top** — don't paper over it. Then paste the filled-in report (below) into the PR body.

### Steps

1. **Tests exist for what you changed.** Review your diff. For each changed file under `src/Api/`, confirm there's a corresponding new/modified test. If you skipped tests deliberately, write down why.
2. **Build is clean.**
   ```powershell
   dotnet build apps/api/Api.slnx --configuration Release
   ```
   Required: `0 Warning(s), 0 Error(s)`. Warnings are not OK — fix or justify.
3. **All tests pass.**
   ```powershell
   dotnet test --solution apps/api/Api.slnx --configuration Release --no-build
   ```
   Required: `failed: 0`. Note the total test count — if it dropped from the previous run on `main`, explain why.
4. **App boots clean.** Launch with the random-port override, confirm it starts without errors, then Ctrl+C.
   ```powershell
   dotnet run --project apps/api/src/Api --urls "http://127.0.0.1:0"
   ```
   Required: see `Now listening on: http://127.0.0.1:<port>`. No stack traces.
5. **Touched/new endpoints respond as expected.** With the app still running from step 4 (or relaunch), hit each affected endpoint and capture the response.
   ```powershell
   Invoke-RestMethod http://localhost:<port>/<your-endpoint>
   ```
   Required: response body and status match what your change intended. If you only changed internals (no endpoint surface change), say so.

### Report template (paste into the PR body)

```markdown
## Validation report

- **Tests covering this change:** <list of new/modified test files, or "N/A — explain">
- **Build:** `dotnet build apps/api/Api.slnx --configuration Release` → 0 warnings, 0 errors
- **Tests:** `dotnet test --solution apps/api/Api.slnx --configuration Release --no-build` → <total> passed, 0 failed
- **App boot:** `dotnet run --urls "http://127.0.0.1:0"` → bound to port <port>, no errors
- **Endpoint check:**
  - `GET /<endpoint>` → <status> + <one-line summary of body>
  - <repeat for each touched endpoint, or "N/A — no endpoint surface change">
```

If any step is N/A, say so explicitly with the reason — never just delete the line. The reviewer needs to know you considered each step.
