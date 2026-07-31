# server/auth

The OAuth2/OIDC authorization server. It owns users, credentials, roles and permission overrides,
and it is the only deployable that issues tokens.

**What exists today is the host skeleton, telemetry, the OpenIddict server, messaging, seeding,
and the account flows**: ASP.NET Core Identity over EF Core/Postgres, health checks, the
security-header set, `MyStack.Observability` wired in, OpenIddict issuing tokens — authorization
code + PKCE with refresh tokens, client credentials for machine clients, userinfo,
introspection, the device flow and PAR alongside the original four protocol endpoints,
back-channel logout propagating sign-out to every registered client, a functional sign-in page,
request logging and the first domain counters — `MyStack.Messaging` speaking Wolverine over RabbitMQ,
with the daily token-prune flowing through the broker, config-driven seeding bringing a fresh
database to a working state before the host serves, and the account flows: register + email
confirmation, forgot/reset password, change password + notification, every email published
through the EF outbox and delivered by `server/worker`.
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
pick the **Local** environment, and run **Auth → Sign In (Browser)** — it opens the real sign-in
page (which links to register), exchanges the callback code, and exports `{{access_token}}`;
**Auth → Decode Access Token** prints the claims. **Auth → Account** is the same flows one HTTP
request at a time — register, read the confirmation link out of Mailpit, confirm, forgot/reset,
sign in, change password — for seeing the anti-enumeration answers and token handling on the
wire, with the worker running so the emails actually deliver.

## Configuration

| Key | Default | Notes |
| --- | --- | --- |
| `ConnectionStrings:AuthDb` | none | Required. Startup throws without it — there is deliberately no fallback, because a default here would be a credential compiled into the binary. |
| `Database:Migrate` | `false` | Applies pending migrations before the host serves. On in development; a deployment applies migrations as its own step. |
| `Database:Seed` | `true` | One safe seed pass before the host serves — roles/scopes from code, clients/accounts from config. Safe to leave on everywhere (writes only on real drift); off is the escape hatch for an organisation managing clients out of band. |
| `Seed:Clients` | `[]` | The OIDC clients to reconcile — id, display name, `Public`/`Confidential`/`Machine`/`Device` + secret, redirect URIs, scopes, optional `RequirePushedAuthorizationRequests` and `BackchannelLogoutUri`. What a client may *do* is fixed in code; see Seeding. |
| `Seed:Users` | `[]` | The accounts to ensure — email, roles, optional password. At least one must carry `globaladmin`, or startup throws: seeding guarantees somebody can administrate. |
| `Oidc:AccessTokenLifetime` | `00:15:00` | The bound on revocation latency (architecture §3.1): a role change or revoked override lives at most this long in issued tokens. |
| `Oidc:IdentityTokenLifetime` | `00:15:00` | |
| `Oidc:AuthorizationCodeLifetime` | `00:05:00` | One redemption, minutes to make it. |
| `Oidc:RefreshTokenLifetime` | `14.00:00:00` | Absolute horizon; the token itself rotates on every use. |
| `Oidc:DeviceCodeLifetime` | `00:15:00` | The device flow's cross-device window — how long the codes stay redeemable while the user walks to a browser. |
| `Oidc:UserCodeLifetime` | `00:15:00` | Same window as the device code on purpose: one of them outliving the other is only a confusing way to fail. |
| `ConnectionStrings:MessageBroker` | none | Required, same no-fallback rule as the database: failing to boot beats silently dropping messages. |
| `Messaging:RetryCooldownsInSeconds` | `[1, 5, 30]` | Seconds between redelivery attempts after a handler throws, one entry per retry; past the last one the message dead-letters. Tests set `[0]`. |
| `Account:PublicBaseUrl` | none | Required, validated at boot as an absolute http(s) URL. Emailed confirm/reset links are built from it — never from the request's `Host` header, which is client-writable and would let a forged forgot-password request steer a victim's real reset link to an attacker's domain. |

`appsettings.Development.json` carries the compose stack's connection string. Those are local
infrastructure credentials, not secrets — every other environment supplies
`ConnectionStrings__AuthDb` from its own configuration, and user-secrets is the local override.
One managed-Postgres caveat: point the connection string at a **direct (session) endpoint**,
never a transaction-mode pooler (PgBouncer et al.) — the boot's advisory lock and Wolverine's
durability agents both need session semantics, and a transaction pooler silently breaks them.

