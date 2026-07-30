# server/auth

The OAuth2/OIDC authorization server. It owns users, credentials, roles and permission overrides,
and it is the only deployable that issues tokens.

**What exists today is the host skeleton, telemetry, the OpenIddict server, and background
jobs**: ASP.NET Core Identity over EF Core/Postgres, health checks, the security-header set,
`MyStack.Observability` wired in, OpenIddict issuing tokens — authorization code + PKCE with
refresh tokens, the four protocol endpoints, a functional sign-in page, request logging and the
first domain counters — and `MyStack.Jobs` running Hangfire with a gated dashboard and the
token-pruning recurring job. There is no seeding and no account flow yet, so clients and users
are still created by hand.
[auth-track.md](auth-track.md) is the order the rest lands in; [architecture.md §7](architecture.md)
is the inventory.

## Running it

```bash
docker compose up -d                  # postgres
dotnet run --project server/auth/src  # http://localhost:5100
```

Development migrates on boot, so a fresh database needs nothing else. The protocol surface hangs
off `/.well-known/openid-configuration`; the sign-in page is `/signin`; the Hangfire dashboard is
`/jobs`, for a signed-in `admin`.

## Configuration

| Key | Default | Notes |
| --- | --- | --- |
| `ConnectionStrings:AuthDb` | none | Required. Startup throws without it — there is deliberately no fallback, because a default here would be a credential compiled into the binary. |
| `Database:Migrate` | `false` | Applies pending migrations before the host serves. On in development; a deployment applies migrations as its own step. |
| `Oidc:AccessTokenLifetime` | `00:15:00` | The bound on revocation latency (architecture §3.1): a role change or revoked override lives at most this long in issued tokens. |
| `Oidc:IdentityTokenLifetime` | `00:15:00` | |
| `Oidc:AuthorizationCodeLifetime` | `00:05:00` | One redemption, minutes to make it. |
| `Oidc:RefreshTokenLifetime` | `14.00:00:00` | Absolute horizon; the token itself rotates on every use. |
| `Jobs:RetryAttempts` | `5` | Retries after the first failed execution; past them the job dead-letters onto the dashboard's Failed page. |
| `Jobs:RetryDelaysInSeconds` | Hangfire's backoff | Seconds between retries, one entry per attempt. Tests set `[0]` so a dead-letter is provable in seconds. |
| `Jobs:PollInterval` | `00:00:02` | Queue and retry-schedule polling — the floor on job latency, and a periodic query against Postgres. |
| `Jobs:WorkerCount` | Hangfire's default | Concurrent job workers. |

`appsettings.Development.json` carries the compose stack's connection string. Those are local
infrastructure credentials, not secrets — every other environment supplies
`ConnectionStrings__AuthDb` from its own configuration, and user-secrets is the local override.

## The schema

Identity's tables live in a Postgres schema named **`auth`**, inside the same database `server/api`
will use. That is the split `MyStack.Jobs` also assumes when it puts its tables in `hangfire_auth`
(architecture §3.3), so both apps can share one Postgres instance without sharing a namespace.

Naming is snake_case throughout, via `EFCore.NamingConventions` — EF Core 10 has no built-in
convention for it. Identity's `AspNet*` table names are replaced with `users`, `roles`,
`user_claims`, `user_roles`, `user_logins`, `user_tokens` and `role_claims`: they describe the
framework rather than this schema, and renaming them later costs a migration.

Keys are **application-generated version 7 UUIDs**. The timestamp in the leading bits means
Postgres, which orders `uuid` by its canonical byte order, keeps appending to the primary key index
instead of fragmenting it. Generating them in the entity rather than the database also means the
`sub` a token will carry is known before `SaveChanges`.

## Identity policy

- **Unique email**, and `SignIn.RequireConfirmedEmail` is on from the first account rather than
  switched on later over users who never went through confirmation.
- **Passwords: twelve characters, no composition rules.** NIST SP 800-63B's position — length
  carries the strength, and mandatory character classes mostly produce predictable substitutions.
- Default token providers are registered, so email confirmation and password reset have their
  tokens available when those flows arrive.
- Identity's principal uses the OIDC claim types (`sub`, `name`, `role`, `email`) instead of its
  SOAP-era defaults, so the cookie and every token OpenIddict mints speak the same names.

## The OpenIddict server

OpenIddict 7 over the same `AuthDbContext` — its tables live in the `auth` schema as
`oidc_applications`, `oidc_authorizations`, `oidc_scopes` and `oidc_tokens`, renamed for the same
reason Identity's were.

