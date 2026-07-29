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
- [ ] Every account flow works end to end **including its email**: register → confirm, forgot →
      reset, change password → notification.
- [ ] Every rendered page is designed, not scaffolded, and passes an accessibility pass.
- [ ] Seeding brings a fresh database to a working state in dev and in production, from config.
- [ ] Logs, traces and metrics come out of auth **running on its own**, with no other service up.
- [ ] The test suite covers the token shape, anti-enumeration, the seeding tiers, and every account
      flow's failure branches.
- [ ] `docs/auth.md` exists and describes what was actually built.
- [ ] A production-hardening review has been done and its findings are either fixed or recorded.

## Deliberately out of scope

Not "forgotten" — genuinely belonging elsewhere:

- **Impersonation** — needs `apps/admin` to have any consumer (plan §3.2). Step 10 records that a
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
lifetimes; a functional sign-in page (designed in step 10, not here); the claims the token carries
(`sub`, `role`, `email`, and the shape `perm` / `perm_deny` will occupy).

**Metrics:** `auth.sign_ins` and `auth.oauth.grants` (architecture §3's table) — the sign-in page
and token endpoint are their emitters, so the counters are part of building them.

**Proves:** `/.well-known/openid-configuration` is correct; a manually registered client completes
the flow.

> **No password grant, in any environment.** Not for a dev client, not for Bruno. Plan §3 rules it
> out and `mystack-old`'s seeder is exactly where it would get copied back in from.

### 5. `auth` seeding

**Lands:** plan §3.4 in full — `Database:Seed:Reference` and `Database:Seed:Sample` as separate
switches, roles and scopes materialized from code, OIDC clients and the bootstrap admin from
config, sample accounts gated to development, ensure-by-natural-key, create-only vs reconcile
declared per item, the session-scoped advisory lock, and seeding completing before the app serves.

**Proves:** a fresh database plus `compose up` yields working clients and an admin; a second boot
writes nothing; `Database:Seed:Sample` in a production environment throws rather than obeying.

> **Checkpoint.** Run the complete authorization-code + PKCE dance from Bruno against a seeded
> client. Decode the token. Confirm the claims, the lifetimes, and that refresh works.

### 6. `MyStack.Jobs`

**Lands:** Hangfire + `Hangfire.PostgreSql` on the `hangfire_auth` schema, the dashboard mounted and
gated behind the Identity cookie plus an admin role check, retry/backoff policy, dead-letter
visibility, recurring-job registration, and trace linking between the enqueuing span and the
executing job's span.

**Metrics:** `jobs.enqueued` and `jobs.executions` on the library's meter — dead-letter visibility
is a dashboard page for a human, but the `outcome: dead_lettered` tag is what an alert watches.

**Proves:** the dashboard is reachable signed in as an admin and refused otherwise; a job that
throws retries on schedule and then dead-letters where you can see it.

> Resolve plan §3.3's open question here: does `Hangfire.PostgreSql`'s `TransactionScope`
> enlistment actually make `Enqueue` transactional with `SaveChanges`? If not, record it and leave
> the transactional-outbox row in §4 pointing at it.

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
password-changed notification. Every email enqueued through `MyStack.Jobs`. Anti-enumeration
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

### 10. Design + finalize

**Lands:** every rendered page designed rather than scaffolded — sign in, register, forgot password,
reset password, confirm email, consent (if used), error — plus an accessibility pass. The Bruno
collection committed. `docs/auth.md` written. A production-hardening review. A note recording that
a grant-support-access endpoint is coming, so "auth is finished" doesn't quietly mean "auth is
closed to impersonation" (plan §3.2's first seam).

**Proves:** the full suite green, a conformance-suite run, and an end-to-end walkthrough of the
whole project until it is genuinely understood rather than merely working.

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
| Postgres | 5432 | auth's database, and Hangfire's `hangfire_auth` schema |
| Mailpit | 8025 (UI), 1025 (SMTP) | the inbox — auth genuinely sends here |
| Aspire Dashboard | 18888 (UI), 18889 (OTLP) | traces, metrics and structured logs in one place |

The Aspire Dashboard container requires a login token by default; set
`DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` for local use only. It sits behind an opt-in
compose profile rather than running always-on.

## Open items to resolve along the way

- **`Hangfire.PostgreSql` transaction enlistment** (step 6) — plan §3.3.
- **Does production reconcile OIDC clients, or only ensure they exist?** (step 5) Full reconcile
  means a bad redirect URI in config silently rewrites a working client on the next boot. The
  descriptor diff limits the blast radius to real changes, but this is still config-driven mutation
  of live authorization config. Decide deliberately.
- **Consent screen: used or not?** (step 4) The seeded clients are first-party, so implicit consent
  is defensible — but that decision should be made rather than inherited.
- **Token lifetimes** (step 4) — plan §3.1 assumes roughly 15 minutes for the access token, because
  that bounds override-revocation latency. Confirm the number and write it down.
