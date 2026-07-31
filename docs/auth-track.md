# Auth track — building `server/auth` to done

A working order for one push: get `server/auth` completely built, tested, reviewed and closed
without touching `server/api` or `apps/web`.

This is **not** a re-imposition of a global sequence. `docs/architecture.md` §7 is still the
inventory and still has no order. This document is scoped to one goal, and it stops existing when
auth is done.

Each numbered step is one PR that leaves auth runnable. Shared libraries appear the moment they
have a consumer and not before.

---

## What "done" means

Auth is closed when all of these hold:

- [ ] The full authorization-code + PKCE flow works, refreshes, and signs out — driven from a
      committed Bruno collection, not from memory.
- [x] The protocol surface is complete and proven from a client's seat: userinfo, introspection,
      client credentials, the device flow, PAR, and back-channel logout, each tested.
- [x] Every account flow works end to end **including its email**: register → confirm, forgot →
      reset, change password → notification.
- [ ] Every rendered page is designed, not scaffolded, and passes an accessibility pass.
- [x] Seeding brings a fresh database to a working state in dev and in production, from config.
- [ ] Logs, traces and metrics come out of auth **running on its own**, with no other service up.
- [x] The test suite covers the token shape, anti-enumeration, the seeding pass, and every account
      flow's failure branches.
- [ ] `docs/auth.md` exists and describes what was actually built.
- [x] A production-hardening review has been done and its findings are either fixed or recorded.
      *Done as step 13: a six-track review (protocol, identity, .NET craft, observability, tests,
      ops) whose findings were fixed in the hardening pass, folded into steps 14–15, or recorded
      as step 16.*

## Deliberately out of scope

Not "forgotten" — genuinely belonging elsewhere:

- **Impersonation** — needs `apps/admin` to have any consumer (plan §3.2). Step 15 records that a
  grant-support-access endpoint is coming, which is the only thing owed now.
- **The permission catalog** — `server/api` owns it (D10). Auth mints permission strings without
  ever interpreting them, so overrides are testable here with arbitrary values.
- **BFF session handling** — `apps/web`'s. Plan §3.2's third seam belongs to that track.
- **change-email, delete-account, external IdPs, MFA** — deferred features, not v1 gaps.

---

## The order

### 1. Foundation

**Lands:** repo skeleton (README, `CLAUDE.md`, `.gitignore`, `.editorconfig`, licence), .NET
toolchain (`Directory.Build.props`, `Directory.Packages.props`, `MyStack.slnx`, CSharpier), the CI
gate workflow, branch protection, `compose.yaml`, `.env.example`.

**Compose services:** Postgres, Mailpit, Aspire Dashboard (see *Local stack* below).

**Proves:** `docker compose up` gives a database, an inbox and a telemetry UI. CI is green against
an empty repo — which is the easiest possible thing to debug, and the reason to do it now.

> The JavaScript half of the toolchain waits until there is JavaScript. The gate grows a job when
> `apps/web` arrives rather than carrying a no-op one until then.

### 2. `auth` host skeleton

**Lands:** `MyStack.Auth.csproj`, ASP.NET Core Identity over EF Core / Npgsql, `ApplicationUser`,
`AuthDbContext`, the first migration, snake_case naming, `/health/live` + `/health/ready`, the full
security-header set (CSP, frame-ancestors, Permissions-Policy, Referrer-Policy, HSTS outside dev),
the `Database:Migrate` switch. `MyStack.Auth.Tests` scaffolded with its fixture.

**Proves:** `dotnet run` boots, the migration applies against compose Postgres, `/health/live`
returns 200.

### 3. `MyStack.Observability`

First shared library. Auth is its consumer and, for now, its only proof.

**Lands:** structured logging with the W3C trace id on every line, OpenTelemetry traces + metrics +
logs over OTLP, the resource identity that will group `api` and `auth` as one product, the
`[Redact]` attribute, and the log/span enricher that emits `act.sub` when the claim is present.

Metrics here are the **host meters only** (ASP.NET Core, HTTP client, runtime, Npgsql). The domain
counters are named in architecture §3's metric table and each lands with its emitter — steps 4, 6,
7 and 8 below — never speculatively in this library.

**Proves:** auth running *by itself* produces traces and structured logs visible in the Aspire
dashboard. This is the requirement that put this step here rather than later.

