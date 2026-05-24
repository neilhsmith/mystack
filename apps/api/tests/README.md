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

- [`HelloEndpointTests`](Api.Tests.Integration/HelloEndpointTests.cs) — smoke test of `/hello`.
- [`HealthEndpointTests`](Api.Tests.Integration/HealthEndpointTests.cs) — `/health`, `/health/live`, `/health/ready` (the last exercises the Postgres ready check).
- [`PostsEndpointsTests`](Api.Tests.Integration/PostsEndpointsTests.cs) — CRUD happy paths, 404s, 400s for validation, "no side effect on rejected input" assertions.
- [`SoftDeleteTests`](Api.Tests.Integration/SoftDeleteTests.cs) — soft delete is observable as 404 + hidden from `GET /posts`, but the row persists with `DeletedAt` populated (verified via `IgnoreQueryFilters()`).
- [`TimestampsDbSafetyNetTests`](Api.Tests.Integration/TimestampsDbSafetyNetTests.cs) — bypasses the interceptor with raw SQL to prove the DB-level safety net (column defaults + UPDATE trigger) does what it says when non-EF writers come along.

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
