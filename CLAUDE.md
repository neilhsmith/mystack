# CLAUDE.md

Working instructions for this repo. [docs/architecture.md](docs/architecture.md) is the founding
document — the stack, the scope boundary and the decisions behind all of this. **Read it before
proposing anything.** If something there isn't built yet, it isn't built; don't invent an
alternative structure.

## What exists today

The foundation only: toolchain, CI gate, `compose.yaml`. No .NET projects yet — `server/auth`'s
host skeleton is next. Keep architecture §7's inventory ticked as things land; it is the honest
answer to "what is built?".

## Layout

Split by **ecosystem**, not by role:

- **`server/`** — everything .NET (`api`, `auth`, `shared` libraries, `Directory.*.props`,
  `MyStack.slnx`). Nothing outside `server/` is C#.
- **`apps/`** — JavaScript/TypeScript *applications*.
- **`packages/`** — shared JavaScript/TypeScript only. Never .NET.
- **`docs/`** — planning docs. Never the repo root.

The two ecosystems share no code. The only contract between them is the OpenAPI document
`server/api` exports. Wanting to share anything else is a design smell to raise, not to solve.

A `server/shared/` library needs all three: identical in both apps, low-churn, no domain knowledge.
Exactly three qualify (`MyStack.Jobs`, `MyStack.Email`, `MyStack.Observability`) and the list is
closed. Everything else starts duplicated.

## Commands

```bash
dotnet build server/MyStack.slnx     # build
dotnet test server/MyStack.slnx      # test
dotnet csharpier format .            # format; CI runs `csharpier check .`
docker compose up -d                 # postgres + mailpit
docker compose --profile otel up -d  # ... plus the telemetry dashboard
```

## Conventions

- **C#:** file-scoped namespaces, nullable + implicit usings on, warnings-as-errors, CSharpier.
- **TypeScript:** ESLint + Prettier, strict `tsconfig`, packages named `@mystack/*`.
- **Packages:** pnpm for JS; central NuGet versions in `server/Directory.Packages.props` — never an
  inline `Version=` on a `<PackageReference>`.
- **Secrets:** never committed. `.env` (gitignored) and user-secrets locally.
- **Comments earn their place.** Write one only for a durable "why" — an invariant, a non-obvious
  constraint, a deliberate trade-off. Never narrate what the code does. Never record why we chose a
  structure in conversation; that belongs in the PR.

### Non-negotiable

- **Expected failures are returned, not thrown.** Not-found, forbidden, conflict, validation — all
  returned as ProblemDetails. Exceptions are for the genuinely exceptional and become a 500.
- **ProblemDetails (RFC 9457) for every error.** No ad-hoc error JSON, anywhere.
- **Authorization is declared on the endpoint**, never buried in the handler. Per-row rules live in
  the handler and are always tested.
- **Validation lives beside the endpoint** in a `Validator<T>`. Invalid requests never reach the
  handler.
- **Keep EF entities off the wire.** Endpoints return DTOs.
- **No password grant, in any environment.** Not for a dev client, not for an HTTP-client
  collection. Authorization code + PKCE only.
- **No tokens in localStorage or client JS.** The BFF holds them in an httpOnly cookie.
- **User input that could be PII never travels in a URL.** Filter and search text goes in a POST
  body; anything sensitive in a logged body is `[Redact]`-masked.
- **Security headers on every response.**
- **Generated files are never hand-edited.** Contract changes → re-export the spec → regenerate.

## Tests

Tests are part of the change, at every level the change touched — a full-stack feature is not done
with only API tests. Every protected endpoint tests its full matrix: 401 anonymous, 403 wrong
scope, 403 right scope but missing permission, the happy path, validation failures, and
not-found/ownership. A job is tested from both ends — that it was enqueued, and that running it
produced its side effect — never by asserting the queue library works.

Fixing tests a change touches is expected; updating and deleting them is fine. Weakening an
assertion to reach green is not.

## Delivery

- **Conventional commits**, always: `type(scope): subject`. Types: `feat`, `fix`, `docs`, `test`,
  `refactor`, `perf`, `build`, `ci`, `chore`. Scope is the area touched (`auth`, `api`, `web`,
  `jobs`, `email`, `ci`, `docs`) and is omitted when the change is repo-wide. Subject is
  imperative and lower-case. Breaking changes get a `!` and a `BREAKING CHANGE:` footer.
- **One concern per PR.** One thing, reviewable in one sitting, leaving `main` green and
  deployable.
- **Branch, then PR.** `main` is protected: no direct pushes, the `gate` check must pass, squash
  merge, branch deleted on merge.
- **The PR body becomes the squash commit message**, so `main`'s history is the PR bodies. It says
  what changed, why, and what it deliberately doesn't do — describing the final state rather than
  the plan, which means rewriting it before merging (`/ready-to-merge`).
- **Docs are updated in the same PR as the code.** There is no docs catch-up PR.
