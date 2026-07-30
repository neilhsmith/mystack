# MyStack — founding document

The plan for the rebuild: what we're building, what we're deliberately **not** building, how the
repo is laid out, and the order we land it in. This is the document `CLAUDE.md` is derived from.

Read this before proposing anything. If something here isn't built yet, it isn't built — don't
invent an alternative structure.

---

## 0. Why we restarted

The previous attempt worked end to end, but it accumulated infrastructure faster than
understanding. Idempotency reservations, ETag/If-Match optimistic concurrency, three layers of
caching with their own metrics, a QueryKit-over-HTTP-QUERY filtering protocol that no codegen tool
understands — every one of those is defensible in isolation and none of them was needed. The cost
showed up as: mechanisms that need re-explaining every time you look at them, and pull requests too
large to review honestly.

The **job queue is the one that comes back**, and the distinction is worth being precise about,
because it's the whole test the rest of this document applies. Last time it was built with no
consumer — a mechanism looking for a use. This time it has one before it has a line of code: every
account email goes through it from day one (§3.3). And it isn't only additive — the old repo sent
account email inline and best-effort, swallowing failures, so a provider outage silently dropped a
confirmation email and the user's only recovery was guessing to press "resend". The rule was never
"no infrastructure". It is *no infrastructure without a current consumer*, and this one has one.

Three rules come out of that, and they outrank every technical preference below.

1. **Understandability is a feature.** If a mechanism has to be re-explained to its own author
   every time it comes up, it does not belong in the codebase yet. A boilerplate you can't hold in
   your head is not a boilerplate.
2. **No speculative infrastructure.** Every non-trivial mechanism needs a concrete problem that is
   *currently* hurting. "A real app might need this" is not a reason. §4 lists the deferred set with
   the trigger condition that would justify each one.
3. **One concern per PR.** A PR does one thing, is reviewable in one sitting, and leaves `main`
   green and deployable. That's a constraint on the *size* of a change, not on its position in a
   queue — §7 is an inventory to pull from, not a running order.

---

## 1. What this is

A reusable boilerplate for spinning up new apps.

| Deployable    | Stack                                            | Role                                                                                                          |
| ------------- | ------------------------------------------------ | --------------------------------------------------------------------------------------------------------------- |
| `server/auth` | .NET — ASP.NET Core + OpenIddict + Identity + EF | OAuth2/OIDC authorization server. Owns users, credentials, roles, and permission overrides. Issues JWTs.      |
| `server/api`  | .NET — ASP.NET Core + FastEndpoints + EF         | Resource server. Validates JWTs from `auth`. Business logic lives in endpoints. Every error is ProblemDetails. |
| `apps/web`    | TanStack Start (React)                           | BFF + SPA. Does the OIDC dance server-side, holds tokens in an httpOnly cookie, proxies the browser to `api`. |
| `apps/admin`  | TanStack Start (React) — **post-v1**             | Admin console: user search, role/permission administration, impersonation. Designed for, outside the v1 scope boundary. |

Request flow — commit this to memory:

```
Browser ──fetch /api/*──▶ apps/web (BFF)
                          │  reads httpOnly cookie → session → access token
                          │  attaches `Authorization: Bearer <jwt>`
                          ▼
                       server/api  ──validates JWT (authority = server/auth)──▶ 401/403 or 2xx
                          │  endpoint: scope policy → permission guard → validator → HandleAsync
                          ▼
                       endpoint logic → ProblemDetails on failure
```

The browser **never** sees a token. Login, refresh, and logout all happen in the BFF.

---

## 2. Repository layout

The split is by **ecosystem**, not by role. That's the rule that was muddy last time:

- **`server/` — everything .NET.** Applications and shared .NET libraries both. Nothing outside
  `server/` is C#.
- **`packages/` — shared JavaScript/TypeScript only.** Never .NET.
- **`apps/` — JavaScript/TypeScript *applications*.** The web BFF today; the admin console next.

`server/` sits at the root rather than under `apps/` on purpose: it keeps the pnpm workspace globs
(`apps/*`, `packages/*`) exactly aligned with the JS ecosystem, and lets the .NET solution own its
whole subtree. The asymmetry is the point — the two ecosystems are orchestrated by different tools.

**The two ecosystems share no code.** The only contract between them is the OpenAPI document
`server/api` exports, from which the TypeScript client is generated. If you find yourself wanting to
share something else across the boundary, that's a design smell to raise, not to solve.

```
mystack/
├─ server/                          # all .NET
│  ├─ api/
│  │  ├─ src/                       # MyStack.Api.csproj
│  │  └─ tests/                     # MyStack.Api.Tests.csproj
│  ├─ auth/
│  │  ├─ src/                       # MyStack.Auth.csproj
│  │  └─ tests/                     # MyStack.Auth.Tests.csproj
│  ├─ shared/                       # .NET libraries shared by api + auth
│  │  ├─ MyStack.Jobs/              # Hangfire setup, conventions, telemetry, test seam
│  │  ├─ MyStack.Email/             # IEmailSender + SMTP, message shape, renderers
│  │  └─ MyStack.Observability/
│  │     ├─ src/
│  │     └─ tests/                  # only if the lib has behavior worth testing
│  ├─ Directory.Build.props         # shared .NET config (nullable, warnings-as-errors, …)
│  ├─ Directory.Packages.props      # central NuGet versions — no loose <PackageReference Version>
│  └─ MyStack.slnx
├─ apps/
│  ├─ web/                          # TanStack Start BFF + SPA
│  └─ admin/                        # post-v1
├─ packages/
│  ├─ api-client/                   # generated from server/api's OpenAPI
│  ├─ bff/                          # shared BFF kit — extracted when apps/admin arrives
│  ├─ tsconfig/
│  └─ eslint-config/
├─ docs/
├─ .github/workflows/
├─ compose.yaml
├─ package.json
└─ pnpm-workspace.yaml
```

**When does something become a `server/shared/` library?** All three must hold: it is genuinely
identical in both apps, it is low-churn, and it carries no domain or feature knowledge. Duplicating
twenty lines is cheaper than a shared library you have to version in your head.

**Exactly three qualify, and the list is closed** until something new earns its way on:

- **`MyStack.Jobs`** — background work is stack-wide infrastructure by definition. `auth` consumes it
  for account email; `api` exercises it with a demo job in its first slice, which is also how it
  stays tested from both sides.
- **`MyStack.Email`** — `auth` is the only sender in v1, so this one is shared *ahead* of its second
  consumer, deliberately. Email is not an identity concern; putting it inside `auth` would say it
  was, and the move later is pure churn. Recorded as a knowing exception, not an oversight.
