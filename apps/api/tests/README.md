# Api tests

Two test projects, separated by what they exercise:

- **`Api.Tests.Unit/`** — pure, in-process tests of individual classes/functions. No I/O, no DI container, no HTTP. Fast (millisecond range). Add tests here as services, validators, handlers, etc. land in `src/Api/`.
- **`Api.Tests.Integration/`** — boots the real ASP.NET host via [`WebApplicationFactory<Program>`](https://learn.microsoft.com/aspnet/core/test/integration-tests) and exercises the app end-to-end over `HttpClient`. As the stack grows (DB, auth, etc.), tests here will spin up real dependencies via [Testcontainers](https://dotnet.testcontainers.org/) rather than mocks.

Both projects use **xUnit v3** on the **Microsoft Testing Platform** runner.

## Run

```bash
# All tests
dotnet test apps/api/Api.slnx

# Just unit tests (fast feedback loop)
dotnet test apps/api/tests/Api.Tests.Unit

# Just integration tests
dotnet test apps/api/tests/Api.Tests.Integration

# With coverage (cobertura XML in TestResults/)
dotnet test apps/api/Api.slnx --collect:"XPlat Code Coverage"
```
