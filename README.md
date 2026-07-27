# MyStack

A reusable boilerplate for spinning up new apps: an OAuth2/OIDC authorization server, a REST API,
and a web front end that talks to both without the browser ever seeing a token.

| Deployable    | Stack                                            | Role                                                                             |
| ------------- | ------------------------------------------------ | -------------------------------------------------------------------------------- |
| `server/auth` | .NET — ASP.NET Core + OpenIddict + Identity + EF | Owns users, credentials, roles and permission overrides. Issues JWTs.            |
| `server/api`  | .NET — ASP.NET Core + FastEndpoints + EF         | Resource server. Validates JWTs from `auth`. Every error is ProblemDetails.      |
| `apps/web`    | TanStack Start (React)                           | BFF + SPA. Does the OIDC dance server-side, holds tokens in an httpOnly cookie.  |
| `apps/admin`  | TanStack Start (React)                           | Admin console — post-v1, designed for but outside the v1 scope boundary.         |

```
Browser ──fetch /api/*──▶ apps/web (BFF) ──Bearer <jwt>──▶ server/api ──validates──▶ server/auth
```

## What exists today

Only the foundation: the toolchain, the CI gate, and local infrastructure. `server/auth` is next —
[docs/auth-track.md](docs/auth-track.md) is the working order for building it to done, and
[docs/architecture.md §7](docs/architecture.md) is the honest answer to "what is built?".

## Getting started

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), Docker with Compose.

```bash
cp .env.example .env          # optional — the defaults work untouched
docker compose up -d          # postgres + mailpit
dotnet tool restore           # csharpier
```

| Service                                 | Port                    | For                                          |
| --------------------------------------- | ----------------------- | -------------------------------------------- |
| Postgres                                | 5432                    | every app's database                         |
| [Mailpit](http://localhost:8025)        | 8025 (UI), 1025 (SMTP)  | the local inbox — email is genuinely sent    |
| [Aspire dashboard](http://localhost:18888) | 18888 (UI), 18889 (OTLP) | traces, metrics and logs; `--profile otel`   |

The Aspire dashboard is opt-in: `docker compose --profile otel up -d`.

## Working on it

```bash
dotnet build server/MyStack.slnx     # build everything .NET
dotnet test server/MyStack.slnx      # every test project
dotnet csharpier format .            # format (CI runs `csharpier check .`)
```

CI runs exactly that as one required `gate` check. `main` is protected: PRs only, gate green,
squash merge.

## Docs

| Doc                                              | Records                                                       |
| ------------------------------------------------ | --------------------------------------------------------------- |
| [architecture.md](docs/architecture.md)          | The stack, the layout, the scope boundary, the decisions      |
| [auth-track.md](docs/auth-track.md)              | Working doc: the order `server/auth` is being built in        |

Further docs (`auth.md`, `api.md`, `web.md`, `jobs-and-email.md`, `authorization.md`) arrive with
the things they describe.

## Licence

[Apache 2.0](LICENSE).