## The schema

Identity's tables live in a Postgres schema named **`auth`**, inside the same database `server/api`
will use. That is the split `MyStack.Messaging` also assumes when it puts its envelope storage in
`wolverine_auth` (architecture §3.3), so the hosts share one Postgres instance without sharing a
namespace.

Naming is snake_case throughout, via `EFCore.NamingConventions` — EF Core 10 has no built-in
convention for it. Identity's `AspNet*` table names are replaced with `users`, `roles`,
`user_claims`, `user_roles`, `user_logins`, `user_tokens` and `role_claims`: they describe the
framework rather than this schema, and renaming them later costs a migration.

`data_protection_keys` holds ASP.NET's data-protection key ring — the keys behind the
confirmation/reset tokens and the cookies. Persisting them here (with a pinned application name,
since the default derives from the content-root path) is what keeps an emailed link valid across
restarts, replicas and deploy-path changes.

`permission_overrides` holds the per-user grant/deny rows minted into tokens (see Permission
overrides): subject, permission string, kind (stored as text, `Grant` or `Deny`), an optional
expiry, and a created timestamp. One row per (user, permission) — a simultaneous grant and deny
is a contradiction the unique index refuses rather than an arithmetic the API resolves — and
rows cascade away with their user.

Keys are **application-generated version 7 UUIDs**. The timestamp in the leading bits means
Postgres, which orders `uuid` by its canonical byte order, keeps appending to the primary key index
instead of fragmenting it. Generating them in the entity rather than the database also means the
`sub` a token will carry is known before `SaveChanges`.

Two index decisions go beyond the frameworks' defaults: the email index is **unique** — Identity
ships it non-unique because emails are optional in the general framework, but here the email *is*
the identity, so the database enforces what `RequireUniqueEmail` promises and the
concurrent-registration race dies at the constraint — and `oidc_tokens` gains indexes on
`subject` and `creation_date`, because OpenIddict's stock indexes lead with the application id
while revocation-on-credential-change looks up by subject alone and the nightly prune filters on
age.

## Identity policy

- **Unique email**, and `SignIn.RequireConfirmedEmail` is on from the first account rather than
  switched on later over users who never went through confirmation.
- **Passwords: twelve characters, no composition rules.** NIST SP 800-63B's position — length
  carries the strength, and mandatory character classes mostly produce predictable substitutions.
- **Two token lifespans.** Email confirmation uses Identity's default provider (24 hours); password
  reset gets its own provider at **2 hours** — a reset token is a full account-takeover credential
  that sets a new password outright, while a confirmation token only flips a boolean.
- **Security stamp validation every 5 minutes** (Identity defaults to 30): a password change
  rotates the stamp, and this interval bounds how long another live cookie session outruns it.
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
| Pushed authorization | `/connect/par` | OpenIddict entirely |
| Token | `/connect/token` | passthrough: user re-validated against the store, claims rebuilt; client principal for machines |
| Userinfo | `/connect/userinfo` | passthrough: scope-gated claims through the same destination logic |
| Introspection | `/connect/introspection` | OpenIddict entirely |
| Device authorization | `/connect/device` | OpenIddict entirely |
| End-user verification | `/connect/verify` | the Verify Razor page: signed-in code entry, approve/deny |
| End session | `/connect/endsession` | passthrough: Identity sign-out, back-channel logout notifications, then the validated post-logout redirect |
| Revocation | `/connect/revocation` | OpenIddict entirely |

**Authorization code + PKCE with refresh tokens for humans, client credentials for machines, the
device grant for clients without a browser — and no other flow.** PKCE is required globally
rather than per client, so no future registration can quietly opt out — and **S256 is the only
accepted challenge method**: OpenIddict's default also takes `plain`, which is challenge ==
verifier and none of the interception protection PKCE exists for, so the hardening pass removed
it (the one OAuth 2.1 deviation the review found). Discovery likewise advertises only the prompt
values the authorize handler implements (`login`, `none`) — the defaults included `consent` and
`select_account`, which nothing here honors. A machine client is
confidential by construction; its token carries the client's own identity — `sub` is the client
id — and its granted scopes, and never a user claim, an id token or a refresh token. **There is
no password grant, in any environment** — `grant_types_supported` in the discovery document is
exactly `[authorization_code, refresh_token, client_credentials,
urn:ietf:params:oauth:grant-type:device_code]`, and the test suite asserts it.