- **`MyStack.Observability`** — the primitives both apps need to look the same in a trace.

Anything else starts duplicated and gets extracted when the duplication actually hurts.

---

## 3. In scope for v1

The complete list. Anything not here is out until we decide otherwise.

### server/auth — the identity service

This is the project to get *right* and *finished*. It gets a dedicated design pass where every
screen it renders is designed, not scaffolded, and the whole project is walked through end to end
until it's fully understood. Nothing else in the stack is more expensive to redo.

- ASP.NET Core Identity over EF Core / Postgres. Owns users, passwords, roles.
- OpenIddict authorization server: **authorization code + PKCE** and **refresh tokens**
  (`offline_access`), from day one. No dev-only password grant, ever — that deferral is what made
  the last attempt un-shippable from its first commit.
- **The full protocol surface**, landed late in the auth track (steps 10–12): userinfo,
  introspection, **client credentials** for machine clients, the **device flow** with its
  verification page, **PAR**, and **back-channel logout**. A deliberate scope call: this is a
  boilerplate and a learning vehicle, so the server is complete, tested and understood rather
  than trimmed to what the BFF happens to use. The password grant stays out of all of it.
- Hosted, fully designed browser pages: sign in, register, forgot password, reset password, confirm
  email, device verification, error. No consent screen (D17). Full security-header set (CSP,
  framing, Permissions-Policy).
- Access tokens are JWTs carrying `role` claims plus per-user permission overrides (§3.1). Explicit,
  config-driven lifetimes.
- Account flows: **register, email confirmation, forgot/reset password, change password.**
  Anti-enumeration by default (generic 200s, no "that email doesn't exist").
- **Permission override store** (§3.1): per-user grant/deny rows with an optional expiry, written by
  the admin console, minted into tokens. Auth treats permission names as opaque strings.
- **Impersonation grant** (§3.2).
- **Every account email is enqueued, rendered, and actually delivered** — including locally (§3.3).
  Registering in development sends a real confirmation message to a real local inbox you open in a
  browser and click. No dev-only sender, no log-scraping, no flow that only works in production.
- **Config-driven seeding** (§3.4): roles and scopes materialized from code, OIDC clients and the
  bootstrap admin declared in configuration, sample accounts hard-gated to development. Idempotent,
  safe under concurrent instances, and complete before the app serves a request.

Deferred to a later PR, not v1: change-email, delete-account, grant/connection management, external
identity providers, MFA.

### server/api — the REST API

- Resource server: validates JWTs against `auth`'s discovery document. Never issues tokens.
- **FastEndpoints**, organized as vertical slices under `Features/<Area>/` — endpoint, request +
  validator, response DTO, entity + EF configuration, and the feature's permission declarations all
  in one folder.
- **ProblemDetails (RFC 9457) for every error**, configured once. Validation failures → 400 with
  per-field errors. Every problem carries `type`, `title`, `status`, `instance`, `traceId`.
- **FluentValidation** via FastEndpoints' `Validator<T>` — auto-discovered, runs before the handler.
- **Three-layer authorization** (§3.1): scope policy → permission guard → per-row rules.
- EF Core + Npgsql, snake_case, migrations checked in.
- **List endpoints:** paging, sorting, and filtering over an allow-listed property set. A list that
  takes user-supplied filter or search **text** is `POST /<resource>/search` with a JSON body, so
  PII never lands in a URL (access logs, referrers, browser history, span attributes). A list with
  no user-supplied text is a plain `GET` with query-string paging. Ordinary POST — no custom verb,
  so every codegen tool understands it.
- OpenAPI export (NSwag via FastEndpoints) — the contract the TS client is generated from.
- Security headers, health checks (`/health/live`, `/health/ready`).
- **Seeding on the same two-tier model as `auth`** (§3.4): whatever reference data the domain can't
  function without, plus sample rows that exist only in development. Same mechanics, written
  separately rather than shared.

### apps/web — the BFF + SPA

- TanStack **Start** (SSR + server functions/routes), **Router** (file-based), **Query** (server
  state), **Form**. shadcn/ui on **Base UI** + Tailwind.
- BFF: the OIDC code+PKCE dance, token exchange and refresh, a signed httpOnly session cookie, and a
  catch-all `/api/*` proxy that attaches the bearer token.
- Feature modules under `src/features/<name>/` own everything domain-shaped (server functions,
  query/mutation factories, components). `src/routes/` is thin wiring. `src/lib/` is domain-free
  infrastructure only.
- Data layer: TanStack Query over the generated typed fetch client. One error type, per-feature
  `queryOptions`/`mutationOptions` factories, loaders prefetch via `ensureQueryData`.
- Routes: `/`, `/login`, `/register`, a guarded layout, `/dashboard` (a real list off the API), and a
  minimal `/account`.

### Observability — in v1, not bolted on later

Logs and telemetry are load-bearing, not a nice-to-have. They stay.

- **Structured logging** in both .NET apps, with the W3C trace id correlated into every log line and
  every ProblemDetails response.
- **OpenTelemetry** traces + metrics with OTLP export, and the **standalone Aspire Dashboard**
  container as the local viewer (traces, metrics and structured logs in one UI). Opt-in in dev —
  a compose profile, not always-on. See D1 for why this isn't adopting Aspire.
- **`[Redact]` attribute** for masking sensitive request fields in logs and span attributes. This
  matters more now than before: filter and search values travel in POST bodies, so body logging is
  exactly where PII would leak. Until the masking machinery exists, request logging is
  envelope-only — method, path, status, duration; the machinery lands with its first body-logging
  consumer (`server/api`, whose POST-body filters are the reason to want bodies at all), and
  response bodies are never logged.
- Telemetry conventions, carried over because they were right: a span is a **unit of work**, not a
  function call; request bodies reach spans only through redaction; the resource identity groups
  `api` and `auth` as one product.

#### Metrics — what v1 counts

`MyStack.Observability` ships the pipeline and the host meters (ASP.NET Core, HTTP client, .NET
runtime, Npgsql). The domain metrics are **named here but land with the feature that emits them** —
a counter with no emitter is exactly the speculative infrastructure §0 rules out.

| Instrument                  | Tags                   | Lands with                    |
| --------------------------- | ---------------------- | ----------------------------- |
| `auth.sign_ins`             | `result`               | the sign-in page (OpenIddict) |
| `auth.oauth.grants`         | `grant_type`, `result` | the token endpoint (OpenIddict) |
| `auth.registrations`        | `outcome`              | account flows                 |
| `auth.email_confirmations`  | `outcome`              | account flows                 |
| `auth.password_resets`      | `stage`, `outcome`     | account flows                 |
| `auth.password_changes`     | `outcome`              | account flows                 |
| `jobs.enqueued`             | `job_type`             | `MyStack.Jobs`                |
| `jobs.executions`           | `job_type`, `outcome`  | `MyStack.Jobs`                |
| `email.sends`               | `outcome`              | `MyStack.Email`               |

