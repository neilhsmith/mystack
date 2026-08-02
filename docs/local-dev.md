# Local development — every port, one instance or many

The canonical map of where everything runs locally, and the recipe for running several complete,
isolated instances of the stack on one machine — the normal situation once work happens in git
worktrees and agents bring up stacks of their own
([deploy-track.md](deploy-track.md) phase 1).

## The map

Defaults, so a plain checkout needs zero configuration. Every value is a knob in `.env`
(see [.env.example](../.env.example)); nothing is compiled in.

| What | Default | `.env` knob | Notes |
| --- | --- | --- | --- |
| web app | http://localhost:3000 | `WEB_PORT` | future `apps/web`; the seeded `web-bff` client already points here |
| auth | http://localhost:5100 | `AUTH_PORT` | sign-in at `/signin`, discovery at `/.well-known/openid-configuration` |
| worker | http://localhost:5200 | `WORKER_PORT` | no UI; `/health/live` proves it's up |
| Postgres | localhost:5432 | `POSTGRES_PORT` | `mystack`/`mystack`, database `mystack` (also `POSTGRES_USER/_PASSWORD/_DB`) |
| RabbitMQ | amqp://localhost:5672 | `RABBITMQ_PORT` | the broker the apps speak |
| RabbitMQ management UI | http://localhost:15672 | `RABBITMQ_UI_PORT` | guest/guest — queue depths, the dead-letter queue and its parked messages |
| Mailpit SMTP | localhost:1025 | `MAILPIT_SMTP_PORT` | where the worker actually delivers email |
| Mailpit inbox UI | http://localhost:8025 | `MAILPIT_UI_PORT` | read what got sent; e2e reads the same messages over its REST API |
| Telemetry dashboard | http://localhost:18888 | `OTEL_DASHBOARD_UI_PORT` | traces, metrics, logs — opt-in via the `otel` compose profile |
| OTLP ingest | localhost:18889 | `OTEL_DASHBOARD_OTLP_PORT` | where the apps export when telemetry is on |
| Bruno callback | http://localhost:8090/callback | `BRUNO_CALLBACK_PORT` | the port Bruno catches the OAuth redirect on; the seeded `bruno` client points here |

`scripts/dev urls` prints this table with the current instance's actual values filled in.

## One instance

```bash
scripts/dev up        # postgres + rabbitmq + mailpit, parameterized by .env if present
scripts/dev auth      # auth on AUTH_PORT, migrating + seeding on the way up
scripts/dev worker    # worker on WORKER_PORT, consuming its queue
```

With no `.env` those are exactly the defaults above, and the bare commands
(`docker compose up -d`, `dotnet run --project server/auth/src`) still behave identically —
`scripts/dev` only matters once an instance deviates from the defaults, and it is the habit worth
keeping so worktree instances never cross-attach (see the warning below).

Telemetry is opt-in twice, on purpose (the dashboard is a compose profile; the exporter is a
no-op without an endpoint):

```bash
scripts/dev up --otel      # also starts the dashboard
scripts/dev auth --otel    # exports OTLP to this instance's dashboard
```

Sign in with a seeded account (`user@mystack.local` / `Devpass!word123`, see
`appsettings.Development.json`), or drive the full OAuth dance from the `bruno/` collection
(environment **Local**, request **Auth → Sign In (Browser)**).

## Many instances

One machine runs several complete stacks side by side; each instance is defined entirely by its
`.env`:

- **Containers** — compose namespaces containers, network and the data volume by
  `COMPOSE_PROJECT_NAME`, and every host port is a `.env` variable. Only host ports can ever
  collide, which is what the port scheme below prevents.
- **Apps** — `scripts/dev` derives the .NET-side configuration from the same `.env`: the listen
  URL, connection strings, broker URL, SMTP port, `Account:PublicBaseUrl`, and the seeded
  client redirect URIs (`web-bff` from `WEB_PORT`, `bruno` from `BRUNO_CALLBACK_PORT`).
