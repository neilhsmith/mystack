# CLAUDE.md

Working instructions for this repo. [docs/architecture.md](docs/architecture.md) is the founding
document — the stack, the scope boundary and the decisions behind all of this. **Read it before
proposing anything.** If something there isn't built yet, it isn't built; don't invent an
alternative structure.

## What exists today

The foundation, `server/auth`'s host skeleton — Identity over EF Core/Postgres, migrations, health
checks, security headers — `server/shared/MyStack.Observability` (OTel traces/metrics/logs over
OTLP, `[Redact]`, the `act.sub` enricher, envelope request logging), the OpenIddict server:
authorization code + PKCE, refresh tokens, the sign-in page, and the `auth.sign_ins` /
`auth.oauth.grants` counters ([docs/auth.md](docs/auth.md)) — and messaging:
`server/shared/MyStack.Messaging` (Wolverine over RabbitMQ, per-app queues and `wolverine_<app>`
envelope schemas, retry→dead-letter policy) plus the `server/worker` deployable, with auth's daily
token-prune flowing through the broker — and seeding: one safe always-on `Database:Seed` pass,
roles/scopes from code, clients + accounts from config, advisory-locked seed-before-serve, plus
the `bruno/` collection driving the PKCE flow against the seeded client — and email:
`server/shared/MyStack.Email` (`IEmailSender` over SMTP/MailKit, the `SendEmail` contract, the
renderer seam, the `email.sends` counter), with the worker delivering published `SendEmail`
messages to Mailpit locally — and the account flows: register + email confirmation, forgot/reset
password, change password + notification, every email published through the Wolverine EF outbox
to the worker's queue, anti-enumeration throughout, the four `auth.*` account counters, and the
`bruno/Auth/Account` folder driving it all by hand — and permission overrides: per-user
grant/deny rows with optional expiry, minted into `perm`/`perm_deny` access-token claims on
every issuance, the strings opaque to auth — and the wider token surface: userinfo (scope-gated,
agreeing with the id token by construction), the client-credentials grant with the seeder's
`Machine` client shape, and introspection for confidential callers only — and the protocol
extras: the device authorization grant (the `Device` client shape, `/connect/device`, the
signed-in `/connect/verify` approval page), PAR with its per-client
`RequirePushedAuthorizationRequests` opt-in, and back-channel logout — per-client
`BackchannelLogoutUri` config and signed logout tokens POSTed to every registered client when a
session ends — and the account-surface guards: IP-partitioned rate limiting over the
credential/email endpoints, timing decoys on the anti-enumeration miss paths, `no-store` on
rendered pages, the root/error/signed-out/access-denied pages with Accept-split error shaping
(browsers get the error page, APIs keep ProblemDetails), the end-session confirmation page, and
remember-me — and the conformance pass: the OpenID Foundation Basic OP plan run from the
replayable `conformance/` harness (not CI), its findings — per-client PKCE, the `name`-claim
email leak, `email_verified`, `prompt=consent`, discovery truthfulness — fixed and pinned by
tests — and
`server/shared/MyStack.Contracts`: the wire vocabulary (`AuthRoles`, `AuthClaims`, `ApiScopes`)
spelled once for every app that speaks it — and the JS workspace: pnpm + Prettier at the root,
`packages/ui` holding the shadcn (Base UI) components imported wholesale and the shared
`light-dark()` theme tokens (`theme.css`) that both the future React apps and auth's rendered
pages style from — and the designed pages themselves: one `_Layout.cshtml` card shell, the
shadcn recipes as utility classes across all thirteen pages, light + dark from the OS with no
JavaScript, the committed `wwwroot/app.css` compiled by `pnpm build:css` and freshness-checked
by the gate, `style-src 'self'` on the pages policy, and the error-summary/`aria-invalid`
accessibility bar ([docs/auth.md](docs/auth.md) § The rendered pages).
[docs/auth-track.md](docs/auth-track.md) is the order the rest lands in;
[docs/deploy-track.md](docs/deploy-track.md) is the hosting and deployment plan.
Keep architecture §7's inventory ticked as things land; it is the honest answer to "what is
built?".

## Layout

Split by **ecosystem**, not by role:

- **`server/`** — everything .NET (`api`, `auth`, `shared` libraries, `Directory.*.props`,
  `MyStack.slnx`). Nothing outside `server/` is C#.
- **`apps/`** — JavaScript/TypeScript *applications*.
- **`packages/`** — shared JavaScript/TypeScript only. Never .NET.
- **`docs/`** — planning docs. Never the repo root.

The two ecosystems share no code. The only contract between them is the OpenAPI document
`server/api` exports. Wanting to share anything else is a design smell to raise, not to solve.

A `server/shared/` library is for what's genuinely shared and low-churn: infrastructure every host
wires the same way, and **wire vocabulary** — names spelled in more than one app's code (role
names, claim types, scope names, message contracts), one directory per topic in
`MyStack.Contracts`. Behavior, policies and DTOs stay local. Four qualify (`MyStack.Messaging`,
`MyStack.Email`, `MyStack.Observability`, `MyStack.Contracts`); everything else starts
duplicated. Permission strings stay out of Contracts: auth handles them as opaque data, and only
`server/api` names them.

## Commands

```bash
dotnet build server/MyStack.slnx     # build
dotnet test server/MyStack.slnx      # test — needs Docker; the suites run real containers
dotnet csharpier format .            # format; CI runs `csharpier check .`
scripts/dev up                       # postgres + rabbitmq (mgmt UI :15672) + mailpit, from .env
scripts/dev auth                     # auth on :5100 (AUTH_PORT), migrating + seeding on the way up
scripts/dev worker                   # worker on :5200 (WORKER_PORT), consuming its queue
scripts/dev auth --otel              # ... exporting telemetry (pair with `scripts/dev up --otel`)
scripts/dev init 2                   # .env for a second isolated stack — docs/local-dev.md
scripts/dev urls                     # where everything in this instance lives

dotnet ef migrations add <Name> --project server/auth/src --output-dir Data/Migrations

pnpm install                         # JS workspace deps
pnpm format                          # prettier over apps/ + packages/; CI runs `pnpm format:check`
pnpm typecheck                       # tsc --noEmit in every package
pnpm build:css                       # regenerate auth's committed wwwroot/app.css; CI diffs it
```

In a worktree with its own `.env`, always run the apps through `scripts/dev` — bare `dotnet run`
reads only `appsettings.Development.json` and silently attaches to the *default* instance's
Postgres and worker queue. [docs/local-dev.md](docs/local-dev.md) is the port map and the
multi-instance story; `dotnet ef` targets an instance via `scripts/dev exec dotnet ef …`.

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
  collection. Authorization code only; PKCE is mandatory for every public client (confidential
  clients may rely on their `nonce` instead — the RFC 9700 split).
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
  `worker`, `messaging`, `email`, `ci`, `docs`) and is omitted when the change is repo-wide.
  Subject is
  imperative and lower-case. Breaking changes get a `!` and a `BREAKING CHANGE:` footer.
- **One concern per PR.** One thing, reviewable in one sitting, leaving `main` green and
  deployable.
- **Branch, then PR.** `main` is protected: no direct pushes, the `gate` check must pass, squash
  merge, branch deleted on merge.
- **The PR body becomes the squash commit message**, so `main`'s history is the PR bodies. It says
  what changed, why, and what it deliberately doesn't do — describing the final state rather than
  the plan. The `describe-pr` skill writes it: run it when opening every PR, and again
  (`/describe-pr`) after pushing more work or before merging.
- **Docs are updated in the same PR as the code.** There is no docs catch-up PR.
