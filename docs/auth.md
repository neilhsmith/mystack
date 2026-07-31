# server/auth

The OAuth2/OIDC authorization server. It owns users, credentials, roles and permission overrides,
and it is the only deployable that issues tokens.

**What exists today is the host skeleton, telemetry, the OpenIddict server, messaging, and
seeding**: ASP.NET Core Identity over EF Core/Postgres, health checks, the security-header set,
`MyStack.Observability` wired in, OpenIddict issuing tokens — authorization code + PKCE with
refresh tokens, the four protocol endpoints, a functional sign-in page, request logging and the
first domain counters — `MyStack.Messaging` speaking Wolverine over RabbitMQ, with the daily
token-prune flowing through the broker, and config-driven seeding bringing a fresh database to a
working state before the host serves. There is no account flow yet — sign-up, confirmation and
password reset arrive in auth-track step 8.
[auth-track.md](auth-track.md) is the order the rest lands in; [architecture.md §7](architecture.md)
is the inventory.

## Running it

```bash
docker compose up -d                  # postgres + rabbitmq + mailpit
dotnet run --project server/auth/src  # http://localhost:5100
```

Development migrates **and seeds** on boot, so a fresh database needs nothing else: the boot
leaves behind the `web-bff` and `bruno` clients, the `api.read`/`api.write` scopes, the
`globaladmin`/`admin`/`user` roles, and one account per role — `globaladmin@mystack.local`,
`admin@mystack.local` and `user@mystack.local`, all `Devpass!word123`. The protocol surface hangs
off `/.well-known/openid-configuration`; the sign-in page is `/signin`. The broker's queues live
in RabbitMQ's management UI at `http://localhost:15672` (guest/guest).

The committed [Bruno](https://www.usebruno.com/) collection in `bruno/` drives the real
authorization-code + PKCE dance against the seeded `bruno` client: open the folder in Bruno,
pick the **Local** environment, use the collection's OAuth2 settings to fetch a token (sign in as
the global admin), then run **Auth → Decode Access Token** and read the claims off the console.

## Configuration

| Key | Default | Notes |
| --- | --- | --- |
| `ConnectionStrings:AuthDb` | none | Required. Startup throws without it — there is deliberately no fallback, because a default here would be a credential compiled into the binary. |
| `Database:Migrate` | `false` | Applies pending migrations before the host serves. On in development; a deployment applies migrations as its own step. |
| `Database:Seed` | `true` | One safe seed pass before the host serves — roles/scopes from code, clients/accounts from config. Safe to leave on everywhere (writes only on real drift); off is the escape hatch for an organisation managing clients out of band. |
| `Seed:Clients` | `[]` | The OIDC clients to reconcile — id, display name, `Public`/`Confidential` + secret, redirect URIs, scopes. What a client may *do* is fixed in code; see Seeding. |
| `Seed:Users` | `[]` | The accounts to ensure — email, roles, optional password. At least one must carry `globaladmin`, or startup throws: seeding guarantees somebody can administrate. |
| `Oidc:AccessTokenLifetime` | `00:15:00` | The bound on revocation latency (architecture §3.1): a role change or revoked override lives at most this long in issued tokens. |
| `Oidc:IdentityTokenLifetime` | `00:15:00` | |
| `Oidc:AuthorizationCodeLifetime` | `00:05:00` | One redemption, minutes to make it. |
| `Oidc:RefreshTokenLifetime` | `14.00:00:00` | Absolute horizon; the token itself rotates on every use. |
| `ConnectionStrings:MessageBroker` | none | Required, same no-fallback rule as the database: failing to boot beats silently dropping messages. |
| `Messaging:RetryCooldownsInSeconds` | `[1, 5, 30]` | Seconds between redelivery attempts after a handler throws, one entry per retry; past the last one the message dead-letters. Tests set `[0]`. |

`appsettings.Development.json` carries the compose stack's connection string. Those are local
infrastructure credentials, not secrets — every other environment supplies
`ConnectionStrings__AuthDb` from its own configuration, and user-secrets is the local override.

## The schema

Identity's tables live in a Postgres schema named **`auth`**, inside the same database `server/api`
will use. That is the split `MyStack.Messaging` also assumes when it puts its envelope storage in
`wolverine_auth` (architecture §3.3), so the hosts share one Postgres instance without sharing a
namespace.

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

**Clients come from seed configuration** (see Seeding): development declares `web-bff` and
`bruno` in `appsettings.Development.json`, the test suite declares its client the same way, and
every one of them takes the only shape the seeder can produce — authorization code + PKCE +
refresh, implicit consent, no exceptions.

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

## Seeding

Architecture §3.4's model, in full: one always-on-by-default `Database:Seed` switch over one safe
pass in `AuthSeeder`. What makes always-on safe is that every account is config-declared — no
environment receives anything it didn't declare — and writes happen only on real drift.

**Code-declared, DB-materialized:** the roles (`AuthRoles`: `globaladmin`, `admin`, `user`) and
the API scopes (`api.read`, `api.write`, resource `api`). These are fixed in code — a role that
exists as a row but not in the API's permission map grants nothing, so a config knob would only
create ways to be wrong.

**Config-declared:** the OIDC clients and the accounts (`Seed:Clients`, `Seed:Users`) — redirect
URIs, secrets, addresses and which of each exist genuinely differ per environment. Config decides
*which* clients exist and where they redirect; *what a client may do* is fixed in code: every
seeded client is authorization code + PKCE + refresh with implicit consent, and there is
deliberately no knob for grant types, so no configuration can reintroduce the password grant.
Confidential clients require a secret and public clients refuse one; missing required values
throw and abort startup — failing to boot beats booting wrong, and a silently-defaulted secret
would ship in a public repo. The client-credentials shape for machine clients arrives with
auth-track step 10.

