# MyStack

A reusable boilerplate for spinning up new apps: an OAuth2/OIDC authorization server, a REST API,
and a web front end that talks to both without the browser ever seeing a token.

| Deployable    | Stack                                            | Role                                                                             |
| ------------- | ------------------------------------------------ | -------------------------------------------------------------------------------- |
| `server/auth` | .NET — ASP.NET Core + OpenIddict + Identity + EF | Owns users, credentials, roles and permission overrides. Issues JWTs.            |
| `server/api`  | .NET — ASP.NET Core + FastEndpoints + EF         | Resource server. Validates JWTs from `auth`. Every error is ProblemDetails.      |
| `server/worker` | .NET — Wolverine over RabbitMQ                 | Background worker. Consumes messages from its own queue — email delivery next.   |
| `apps/web`    | TanStack Start (React)                           | BFF + SPA. Does the OIDC dance server-side, holds tokens in an httpOnly cookie.  |
| `apps/admin`  | TanStack Start (React)                           | Admin console — post-v1, designed for but outside the v1 scope boundary.         |

```
Browser ──fetch /api/*──▶ apps/web (BFF) ──Bearer <jwt>──▶ server/api ──validates──▶ server/auth
```

## What exists today

The foundation — toolchain, CI gate, local infrastructure — and `server/auth` through its
OpenIddict server and messaging: Identity over EF Core/Postgres, health checks and security
headers, `server/shared/MyStack.Observability` (traces, metrics and logs over OTLP, request
logging), token issuance via authorization code + PKCE with refresh tokens and a functional
sign-in page, and `server/shared/MyStack.Messaging` — Wolverine over RabbitMQ with per-app queues,
a retry-then-dead-letter policy, and the `server/worker` deployable consuming alongside auth,
whose daily token-prune flows through the broker — plus config-driven seeding, so a fresh
database boots to working clients, roles and accounts, provable from the committed
`bruno/` collection ([docs/auth.md](docs/auth.md)). No account flows yet.
[docs/auth-track.md](docs/auth-track.md) is the working order for building `auth` to done, and
[docs/architecture.md §7](docs/architecture.md) is the honest answer to "what is built?".

## Getting started

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), Docker with Compose.

```bash
cp .env.example .env                  # optional — the defaults work untouched
docker compose up -d                  # postgres + rabbitmq + mailpit
dotnet tool restore                   # csharpier, dotnet-ef
dotnet run --project server/auth/src  # auth on :5100, migrating + seeding on the way up
```

| Service                                 | Port                    | For                                          |
| --------------------------------------- | ----------------------- | -------------------------------------------- |
| Postgres                                | 5432                    | every app's database                         |
| [RabbitMQ](http://localhost:15672)      | 5672 (AMQP), 15672 (UI) | the message broker; UI login guest/guest     |
| [Mailpit](http://localhost:8025)        | 8025 (UI), 1025 (SMTP)  | the local inbox — email is genuinely sent    |
| [Telemetry dashboard](http://localhost:18888) | 18888 (UI), 18889 (OTLP) | traces, metrics and logs; `--profile otel` |

The telemetry dashboard is opt-in: `docker compose --profile otel up -d`, then run the app with
`--launch-profile otel` so it has somewhere to export to. It's the Aspire dashboard image today,
but the apps only speak OTLP, so any collector can take its place.

## Working on it

```bash
dotnet build server/MyStack.slnx     # build everything .NET
dotnet test server/MyStack.slnx      # every test project — needs Docker, the suites run containers
dotnet csharpier format .            # format (CI runs `csharpier check .`)

dotnet ef migrations add <Name> --project server/auth/src --output-dir Data/Migrations
```

CI runs exactly that as one required `gate` check. `main` is protected: PRs only, gate green,
squash merge.

## Docs

| Doc                                              | Records                                                       |
| ------------------------------------------------ | --------------------------------------------------------------- |
| [architecture.md](docs/architecture.md)          | The stack, the layout, the scope boundary, the decisions      |
| [auth.md](docs/auth.md)                          | `server/auth` — schema, health, security posture              |
| [auth-track.md](docs/auth-track.md)              | Working doc: the order `server/auth` is being built in        |

Further docs (`api.md`, `web.md`, `jobs-and-email.md`, `authorization.md`) arrive with the things
they describe.

## Licence

[Apache 2.0](LICENSE).
