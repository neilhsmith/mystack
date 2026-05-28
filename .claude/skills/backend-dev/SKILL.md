---
name: backend-dev
description: Backend workflow for the ASP.NET API at `apps/api/`. Use when starting any task that touches `apps/api/` (source or tests) — adding endpoints or entities, writing EF Core migrations, adding/editing/removing tests, running the API locally, or validating changes before opening a PR. Covers feature-folder layout, entity conventions (timestamps, soft delete, xmin concurrency), the per-feature service layer returning ErrorOr<T>, how ETags and 409-on-concurrency are handled by middleware (so handlers stay ignorant), test organisation rules, the random-port launch trick, and the mandatory pre-PR validation checklist.
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
│   │   └── Posts/                        # Post entity, DTOs, mapper, validators,
│   │                                     #   PostsService (ErrorOr<T> returns), PostErrors
│   │                                     #   (centralised Error factories), MapPostsEndpoints
│   ├── Data/                             # cross-cutting data layer
│   │   ├── AppDbContext.cs
│   │   ├── DesignTimeDbContextFactory.cs
│   │   ├── ITimestamped.cs               # marker: opts entity into auto CreatedAt/UpdatedAt
│   │   ├── ISoftDeletable.cs             # marker: opts entity into soft delete (DeletedAt)
│   │   ├── AuditInterceptor.cs           # SaveChanges hook: timestamps + soft-delete intercept
│   │   └── Migrations/                   # EF Core generated
│   ├── Http/                             # cross-cutting HTTP layer
│   │   ├── EtagMiddleware.cs             # body-hash ETag + 304 on every GET (outside /health*)
│   │   ├── DbUpdateConcurrencyExceptionHandler.cs  # maps EF concurrency exception → 409 problem+json
│   │   └── ErrorResults.cs               # ErrorOr.Error → ProblemHttpResult adapter (single source of truth)
│   └── Validation/                       # cross-cutting validation layer
│       ├── JsonPropertyNaming.cs         # CLR → JSON property-name mapping (used by service-layer
│       │                                 #   validation to shape error keys for the wire)
│       └── FluentValidationSchemaTransformer.cs  # reflects validator rules into the OpenAPI schema
└── tests/
    ├── Api.Tests.Unit/                   # pure in-process tests
    └── Api.Tests.Integration/            # WebApplicationFactory<Program>-based
        └── Fixtures/                     # ApiTestFactory (Testcontainers)