**Seeding guarantees an administrator.** At least one declared user must carry the `globaladmin`
role, or startup throws — the deliberate opt-out of seeded accounts is the switch, never a config
that quietly leaves nobody able to administrate.

**Accounts carry no password in production.** A `Seed:Users` entry without a `Password` is
created with a confirmed email and no usable password; it is activated through the ordinary
forgot-password flow once account flows exist (step 8) — only the address is configured.
Development supplies passwords directly (one convenience account per role), because convenience
is the entire point there.

**Everything reconciles on real drift.** Ensured by natural key — client id, scope name, role
name, email — never by "is the table empty", and config is the source of truth for what it
declares: a changed redirect URI, display name or scope list updates the stored client on the
next boot (the secret compared through `ValidateClientSecretAsync`, since it's stored hashed),
and a declared account's roles sync exactly to config. An unchanged item is never rewritten. The
one carve-out is **passwords, which reconcile only where config declares one** — an absent
password is "no opinion", never "remove it", so production, which declares addresses alone,
can never reset a password a human set through the reset flow. Accounts config doesn't declare
are never touched, and seeding never deletes.

**Mechanics.** `DatabaseInitializer` runs in `IHostedLifecycleService.StartingAsync` — before
Kestrel binds, so nothing serves mid-seed. Concurrent instances are serialized by a
session-scoped `pg_advisory_lock` on a dedicated connection, held across migrate *and* seed
(transaction-scoped locks can't span `MigrateAsync`, which runs its own transactions). The seed
itself runs in one transaction inside that lock, so a mid-seed failure leaves nothing
half-written.

## Messaging

`server/shared/MyStack.Messaging` wires Wolverine over RabbitMQ with the stack's conventions —
auth is its first host, `server/worker` its second (architecture §3.3):

- **Auth consumes exactly its own queue** (`auth` — every app's queue is named after it), and
  Wolverine persists envelopes durably in the **`wolverine_auth`** schema before they ride the
  broker, so a crash between publish and broker-ack loses nothing. Which messages auth publishes
  is declared in auth's own composition root, not in the library.
- **A handler that throws retries on the cooldown schedule** (`Messaging:RetryCooldownsInSeconds`)
  **and then dead-letters** into the broker's `wolverine-dead-letter-queue` — parked where the
  management UI can inspect, shovel back or delete it, never silently dropped.
- **The first real flow is the token prune**: `oidc_tokens` and `oidc_authorizations` gain rows
  on every sign-in and OpenIddict never deletes them on its own, so
  `AddScheduledMessage<PruneOidcTokens>("0 3 * * *")` publishes the message daily and auth's own
  handler prunes expired and invalidated entries older than 30 days — comfortably past the 14-day
  refresh horizon, and `PruneAsync` itself never touches a live grant. Auth handles this itself
  because pruning touches auth's tables; cross-app work (email, step 7) goes to the worker's
  queue instead. Scheduling is one declarative line per schedule: the library's clock publishes
  and the handler's queue owns everything else. Cron strings are validated at boot (Cronos), and
  the semantics are deliberate — a missed tick skips, a duplicate publish is safe, because
  schedules carry idempotent maintenance work.
- **Traces cross the queue on their own.** Wolverine propagates W3C context and publishes the
  `Wolverine` activity source plus a `Wolverine:auth` meter (`wolverine-messages-sent`,
  `wolverine-execution-time`, `wolverine-execution-failure`, `wolverine-dead-letter-queue` — the
  last one is the alert signal), both subscribed by `MyStack.Observability`.
- **Handlers may depend on framework services.** The library sets Wolverine's
  `ServiceLocationPolicy` to allowed: the v6 default forbids service location in generated handler
  code, which would ban dependencies on anything factory-registered — OpenIddict's managers, which
  the prune handler injects, are exactly that.

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

- **Traces** — inbound requests (ASP.NET Core), outbound HTTP, Npgsql's database spans, and
  Wolverine's publish/receive/handle spans, which carry W3C context across the queue (see
  Messaging). `/health/*` requests are filtered out: probes are polled forever, and their spans
  would be most of the volume while answering nothing. Their child queries drop with them (the
  default sampler is parent-based); the boot-time migration queries stay, as root spans of real
  work.
- **Metrics** — the host meters (ASP.NET Core, HTTP client, .NET runtime, Npgsql) plus the domain
  counters from [architecture §3's table](architecture.md): `auth.sign_ins`, tagged
  `result` (`success`, `invalid_credentials`, `locked_out`, `not_allowed`,
  `requires_two_factor`), and `auth.oauth.grants`, tagged `grant_type` and `result`. Grants are
  counted where every token response passes (OpenIddict's `ApplyTokenResponse` event), so
  protocol rejections — a password-grant attempt, a bad PKCE verifier — are counted too;
  client-supplied grant types collapse to a closed set first. Wolverine adds its own
  `Wolverine:auth` meter. Domain meters and activity sources follow the `MyStack.*` naming
  convention, which the library subscribes by wildcard.
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
- **OpenIddict key material outside development is not configured.** Development uses the dev
  certificates and tests use ephemeral keys; a deployed environment has to supply signing and
  encryption credentials deliberately — a certificate from the environment, never one generated
  on boot — and startup throws without them. Deciding the source (file, store, secret manager)
  belongs with the deployment topology (architecture D12).