| Endpoint | Path | Handled by |
| --- | --- | --- |
| Discovery + JWKS | `/.well-known/openid-configuration` | OpenIddict entirely |
| Authorization | `/connect/authorize` | passthrough: cookie check, implicit-consent check, principal |
| Token | `/connect/token` | passthrough: user re-validated against the store, claims rebuilt |
| End session | `/connect/endsession` | passthrough: Identity sign-out, then the validated post-logout redirect |
| Revocation | `/connect/revocation` | OpenIddict entirely |

**Authorization code + PKCE and refresh tokens are the only flows.** PKCE is required globally
rather than per client, so no future registration can quietly opt out. **There is no password
grant, in any environment** — `grant_types_supported` in the discovery document is exactly
`[authorization_code, refresh_token]`, and the test suite asserts it.

**Lifetimes are configuration** — the `Oidc:*` keys above, with the defaults committed in
`appsettings.json`. Refresh tokens rotate: every refresh issues a new one, and the token endpoint
re-validates the user against the store on each exchange, so role and email changes take effect on
the next refresh rather than surviving to the token's horizon.

**Claim destinations are deny-by-default.** `email`, `role` and `name` reach the access token —
plus the identity token when their scope was granted; the `perm`/`perm_deny` shape (overrides,
auth-track step 9) is declared access-token-only; anything unlisted — Identity's security stamp,
concretely — reaches no token at all, which the flow test proves. A token granted an `api.*` scope
carries `aud: api`. Access tokens are signed but **not encrypted** JWTs: `server/api` validates
them against the discovery document rather than sharing auth's key material.

**Keys.** Development uses the framework's development certificates; tests use ephemeral in-memory
keys so CI never writes a certificate store; any other environment must supply real signing and
encryption credentials deliberately, and OpenIddict refuses to boot without them (see the
hardening items).

**No consent screen** (architecture D17). Every v1 client is first-party and registered with
implicit consent; the authorization endpoint refuses a client registered any other way, so a
future third-party client forces the decision to be remade rather than silently inheriting it.

**No clients are seeded yet** — that is auth-track step 5. The test suite registers its own
public + PKCE client through `IOpenIddictApplicationManager`; a manual local flow needs the same
done by hand.

## The sign-in page