```

**New features go under `src/Api/Features/<FeatureName>/`.** Keep cross-cutting concerns (DbContext, migrations, future Auth/Telemetry/etc.) at `src/Api/` root, not under `Features/`.

### Conventions for new entities

- **Timestamps:** implement `ITimestamped` from `Api.Data` to get `CreatedAt` / `UpdatedAt` set automatically by `AuditInterceptor` on every SaveChanges. **Do not** initialize the properties on the entity or assign them in endpoint handlers — the interceptor is the single source of truth, and it truncates to microsecond precision so API responses round-trip identically to the persisted row (no sub-microsecond drift between in-memory and Postgres `timestamptz`).
- **Concurrency control:** every entity gets Postgres's `xmin` system column as a shadow concurrency token automatically — convention loop in `AppDbContext.OnModelCreating` applies it, no marker interface and no entity property required. EF throws `DbUpdateConcurrencyException` on stale writes (two writers race load → save, the loser's `UPDATE WHERE xmin = <stale>` matches zero rows). [`DbUpdateConcurrencyExceptionHandler`](../../../apps/api/src/Api/Http/DbUpdateConcurrencyExceptionHandler.cs) — an `IExceptionHandler` registered globally — maps that to a `409 Conflict` problem+json response. Handlers stay ignorant: just call `await db.SaveChangesAsync(ct)` and let the middleware translate. The protection is "narrow race only" by design: it catches concurrent in-flight writes but does NOT protect against the broader lost-update class where a client edits stale state offline and submits much later — there's no `If-Match` precondition in this stack (see the "ETags via middleware" bullet below).
- **Soft delete:** implement `ISoftDeletable` from `Api.Data` to get a `DeletedAt` (nullable `DateTimeOffset`) column the `AuditInterceptor` populates whenever you call `db.<Entity>.Remove(...)`. The row is never hard-deleted by EF — a `DELETE` becomes an `UPDATE` setting `DeletedAt = now()`. A global query filter (applied automatically in `AppDbContext.OnModelCreating`) hides soft-deleted rows from every query; use `.IgnoreQueryFilters()` to opt out for admin/audit views. Add a convenience `public bool IsDeleted => DeletedAt is not null;` on the entity. Hard delete is still possible via raw SQL when truly needed (GDPR erasure, maintenance). **HTTP-wise**, a soft-deleted resource returns `404` on every verb, indistinguishable from never-existed — rationale in [`ISoftDeletable`](../../../apps/api/src/Api/Data/ISoftDeletable.cs)'s XML doc. Do not return 410.
- **Primary keys:** `Guid` initialized with `Guid.CreateVersion7()` — sortable, distributed-safe.
- **The current time:** inject `TimeProvider` (registered as `TimeProvider.System`) and call `GetUtcNow()`. Don't call `DateTimeOffset.UtcNow` directly — that ties the code to the wall clock and makes deterministic tests painful.
- **ETags via middleware:** [`EtagMiddleware`](../../../apps/api/src/Api/Http/EtagMiddleware.cs) buffers every `GET` response (outside `/health*`), hashes the body (SHA1, strong tag), sets the `ETag` header, and short-circuits to `304 Not Modified` on matching `If-None-Match`. **Endpoint handlers do nothing** — no per-route opt-in, no per-handler ETag computation. The tag is a body hash, not a row-version derivative, so it is intentionally NOT used for `If-Match` write preconditions (a body hash can't safely back lost-update detection). Writes don't honour `If-Match` at all; concurrency safety comes from the DB-level `xmin` check described in the "Concurrency control" bullet above.
- **Field constraints:** define max lengths, regex patterns, and any other field-level constraints as `public const` (or `public static readonly`) fields on a **nested `public static class Constraints`** on the entity. Reference them from EF fluent config, the FluentValidation validators, and any other layer that needs to know. One change, three layers (DB schema, runtime validation, OpenAPI spec). [`Post.Constraints`](../../../apps/api/src/Api/Features/Posts/Post.cs) is the worked example — callers read e.g. `Post.Constraints.MaxTitleLength`. The nesting is the convention: do NOT inline these constants directly on the entity, and do NOT split them into a sibling class — that breaks the "one place to look per entity" rule.
- **Request DTO validation runs inside the service, not at the endpoint.** One FluentValidation `AbstractValidator<T>` per request DTO, **co-located with the DTOs in `<Feature>Dtos.cs`** (e.g. [`PostDtos`](../../../apps/api/src/Api/Features/Posts/PostDtos.cs) holds the records *and* `CreatePostRequestValidator` / `UpdatePostRequestValidator`). Set `RuleLevelCascadeMode = CascadeMode.Stop` so each property reports one failure at a time. Validators are auto-discovered by `AddValidatorsFromAssemblyContaining<Program>()` in `Program.cs` and injected into the feature's service. The service calls `await validator.ValidateAsync(request, ct)` at the top of every write method; on failure it returns `List<Error.Validation>` with the JSON property path in `Error.Code`, which [`ErrorResults.ToProblem`](../../../apps/api/src/Api/Http/ErrorResults.cs) groups into the standard RFC 9457 validation envelope (same wire shape clients have always seen). Validation runs **before** the 404 lookup, so a malformed body never burns a DB roundtrip — and any non-HTTP caller (background job, internal service, integration test) gets the same guarantees as a request through the endpoint pipeline. See [`PostsService`](../../../apps/api/src/Api/Features/Posts/PostsService.cs)'s `CreateAsync` / `UpdateAsync` for the pattern.
- **Error responses are always `application/problem+json`.** `Program.cs` wires `AddProblemDetails` + `UseExceptionHandler` + `UseStatusCodePages` so unhandled exceptions, bare `Results.StatusCode(...)` returns, and any handler-built `Results.Problem` calls all share the same shape (RFC 7807 / 9457). A customizer adds `traceId` to every problem response. Registered `IExceptionHandler`s (currently just `DbUpdateConcurrencyExceptionHandler` → 409) run first; anything they don't handle falls through to the default 500. If you're returning an error from a handler, prefer routing service-returned `ErrorOr.Error` values through [`ErrorResults.ToProblem`](../../../apps/api/src/Api/Http/ErrorResults.cs) (see the "Service layer" bullet below). For middleware / handlers that don't go through a service, `Results.Problem(statusCode: ..., title: ...)` or `Results.ValidationProblem(...)` is fine — never `Results.BadRequest(new { error = "..." })` or other ad-hoc shapes; those bypass the customizer and break consumer parsing.
- **Service layer + `ErrorOr<T>` (the standard pattern):** every feature with a write surface has a `<Feature>Service.cs` in the feature folder that owns `AppDbContext` access *and* request validation. The service injects `IValidator<TRequest>` for each write DTO and runs validation at the top of the corresponding method — see the "Request DTO validation" bullet above for the pattern. Service methods return `ErrorOr<T>` for anything that can fail (or `IReadOnlyList<T>` / `Task<T>` for operations where "no result" or "empty" is a valid success). Endpoint handlers stay thin — they call the service and `.Match` over the result, routing the error path through [`ErrorResults.ToProblem`](../../../apps/api/src/Api/Http/ErrorResults.cs) which is the single source of truth for `ErrorType` → HTTP status → problem+json. The kind-to-status mapping (`NotFound`→404, `Conflict`→409, `Validation`→400 with `errors` bag, `Unauthorized`→401, `Forbidden`→403, `Failure`/`Unexpected`→500) and the validation aggregation (`Error.Code` = field path, `""` = global) all live there — do NOT invent statuses or build `ProblemDetails` inline in handlers. **Centralised error factories** live in `<Feature>Errors.cs` (e.g. [`PostErrors`](../../../apps/api/src/Api/Features/Posts/PostErrors.cs)): one static method per *business* failure mode, with stable machine `code` strings (`"posts.not_found"`) and user-safe `description` strings — internal detail (the actual exception, SQL state, file path) goes in logs keyed by `traceId`, never in `Error.Description`. (Validation errors don't go through the per-feature `Errors` class — they're built from the FluentValidation result inside the service, see `PostsService.ToValidationErrors`.) Services do NOT catch `DbUpdateConcurrencyException` — let the global `IExceptionHandler` map it to 409. Throw exceptions only for genuine surprises (NRE, infrastructure failure); use `Error` values for expected business outcomes. [`PostsService`](../../../apps/api/src/Api/Features/Posts/PostsService.cs) is the worked example, [`PostsEndpoints`](../../../apps/api/src/Api/Features/Posts/PostsEndpoints.cs) is the matching thin-handler reference.
- **Global rate limiter** runs after the error-shaping middleware (`AddRateLimiter` + `UseRateLimiter`). Per-IP fixed window, configurable via `RateLimiting:PermitLimit` and `RateLimiting:WindowSeconds` in `appsettings.{env}.json`. Defaults: strict in `appsettings.json` (100/60s, suitable for production), lenient in `appsettings.Development.json` (10000/60s, so tests and local dev don't trip it). `/health*` is exempt — don't break liveness probes. Rejected requests get `429 Too Many Requests` as RFC 7807 problem+json plus a `Retry-After` header. If you need to exempt or tighten a specific endpoint, the limiter's partitioner lives at the top of `Program.cs`; per-endpoint policies can be added via `.RequireRateLimiting("policyName")` once you have differentiated quotas.
- **DTO ↔ entity mapping:** one `[Mapper] partial class <Feature>Mapper` per feature folder (e.g. [`PostMapper`](../../../apps/api/src/Api/Features/Posts/PostMapper.cs)) using Mapperly. Source-generated — the mapping code is plain C# you can step through, no runtime reflection. Register as a singleton in `Program.cs`. Use `RequiredMappingStrategy.Target` on the `[Mapper]` attribute and `[MapperIgnoreTarget(nameof(Post.Id))]` etc. to silence warnings for framework-managed fields (Id, CreatedAt, UpdatedAt, DeletedAt) that the mapper must not touch.

### Adding a new entity — checklist

When you create a new DB-backed entity, walk this list:

- [ ] **Entity** in `src/Api/Features/<Feature>/<Entity>.cs`. Implements `ITimestamped` (timestamps), `ISoftDeletable` (soft delete), or both. `Id` is `Guid` with `Guid.CreateVersion7()` default. Do not initialize `CreatedAt` / `UpdatedAt` / `DeletedAt`. If you implement `ISoftDeletable`, add `public bool IsDeleted => DeletedAt is not null;`. Declare every field-level constraint inside a **nested `public static class Constraints`** on the entity (max lengths, regex patterns, value ranges) so EF config, validators, and the OpenAPI spec read from one source of truth — e.g. `Post.Constraints.MaxTitleLength`.
- [ ] **DbSet** added to `AppDbContext`: `public DbSet<MyEntity> MyEntities => Set<MyEntity>();`.
- [ ] **OnModelCreating** in `AppDbContext`: configure keys, max lengths (reference the nested constants — e.g. `HasMaxLength(MyEntity.Constraints.MaxTitleLength)`), unique indexes, FKs. Column defaults for `ITimestamped` timestamps, the global query filter for `ISoftDeletable`, AND the `xmin` shadow concurrency token are all applied automatically by the convention loops — don't repeat them per entity.
- [ ] **DTOs + validators** in the feature folder (`<Entity>Dtos.cs`): one request record per write operation (`Create…Request`, `Update…Request`), one response record (`<Entity>Response`), and one `AbstractValidator<TRequest>` per write DTO in the *same* file. `RuleLevelCascadeMode = CascadeMode.Stop`. Reference the entity's nested `Constraints` for length limits and patterns (e.g. `MaximumLength(Post.Constraints.MaxTitleLength)`). Validators are auto-discovered via `AddValidatorsFromAssemblyContaining<Program>()` — no manual registration. [`PostDtos`](../../../apps/api/src/Api/Features/Posts/PostDtos.cs) is the reference.
- [ ] **Mapper** in the feature folder: `[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)] public partial class <Entity>Mapper`. Declare `partial PostResponse ToResponse(Post source)`, `partial Post ToEntity(CreatePostRequest source)`, `partial void Apply(UpdatePostRequest source, Post target)`. Use `[MapperIgnoreTarget(...)]` on `ToEntity`/`Apply` for framework-managed fields (`Id`, `CreatedAt`, `UpdatedAt`, `DeletedAt`). `xmin` is a shadow property so it doesn't appear on the entity and needs no ignore. Register as singleton in `Program.cs`. [`PostMapper`](../../../apps/api/src/Api/Features/Posts/PostMapper.cs) is the reference.
- [ ] **Error factories** in `src/Api/Features/<Feature>/<Entity>Errors.cs` — a `public static class <Entity>Errors` whose methods return `ErrorOr.Error` values (`Error.NotFound(code: "...", description: "...")` etc.). One method per *business* failure mode (NotFound, conflict, forbidden, …) — request-shape validation errors are built by the service from the FluentValidation result, not declared here. `code` is a stable machine string clients may key off (`"posts.not_found"`); `description` is always user-safe. Even if the only failure today is `NotFound`, create the file — that's where future business-rule errors land. [`PostErrors`](../../../apps/api/src/Api/Features/Posts/PostErrors.cs) is the reference.
- [ ] **Service** in `src/Api/Features/<Feature>/<Entity>Service.cs`. Constructor takes `AppDbContext`, `<Entity>Mapper`, and one `IValidator<TRequest>` per write DTO. Write methods call `await validator.ValidateAsync(request, ct)` first; on failure they return `List<Error.Validation>` built from the FluentValidation result via `JsonPropertyNaming.ToJsonPath` (so error codes match the on-the-wire field names). Read methods return `Task<IReadOnlyList<TEntity>>` when "empty = success" (no failure mode) or `Task<ErrorOr<TEntity>>` otherwise. Write methods return `Task<ErrorOr<TEntity>>` (or `Task<ErrorOr<Deleted>>` for delete — use ErrorOr's `Result.Deleted` marker, not `Success`). Do NOT catch `DbUpdateConcurrencyException` — the global handler maps it to 409. Don't compute ETags (middleware does that). Return business errors via the centralised `<Entity>Errors` factories — never `Error.NotFound(...)` inline. [`PostsService`](../../../apps/api/src/Api/Features/Posts/PostsService.cs) is the reference; note `ToValidationErrors` as the shared FluentValidation → `List<Error>` translator.
- [ ] **Endpoint handlers stay thin** — see [`PostsEndpoints`](../../../apps/api/src/Api/Features/Posts/PostsEndpoints.cs). Inject the service; call one service method; `.Match` over the result with the success branch building `TypedResults.Ok` / `Created` / `NoContent` and the error branch calling `errors.ToProblem()`. Return type is `Results<Ok<TResponse>, ProblemHttpResult>` (or the equivalent for `Created` / `NoContent`) so OpenAPI sees the success shape; advertise expected failure statuses with `.ProducesProblem(StatusCodes.Status404NotFound)` etc. on the endpoint registration. **Do not** import `AppDbContext` into the handler — that's the service's job.
- [ ] **Register the service in `Program.cs`** as `builder.Services.AddScoped<<Entity>Service>();`. Scoped because it shares the request's `AppDbContext`. Place it next to the existing `AddScoped<PostsService>()` line.
- [ ] **Register the feature in `Program.cs` under the `/v1` group**: call `v1.Map<Feature>Endpoints();` on the `var v1 = app.MapGroup("/v1");` already created near the bottom of `Program.cs`. Do NOT register against `app` directly — the API surface is versioned, only health checks and similar ops endpoints stay unversioned. The feature's own `MapGroup("/<resource>")` chains under the v1 group automatically; full route becomes `/v1/<resource>`.
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

- [ ] **Empty the spurious `xmin` AddColumn from the migration's `Up()` / `Down()`.** The `xmin` convention loop in `AppDbContext.OnModelCreating` declares a shadow property mapped to the Postgres system column `xmin`. EF doesn't know `xmin` already exists on every table, so it generates `migrationBuilder.AddColumn<uint>("xmin", "<TableName>", ...)` in `Up()` (and the matching `DropColumn` in `Down()`). Applying the `AddColumn` would fail because `xmin` is a system column. Replace both bodies with empty methods (or with a short comment explaining why) — the model snapshot still records the shadow property so future migrations diff correctly. See [`AddXminConcurrencyToken`](../../../apps/api/src/Api/Data/Migrations/20260524225152_AddXminConcurrencyToken.cs) for the pattern.

- [ ] **Tests** in `Api.Tests.Integration/` exercising the endpoints. **One file per resource** (e.g. `PostsEndpointsTests`), grouped internally by HTTP verb. Everything that's observable at the API boundary — CRUD, validation, ETag/304, soft-delete behaviour — lives in that single file, next to the verb it concerns. Don't split out per-concern files (no `PostsEtagTests`, no `PostsSoftDeleteTests`); see [`PostsEndpointsTests`](../../../apps/api/tests/Api.Tests.Integration/PostsEndpointsTests.cs) for the layout. The exception is concerns that *bypass* the API — e.g. DB-level safety nets that simulate non-EF writers — which belong in their own file like [`TimestampsDbSafetyNetTests`](../../../apps/api/tests/Api.Tests.Integration/TimestampsDbSafetyNetTests.cs) (different boundary, different file). For `ISoftDeletable` entities, prove from the API that the row survives + the query filter hides it (peek with `IgnoreQueryFilters()`) — `PostsEndpointsTests.Delete_KeepsRowInDb_WithDeletedAtPopulated` is the pattern. **Per-test cleanup must use `IgnoreQueryFilters()`** when calling `ExecuteDeleteAsync()` on soft-deletable tables, otherwise soft-deleted rows from previous tests accumulate.

`Program` is a `public partial class` solely so `WebApplicationFactory<Program>` can reach it from the integration test project. **Do not delete that declaration** at the bottom of `Program.cs`.

### ETags & concurrency (handled for you)

Two pieces of cross-cutting infrastructure mean endpoint handlers don't have to think about ETags or write races at all:

- [`EtagMiddleware`](../../../apps/api/src/Api/Http/EtagMiddleware.cs) runs after the rate limiter and before the endpoint pipeline. For every `GET` outside `/health*`, it buffers the response body, hashes it (SHA1, strong tag formatted as `"<hex>"`), sets the `ETag` response header, and short-circuits to `304 Not Modified` (with an empty body) on matching `If-None-Match`. Any handler the inner handler set on its own is preserved — the middleware only computes one when none is present.
- [`DbUpdateConcurrencyExceptionHandler`](../../../apps/api/src/Api/Http/DbUpdateConcurrencyExceptionHandler.cs) is registered as an `IExceptionHandler`. When EF throws `DbUpdateConcurrencyException` (the `xmin` token in `UPDATE WHERE xmin = <stale>` matched zero rows because another writer raced in), the handler emits `409 Conflict` problem+json via the same `IProblemDetailsService` everything else uses, so `traceId` lands on the body automatically.

The combined effect on endpoint shape — the handler delegates the data work to a service, the service returns `ErrorOr<T>`, and the error path goes through `ErrorResults.ToProblem` (see the "Service layer + `ErrorOr<T>`" bullet in Conventions):

```csharp
// GET /<resource>/{id} — middleware adds the ETag and handles If-None-Match → 304.
// PostsService.GetByIdAsync returns PostErrors.NotFound(id) when the row is missing
// (or soft-deleted, hidden by the global query filter); the adapter turns that into
// a 404 problem+json with the per-resource title.
var result = await service.GetByIdAsync(id, ct);
return result.Match<Results<Ok<TResponse>, ProblemHttpResult>>(
    entity => TypedResults.Ok(mapper.ToResponse(entity)),
    errors => errors.ToProblem());

