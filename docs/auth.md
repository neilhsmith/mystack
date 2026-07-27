# server/auth

The OAuth2/OIDC authorization server. It owns users, credentials, roles and permission overrides,
and it is the only deployable that issues tokens.

**What exists today is the host skeleton**: ASP.NET Core Identity over EF Core/Postgres, the first
migration, health checks and the security-header set. There is no OpenIddict, no rendered page and
no account flow yet. [auth-track.md](auth-track.md) is the order the rest lands in;
[architecture.md §7](architecture.md) is the inventory.

## Running it

```bash
docker compose up -d                  # postgres
dotnet run --project server/auth/src  # http://localhost:5100
```

Development migrates on boot, so a fresh database needs nothing else.

## Configuration

| Key | Default | Notes |
| --- | --- | --- |
| `ConnectionStrings:AuthDb` | none | Required. Startup throws without it — there is deliberately no fallback, because a default here would be a credential compiled into the binary. |
| `Database:Migrate` | `false` | Applies pending migrations before the host serves. On in development; a deployment applies migrations as its own step. |

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

## Security posture

Every response carries, from middleware rather than from any endpoint:

| Header | Value |
| --- | --- |
| `Content-Security-Policy` | `default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'` |
| `Permissions-Policy` | the sensitive feature set denied outright |
| `Referrer-Policy` | `no-referrer` |
| `X-Content-Type-Options` | `nosniff` |
| `X-Frame-Options` | `DENY` |
| `Cross-Origin-Opener-Policy` | `same-origin` |
| `Cross-Origin-Resource-Policy` | `same-origin` |
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains`, outside development only |

The CSP is written for a host that serves no HTML. The sign-in page has to loosen it deliberately,
which is the right way round for the one deployable that holds credentials. `Referrer-Policy` is
`no-referrer` everywhere because confirmation and reset links carry a single-use credential in the
query string.

HSTS **preload is off**: it puts the domain on a list shipped inside browsers and is painful to
undo, so it is an operator's decision rather than a framework default. Kestrel's `Server` header is
suppressed.

## Production hardening — open items

Recorded as they appear, resolved in the finalize pass (auth-track step 10).

- **Forwarded headers are not configured.** Behind a TLS-terminating proxy `Request.Scheme` will be
  `http`, which OpenIddict's discovery document and redirect URI validation both care about. It
  waits for a decided deployment topology (architecture D12) because `UseForwardedHeaders` without
  a `KnownProxies` list is spoofable.
- **`Database:Migrate` is not safe for concurrent instances.** EF Core takes no lock around
  `MigrateAsync` here. Architecture §3.4's session-scoped advisory lock has to span migrate *and*
  seed, so it lands with seeding; until then the switch is a development convenience and defaults
  off.
