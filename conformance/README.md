# Conformance

The OpenID Foundation conformance suite, run locally against `server/auth` — the harness behind
auth-track 15's "conformance-suite run". Deliberately **not** in CI: the suite's discovery value
is one-time per finding (each real finding became a permanent test in `server/auth/tests`), a
full plan runs 20–40 minutes of browser-interactive modules, and the remaining PRs don't touch
the protocol surface. This directory exists so the run is a documented, replayable procedure
instead of a memory.

## Prerequisites

- Docker, and the repo's own `docker compose up -d` stack (auth needs Postgres + RabbitMQ).
- A checkout of the suite (about a minute; images are prebuilt, nothing is compiled):

  ```bash
  git clone --depth 1 https://gitlab.com/openid/conformance-suite.git ~/Repos/conformance-suite
  ```

  Elsewhere? Set `CONFORMANCE_SUITE_DIR`.

## Run

```bash
./run-suite.sh            # suite UI on https://localhost.emobix.co.uk:8443 (self-signed; accept)
./run-auth.sh             # auth on 0.0.0.0:5100, conformance clients seeded
```

Both hostnames resolve to 127.0.0.1 in public DNS; `compose.override.yml` maps
`auth.localtest.me` to the host gateway inside the suite's container so the issuer is the same
string from the browser and from the suite. A resolver with DNS-rebind protection breaks both
names the same way — add them to `/etc/hosts` if `dig localhost.emobix.co.uk` returns nothing.

In the suite UI: **Create a new test plan** → advanced (not an ecosystem) → specification
"OpenID Connect Core", entity under test "OpenID Provider / Authorization Server", test type
**"OpenID Connect Core: Basic Certification Profile Authorization server test"** → variants:
server metadata = `discovery`, client registration = `static_client` → paste
[plan-config.json](plan-config.json) into the JSON editor → create, then run the ~35 modules
top to bottom. Sign in as the Development seed user (`user@mystack.local` /
`Devpass!word123`) when a module opens the sign-in page.

## Reading the results

The 2026-08-01 run on auth-track 15a's build: **20 passed, 6 warnings, 4 review, 5 skipped,
0 failed.** Deviations from all-green that are deliberate, documented in
[docs/auth.md](../docs/auth.md):

| Module(s) | Result | Why it stays |
| --- | --- | --- |
| `oidcc-server` | WARNING | `oi_tkn_id`/`oi_au_id` — OpenIddict bookkeeping claims; opaque row ids, load-bearing for `id_token_hint`, revocation, prune |
| `oidcc-scope-email`, `oidcc-alternate-happy-flow` | WARNING | email rides the id token deliberately (first-party BFF reads it there; Google/Microsoft ship the same) |
| `oidcc-scope-profile`, `oidcc-claims-essential` | WARNING | the profile bundle returns what auth truthfully holds — no name/picture/locale data exists (voluntary claims) |
| `oidcc-ensure-request-with-acr-values-succeeds` | WARNING | no `acr` asserted — a single-method password OP has no honest assurance taxonomy; arrives with MFA |
| screenshot modules | REVIEW | terminal state: machine checks passed, the screenshot awaits human judgment — locally, that reviewer is you |
| `oidcc-scope-address/phone/all`, request-object modules | SKIPPED | scopes not advertised; `request_object_signing_alg_values_supported` is explicitly `[]` (PAR is the by-reference channel) |

Module quirks to expect:

- `oidcc-prompt-none-not-logged-in` requires **no live session** — sign out first at
  `http://auth.localtest.me:5100/connect/endsession` (its sibling `-logged-in` wants the
  opposite: sign in on its first leg, the silent second leg is automatic).
- Editing a plan's configuration creates a **new plan**; old results stay under the old one.
- Screenshot uploads use the labeled placeholder slot; the description slot below is optional.

Anything outside this table — any FAILED, any new warning — is a real finding: fix it and pin
it with a test in `server/auth/tests`, the way every finding above was closed.