- **Cookies** — cookie names carry `Instance:Name` (set from `COMPOSE_PROJECT_NAME`), because
  browsers scope cookies to the host and ignore the port: without distinct names two instances
  don't just share a jar, each evicts the other's session ([auth.md](auth.md) § Configuration).
  Distinct names mean you can be signed into every instance in one browser at once.

### The recipe

In the worktree (or checkout) that should become instance *n*:

```bash
scripts/dev init 2    # writes .env: COMPOSE_PROJECT_NAME=mystack2, every port = default + 1000
scripts/dev up
scripts/dev auth      # in one terminal
scripts/dev worker    # in another
scripts/dev urls      # where everything for THIS instance lives
```

Instance *n* gets `defaults + (n−1)×1000`: instance 2 is auth on :6100, Mailpit UI on :9025,
Postgres on :6432, and so on — always recognizable shapes (a 6432 reads as "a Postgres").

For Bruno, duplicate the **Local** environment inside the app and set the three URL variables
(`auth_url`, `callback_url`, `mailpit_url`) to the values `scripts/dev urls` prints.

`scripts/dev exec` runs anything under the instance's derived environment — this is how tooling
targets the right database:

```bash
scripts/dev exec dotnet ef database update --project server/auth/src
```

### Caveats, all deliberate

- **Instance numbers are per *concurrently running* stack, not per branch.** Two worktrees both
  on instance 2 share a project name and fight over the same ports and containers —
  `scripts/dev urls` shows which instance a checkout is, `init --force` reassigns it.
- **n ≤ 7.** At +7000 the offsets start landing on other instances' defaults (1025+7000 = 8025).
  Seven concurrent stacks is comfortably past the practical limit anyway.
- **Instance 4's `WEB_PORT` is 6000, which browsers refuse** (`ERR_UNSAFE_PORT`). `init` warns;
  edit `WEB_PORT` by hand if that instance needs the web app in a browser.
- **Run two instances from two checkouts (worktrees), not one.** Two concurrent `dotnet run`
  from one checkout race each other's `obj/`; worktrees are the supported shape.
- **In a worktree, use `scripts/dev auth`, never bare `dotnet run`** once a `.env` exists —
  the bare command reads only `appsettings.Development.json` and silently attaches to the
  *default* instance's Postgres and worker queue.
- **Clean up finished instances with `scripts/dev down -v`** — otherwise each abandoned worktree
  instance leaves a named Postgres volume behind.
- **Tests need none of this.** The suites run Testcontainers on random host ports and in-memory
  hosts; `dotnet test` in any number of worktrees coexists with any number of running instances.
- **The conformance harness (`conformance/`) is single-instance by design** — it pins
  `auth.localtest.me:5100` and is run by hand, one at a time, against the default instance.

## For agents

Bringing up a full isolated stack in a worktree, start to finish:

```bash
scripts/dev init <n>            # pick an n no other running instance uses
scripts/dev up                  # waits for healthchecks
scripts/dev auth                # blocks; run in background
scripts/dev worker              # blocks; run in background
scripts/dev urls                # every URL you need, including the discovery document
curl -fsS "http://localhost:$((5100 + (n-1)*1000))/health/ready"
```

Register/confirm flows are drivable without a browser: register via POST, read the confirmation
link from Mailpit's REST API (`GET <mailpit>/api/v1/message/latest`), and the Bruno collection's
**Sign In (Scripted)** request runs the whole code + PKCE dance from the command line via
`bru run` if needed.

## How the glue works

Compose reads `.env` natively. `dotnet run` does not — so `scripts/dev` sources the same file and
exports the derived configuration as environment variables (`ConnectionStrings__AuthDb`,
`Account__PublicBaseUrl`, `Seed__Clients__1__RedirectUris__0`, …), which .NET's configuration
layering applies over `appsettings.Development.json`. The listen URL rides `--urls`, which as a
command-line provider beats every other source. One deliberate subtlety: `scripts/dev … --otel`
exports the OTLP endpoint itself and never uses the `otel` *launch profile* — a launch profile's
`environmentVariables` override the process environment, so the profile's fixed `:18889` would
beat the derived per-instance endpoint.