**The device flow is the browserless client's front door** (RFC 8628). The device POSTs its
client id to `/connect/device` and gets a `device_code` to poll the token endpoint with, plus a
short `user_code` and a verification link for a human. The user opens `/connect/verify` on a
real browser — signing in first; approval binds the device to whoever approves, so the page
demands a user before rendering anything — enters the code (or arrives with it via
`verification_uri_complete`), sees which client is asking and for what, and approves or denies.
Approval runs the same principal funnel as every sign-in, so the device's tokens carry the
user's roles and permission overrides like any other; denial turns the device's next poll into
`access_denied`. Both codes live for `Oidc:UserCodeLifetime` / `Oidc:DeviceCodeLifetime`
(15 minutes each) and are single-use. The verification page is functional now and designed in
the design + finalize pass, like the sign-in page.

**PAR moves authorize parameters off the URL** (RFC 9126). A client may POST its authorization
request — scope, redirect, PKCE challenge, state — to `/connect/par` over the back channel and
send the browser to `/connect/authorize` with nothing but its client id and the one-time,
short-lived `request_uri` handle it got back: nothing sensitive in history or logs, nothing
tamperable in flight, and a confidential client authenticated before any page renders. Every
browser client is *allowed* to push; a client seeded with
`RequirePushedAuthorizationRequests: true` is *refused* plain front-channel authorize URLs
entirely — the opt-in meant for the production BFF once it pushes.

**Sign-out propagates over the back channel** (OIDC Back-Channel Logout 1.0). Ending a session at
`/connect/endsession` doesn't just clear auth's cookie: every registered client that declares a
`BackchannelLogoutUri` in seed config is POSTed a **logout token** — a short-lived signed JWT with
`iss`/`aud`/`iat`/`exp`/`jti`, the user's `sub`, the back-channel-logout `events` claim and a
`logout+jwt` type header, and deliberately no `nonce`, which is what stops it doubling as an id
token — as `logout_token=…`, form-encoded, server to server. A consumer validates it against the
same discovery document and JWKS as every other token, then ends its own sessions for that
subject; that consumer side lands with each BFF, not here. The subject comes from the live cookie,
or from a validated `id_token_hint` when the cookie already expired — so a client-initiated
sign-out still propagates. Deliberate shape, recorded once:

- **`sub` only, no `sid`.** Auth's session store is the Identity cookie — there is no server-side
  session table to mint a session id from — and "sign this user out everywhere" is exactly what
  single sign-out wants. Per-session precision can be added later by minting `sid` into id
  tokens; nothing here precludes it.
