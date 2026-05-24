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