`/signin`, a Razor page — functional now, designed in the design + finalize pass at the end of
the track. It signs into Identity's
application cookie; the authorization endpoint challenges to it and the round trip lands back on
the interrupted request (with `prompt=login` stripped, so honoring that prompt can't loop).

- **One generic failure message.** Unknown email, wrong password, unconfirmed account and lockout
  all read identically — anti-enumeration (architecture §3) — and the honest outcome goes to the
  `auth.sign_ins` metric tag instead.
- **Lockout is on**: failed attempts count against Identity's defaults (five tries, five minutes).
- **`ReturnUrl` is followed only when local.** It is attacker-writable, and an absolute URL there
  is a phishing redirect hanging off a legitimate sign-in.
- The page runs under its own security-header policy, which differs from the default in exactly
  one directive: `form-action 'self'`.

## Background jobs

`server/shared/MyStack.Jobs` wires Hangfire against the same Postgres database, in its own
**`hangfire_auth`** schema — one Hangfire server per app against its own schema, never a shared
queue (architecture §3.3). The library is the conventions, so `api` will inherit them unchanged:
storage setup, retry policy, telemetry, and the recurring-job seam.

- **The dashboard is `/jobs`**, gated the way every other protected resource is — authorization
  declared on the endpoint: the Identity cookie plus the `admin` role. Anonymous browsers bounce
  to `/signin` and land back after signing in; a signed-in non-admin gets a plain 403 (no
  access-denied page exists yet — the design pass owns pages). Hangfire's own default filter
  (local requests only) is removed rather than stacked, because behind a reverse proxy every
  request looks local. The dashboard runs under its own security-header policy: every CSP source
  pinned to `'self'`, plus inline *styles* only — Hangfire's UI sets `style=""` attributes for its
  progress bars; scripts stay `'self'`.
- **Failures retry, then dead-letter visibly.** `Jobs:RetryAttempts` retries with Hangfire's
  backoff, and an exhausted job parks on the dashboard's Failed page with its exception attached
  and a requeue button next to it — never silently deleted.
- **Recurring jobs are declared in code**: `AddRecurringJob<TJob>(id, cron)` registers an
  `IRecurringJob` at startup, idempotently, so each boot converges the schedule to what the code
  says. Auth's first is **`prune-oidc-tokens`** (daily, 03:00 UTC): `oidc_tokens` and
  `oidc_authorizations` gain rows on every sign-in and OpenIddict never deletes them on its own,
  so the job prunes expired and invalidated entries older than 30 days — comfortably past the
  14-day refresh horizon, and `PruneAsync` itself never touches a live grant.
- **Every enqueue is trace-linked, transparently.** A Hangfire client filter stamps the current
  W3C trace context into the job's parameters; the execution then runs in a span of its own —
  it may happen minutes later, on another instance — carrying a link back to the request that
  enqueued it, surviving restarts because the context rides in storage. Failed executions mark
  the span errored.
- **Metrics on the `MyStack.Jobs` meter** (architecture §3's table): `jobs.enqueued` tagged
  `job_type`, and `jobs.executions` tagged `job_type` and `outcome` — `succeeded`, `failed`
  (retry scheduled) or `dead_lettered`, counted from the state transitions Hangfire actually
  persisted. `dead_lettered` is the alert signal; the dashboard is for the human that alert pages.
- **Transactional enqueue is available, opt-in.** Wrapping a `SaveChanges` and an `Enqueue` in
  one `TransactionScope` commits or rolls back both as a single local Postgres transaction —
  verified, not assumed; the constraints live in architecture §3.3. A bare `Enqueue` remains its
  own write.

## Health

Two endpoints, both unauthenticated, both `no-store`.

| Endpoint | Runs | Answers |
| --- | --- | --- |
| `/health/live` | nothing | Can this process still respond? |
| `/health/ready` | `database`, `database-schema` | Should this instance be sent traffic? |

**Liveness deliberately checks no dependency.** A liveness probe that fails on a database blip has
an orchestrator restart every instance at once, which turns a recoverable outage into a restart
loop. Dependencies belong to readiness, where the consequence is "stop routing" rather than "kill
the process".

Readiness runs two real checks:

- **`database`** — `CanConnectAsync` over the pooled `AuthDbContext`, so it exercises the same
  connection path the application uses rather than opening one of its own.
- **`database-schema`** — unapplied migrations. A schema behind the code doesn't fail on boot; it
  fails on the first query needing the new column, which is much later and much less obvious.

The JSON body names each check, its status and its duration. It never carries the exception: a
check that throws has its message copied into `Description` by the framework, and that message is
where a connection string would reach an unauthenticated response.

## Telemetry

`server/shared/MyStack.Observability` wires the host: OpenTelemetry traces, metrics and logs over
OTLP, and the W3C trace id on every console line — carried as a scope, so a line logged outside a
request correctly has none.

```bash
docker compose --profile otel up -d                          # dashboard UI :18888, OTLP :18889
dotnet run --project server/auth/src --launch-profile otel   # auth, exporting to it
```

Export happens only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. The `otel` launch profile sets it
(plus a five-second metric export interval, so counters appear while you watch); the default
profile doesn't, so a bare `dotnet run` never spends the exporter's retry budget against a
dashboard that isn't running.

What auth emits today:

- **Traces** — inbound requests (ASP.NET Core), outbound HTTP, Npgsql's database spans, and a
  span per job execution that links back to the enqueuing request's trace (see Background jobs).
  `/health/*` requests are filtered out: probes are polled forever, and their spans would be most
  of the volume while answering nothing. Their child queries drop with them (the default sampler is
  parent-based); the boot-time migration queries stay, as root spans of real work.
- **Metrics** — the host meters (ASP.NET Core, HTTP client, .NET runtime, Npgsql) plus the domain
  counters from [architecture §3's table](architecture.md): `auth.sign_ins`, tagged
  `result` (`success`, `invalid_credentials`, `locked_out`, `not_allowed`,
  `requires_two_factor`), and `auth.oauth.grants`, tagged `grant_type` and `result`. Grants are
  counted where every token response passes (OpenIddict's `ApplyTokenResponse` event), so
  protocol rejections — a password-grant attempt, a bad PKCE verifier — are counted too;
  client-supplied grant types collapse to a closed set first. `MyStack.Jobs` adds
  `jobs.enqueued` and `jobs.executions`. Domain meters and activity sources follow the
  `MyStack.*` naming convention, which the library subscribes by wildcard.
- **Logs** — every log line, with scopes and the formatted message.

### Request logging

One envelope line per request — method, path, status, duration — via ASP.NET Core's HTTP-logging
middleware, configured in `MyStack.Observability` so `api` inherits the same shape.

- `/health/*` is suppressed by an interceptor — the same reasoning as the span filter.
- **Query strings are never logged here**: confirm/reset tokens ride them, the same reason span
  query-redaction stays on. `api`, whose query strings are paging, opts in by post-configuring
  `HttpLoggingOptions`.
- Bodies wait for the `[Redact]` masking machinery (architecture §3); response bodies are never
  logged.
- Volume is ordinary log configuration: the `Microsoft.AspNetCore.HttpLogging` category is
  `Information` in `appsettings.json`, and any environment can quiet it (or turn on more fields)
  through standard `Logging:LogLevel` configuration.

Unhandled exceptions are caught inside the request log — the envelope records the 500 the client
actually received — logged with the trace id, and answered as a ProblemDetails body carrying
`traceId`. The exception message itself never reaches the response.

The resource identity is `service.namespace=mystack`, `service.name=auth` — the namespace is what
groups `api` and `auth` as one product in a telemetry backend that sees more than this stack. The
version reads the app assembly, not the entry assembly, which under `WebApplicationFactory` would
be the test host.

Two conventions with teeth:

- **URL query redaction stays on for auth** — the instrumentation's default, kept deliberately.
  Confirmation and reset tokens will travel in query strings here, so `url.query` on a span is a
  credential leak. `api` will make the opposite call (its query strings are paging, worth seeing);
  the asymmetry is the point.
- **`act.sub`** — when a principal carries an RFC 8693 `act` claim, enrichment middleware tags the
  request span and every log line in its scope with the acting party's subject. Nothing sets the
  claim yet; the seam exists so impersonation (architecture §3.2) never has to reopen this code.

## Security posture

Headers come from [`NetEscapades.AspNetCore.SecurityHeaders`](https://github.com/andrewlock/NetEscapades.AspNetCore.SecurityHeaders)
as middleware, so a response no endpoint handled carries them too. The policy is the library's
**API baseline** (`AddDefaultApiSecurityHeaders`) — which keeps the parts that drift as browsers
move, like the `Permissions-Policy` feature list, maintained upstream instead of by hand — plus
three tightenings:

| Header | Value | |
| --- | --- | --- |
| `Content-Security-Policy` | `default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'` | baseline + `base-uri`/`form-action` pinned shut |
| `Cross-Origin-Resource-Policy` | `same-origin` | baseline says `same-site`; nothing legitimate embeds an auth response |
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains` | baseline lacks `includeSubDomains` |
| `Permissions-Policy` | the library's maintained deny list | baseline |
| `Referrer-Policy` | `no-referrer` | baseline |
| `X-Content-Type-Options` | `nosniff` | baseline |
| `X-Frame-Options` | `DENY` | baseline |
| `Cross-Origin-Opener-Policy` | `same-origin` | baseline |
| `Cross-Origin-Embedder-Policy` | `require-corp` | baseline |

The CSP is written for a host that serves no HTML. Rendered pages carry a second, named policy
differing in exactly one directive — `form-action 'self'`, so the sign-in form can post back to
itself — which is the right way round for the one deployable that holds credentials: the
loosening is opt-in per endpoint, and the design pass widens `style-src` only when there is
styling to allow. `Referrer-Policy` is `no-referrer` everywhere because confirmation and reset
links carry a single-use credential in the query string.

HSTS is emitted by the library on https responses only, with localhost excluded — so development
never sees it and no environment gate is needed. **Preload is off**: it puts the domain on a list
shipped inside browsers and is painful to undo, so it is an operator's decision rather than a
framework default. Kestrel's `Server` header is suppressed.

## Production hardening — open items

Recorded as they appear, resolved in the finalize pass (auth-track's final step).

- **Forwarded headers are not configured.** Behind a TLS-terminating proxy `Request.Scheme` will be
  `http`, which OpenIddict's discovery document and redirect URI validation both care about — and
  which also means **HSTS is silently not emitted** there, since the library only writes it on
  requests it sees as https. It waits for a decided deployment topology (architecture D12) because
  `UseForwardedHeaders` without a `KnownProxies` list is spoofable.
- **`Database:Migrate` is not safe for concurrent instances.** EF Core takes no lock around
  `MigrateAsync` here. Architecture §3.4's session-scoped advisory lock has to span migrate *and*
  seed, so it lands with seeding; until then the switch is a development convenience and defaults
  off.
- **OpenIddict key material outside development is not configured.** Development uses the dev
  certificates and tests use ephemeral keys; a deployed environment has to supply signing and
  encryption credentials deliberately — a certificate from the environment, never one generated
  on boot — and startup throws without them. Deciding the source (file, store, secret manager)
  belongs with the deployment topology (architecture D12).