- **Delivery is inline, concurrent and best-effort** — no queue, no retry schedule. A logout
  token outlives its usefulness in minutes, so broker retries would mostly deliver dead tokens;
  the unreachable-client bound is the HTTP client's 5-second timeout, deliveries run
  concurrently so a dead client never blocks a live one (or the user's redirect), and a missed
  notification is bounded by the consumer's own session and token lifetimes. Failures are logged
  and counted (`auth.logout_notifications`), which is the operator's signal that a client's
  endpoint is down.
- **Front-channel logout is rejected, not deferred.** The front-channel spec delivers logout by
  rendering hidden iframes in the departing user's browser: no confirmation, the browser must
  stay on the page while they fire, and third-party cookie partitioning increasingly means the
  iframe can't see the client's session cookie at all. Every client here is a BFF with a server
  to receive a POST, so the fragile variant would be dead weight beside the reliable one.

**Lifetimes are configuration** — the `Oidc:*` keys above, with the defaults committed in
`appsettings.json`. Refresh tokens rotate: every refresh issues a new one, and the token endpoint
re-validates the user against the store on each exchange, so role and email changes take effect on
the next refresh rather than surviving to the token's horizon. **The horizon is absolute**:
OpenIddict's default slides the window forward on every rotation — a session refreshing at least
fortnightly would never re-authenticate — so sliding expiration is disabled and fourteen days
means fourteen days. Replaying a refresh token that was already rotated away revokes the whole
grant chain (reuse detection), so a stolen copy turns into a forced re-authentication rather than
a silent parallel session.

**Claim destinations are deny-by-default, and scope gates both copies.** `email`, `name` and
`role` reach the access token *and* the identity token only when their scope (`email`, `profile`,
`roles`) was granted — a token granted only `api.read` authorizes API calls and identifies
nobody, because an unencrypted JWT is not exempt from data minimization just because the API is
first-party. `auth_time` — when the user actually authenticated — rides both tokens (OIDC
requires it in the id token whenever the client sent `max_age`; RFC 9068 lists it for access
tokens) and survives refresh unchanged, because refreshing is not authenticating. `perm` and
`perm_deny` (see Permission overrides) are access-token-only — an identity token describes who
the user is, never what they may do; anything unlisted — Identity's security stamp, concretely —
reaches no token at all, which the flow test proves. A token granted an `api.*` scope carries
`aud: api`. Access tokens are signed but **not encrypted** JWTs: `server/api` validates them
against the discovery document rather than sharing auth's key material.

**Userinfo answers exactly per granted scope.** `/connect/userinfo` rebuilds the principal
through the same funnel token issuance uses and returns the claims whose destination includes the
identity token — `sub` always, `auth_time` carried forward from the presented token, then
`email`, `name` and `role` as their scopes were granted — so userinfo and the id token agree by
construction and can never drift; `perm`/`perm_deny` stay out of both. A token with no user
behind it — any machine token — gets `invalid_token`: there is nobody to describe.

**Introspection is for confidential callers only.** `/connect/introspection` (RFC 7662) answers
whether a token is live, for callers that can't validate JWTs locally. OpenIddict handles it
entirely, and its posture is deliberate: a public client is refused outright
(`unauthorized_client`); a confidential caller gets the real answer only for a token it presented
or is an audience of — any other token answers `active: false`, so the endpoint can't be used to
probe stolen tokens; and claim details such as `scope` are released to a token's audiences alone,
a mere presenter seeing liveness and metadata.

**Keys.** Development uses the framework's development certificates; tests use ephemeral in-memory
keys so CI never writes a certificate store; any other environment must supply real signing and
encryption credentials deliberately, and OpenIddict refuses to boot without them (see the
hardening items).

**No consent screen** (architecture D17). Every v1 client is first-party and registered with
implicit consent; the authorization endpoint refuses a client registered any other way, so a
future third-party client forces the decision to be remade rather than silently inheriting it.

**Clients come from seed configuration** (see Seeding): development declares `web-bff`, `bruno`,
the `dev-machine` machine client and the `dev-device` device client in
`appsettings.Development.json`, the test suite declares its clients the same way, and every one
of them takes one of the three shapes the seeder can produce — browser (authorization code +
PKCE + refresh, implicit consent, public or confidential, optionally PAR-required) or machine
(client credentials only) or device (the device grant only: public, no secret, no redirect
URIs) — no exceptions.

## Permission overrides

Architecture §3.1's exception mechanism: a role grants a user's permissions in bulk, an override
row grants one the roles don't carry or denies one they do. Every token issuance — the authorize
passthrough and every refresh alike — reads the subject's live rows and mints them as `perm`
(grants) and `perm_deny` (denials) claims, so the API's
`effective = expand(roles) ∪ granted − denied` stays a pure function of the token.

- **The strings are opaque here.** Auth stores and mints them verbatim; `server/api` owns the
  permission catalog and the arithmetic. A typo'd override is silently inert — the admin console
  picking from the API's catalog is what will prevent that.
- **Expiry is enforced at minting.** A row past its `ExpiresAt` simply stops producing its claim;
  nothing deletes it, no job runs. The grant lives at most as long as the last token minted
  before the deadline — which bounds every override change, expiry and removal included, by
  `Oidc:AccessTokenLifetime` (15 minutes). The immediate kill switch is revoking the user's
  tokens, same as any credential event.
- **No management surface yet.** Rows are written by the admin console (post-v1, architecture
  §3.2's picker) — today they enter through SQL or tests. Nothing reads the claims either until
  `server/api` exists; the minting is built now because retrofitting it later would reopen token
  generation, the most security-sensitive code here.
- **The shape is deliberately reusable.** Impersonation's user-granted access window
  (architecture §3.2) is designed as a sibling row — subject, expiry, audit trail — so building
  it later extends this pattern rather than inventing a new one.

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

## Account flows

Six Razor pages — functional now, designed in the finalize pass:

| Page | Who | Does |
| --- | --- | --- |
| `/register` | anonymous | creates the account, publishes the confirmation email |
| `/confirm-email?userId=…&token=…` | emailed link | GET renders a Confirm button; POST consumes the token |
| `/resend-confirmation` | anonymous | reissues the confirmation for an existing unconfirmed account |
| `/forgot-password` | anonymous | publishes the reset email for an existing confirmed account |
| `/reset-password?userId=…&token=…` | emailed link | GET renders the new-password form; POST performs the reset |
| `/change-password` | signed-in cookie | rotates the password with the current one in hand |

**Emailed links are pages that POST, never endpoints that act on GET.** A mailbox link-scanner
prefetching the URL renders a form and changes nothing; the button's POST is what consumes the
single-use token. The link stays an ordinary copyable URL — the GET needs no prior state and
issues the antiforgery cookie itself, so pasting it into any browser at any later time works.
There is deliberately no JavaScript auto-submit, which would re-open the hole for scanners that
execute JS. The boring branches are first-class: already-confirmed says so and offers sign-in,
invalid/expired offers a resend or a new link, and both collapse unknowable causes into one
generic message.

**The token is a credential travelling in a URL**, so: single-use (consuming it rotates the
security stamp or flips the flag it checks), short expiry (2 h reset / 24 h confirmation),
`Referrer-Policy: no-referrer` on every response, OTel URL-query redaction and query-free request
logging (Telemetry below), and links built only from `Account:PublicBaseUrl`. Identity's raw
tokens are standard Base64 — `+`, `/`, `=`, exactly what query strings and mail-client link
rewriters mangle — so they travel Base64Url-encoded over the UTF-8 bytes, and a token that fails
decoding reads as invalid rather than throwing a distinguishable 500.

**Every email rides the EF outbox to the worker.** Pages publish `SendEmail` through
`IDbContextOutbox<AuthDbContext>` inside one transaction with the user write, so "user created +
email published" (and "password reset + grants revoked + owner notified") commit together or not
at all — architecture §3.3's outbox, now real. The worker delivers over SMTP; auth never sends
inline from a request. The bodies come from `IEmailRenderer` implementations in auth
(`ConfirmationEmail`, `PasswordResetEmail`, `PasswordChangedEmail`) — subjects and copy are
domain knowledge, so they live here, not in the library.

**Anti-enumeration throughout** (architecture §3): register, resend and forgot answer the same
generic page whether the address exists, is unconfirmed, or is already taken — and register
validates password policy *before* the existence lookup, so a deliberately weak password can't
probe either. Reset links go to confirmed addresses only: an unconfirmed address was never proven
to belong to its registrant, and a reset link would leapfrog confirmation. The honest outcomes
live in the metric tags. Change-password is the deliberate exception — the caller is already that
account's session, so "incorrect password" reveals nothing. It is **not** exempt from lockout,
though: the form verifies the current password, which makes it a guessing surface for a hijacked
cookie, so wrong attempts count against the same five-strikes lockout the sign-in page enforces —
`ChangePasswordAsync` never touches the counters on its own, so the page does it by hand.

**A credential change ends the sessions it should.** Reset and change both revoke the subject's
OpenIddict tokens and authorizations (refresh tokens on other devices die immediately; access
tokens age out within 15 minutes), rotate the security stamp (other cookie sessions die at the
next 5-minute check; the browser that made the change is refreshed and survives), and publish the
password-changed notification to the account's address.

## Seeding

Architecture §3.4's model, in full: one always-on-by-default `Database:Seed` switch over one safe
pass in `AuthSeeder`. What makes always-on safe is that every account is config-declared — no
environment receives anything it didn't declare — and writes happen only on real drift.

**Code-declared, DB-materialized:** the roles (`AuthRoles`: `globaladmin`, `admin`, `user`) and
the API scopes (`ApiScopes`: `api.read`, `api.write`, resource `api`) — both declared in
`server/shared/MyStack.Contracts`, the one spelling auth and every resource server share. These
are fixed in code — a role that exists as a row but not in the API's permission map grants
nothing, so a config knob would only create ways to be wrong.

**Config-declared:** the OIDC clients and the accounts (`Seed:Clients`, `Seed:Users`) — redirect
URIs, secrets, addresses and which of each exist genuinely differ per environment. Config decides
*which* clients exist and where they redirect; *what a client may do* is fixed in code, per
shape: a browser client (`Public` or `Confidential`) is authorization code + PKCE + refresh with
implicit consent — optionally PAR-required via `RequirePushedAuthorizationRequests`, optionally
notified of sign-outs via `BackchannelLogoutUri` — a
`Machine` client is client credentials only, a `Device` client is the device grant only, and
there is deliberately no knob for grant types, so no configuration can reintroduce the password
grant. Confidential and machine clients require a secret, public and device clients refuse one,
machine and device clients refuse redirect URIs, and only a browser client may require PAR or
declare a back-channel logout URI (the other shapes never touch the authorization endpoint, have
no user session to end, and no server endpoint to notify); every misdeclaration throws and aborts
startup — failing to boot beats booting wrong, and a silently-defaulted secret would ship in a
public repo.

**Seeding guarantees an administrator.** At least one declared user must carry the `globaladmin`
role, or startup throws — the deliberate opt-out of seeded accounts is the switch, never a config
that quietly leaves nobody able to administrate.

**Accounts carry no password in production.** A `Seed:Users` entry without a `Password` is
created with a confirmed email and no usable password; it is activated through the ordinary
forgot-password flow — only the address is configured, and the test suite proves the
passwordless-activation path. Development supplies passwords directly (one convenience account
per role), because convenience is the entire point there.

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
  because pruning touches auth's tables; cross-app work goes to the worker's queue instead —
  `SendEmail` is published there, through the EF outbox (see Account flows), and the worker
  delivers it. Scheduling is one declarative line per schedule: the library's clock publishes
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
  client-supplied grant types collapse to a closed set first. The account flows add
  `auth.registrations` (`outcome`: `created`, `resent_confirmation`, `already_registered`,
  `invalid_password`, `failed`), `auth.email_confirmations` (`outcome`: `confirmed`,
  `already_confirmed`, `invalid_token`, `unknown_user`, `resent`, `resend_already_confirmed`,
  `resend_unknown_email`), `auth.password_resets` (`stage` `requested`: `sent`, `unknown_email`,
  `unconfirmed_email`; `stage` `completed`: `reset`, `invalid_token`, `unknown_user`,
  `invalid_password`) and `auth.password_changes` (`outcome`: `changed`,
  `wrong_current_password`, `invalid_new_password`, `locked_out`) — the anti-enumeration flows' honest
  outcomes, which the generic responses deliberately hide, so an enumeration run is a rate an
  operator can alert on. Back-channel logout adds `auth.logout_notifications`, tagged
  `client_id` (operator-declared seed config, still a closed set) and `outcome` (`delivered`,
  `failed`) — the failure rate is the signal that a client's logout endpoint is down, since
  delivery is deliberately best-effort. Wolverine adds its own `Wolverine:auth` meter. Domain meters and
  activity sources follow the `MyStack.*` naming convention, which the library subscribes by
  wildcard.
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
  Confirmation and reset tokens travel in query strings here, so `url.query` on a span is a
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
- **No rate limiting on the account endpoints.** The anti-enumeration posture makes probing
  operator-visible (the metric tags) but nothing yet makes it expensive. An IP-partitioned
  limiter over the anonymous account pages — register, forgot, resend, and the sign-in POST —
  belongs in the finalize pass.
- **A timing residual on the anonymous account flows.** The response *shape* is constant, but
  the hit path composes a token and an outbox write while the miss path only looks up a user, so
  latency differs by existence. The queue already moved delivery off the request path; closing
  the remaining gap (dummy work on the miss path, as the sign-in page would need too) is a
  finalize-pass call, with the rate limiter as the practical brake.