> The `act.sub` enricher satisfies plan §3.2's second seam. Nothing will set that claim for a long
> time; the point is that the enricher never has to be reopened.

### 4. `auth` OpenIddict

**Lands:** OpenIddict server configuration; authorization, token, end-session and revocation
endpoints; **authorization code + PKCE**; refresh tokens (`offline_access`); config-driven token
lifetimes; a functional sign-in page (designed in step 15, not here); the claims the token carries
(`sub`, `role`, `email`, and the shape `perm` / `perm_deny` will occupy).

**Metrics:** `auth.sign_ins` and `auth.oauth.grants` (architecture §3's table) — the sign-in page
and token endpoint are their emitters, so the counters are part of building them.

**Request logging:** arrives here, with the first real traffic to log. The envelope only — method,
path, status, duration — via ASP.NET Core's built-in HTTP logging middleware, wired in
`MyStack.Observability` so `api` inherits the same shape. `/health/*` is filtered, and query
strings are excluded on auth for the same reason its span query-redaction stays on: confirm/reset
tokens ride them. The exception handler that logs unhandled errors and answers a ProblemDetails 500
lands with it. Bodies wait for the `[Redact]` masking machinery (architecture §3).

**Proves:** `/.well-known/openid-configuration` is correct; a manually registered client completes
the flow.

> **No password grant, in any environment.** Not for a dev client, not for Bruno. Plan §3 rules it
> out and `mystack-old`'s seeder is exactly where it would get copied back in from.

### 5. `auth` seeding

**Lands:** plan §3.4 in full — `Database:Seed` as a single always-on-by-default switch over one
safe pass: roles and scopes materialized from code, OIDC clients and every account from config,
ensure-by-natural-key, reconcile-on-real-drift (passwords only where config declares one), the
session-scoped advisory lock, and seeding completing before the app serves.

**Proves:** a fresh database plus `compose up` yields working clients and accounts; a second boot
writes nothing; a seed config in which nobody can administrate fails startup rather than obeying.

> **Checkpoint.** Run the complete authorization-code + PKCE dance from Bruno against a seeded
> client. Decode the token. Confirm the claims, the lifetimes, and that refresh works.

### 6. `MyStack.Messaging` + `server/worker`

**Lands:** Wolverine over RabbitMQ behind `server/shared/MyStack.Messaging` — per-app queues with
durable envelope storage in `wolverine_<app>` schemas, the retry-cooldowns-then-dead-letter
policy, W3C trace propagation across the queue — plus the `server/worker` deployable consuming its
own queue, and auth's first real flow: `PruneOidcTokens` declared as a scheduled message
(`AddScheduledMessage`, daily) and handled by auth itself (pruning touches auth's tables, so no
other deployable may do it).

**Metrics:** Wolverine's own `Wolverine:<app>` meter — `wolverine-execution-failure` and
`wolverine-dead-letter-queue` are the alert signals; the broker's management UI is the human's
view of the same parked messages.

**Proves:** a message published to a queue is handled by that host with its own DI; a handler that
throws retries on the cooldown schedule and then dead-letters where the management UI can see and
replay it; the prune actually deletes a long-expired token when its message arrives.

> **Resolved** (plan §3.3 records it): atomicity of "app write + publish" is the durable outbox's
> job, wired through Wolverine's EF Core integration when the account flows land in step 8 — a
> mechanism, not a `TransactionScope` pattern. The §4 outbox row is retired by construction.

### 7. `MyStack.Email`

**Lands:** `IEmailSender`, the `EmailMessage` / `EmailAddress` / `EmailAttachment` shape, an SMTP
adapter (MailKit), `EmailOptions`, and the renderer interface behind which the interpolated-string
bodies live.

**Metrics:** `email.sends` on the library's meter, tagged by outcome — the delivery-rate signal a
provider outage moves first.

**Proves:** unit tests against a fake sender, plus an integration test that sends to Mailpit and
reads the message back through Mailpit's REST API.

### 8. `auth` account flows

The first real consumer of steps 6 and 7.

**Lands:** register + email confirmation, forgot/reset password, change password, and a
password-changed notification. Every email is published through `MyStack.Messaging` and delivered
by the worker, with the EF outbox making "user created + email published" atomic. Anti-enumeration
throughout — generic 200s, never "that email doesn't exist".

**Carry over from `mystack-old`:** the confirmation link points at a page that POSTs on click rather
than at an endpoint that acts on GET, so a mailbox link-scanner prefetching the URL doesn't consume
the single-use token. That was right and the reasoning is easy to lose.

**The link stays an ordinary, copyable URL.** The two properties are not in tension, and losing the
second one to protect the first would be a real regression — mailing something to a work address and
pasting the link into a personal browser is a flow people actually use.

- `GET /confirm-email?userId=…&token=…` renders a page with a Confirm button and the token in a
  hidden field. It changes nothing, so a prefetch is inert.
- The button POSTs, and that is what consumes the token.
- Pasting the URL into any browser, on any device, at any later time works, because the GET render
  needs no prior state there. The antiforgery cookie is issued *by that render*, so it is always
  paired with the form it just produced.

Three things this depends on:

- **No JavaScript auto-submit on page load.** It re-opens the hole for scanners that execute JS,
  breaks without JS, and removes the intentionality the button exists for.
- **The token is a credential travelling in a URL:** single-use, short expiry,
  `Referrer-Policy: no-referrer` on that page, and OTel URL-query redaction stays **on** for auth —
  the opposite of `api`, where query strings are paging and worth seeing.
- **The boring branches are the ones users hit:** already-confirmed → say so and offer sign-in;
  expired → offer resend; invalid → generic message, since anti-enumeration applies here too.

Reset-password uses the same shape, where it is even more natural: GET renders the new-password
form with the token hidden in it, POST performs the reset.

**Metrics:** `auth.registrations`, `auth.email_confirmations`, `auth.password_resets`,
`auth.password_changes` (architecture §3's table). These carry the anti-enumeration flows' honest
outcomes — the `unknown_email` a generic 200 deliberately hides is a tag value here, which is what
makes an enumeration run visible to an operator without the response giving anything away.

**Proves:** the full failure matrix in tests, plus one integration test that runs the whole thing —
register, job executes, Mailpit holds the message, extract the link, confirm, sign in. The e2e
version clicks the button rather than navigating to the URL, which is also what proves the
scanner-safety property.

> **Checkpoint.** Do it by hand: register in a browser, open Mailpit, click the link, sign in.

### 9. Permission overrides — auth's half

**Lands:** override rows (subject, permission string, grant or deny, optional `ExpiresAt`), minted
into `perm` / `perm_deny` claims, with expired overrides silently not minted. Built so that an
access grant is a sibling shape rather than a new concept (plan §3.2's fourth seam).

**Proves:** the token carries the claims; an expired override is absent; auth never interprets a
permission string.

> Nothing reads these claims until `server/api` exists. Included anyway, because retrofitting
> claim-minting means reopening token generation — the most security-sensitive code here — and
> that's the difference between "auth is finished" and "auth is finished except".

### 10. The rest of the token surface

The first of three protocol-completion steps: the goal is a **full-featured OAuth/OIDC server**,
every capability tested from a client's seat and genuinely understood — not just the subset the
BFF happens to need.

**Lands:** the **userinfo** endpoint (scope-gated claims, reusing the destination logic — the id
token and userinfo must agree); the **client-credentials grant** for machine clients — a
confidential client with a secret, a client principal minted at the token endpoint carrying the
client's own identity and scopes and **no user claims**; **introspection** (RFC 7662) for callers
that can't validate JWTs locally, permitted to confidential clients only. Seed config grows the
confidential/machine client shape.

**Metrics:** nothing new — `auth.oauth.grants` already counts every token response, so
`client_credentials` shows up the moment it exists.

**Proves:** userinfo answers exactly per granted scope; a machine client's token carries its
client id and scopes with no user's `sub`; introspection says active/inactive truthfully and
refuses public clients; the password grant is still absent from everything.

### 11. Device flow + PAR

**Lands:** the **device authorization grant** — device + verification endpoints and the
user-code verification page (functional here, designed in step 15) — for clients without a
browser or keyboard; **pushed authorization requests** (PAR), with the endpoint on and a
per-client opt-in requirement, so authorize parameters can travel the back channel instead of the
URL.

**Proves:** the whole device dance from tests — poll while `authorization_pending`, enter the
user code signed in, approve, poll again and receive tokens — plus a PAR round trip driving the
normal code flow off a `request_uri`.

### 12. Logout notifications

**Lands:** **back-channel logout** — per-client `backchannel_logout_uri` config and a signed
logout token POSTed to every registered client when a session ends, which is what makes sign-out
propagate once `apps/web` and `apps/admin` share the SSO session. OpenIddict provides the
end-session protocol but not this notification layer, so it is custom work — the reason it has
its own step. Front-channel logout is **decided** here rather than assumed (likely rejected in
favor of back-channel; record why).

**Proves:** ending a session delivers valid logout tokens to fake RPs registered in tests; the
consumer side lands with each BFF, not here.

### 13. Hardening pass

The full-stack review before the finalize pass: six parallel tracks (protocol/OpenIddict,
identity + account flows, .NET craft, observability, tests, ops/deployability) over everything
steps 1–12 built, every finding fixed here, folded into steps 14–15, or recorded as step 16.

**Lands (protocol):** PKCE accepts **S256 only** — OpenIddict's default also took `plain`, the
one OAuth 2.1 deviation found; discovery's prompt values trimmed to the implemented `login`/
`none`; refresh-token expiration made genuinely **absolute** (the default slides the window on
every rotation, so a session refreshing fortnightly would never re-authenticate); `auth_time`
minted into id and access tokens and carried through refresh and userinfo (OIDC requires it
whenever a client sends `max_age` — the BFFs' silent-SSO checks will); access-token claims
**scope-gated** — a token granted only `api.*` carries no email, name or role anywhere, instead
of PII riding every unencrypted JWT.

**Lands (identity + data):** change-password counts against the same lockout as sign-in — it
verifies the current password, so a hijacked cookie must not get unlimited guesses (new
`locked_out` metric outcome); a unique email index — Identity ships it non-unique, and the
concurrent-registration race now dies at the constraint; `oidc_tokens` indexes on subject and
creation date for revocation and the prune; oversized emails become a validation message instead
of a database-truncation 500; the seeder refuses non-http(s) URIs (a bare `/path` parses as an
absolute `file://` URI on Unix — found by the new misdeclaration tests).

**Lands (libraries + observability):** `deployment.environment.name` on the OTel resource so a
hosted backend can tell staging from production; the authenticated `sub` enriched onto spans and
log scopes beside `act.sub`; the envelope log-level pair defaulted inside `AddObservability` so
`server/api` can't forget it; EF's per-query logs quieted to Warning; the nightly prune logs its
counts (its only observable outcome); SMTP gains TLS-mode and timeout knobs, and booting a hosted
environment against Mailpit's port refuses outright — the guard the architecture doc promised;
`OidcOptions` validated at boot and resolvable through DI.

**Proves:** the review's missing negatives, now tests — sign-in and change-password lockout,
antiforgery rejection, the unregistered post-logout redirect refused with the session surviving,
replay of a rotated refresh token killing the whole grant chain, `prompt=none` answering
`login_required`, `prompt=login` re-authenticating without looping, the full security-header set
on both policies, a prune survivor (over-deletion was previously invisible), the confidential
browser client — the BFF's exact shape — completing the flow and introspecting its own token,
and a theory over every misdeclared seed-client shape.

### 14. Account-surface guards

Everything the hardening review routed to "before design" that isn't visual, landed as one step —
so the design pass that follows is purely design.

**Lands:** the **rate limiter** — ASP.NET Core's built-in middleware, IP-partitioned fixed
windows over the endpoints that take credentials or drive email: register, forgot-password,
resend-confirmation, the sign-in POST, the change-password POST and `/connect/verify`.
Anti-enumeration made probing operator-visible (the metric tags); this makes it expensive, and it
is also the practical brake on the timing residual and the device user-code space. (The limiter's
state is in-memory per instance — right for the single-VPS topology, revisit on replicas.) The
**timing equalization**: a dummy password hash on the sign-in unknown-email path — today it
returns before any PBKDF2 work, the sharpest oracle the review found — and equivalent dummy work
on the email flows' miss paths. **`Cache-Control: no-store` on the credential-bearing pages**, so
bfcache and history can't re-show a filled form after sign-out. The **missing pages,
functionally**: a root landing page (`/` is the default post-sign-in target and the end-session
fallback, and currently 404s), an error page (a browser mid-flow must never see a raw
ProblemDetails body), status-code/404 handling, a signed-out confirmation and an access-denied
page — functional here, designed in step 15 like every page before them. The **end-session
confirmation**: a bare GET `/connect/endsession` with no validated `id_token_hint` currently
signs out of every app with no confirmation — spec-tolerated, but one forced navigation should
not end every session; a confirmation POST page closes it. And the **session-persistence
decision**: the auth cookie is a browser-session cookie with no remember-me — decide rather than
inherit, and if the answer is a remember-me option, its mechanics land here and its looks in
step 15.

**Proves:** 429s from every limited endpoint under hammering while the ordinary flows stay
untouched; every new page wired and tested — the error page answering HTML accepts while API
accepts keep ProblemDetails, `/` no longer a 404, the hint-less end-session confirming instead of
acting; and the sign-in miss path doing hash-shaped work.

### 15. Design + finalize

**Lands:** every rendered page designed rather than scaffolded — sign in, register, forgot password,
reset password, confirm email, device verification, and step 14's new pages (root, error, 404,
signed-out, access-denied, the end-session confirmation); there is no consent page (architecture
D17). The pages policy's CSP widened deliberately for the styling it now allows — `style-src
'self'` (plus `img-src 'self'` if a logo lands), never `'unsafe-inline'`: everything comes from
the emitted stylesheet. An accessibility pass over all of them. The Bruno collection committed.
`docs/auth.md` finalized. A note recording that
a grant-support-access endpoint is coming, so "auth is finished" doesn't quietly mean "auth is
closed to impersonation" (plan §3.2's first seam).

**Proves:** the full suite green, a conformance-suite run, and an end-to-end walkthrough of the
whole project until it is genuinely understood rather than merely working.

> **Styling note for the design pass.** Tailwind works here without crossing the ecosystem
> boundary: the standalone CLI scans `.cshtml` files and emits one static stylesheet auth serves
> itself — a build step, no Node at runtime. React components are **not** reused on these pages
> (§2's rule, and a credentials host must not depend on a JS bundle); visual parity with the
> future component library comes from sharing the utility classes and design tokens, consciously
> duplicated like the role names in §3.4.

### 16. Deploy prep (D12)

Auth's production readiness, landing when the deployment topology (architecture D12) is decided —
the review's recommendation is **Kamal 2** (built for exactly the architecture doc's shape:
multiple containers on one VPS, health-gated zero-downtime swaps, per-destination staging/prod).
Partly auth code, partly repo-wide; recorded here because most of it blocks *auth* specifically:

- **OpenIddict key material from configuration** — today only dev certs and test keys exist, and
  production refuses to boot. Needs code (a config-loaded signing + encryption certificate path),
  plus a two-key JWKS overlap story for rotation.
- **Forwarded headers** — `UseForwardedHeaders` with `KnownProxies` pinned to the proxy, plus
  cookie `SecurePolicy=Always`, so scheme, HSTS, issuer and the `Secure` flag survive TLS
  termination.
- **`SetIssuer` pinned from configuration** — the issuer currently follows the Host header
  (`AllowedHosts` is `*`); a production IdP names itself.
- **The data-protection key ring's at-rest posture** — decide: `ProtectKeysWithCertificate` once
  key material exists, or record accepting DB-read ⇒ key compromise on a single-box deploy.
- Dockerfiles for auth + worker (multi-stage publish, non-root, `/health/ready` as the probe), an
  image build+push job, and the migration step (an EF bundle run before rollout — `Database:Migrate`
  stays `false`).
- Stop grace ≥ 30 s so Wolverine drains on SIGTERM; real worker readiness checks (its
  `/health/ready` currently runs none); `/health/*` kept off the public proxy; docker log
  rotation; the OTLP retry env var (`OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY`) or a local collector,
  since the exporter drops batches silently on transient failures; a queue/outbox-depth gauge.

---

## Testing the OAuth flows

Three tools, three jobs. Only the first is a deliverable.

| Tool | Use | Notes |
| --- | --- | --- |
| **Bruno** | The committed artifact — every flow, in the repo, reviewable | Catches its own callback. This is what makes the flows part of "done" rather than something that worked once. |
| **[oauth.tools](https://oauth.tools)** | Exploratory poking, token inspection while building | Curity's, free, browser-based. **oauthdebugger.com** is the simpler single-flow version. |
| **[OIDF conformance suite](https://gitlab.com/openid/conformance-suite)** | Proving conformance rather than assuming it | Self-hostable in Docker. Heavier, but it's the only one that tells you where you deviate from the spec. |

**oauth.tools caveats.** It runs in your browser, so the token exchange happens from browser
JavaScript: the token endpoint needs CORS for its origin, and `https://oauth.tools/callback` has to
be a registered redirect URI. Use a **public + PKCE** dev client only — a confidential client would
put its secret in the browser. Keep both the CORS origin and that redirect URI development-gated in
seed config so neither can reach a deployed environment.

## Local stack

| Service | Port | For |
| --- | --- | --- |
| Postgres | 5432 | auth's database, and Wolverine's `wolverine_*` envelope schemas |
| RabbitMQ | 5672 (AMQP), 15672 (management UI) | the message broker; the UI shows queues and the dead-letter queue |
| Mailpit | 8025 (UI), 1025 (SMTP) | the inbox — auth genuinely sends here |
| Aspire Dashboard | 18888 (UI), 18889 (OTLP) | traces, metrics and structured logs in one place |

The Aspire Dashboard container requires a login token by default; set
`DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` for local use only. It sits behind an opt-in
compose profile rather than running always-on.

## Open items to resolve along the way

- **Does production reconcile OIDC clients, or only ensure they exist? — resolved in step 5:
  reconcile.** Config is the source of truth for a client, and a fixed redirect URI that never
  deploys because the row already existed is the worse failure. The descriptor diff limits writes
  to real changes (the secret compared through `ValidateClientSecretAsync`), seeding never
  deletes, and `Database:Seed` off remains the escape hatch for an organisation managing clients
  out of band. Users reconcile too — config owns a declared account's roles — with one carve-out:
  a password reconciles only where config declares one, so production, which declares addresses
  alone, never resets a password a human set through the reset flow.
- **Consent screen — resolved in step 4: not used** (architecture D17). Every v1 client is
  first-party and registered with implicit consent, and the authorization endpoint refuses any
  other registration — so onboarding a third-party client reopens the decision rather than
  inheriting this one.
- **Token lifetimes — resolved in step 4: 15 minutes for the access token**, committed as
  configuration (`Oidc:*` in `appsettings.json`, documented in auth.md) rather than prose:
  identity token 15 minutes, authorization code 5, refresh token 14 days with rotation.
- **Revisit the password-grant ban for local tooling.** The ban is absolute today (plan §3, step
  4's callout, CLAUDE.md), so Bruno signs in through the real browser window — faithful to the
  protocol, but clumsier than the old project's sign-in-with-credentials request. Before auth
  closes, decide deliberately whether that stays the answer or whether some development-only
  concession is acceptable, and record the reasoning either way rather than leaving the friction
  unexamined. *Step 10 changed the calculus:* the collection gained Sign In (Scripted), which
  drives the real code + PKCE dance from a pre-request script — one click, credentials from env
  vars, no browser — so the friction the ban imposed is essentially gone; the finalize-pass
  decision remains, but the default is now to keep the ban.
- **Extract the wire vocabulary into `server/shared/MyStack.Contracts` — resolved: landed
  immediately after step 10.** The shared-library rule's intent (per the rebuild's author):
  remove confusing unused tooling, not forbid structure — names both sides of the wire must
  spell identically belong in a shared contract, the way `SendEmail` does. The lib carries
  `AuthRoles` (auth seeds and mints them; every resource server keys its role→permission map
  off them), `AuthClaims` (`perm`/`perm_deny` — every resource server reads them for the §3.1
  arithmetic) and — reconsidered the same day — `Api/ApiScopes`: auth registers, seeds and
  mints the scopes and the `api` audience that `server/api`'s policies will enforce, so the
  names are spelled in two apps' code, which is architecture §2's admission test. One assembly,
  a directory per topic; a future resource adds `Billing/`, not a project. Permission strings
  stay out — they fail the test: auth handles them as opaque data, and only `server/api` names
  them.