Two rules make these safe and useful:

- **Tag values come from closed sets.** Never a user id, an email address or anything
  user-supplied — those belong on spans and logs, where cardinality costs nothing. One unbounded
  tag value ruins the time series it lives in.
- **The anti-enumeration flows are why several of these exist.** Register and forgot-password
  deliberately answer the same 200 whether the account exists or not, so the honest outcome is
  invisible in responses *by design* — the metric tag (`outcome: unknown_email`) is where it goes
  instead. An enumeration or credential-stuffing run then shows up as a rate an operator can alert
  on, without the response giving anything away.

Deliberately not metrics: **seeding** (one-shot boot work is a log line and a span), **health
probes** (polled — a counter of them measures the orchestrator, not the app), and per-request
authorization decisions (span attributes; the volume signal is already in
`http.server.request.duration`'s status tags). Deferred features — delete-account, change-email,
MFA — bring their counters with them when they land.

### Background jobs & email — in v1

The mechanism and its first consumer land together. Full design in §3.3.

- **Hangfire on Postgres**, wrapped by `server/shared/MyStack.Jobs`: durable enqueue, retries with
  backoff, dead-lettering, recurring/scheduled jobs, and an authorization-gated dashboard.
- **One Hangfire server per app, against its own schema.** The library is setup, conventions,
  telemetry and the test seam — never a shared queue.
- **`IEmailSender` over SMTP** in `server/shared/MyStack.Email`. Account emails are enqueued, never
  sent inline from a request.
- **Mailpit in `compose.yaml`** — local development sends genuine SMTP to a real inbox with a web UI
  and a REST API the e2e suite reads.
- `server/api` carries a demo job in the Projects/Tasks slice, so the mechanism is proven from both
  apps rather than only the one that needed it first.

### Cross-cutting

- Docker Compose for Postgres, Mailpit, and the full stack.
- CI on GitHub Actions from the first commit; `main` protected.
- Tests at every level a change touches (§5).

---

## 3.1 Authorization model

Three layers, evaluated in that order. Each answers a different question.

| Layer           | Question                              | Where it's declared                          |
| --------------- | ------------------------------------- | ---------------------------------------------- |
| **Scope**       | What may this *client application* do? | A policy on the endpoint (`api.read` / `api.write`) |
| **Permission**  | What may this *user* do?              | A guard on the endpoint (`users:read`, `projects:create`) |
| **Per-row**     | May they do it to *this row*?         | In the handler (ownership, tenancy)            |

Scope stays. It's the dashboard-safety layer — an OAuth token concept, so it lives at the HTTP edge,
and it's what stops a read-only integration token from ever reaching a mutation.

### Effective permissions

```
effective = expand(roles)  ∪  granted  −  denied
```

- **`expand(roles)`** — the in-memory role→permission map, declared per feature next to its
  endpoints, registered explicitly at startup (no assembly scanning). Code-owned; roles are fixed in
  code, not editable rows.
- **`granted` / `denied`** — per-user overrides: a permission an admin granted that the user's role
  doesn't carry, or one they revoked that it does.

The API's claims transformation computes this once per request, at the edge. **Authorization is a
pure function of the token** — the API reads no permission store, does no extra query, and the whole
model fits in the line of arithmetic above. That is the property worth protecting.

### Where overrides live

**In `auth`, on the user, minted into the access token as claims.** The token already carries `role`;
it also carries `perm` (extra grants) and `perm_deny` (revocations). Auth stores these as **opaque
strings it never interprets** — the API owns the permission catalog and exposes it
(`GET /api/v1/permissions`) so the admin console can offer a valid picker. That keeps auth free of
domain knowledge while still being the single place a user's authority is described.

An override row carries an optional **`ExpiresAt`**. Past it, auth stops minting the claim — which is
precisely the "allowed to do something briefly, from a given token" behavior: the grant lives as long
as the token that carries it, and at most until its own expiry.

Consequences, stated honestly because they're the price of the simplicity above:

- **Revocation latency is bounded by the access-token lifetime** — 15 minutes, configured as
  `Oidc:AccessTokenLifetime`. Removing an override takes effect on the next token. The immediate
  kill switch is revoking the user's grants/refresh token, which forces re-authentication.
- **Token size grows with overrides.** They're meant to be exceptional. If a role's worth of
  permissions ends up as per-user grants, the answer is a new role, not more claims.
- **Auth can't validate permission names.** A typo'd override is silently inert. The admin console
  picking from the API's catalog is what prevents that, so it isn't optional.

*Alternative if the latency proves unacceptable:* move the override store into `server/api` (it owns
the catalog and could validate names, and revocation becomes immediate) at the cost of a per-request
store read, a local user mirror, and losing the pure-function property. Recorded here so the trade is
visible; not the starting point.

---

## 3.2 Impersonation

An admin acting as another user, without knowing their password. The **largest** item in the
inventory and the one with the most dependencies — everything below is the shape to build, recorded
now so nothing else has to be retrofitted when it is.

### What the industry actually does

Three patterns, and the choice between them is about who owns the data:

1. **Admin-initiated, no consent.** The admin clicks "log in as", gets a short-lived session marked
   with who's really driving, and every action is audited. This is what most app-framework tooling
   ships: Better Auth's admin plugin (`impersonateUser` / `stopImpersonating`, a session carrying
   `impersonatedBy` and a short expiry), django-hijack, the Laravel impersonation packages. Normal
   for internal tools and B2C products where the operator already owns the data.