// PUT /<resource>/{id} — same shape; concurrency races thrown inside the service's
// SaveChangesAsync are caught by DbUpdateConcurrencyExceptionHandler → 409, NOT by the
// service. Service stays oblivious, handler stays oblivious, the global handler owns it.
var result = await service.UpdateAsync(id, request, ct);
return result.Match<Results<Ok<TResponse>, ProblemHttpResult>>(
    entity => TypedResults.Ok(mapper.ToResponse(entity)),
    errors => errors.ToProblem());
```

**Order of checks** (mirror this in your endpoint):

1. Endpoint handler calls the service — that's the only thing the handler does for write verbs (besides the response `.Match`).
2. Inside the service: request body validation (400) runs first via the injected `IValidator<T>`; failures short-circuit before the DB lookup with a `List<Error.Validation>`. On valid input, the service does the 404 lookup, applies the change, calls `await db.SaveChangesAsync(ct)`, and returns `ErrorOr<T>`. A concurrent writer's race surfaces as `DbUpdateConcurrencyException` → 409 from the global exception handler. No try/catch in the service or the handler.
3. Handler `.Match`es over the result, building the success result with `TypedResults` and routing errors through `errors.ToProblem()` — `ErrorResults` groups validation errors into the standard envelope, picks the status for everything else.

What you give up (and why this is fine for now): a body-hash ETag can't safely back `If-Match` preconditions for safe writes (different bytes can hash the same, and the hash has no relationship to the row version). So this stack does NOT support cooperative clients sending `If-Match` to detect their own staleness — a stale-edit lost-update surfaces as the next writer's `409` *only* if it's an in-flight race. A client that fetched yesterday, edited offline, and submitted today will silently overwrite intermediate changes. Acceptable for personal-boilerplate scope; if/when a downstream consumer needs `If-Match`, see "scaling up" below.

When you add a new GET endpoint, an integration test should cover at minimum: `ETag` present on a 200 response, `304` returned on matching `If-None-Match`. Concurrency-handler coverage is centralised in `ProblemDetailsTests` against the `/v1/diagnostics/throw-concurrency` probe — you don't need a per-resource 409 test.

**Scaling up:** if you ever need RFC 7232 `If-Match` preconditions (lost-update protection without relying on narrow in-flight races), the right path is to switch the ETag source from "body hash in middleware" to "xmin in handler" and reintroduce a small per-endpoint precondition helper. The `xmin` shadow concurrency token is already wired by convention, so the work is HTTP-side only — but it's a real chunk of code and it puts the per-endpoint ceremony back. Don't preemptively add it.

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
