# Deploy track

The path from "everything runs on this machine" to "the whole stack hosts itself": one cheap VPS,
every deployable a container behind one proxy, deploys that happen because `main` moved.
[architecture.md](architecture.md) §6 set that shape and deliberately left the mechanism open
(D12); this doc is the order it lands in, auth-track-style — phases in dependency order, each a
handful of one-concern PRs, decisions recorded where they're made. It supersets
[auth-track.md](auth-track.md) §16, which stays as the auth-code slice this track consumes as its
phase 2.

This is new territory on purpose. Each phase lands with a walkthrough of the concepts it
introduces, and the pace follows understanding, not the calendar.

Two goals anchor the track, in order:

1. **The whole app as one docker group.** One production-shaped compose file brings up proxy,
   Postgres, RabbitMQ, migrations, auth and worker as a unit — on this machine, in a VM on the
   LAN, on anything that runs Docker.
2. **Hosted for real.** A VPS runs that same group under a bought domain with real TLS and real
   email; GitHub Actions builds images and deploys on merge; migrations run themselves; and a
   redeploy drops nothing — or we know exactly what it drops and why.

Decided up front (2026-08-02), so the phases don't relitigate them:

- **Email: Resend.** It exposes plain SMTP, so `MyStack.Email` ships unchanged — production is a
  verified sending domain (SPF + DKIM) plus credentials in configuration.
- **Domain: dedicated, to be bought.** `mystack.example` stands in until then. Nothing before
  phase 4 needs it.
- **VPS: compared in phase 4**, criteria on the table, picked there.
- **Mechanism: not pre-decided.** Phase 5 builds compose-over-SSH and spikes Kamal 2 against the
  same stack; D12 is resolved by that comparison, not by a review's hunch.

---

## Phase 1 — parameterize: many stacks, one machine

**Goal:** two (or five) complete, isolated instances of the stack — infrastructure containers and
apps — running side by side on one machine, each configured entirely by environment.

Most of this exists. `compose.yaml` already namespaces containers, network and volumes by
`COMPOSE_PROJECT_NAME` and exposes every host port as an env-overridable variable; and every URL
the apps consume — connection strings, broker, SMTP host, `Account:PublicBaseUrl`, seeded client
redirect URIs, the OTLP endpoint — is .NET configuration, which environment variables override by
construction (`Account__PublicBaseUrl`, `Seed__Clients__0__RedirectUris__0`, …). Nothing
URL-shaped is compiled in; the survey found `localhost` only in the two
`appsettings.Development.json` defaults, `launchSettings.json`, and tests.

What's missing is the one-knob story:

- **A per-instance `.env` file both halves read.** Compose reads `.env` natively; `dotnet run`
  does not. Decide the glue — a small `scripts/` wrapper that exports it, a Development-only
  dotenv loader, or documented `env $(cat .env)` invocation — and use it for the app ports too
  (`ASPNETCORE_URLS` / `--urls` already beat `launchSettings.json`'s defaults).
- **The cookie-jar collision.** Browsers scope cookies to host, not port, so two auth instances
  on `localhost:5100` and `localhost:5101` overwrite each other's session cookies. Two candidate
  fixes, decided in the phase: per-instance cookie names from configuration, or per-instance
  hostnames (`auth1.localhost`, `auth2.localhost` — `*.localhost` resolves to loopback).
- **The dev-client seeds follow the instance.** The seeded redirect/client URIs and `bruno`'s
  environment must track the chosen ports, not assume `3000`/`5100`/`8090`.

**Deliverable:** a documented recipe, proven by running `mystack` and `mystack2` concurrently and
completing the sign-in flow in both.

## Phase 2 — production trust in code

**Goal:** `ASPNETCORE_ENVIRONMENT=Production` boots on a laptop against production-shaped
configuration. This is [auth-track.md](auth-track.md) §16's code half, unchanged:

- **OpenIddict key material from configuration** — a config-loaded signing + encryption
  certificate path replacing the dev certs production refuses to boot without, with a two-key
  JWKS overlap so keys can rotate without invalidating live tokens.
- **`SetIssuer` pinned from configuration** — a production IdP names itself; the issuer stops
  following the Host header.
- **Forwarded headers** — `UseForwardedHeaders` with `KnownProxies`/`KnownNetworks` pinned to the
  proxy, cookie `SecurePolicy=Always`, so scheme, HSTS and the `Secure` flag survive TLS
  terminating at the proxy instead of in the app.
- **The data-protection key ring's at-rest posture** — `ProtectKeysWithCertificate` once key
  material exists, or a recorded acceptance that DB read access implies key compromise on a
  single-box deploy.
- **The Mailpit invariant becomes code** — outside Development, startup refuses an unauthenticated
  SMTP shape (architecture §6: mail silently going nowhere is worse than an outage).

**Deliverable:** a `Production` boot with real config on localhost — tokens issued, cookies
`Secure`, issuer pinned — before any container exists.

## Phase 3 — containerize: the app joins the docker group

**Goal:** goal #1 — the entire stack as one production-shaped compose group, no `dotnet` on the
host.

- **Dockerfiles for auth and worker** — multi-stage publish onto the chiseled (non-root) ASP.NET
  base, `/health/ready` as the probe.
- **The migration step as a one-shot container** — an EF bundle image that runs to completion
  before the apps start (`depends_on: service_completed_successfully`); `Database:Migrate` stays
  `false` everywhere but local dev.
- **`deploy/compose.yaml`** — proxy (Caddy is the working assumption: config that fits on one
  screen and automatic TLS when phase 4 arrives), Postgres, RabbitMQ, migrations, auth, worker.
  Mailpit is absent by construction, per architecture §6. `/health/*` is never routed by the
  proxy.
- **Shutdown and hygiene** — `stop_grace_period` ≥ 30s so Wolverine drains in-flight messages on
  SIGTERM; json-file log rotation limits so a chatty container can't fill a disk.
- **Real worker readiness** — its `/health/ready` currently runs no checks; a probe that can't
  fail is not a probe.

**Deliverable:** `docker compose -f deploy/compose.yaml up` on any Docker host — this machine or
a LAN VM — serves the whole app through the proxy.

## Phase 4 — host it: the box and the names

**Goal:** phase 3's group on the public internet with real TLS and real email.

- **Pick the VPS** — a comparison lands here first: Hetzner, DigitalOcean, Vultr, Netcup at
  minimum, on $/GB RAM, US regions, egress terms, snapshot/backup pricing and track record. The
  stack wants ~2 vCPU / 4 GB to start.
- **Provision it like it will be attacked** — SSH keys only, a non-root deploy user, a firewall
  that admits 80/443/SSH and nothing else, unattended security upgrades. RabbitMQ's management UI
  stays operator-only (SSH tunnel; D15's aggregated view is the eventual answer).
- **Buy the domain and lay out DNS** — auth on its own hostname, an apex/app placeholder for the
  future web app, and a dedicated sending subdomain for Resend's SPF/DKIM verification.
- **TLS at the proxy** — Caddy's automatic HTTPS earns its keep here.
- **Resend wired** — domain verified, SMTP credentials into the box's environment, a real
  password-reset email received in a real inbox.
- **Secrets posture** — a `.env` on the box (mode 600, deploy user) mirrored by GitHub Actions
  secrets; anything fancier (sops/age) is parked until two boxes exist.

**Deliverable:** sign-in, registration and password reset working end-to-end on the real domain.

## Phase 5 — CI/CD: deploys without hands, and D12 decided

**Goal:** merge to `main` → live, and the compose-vs-Kamal question answered by having done both.

- **Build + push on merge** — GHCR images for auth, worker and the migration bundle, tagged with
  the commit SHA.
- **Deploy A: compose over SSH** — the honest baseline: an Action that copies the compose file,
  pulls images, runs the migration container, `up -d`. Transparent, debuggable, and briefly
  down during the swap.
- **Deploy B: the Kamal 2 spike** — the same stack deployed by Kamal: registry push, health-gated
  swap behind kamal-proxy, `kamal rollback`. Judged on understandability, real downtime, rollback
  story, secret handling, and how it treats the non-app containers (accessories) next to phase
  3's compose file.
- **Record D12** in [architecture.md](architecture.md) with the winner and the reasons.
- **Migration discipline** — the bundle always runs before new app containers, which phase 6
  sharpens into expand/contract.

**Deliverable:** a merged PR reaching production in minutes with no hands on the box, twice — once
per mechanism — and a decision.

## Phase 6 — the zero-downtime exploration

**Goal:** a measured redeploy that drops zero requests, and a written account of what made it
possible.

- **Measure the baseline first** — what a plain `compose up -d` swap actually costs under load.
- **The mechanism depends on D12** — kamal-proxy does health-gated swaps natively; the compose
  path builds it: two app containers behind Caddy, flip upstreams when the new one reports ready
  (single-node Swarm's `start-first` rolling update is the third candidate).
- **The app-side prerequisites are phase 2, and that's the lesson** — two versions serve side by
  side only because signing keys and the data-protection ring live in configuration and the
  database rather than in a container's memory; Wolverine consumers already compete safely; the
  in-memory rate limiter stays per-instance (accepted; Redis is the parked answer).
- **Expand/contract migrations** — during the overlap, old code runs against the new schema, so
  schema changes split into add-then-migrate-then-remove steps.

**Deliverable:** a load test running across a deploy with a flat error graph, and the doc updated
with the how.

## Phase 7 — the ops floor

**Goal:** the boring questions answered before they're incidents.

- **Postgres backups, rehearsed** — nightly `pg_dump` shipped off the box (provider object
  storage), a retention policy, and one actual restore performed. Non-negotiable before real
  accounts exist.
- **Outside-in uptime check** — something not on the box notices the box is down and says so.
- **Production telemetry destination** — the Aspire dashboard is local-only by design (D1);
  decide between a self-hosted stack and a free-tier SaaS, and until then set
  `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY` or run a local collector so the exporter stops dropping
  batches silently.
- **A queue/outbox-depth gauge** — the one metric that notices a stuck worker before users do.
- **Disk hygiene** — image prune policy beside the log rotation phase 3 set.

**Deliverable:** a `docs/runbook.md` recording where backups live, how to restore, and what to do
when the uptime check fires.

---

## What this track deliberately isn't

- **No staging environment.** One production destination; staging earns its keep when someone
  other than us uses the stack. The mechanism chosen in phase 5 must merely not preclude it.
- **No Kubernetes, no managed PaaS.** One box, containers, a proxy — architecture §6's shape. The
  point is understanding every layer, and the scale ceiling is years away.
- **No multi-node.** Everything here assumes one VPS; the first second box reopens the parked
  distributed questions (Redis rate limiting, externalized Postgres) on its own merits.