2. **User-granted access window.** The *user* opens their settings and grants support access for a
   period; the admin can only impersonate while that grant is live. Salesforce ("Grant Account Login
   Access"), Shopify, and Stripe-style support access all work this way. Standard for B2B, where the
   customer owns the data and consent is a compliance requirement. The standards-track expression of
   this is RFC 8693's **`may_act`** claim — "these parties are authorized to become me".
3. **Not offered at all.** Google Workspace admins can't log in as a user; they reset the password
   instead. Auth0 deprecated its impersonation feature over the security and audit exposure. A real
   option when the liability outweighs the support value.

Read-only versus full write is a separate axis, and the newer tools lean read-only-by-default because
it covers the overwhelmingly common case — *reproduce what the user is seeing* — without any ability
to take a destructive action in someone else's name. Full write exists, gated harder.

### What we build

Pattern 1 as the base, with pattern 2 available by configuration — which is close to your instinct,
and cheaper than it sounds because the scope layer already does the hard part.

- Gated on a dedicated permission (`users:impersonate`) held by the admin's own role.
- `auth` mints the token through a dedicated grant modelled on **RFC 8693 token exchange**: the
  admin's token goes in, a token for the target subject comes back, carrying an **`act` (actor)
  claim** naming the admin. `sub` is the impersonated user; `act.sub` is who's really driving.
- **Read-only is the default, and it costs us nothing new**: the impersonation token is issued with
  the `api.read` scope only. The existing scope policy on every mutating endpoint rejects it. No
  "read-only mode" flag threaded through the authorization code — this is the scope layer paying for
  itself, and a good reason it stays.
- **Write-capable impersonation is a second, separately-granted permission**
  (`users:impersonate:write`) that issues `api.write` as well. Still subject to the constraints below.
- **Consent, config-gated per environment.** Development: admin-initiated, no consent, so support and
  debugging aren't obstructed. Production: require a live **access grant** the user created from their
  own account settings — a row with an expiry, exactly the same shape as a permission override, and
  surfaced to `auth` as `may_act`. One config switch decides which applies, so the mechanism is the
  same in both and only the gate moves.
- **Constraints, non-negotiable in every mode:**
  - short lifetime, and **no refresh token** — impersonation ends, it doesn't renew;
  - never carries `users:impersonate` itself (no nested impersonation);
  - credential- and identity-changing operations (password, email, deleting the account, managing
    that user's own overrides) are **refused while `act` is present**, regardless of scope;
  - `auth` writes an audit row when an impersonation session starts.
- Every API log line and span carries both `sub` and `act.sub`, so the audit trail never loses the
  real actor.
- The BFF stores it as a distinct session state and renders a persistent "You are viewing as
  &lt;user&gt;" banner with an exit action that drops back to the admin's own session.

One thing to skip: the "user is handed a token and reads it out to the admin" flow. It's a support
PIN, and it's meaningfully worse than the grant window — the secret travels over whatever channel
support is using, it can be socially engineered out of a user who doesn't understand what they're
authorizing, and it leaves no artifact in the user's own account. The grant window gives the same
consent property with a revocable, visible, auditable record.

### What it depends on, and the four seams

The OpenIddict half is small: a custom grant on the token endpoint that validates the admin's
`subject_token`, swaps `sub` for the target user, stamps `act`, and issues read-only scope. The
weight of the compliant version is everywhere else — the access-grant feature and its screens, the
`act`-present guards, the audit trail, the BFF session state — and none of that has a consumer
without `apps/admin`. That, plus the override store the access grant is modelled on, is what it
genuinely waits for.

The point of writing it down now is only that we shouldn't have to *retrofit* it. Four seams — none
of them code written early, all of them cheap if considered when the surrounding thing is built and
expensive afterwards:

- **Auth's finalize pass:** the account API inventory records that a "grant support access"
  endpoint is coming, so "auth is finished" doesn't quietly mean "auth is closed to this".
- **Observability:** the log/span enricher emits `act.sub` when the claim is present. It never will
  be yet; the enricher shouldn't have to be reopened when it is.
- **BFF auth:** the session type can represent "acting as someone", even with nothing setting it.
  Threading a second identity through a session model that assumed one is the expensive version of
  this change.
- **Permission overrides:** an access grant is *the same shape* as an override — subject, expiry,
  audit, admin-visible. Build overrides so the grant window is a sibling, not a new concept.

---

## 3.3 Background jobs & email

Two mechanisms documented together because the first exists to serve the second, and neither is
finished without the other.

### Why this is v1 and not §4

An account email must not be lost because a mail provider had a bad thirty seconds, and a
registration request must not block on one either. Enqueuing solves both: the endpoint's work stays
local and fast, delivery is retried with backoff, and a send that truly fails lands somewhere you
can see it and replay it. That is a problem the product has on the day it has a register button —
which is the test §0 demands, and this passes it.

### Hangfire

**Hangfire on Postgres**, behind `server/shared/MyStack.Jobs`. It is the mainstream .NET choice and
the reason is the operator story: a dashboard showing queued, processing, scheduled, succeeded and
failed jobs, with the exception attached and a button to requeue by hand. Retries, dead-lettering
and cron-style recurring jobs come with it rather than being written.

Four properties that are load-bearing and easy to get wrong:

- **Each app runs its own Hangfire server against its own schema** (`hangfire_auth`, `hangfire_api`).
  A single shared schema would have either app dequeuing the other's jobs and needing the other's
  types loadable — two deployables coupled through a table, which is exactly the sort of mechanism
  this rebuild exists to avoid. The shared library is setup, conventions, telemetry, and the testing
  seam. **It is not a shared queue.**
- **The dashboard is unauthenticated by default** and is gated behind an authorization filter, never
  exposed publicly. In `auth` the cookie session gates it naturally. `api` authenticates bearer
  tokens only, so a browser has no way to log into its dashboard — in v1 that dashboard is
  development plus restricted-network only. A production-grade route for it is genuinely open; the
  most likely answer is that `apps/admin` grows a jobs screen rather than `api` growing a UI.
- **`Enqueue` does not join your EF transaction.** Hangfire writes its own tables through its own
  connection, so "user created" and "confirmation email enqueued" are two writes, not one. That is
  the honest price of Hangfire over a hand-rolled queue sharing the `DbContext`, and it is paid
  knowingly: the failure mode is a created account with no email sent, and resend-confirmation
  already exists to recover it. `Hangfire.PostgreSql` exposes a `TransactionScope` enlistment option
  that may close the gap — **verify it when building this rather than assuming it.** If it doesn't, this is
  precisely the transactional-outbox trigger in §4, and that row stays there pointing here.
- **Trace context does not propagate into a job by itself.** The enqueuing span and the executing
  job's span are linked explicitly, so a failed email is traceable back to the request that asked
  for it. `mystack-old`'s `TraceLinkedJobExtensions` is the reference.

Licence note: Hangfire.Core is LGPL v3. Fine to link in a closed application, worth knowing before
it's load-bearing.

### Email

One `IEmailSender` seam in `server/shared/MyStack.Email` and one **SMTP** implementation (MailKit),
used in every environment. Every transactional provider worth using — Resend, Postmark, SES,
Mailgun — speaks SMTP, so a single adapter covers local, staging and production with nothing but a
host, a port and a credential changing between them. An HTTP-API adapter can slot behind the same
interface later if a provider's API earns it; `mystack-old`'s `HttpEmailSender` is the reference.

**Local development sends real email.** `compose.yaml` runs **Mailpit**; the SMTP settings point at
it. The flow is genuinely end to end — register, the job runs, the message is delivered, you open
Mailpit's inbox in a browser, click the confirmation link, the account is confirmed. There is no
`LoggingEmailSender`, no dev-only branch, and no code path that exists only in production. The
e2e suite reads Mailpit's REST API to assert an email arrived and to pull the link out of it, so
"registration works" is provable rather than assumed.

Bodies are **plain interpolated strings** for now — HTML and text — built behind a small renderer
interface so where the markup comes from stays an implementation detail. The intended endgame is a
separate emails package (React Email or similar) that compiles to HTML the .NET side merely holds.
Designing emails as Razor views inside `auth` is explicitly rejected: emails are not an identity
concern and that would put them in the wrong project permanently.

The account emails in v1: **confirm email**, **reset password**, and a **password changed**
notification. Change-email and delete-account emails arrive with their features.

---

## 3.4 Seeding

Three unrelated things share the word, and separating them is most of the design. Almost nothing
crosses between them.

| Tier          | What it is                                                          | Owned by | Exists in                |
| ------------- | ------------------------------------------------------------------- | -------- | ------------------------ |
| **Reference** | the app cannot function without it: roles, scopes, OIDC clients, someone able to sign in | the app that owns the table | every environment, forever |
| **Sample**    | development convenience: demo accounts, a few rows to look at        | the app  | development and e2e only |
| **Fixture**   | one test's arrangement                                               | the test | that test                |

There is no central seed store and no shared seed data. Two apps holding two unrelated datasets in
one folder is a folder, not an abstraction — and reaching across the .NET/TypeScript boundary for it
is exactly the coupling §2 rules out.

### Two switches, not one

The earlier framing — one `Database:Seed` that's "dev on, production off" — was **wrong**, and
it's worth being clear why rather than quietly correcting it. Production needs the roles to exist,
the scopes to exist, the web-BFF client to exist, and needs somebody able to log in. A single
boolean cannot express "always do this half, never do that half."

| Switch                      | Default                     | Runs                                                        |
| --------------------------- | --------------------------- | ----------------------------------------------------------- |
| `Database:Migrate`          | on in dev, off in production | schema migration, independent of everything below            |
| `Database:Seed:Reference`   | **on everywhere**           | roles, scopes, clients, bootstrap admin — reconciled         |
| `Database:Seed:Sample`      | on in dev/e2e only          | demo accounts and rows                                       |

`Database:Seed:Sample` **throws on startup** if it's enabled in a production environment rather than
quietly obeying. A switch that can silently put `user@mystack.local / User!23456` into production is
not a switch worth having.

`Database:Seed:Reference` stays a switch — not hardcoded on — because an organisation may manage
OIDC clients out of band and want the app to keep its hands off. Defaulting it on is the right
default; removing the escape hatch is not.

### Code-declared vs config-declared

"Make it all config" is the wrong reading of this, and the line between the two matters:

- **Code-declared, DB-materialized — roles and scopes.** §3.1 fixes roles in code and the API's
  permission map keys off those constants; a role that exists as a row but not in the map grants
  nothing, so a config knob here only creates ways to be wrong. Seeding materializes the code list,
  nothing more. Scopes (`api.read`, `api.write`) are the same.

  The role *names* are therefore duplicated between `auth` (which stores membership) and `api`
  (which maps them to permissions). **That duplication is deliberate** — it's the same trade as auth
  treating permission strings as opaque (§3.1), and two short string lists is the cheap side of it.
  It is not a shared library waiting to happen.

- **Config-declared — OIDC clients and the bootstrap admin.** Redirect URIs, post-logout URIs and
  secrets genuinely differ per environment, so they are the thing config is for. Missing required
  values **throw and abort startup**; there is no fallback, because a silently-defaulted secret
  seeds — and keeps re-seeding — a credential that ships in a public repo.

- **Config-declared and hard-gated — sample data.** Values supplied by whoever is running the app,
  never baked into the binary.

### Every consumer supplies its own values

This is what config-driven actually buys, and it's the point:

```
appsettings.Development.json ──────────────────▶ local dev
Seed__Sample__Users__0__Email=… (env) ─────────▶ the e2e container
secret manager / deploy environment ───────────▶ production bootstrap
```

One code path, three configurations, zero shared artifacts. The e2e suite **declares** the account
it signs in with rather than discovering one the app happened to write — which also removes the
clone-a-template-user trick `mystack-old`'s `e2e/support/db.ts` needed in order to work at all.

### The bootstrap admin

Production does **not** carry an admin password in configuration. A boilerplate that tells you to
put one there is a boilerplate that gets that password committed.

Instead: reference seeding creates the admin account with its email confirmed and **no usable
password**, and it is activated through the ordinary forgot-password flow. That works because §3.3
makes email real in every environment — the bootstrap path is the same path every other user takes,
so it's already tested. Only the address is configured.

Development supplies a password directly, because convenience is the entire point there. If an
environment genuinely needs a password-configured admin, that's a supported option and a documented
trade, not the default.

### No password grant, anywhere

`mystack-old`'s seeder granted `GrantTypes.Password` to `web-bff` in development and unconditionally
to the Bruno client. §3 rules that grant out entirely, so it appears in no seeded client, in no
environment. Called out here because it is precisely the kind of line that survives a copy-paste.

### Mechanics — the four things that go wrong on boot

1. **Ensure by natural key, one item at a time.** Never "is the table empty?" — `mystack-old`'s
   `DataSeeder` bailed if *any* demo row existed, so a fourth sample row added later would never
   appear, and the `IgnoreQueryFilters` workaround existed only to patch that guard. Give every seed
   item a deterministic id or unique slug and ensure them individually; the guard and its workaround
   both disappear.

2. **Declare create-only or reconcile, per item, in the code.** Both policies are correct for
   different things and picking the wrong one silently destroys data. Clients **reconcile** — diff
   the descriptor against what's stored and write only on a real change, comparing the secret
   through `ValidateClientSecretAsync` since it's stored hashed, so an unchanged client is never
   rewritten and an out-of-band edit survives a reboot. Users are **create-only** — never reset a
   password a human has changed. `mystack-old` got both right by accident; make the choice explicit.

3. **Seed before serving traffic.** `AddHostedService<AuthSeeder>()` registered after the web host
   means Kestrel is already accepting requests while the seeder runs. Run it inline before
   `RunAsync()`, or implement `IHostedLifecycleService.StartingAsync`, which runs before any
   `StartAsync`. If it stays an ordinary hosted service, `/health/ready` must report unready until
   seeding completes, so nothing routes traffic mid-seed.

4. **Take a Postgres advisory lock around migrate *and* seed.** Concurrent instances race on both.
   Note that `pg_advisory_xact_lock` is transaction-scoped and `MigrateAsync` runs its own
   transactions, so it cannot wrap the migration — this needs a **session-scoped `pg_advisory_lock`
   on a dedicated connection**, held across both operations and released explicitly. The seed itself
   runs in a single transaction inside that lock, so a mid-seed failure leaves nothing half-written.
   (The migration keeps its own transaction handling; don't try to nest it.)

Missing required configuration throws and aborts startup. Failing to boot is strictly better than
booting wrong.

### Both apps, written twice

`api` seeds on the same model. The safety mechanics — the advisory lock, the tier gate, the
ensure/reconcile helpers — are roughly eighty identical, low-churn, domain-free lines, so they do
pass §2's three-part test. They are still **written twice**, kept deliberately structurally
identical, so that extraction is mechanical if a third consumer ever appears. This is the most
likely fourth `server/shared/` library. It is not one yet.

---

## 4. Explicitly out of scope

Not "bad ideas" — ideas without a current problem. Each has the trigger that would justify picking it
up. Anything added later gets its own PR and its own doc entry.

| Deferred                                        | Add it when…                                                                    |
| ----------------------------------------------- | --------------------------------------------------------------------------------- |
| Idempotency keys / reservation store            | you have a write with a real-money or externally-visible side effect            |
| ETag + `If-Match` optimistic concurrency        | two users genuinely edit the same row and you've lost an update                 |
| Output caching, cache tags, cache metrics       | a profiler — not a hunch — says a specific endpoint is the bottleneck           |
| Distributed cache (Redis)                       | you run more than one instance *and* have measured cache pressure               |
| Transactional outbox                            | Hangfire's non-transactional `Enqueue` (§3.3) actually loses a job you needed  |
| A designed emails package (React Email → HTML)  | interpolated string bodies stop being good enough to send to a real user        |
| Postgres full-text / trigram search             | list search is slow with real data volume                                       |
| The HTTP `QUERY` verb + a filter DSL            | ordinary POST-with-a-body demonstrably can't express what a screen needs        |
| Soft delete, audit trail, multi-tenancy         | the product asks for them                                                       |
| Generated TanStack Query options / zod schemas  | hand-written factories become repetitive enough to hurt                         |
| HTTP resilience pipelines, response compression | you have a flaky dependency / a measured payload problem                        |
| Editable roles (roles as DB rows)               | per-user overrides stop being enough — that's the signal, and it's a real design |

The retired implementations of most of these live in `mystack-old` — reference them rather than
rewriting from scratch if one earns its way back in.

---

## 5. Conventions

The short spine. These are the reasons the boilerplate exists.

1. **Endpoints own their behavior; expected failures are returned, not thrown.** `HandleAsync` holds
   the logic. A not-found, a forbidden, a conflict, a validation failure — all *returned* as
   ProblemDetails. Exceptions are for the genuinely exceptional and are caught centrally → 500.
2. **ProblemDetails for every error.** No ad-hoc error JSON, anywhere.
3. **Authorization is declared on the endpoint**, never buried in the handler. Per-row rules live in
   the handler and are always tested.
4. **Validation lives beside the endpoint** in a `Validator<T>`. Invalid requests never reach the
   handler.
5. **Keep EF entities off the wire.** Endpoints return DTOs.
6. **OpenAPI is the source of truth for the client.** Contract changes → re-export the spec →
   regenerate the client → commit the diff. Generated files are never hand-edited.
7. **BFF cookie auth.** No tokens in localStorage, no tokens in client JS, ever.
8. **Security headers on every response.**
9. **User input that could be PII never travels in a URL.** Filter and search text goes in a POST
   body, and anything sensitive in a logged body is `[Redact]`-masked.
10. **Comments earn their place.** Write one only for a durable "why" — an invariant, a non-obvious
    constraint, a deliberate trade-off. Never narrate what the code does; never record why we chose a
    structure in conversation (that belongs in the PR).
11. **Docs are updated in the same PR as the code**, not after.

### Testing

Tests are part of the change. Test at every level the change touched — a full-stack feature is not
done with only API tests.

| Level               | Suite                    | Proves                                                        |
| ------------------- | ------------------------ | --------------------------------------------------------------- |
| API slice           | `server/api/tests`       | Authorization matrix, validation, per-row rules, persistence  |
| Auth / account flow | `server/auth/tests`      | Identity + OpenIddict behavior, token shape, anti-enumeration |
| Background job      | the enqueuing app's tests | The job was enqueued, and running it produced its side effect |
| Web units           | `apps/web/src/**/*.test` | BFF seams, factories, schemas, logic-bearing components       |
| The user's flow     | `apps/web/e2e`           | The whole product, real containers: it actually works         |

A job is tested from both ends — that the endpoint enqueued it, and that executing it does what it
claims — never by asserting Hangfire works. Email assertions go through Mailpit's REST API, so
"a confirmation email arrived and its link confirms the account" is proven end to end in e2e.

Every protected endpoint tests its full matrix: 401 anonymous, 403 wrong scope, 403 right scope but
missing permission, the happy path, validation failures, and not-found/ownership. Once overrides
exist, add: permission granted only by override, and permission denied by override despite the role.
Never leave an authorization branch unproven.

When you change code, fix the tests it touches — updating and deleting tests is fine; weakening an
assertion to reach green is not.

### Conventions quick-reference

- **C#:** file-scoped namespaces, nullable + implicit usings on, warnings-as-errors, CSharpier.
- **TypeScript:** ESLint + Prettier, strict `tsconfig`, packages named `@mystack/*`.
- **Package management:** always pnpm for JS; central NuGet versions for .NET.
- **Secrets:** never committed. `.env` (gitignored) + user-secrets locally.
- **Docs:** planning docs live in `docs/`, never the repo root.

---

## 6. Delivery

How changes land, whatever the change is:

- Repo created, `main` protected: PRs required, CI must pass, no direct pushes, squash merge, delete
  branch on merge. (Note: branch protection does not apply to repo admins by default — enable "Do not
  allow bypassing the above settings" or the rules are advisory for you.)
- One CI workflow with a single required `gate` job: lint → typecheck → build → format check → tests,
  for both ecosystems. E2E runs separately (it builds container images).
- Conventional commits. PR body says what changed and why, and what it deliberately doesn't do.

Docs get updated inside the PR that changes the thing they describe — there is no docs catch-up PR.

The gate and the branch rules are worth having in place early, not because anything depends on them
but because a workflow that has only ever been green against an empty repo is trivial to debug, and
one retrofitted over a half-built stack is not.

### Hosting & deployment

Containers everywhere, including production. Images are built in CI, pushed to a registry (GHCR is
free for this), and pulled by the host — the same Dockerfiles that back local `compose` and the e2e
suite.

**One small VPS runs all of it, and that's a perfectly normal target.** Four app containers plus
Postgres, behind a reverse proxy (Caddy or Traefik) doing TLS and hostname routing. The .NET apps
idle around 100–150 MB each and the Node BFFs are similar, so 2 GB of RAM is comfortable and 4 GB is
roomy; this is a $10–20/month machine, not an infrastructure project. Multiple apps on one host is
the *cheap* option, not the expensive one — the per-app costs are a DNS record and a proxy rule.

- `apps/admin` gets its own hostname and can be restricted further than the public app: a separate
  proxy rule, an IP allowlist, or a VPN-only network. That restriction is the whole reason it's a
  separate deployable rather than a route.
- Postgres on the same host with a volume is fine to start; it's also the easiest thing to move to a
  managed instance later, since nothing else depends on where it lives.
- **Mailpit is local only.** Every hosted environment — including staging — points its SMTP settings
  at a real provider. Mailpit reaching a deployed environment would mean email silently going
  nowhere while every send still reports success, so the compose profile that defines it must not be
  one production can select, and startup should refuse to boot outside development against it.
- Deployment mechanism is deferred (D12). The realistic options are plain Compose plus a GitHub
  Action that SSHes and pulls, or **Kamal**, which is built precisely for "deploy containers to a
  cheap VPS" and handles zero-downtime swaps and multi-app hosting for you.

---

## 7. The inventory

**This is a list, not a sequence.** Nothing here claims a position in a queue; pick up whatever is
worth building next. Each line is one standalone, reviewable, mergeable change that leaves `main`
green — which is a statement about *size*, not about *when*.

Mark items done as they land, so this stays the honest answer to "what exists?".

### Foundation

- [x] **Repo skeleton** — layout, README, this doc, `CLAUDE.md`, `.gitignore`, `.editorconfig`, licence
- [x] **Toolchain** — `Directory.*.props`, `.slnx`, CSharpier. *The pnpm workspace and the JS
      linters wait until there is JavaScript.*
- [x] **CI + branch protection** — the `gate` workflow, green against an empty repo
- [x] **Local infrastructure** — `compose.yaml` (Postgres, Mailpit, opt-in Aspire dashboard),
      `.env.example`. *No dev scripts: D1 settles orchestration as "nothing", so the handful of
      commands live in the README.*

### server/auth

- [x] **Host skeleton** — Identity, EF + first migration, health checks, security headers
- [x] **OpenIddict server** — config, code + PKCE, refresh, sign-in page
- [ ] **Seeding** — two-tier switches, config-declared clients + bootstrap admin, advisory lock,
      seed-before-serve (§3.4)
- [ ] **Protocol completion** — userinfo, introspection, client credentials, device flow +
      verification page, PAR
- [ ] **Logout notifications** — back-channel logout tokens to registered clients; front-channel
      decided, not assumed
- [ ] **Account flows** — register, email confirmation, forgot/reset password, change password,
      anti-enumeration throughout
- [ ] **Design + finalize pass** — every rendered screen designed rather than scaffolded, walked
      through end to end, declared done (§3)
- [ ] **Permission override store** — grant/deny rows with `ExpiresAt`, minted into token claims (§3.1)
- [ ] **Impersonation grant** — token exchange, `act` claim, read-only scope, consent gate, audit (§3.2)

### server/api

- [ ] **Host skeleton** — ProblemDetails, security headers, health, EF + Postgres
- [ ] **Authn + RBAC** — JWT validation, scope policies, role→permission map, the endpoint guard (§3.1)
- [ ] **First vertical slice** — one real entity (`Projects`/`Tasks`): CRUD, validation, paging,
      POST search, a demo job, the full authorization test matrix
- [ ] **Permission catalog** — `GET /api/v1/permissions` returning key, resource group, description (D10)
- [ ] **Seeding** — same two-tier model and mechanics as `auth`, written separately (§3.4)
- [ ] **OpenAPI export** — spec generation plus a CI drift check

### Shared .NET libraries

- [ ] **`MyStack.Jobs`** — Hangfire on Postgres, per-app schema, gated dashboard, recurring jobs,
      trace linking (§3.3)
- [ ] **`MyStack.Email`** — `IEmailSender`, SMTP adapter, message shape, the account emails (§3.3)
- [x] **`MyStack.Observability`** — structured logs, OTel traces + metrics, `[Redact]`, dev dashboard

### apps/web

- [ ] **Shell** — Start scaffold, Tailwind + shadcn/Base UI, layout, a public route
- [ ] **BFF auth** — code + PKCE dance, session cookie, `/api/*` proxy, sign in + register
- [ ] **Guarded dashboard** — route guard, the Query architecture, a list screen off the real API
- [ ] **Account page** — profile + change password
- [ ] **Impersonation banner** — distinct session state, "viewing as", exit action (§3.2)

### packages/

- [ ] **`api-client`** — codegen from the OpenAPI spec
- [ ] **`bff`** — the session/refresh/proxy kit extracted out of `apps/web` for a second consumer

### apps/admin

- [ ] **Console** — user search + detail, role and override administration, impersonation entry point

### Testing

- [ ] **E2E harness** — Playwright over the real container stack; sign in, see data, read Mailpit
- [ ] **Per-feature suites** — every item above ships with tests at the levels it touched (§5)

### The only ordering that's real

Not preferences — technical dependencies. Everything not listed here is genuinely free-floating.

- `packages/api-client` needs an OpenAPI spec to generate from.
- `apps/web`'s BFF auth needs an OpenIddict server with code + PKCE to talk to.
- `packages/bff` is an *extraction*, so it needs `apps/web` to extract from and a second consumer to
  justify it.
- Impersonation's compliant form needs the override store (the access grant is the same shape) and
  `apps/admin` (nothing else drives it). The grant itself is small; the surrounding work isn't.
- The email seam needs somewhere to send from — but jobs, email, and the account flows are three
  separate changes, and the flows are what make the other two non-speculative. Build the mechanism
  as its own change rather than inside the feature that first needs it; that's how the last attempt
  produced pull requests too large to review.

§3.2's *four seams* are the other half of this: a handful of places that should be built able to
accommodate impersonation whenever it arrives, so nothing has to be retrofitted. They are cheap
if considered up front and expensive if not.

---

## 8. Decisions

### Settled

- **D1 — Orchestration: nothing, for now.** `dotnet build`, `pnpm -r`, `compose up`, and a few root
  scripts. No Turborepo, no Aspire. Revisit when CI time or repeated local rebuilds actually hurt —
  Turbo is easy to add later, and Aspire deserves a real spike (it would reshape local dev) rather
  than being adopted by default. Recorded as an ADR when we revisit.

  **The standalone Aspire Dashboard container is not covered by this.** It is an OTLP receiver with
  a web UI, added to `compose.yaml` like any other image — no AppHost, no SDK reference, no change
  to how anything is built or run. Using it costs one compose service and replaces the
  Jaeger + Prometheus + Loki alternative with a single container.
- **D2 — Authorization: scope stays; roles drive permissions; per-user overrides ride in the token.**
  Full design in §3.1, impersonation in §3.2.
- **D3 — No `QUERY` verb.** Lists with user-supplied filter/search text are `POST
  /<resource>/search` with a JSON body — PII stays out of URLs, and ordinary POST is codegen-native.
  Simple paged lists stay `GET`.
- **D4 — Authorization code + PKCE from day one**, plus a dedicated auth design-and-finalize pass —
  `auth` is the one project we declare *finished* rather than leave open.
- **D5 — Naming:** `MyStack.Api` / `MyStack.Auth`, matching `server/api` and `server/auth`.
- **D6 — Reference domain: `Projects` / `Tasks`.** A real one-to-many with ownership rules, so the
  first slice teaches something instead of reading as scaffolding.
- **D7 — Observability is v1**, not deferred: structured logs, OpenTelemetry, `[Redact]`.
- **D8 — No `server/package.json`.** With D1 settled as "nothing", the .NET side is driven by
  `dotnet` and the solution file; root scripts shell out to both ecosystems.
- **D9 — `apps/admin` is a separate app.** The boundary *is* the point: its own hostname, its own
  OIDC client, and the option to put it behind an IP allowlist or VPN — which matters a lot once it
  can impersonate. Multiple containers on one host is cheap and normal (see *Hosting & deployment*),
  so the real cost isn't runtime, it's the duplicated BFF. That's paid off once by extracting
  `packages/bff` — session cookie, token refresh, and the `/api/*` proxy are domain-free
  TypeScript infrastructure, which is exactly what `packages/` is for.
- **D10 — Flat permission strings**, `resource:action` (`projects:create`). The norm, and the right
  call. Nuance worth building in: the string is the *identity*, but `GET /api/v1/permissions` returns
  a catalog entry per permission — key, resource group, human description — so the admin console can
  group, sort, search, and paginate a table without hardcoding anything the API knows.
- **D11 — Impersonation: user-granted access window, read-only by default, fully audited.** The
  compliant pattern, chosen because a boilerplate can't know where it'll be deployed and the consent
  model is the one that survives a B2B or regulated context. Development can bypass the grant by
  config so support and debugging aren't obstructed; production can't. Read-only falls out of the
  existing scope layer for free; write access is a separate permission. It depends on the override
  store and `apps/admin`; §3.2 records the four seams to leave open in the meantime.
- **D13 — Background jobs are v1, on Hangfire, shared, with scheduling.** The queue's consumer is
  account email and it exists from the register button, so it passes the no-speculative-infra test
  (§0). Hangfire over a hand-rolled Postgres queue for the operator story — a dashboard that shows
  what failed and requeues it — accepting a non-transactional `Enqueue` as the price (§3.3).
  Recurring/scheduled jobs land with it rather than as a second pass, because they're the same
  mechanism and splitting them would mean reopening the setup. `MyStack.Jobs` and `MyStack.Email`
  are shared libraries from day one, which is a **deliberate exception** to §2's duplicate-first
  rule: both are stack-wide by nature, and `api` proves it with a demo job in its first slice.
- **D14 — Local development sends real email, to Mailpit.** One SMTP sender in every environment,
  differing only by host. The alternative — a logging sender in dev — creates a code path that only
  runs locally and a flow that is never actually exercised until production. Mailpit costs one
  compose service and buys a clickable inbox plus a REST API the e2e suite asserts against.
- **D16 — Seeding is config-driven and two-tier.** Reference data (roles, scopes, clients, an admin
  who can sign in) is seeded in *every* environment including production; sample data is
  development-only and refuses to run outside it. Values come from configuration rather than string
  literals, so each consumer — dev, the e2e container, a deploy environment — supplies its own and
  no seed artifact is shared between them. Roles and scopes stay code-declared because §3.1 fixes
  them in code. The production bootstrap admin gets no password: it's activated through the normal
  reset flow, which exists and is tested. Full design in §3.4.
- **D17 — No consent screen in v1.** Every client is first-party — the web BFF, the admin
  console, a dev client — so implicit consent is the honest description, not a shortcut. Enforced
  rather than assumed: clients are registered with `ConsentType = Implicit` and the authorization
  endpoint refuses any client registered otherwise, so onboarding a third-party client forces
  this decision to be remade instead of silently inheriting it.

### Still open

- **D12 — Deployment mechanism.** Compose + an SSH-based GitHub Action, or Kamal. Doesn't block
  anything until there's something worth deploying.
- **D15 — Production access to `api`'s Hangfire dashboard.** `auth`'s is gated by its cookie
  session; `api` has no cookie auth, so a browser can't log into its dashboard. Development and
  restricted-network only for now. Most likely resolution: `apps/admin` grows a jobs screen over an
  API endpoint rather than `api` serving a UI — so this wants deciding whenever `apps/admin` does.

---

## 9. Docs map

Kept small on purpose. Each is updated in the PR that changes what it describes.

| Doc                    | Records                                                                  |
| ---------------------- | -------------------------------------------------------------------------- |
| `docs/architecture.md` | This document — the stack, the layout, the scope boundary, the decisions |
| `docs/authorization.md` | The permission model, the catalog, overrides, impersonation (from §3.1/§3.2 once built) |
| `docs/api.md`          | `server/api` — request lifecycle, cross-cutting features, every slice     |
| `docs/auth.md`         | `server/auth` — screens, account flows, security posture, prod hardening  |
| `docs/web.md`          | `apps/web` — routes, feature modules, BFF seams, config                   |
| `docs/jobs-and-email.md` | Job conventions, the dashboard's gate, the email seam, every email we send |
| `docs/decisions/`      | One short ADR per non-obvious call                                       |
| `docs/deferred.md`     | §4's table, kept current, with enough context to pick an item up cold     |

Working docs are a separate thing: scoped to one push, deleted when it's done. `docs/auth-track.md`
is the current one — the order for building `server/auth` to closure. It doesn't override §7.
