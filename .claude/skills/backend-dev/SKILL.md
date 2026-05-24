---
name: backend-dev
description: Backend workflow for the ASP.NET API at `apps/api/`. Use when starting any task that touches `apps/api/` (source or tests), running the API locally, adding/editing/removing tests, or validating API changes before opening a PR. Covers app launch with collision-free ports, test discipline rules, and the mandatory pre-PR validation checklist.
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
├── src/Api/                              # the API itself
│   ├── Program.cs                        # entry point + endpoint mapping
│   ├── Api.csproj
│   └── Properties/launchSettings.json    # local debug ports (5084 / 7199)
└── tests/
    ├── Api.Tests.Unit/                   # pure in-process tests
    └── Api.Tests.Integration/            # WebApplicationFactory<Program>-based
```

`Program` is a `public partial class` solely so `WebApplicationFactory<Program>` can reach it from the integration test project. **Do not delete that declaration** at the bottom of `Program.cs`.

### Running the API locally

```powershell
# Default profile — uses ports from launchSettings.json (5084 http / 7199 https).
# Fine for solo work; will clash if another agent is already running the API.
dotnet run --project apps/api/src/Api
```

**If you're running in a worktree (`.claude/worktrees/...`), assume another agent might also be running the API.** Override the ports so the kernel picks free ones:

```powershell
# Bind to a random free port. ASP.NET prints the bound URL to stdout.
dotnet run --project apps/api/src/Api --urls "http://localhost:0"
```

After launch you'll see something like:

```
Now listening on: http://localhost:54321
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
   dotnet run --project apps/api/src/Api --urls "http://localhost:0"
   ```
   Required: see `Now listening on: http://localhost:<port>`. No stack traces.
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
- **App boot:** `dotnet run --urls "http://localhost:0"` → bound to port <port>, no errors
- **Endpoint check:**
  - `GET /<endpoint>` → <status> + <one-line summary of body>
  - <repeat for each touched endpoint, or "N/A — no endpoint surface change">
```

If any step is N/A, say so explicitly with the reason — never just delete the line. The reviewer needs to know you considered each step.
