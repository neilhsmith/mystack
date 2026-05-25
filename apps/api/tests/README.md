# Api tests

Two test projects, separated by what they exercise:

- **`Api.Tests.Unit/`** — pure, in-process tests of individual classes/functions. No I/O, no DI container, no HTTP. Fast (millisecond range). No Docker needed.
- **`Api.Tests.Integration/`** — boots the real ASP.NET host via [`WebApplicationFactory<Program>`](https://learn.microsoft.com/aspnet/core/test/integration-tests) against a throwaway Postgres container spun up by [Testcontainers](https://dotnet.testcontainers.org/). Exercises endpoints end-to-end over `HttpClient`. **Docker must be running on the host.**

Both projects use **xUnit v3** on the **Microsoft Testing Platform** runner.

## Testcontainers shape

One Postgres container per test-assembly run, shared across all integration test classes via [`ICollectionFixture<ApiTestFactory>`](Api.Tests.Integration/Fixtures/IntegrationTestCollection.cs). `ApiTestFactory`:

- Starts a `postgres:16-alpine` container in `InitializeAsync`.
- Sets `ConnectionStrings__DefaultConnection` to the container's dynamic connection string (env var so it wins over `appsettings.Development.json`).
- Forces `UseEnvironment("Development")` so `Program` applies EF migrations on startup against the test container.
- Disposes the container at the end of the assembly run.

Per-test data reset happens in each test class's `IAsyncLifetime.InitializeAsync` via `ExecuteDeleteAsync()` on the relevant tables — see [`PostsEndpointsTests`](Api.Tests.Integration/PostsEndpointsTests.cs) for the pattern.

> **Soft delete cleanup gotcha:** for any `ISoftDeletable` table, the cleanup must use `.IgnoreQueryFilters().ExecuteDeleteAsync()` — otherwise soft-deleted rows from previous tests are hidden by the global query filter and never get wiped, accumulating across runs.

## Test classes

- [`HelloEndpointTests`](Api.Tests.Integration/HelloEndpointTests.cs) — smoke test of `/v1/hello`.
- [`HealthEndpointTests`](Api.Tests.Integration/HealthEndpointTests.cs) — `/health`, `/health/live`, `/health/ready` (the last exercises the Postgres ready check).
- [`PostsEndpointsTests`](Api.Tests.Integration/PostsEndpointsTests.cs) — full Posts contract: CRUD happy paths, 404s, validation 400s with "no side effect on rejected input" assertions, RFC 7232 conditional-request semantics (ETag on responses, 304 on `If-None-Match`, 428/412 on missing/stale `If-Match` for writes), and soft-delete observability (404 + hidden from list, row persists with `DeletedAt` populated via `IgnoreQueryFilters()`).
- [`ProblemDetailsTests`](Api.Tests.Integration/ProblemDetailsTests.cs) — cross-cutting guard for the RFC 7807 / 9457 error pipeline: unhandled exceptions come back as `application/problem+json` without leaking a stack trace, and every problem response (including handler-built 412 / 428) carries a `traceId`.
- [`OpenApiSpecTests`](Api.Tests.Integration/OpenApiSpecTests.cs) — regression guard for `/openapi/v1.json`: confirms FluentValidation rules flow through into the schema so downstream consumers see the same constraints the runtime enforces.
- [`TimestampsDbSafetyNetTests`](Api.Tests.Integration/TimestampsDbSafetyNetTests.cs) — bypasses the interceptor with raw SQL to prove the DB-level safety net (column defaults + UPDATE trigger) does what it says when non-EF writers come along.
- [`XminConcurrencyDbSafetyNetTests`](Api.Tests.Integration/XminConcurrencyDbSafetyNetTests.cs) — drives `DbContext` directly to prove the Postgres `xmin` system column is wired up as an EF concurrency token (the schema/convention side of the optimistic-concurrency story, independent of any endpoint's wiring).

## Run

```powershell
# All tests
dotnet test --solution ../Api.slnx

# Just unit tests (fast feedback, no Docker)
dotnet test Api.Tests.Unit

# Just integration tests (needs Docker)
dotnet test Api.Tests.Integration

# With coverage (cobertura XML in TestResults/)
dotnet test --solution ../Api.slnx --collect:"XPlat Code Coverage"
```
