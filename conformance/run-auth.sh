#!/usr/bin/env bash
# Runs auth the way the conformance suite needs it: bound to every interface (the suite's
# containers reach it through the docker host gateway) and with the suite's two confidential
# clients seeded on top of the Development seed. The clients land in the dev database as
# ordinary seeded clients; they are harmless to keep.
set -euo pipefail

cd "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/.."

suite="https://localhost.emobix.co.uk:8443"

exec dotnet run --project server/auth/src -- \
  --urls http://0.0.0.0:5100 \
  --Seed:Clients:4:ClientId=conformance \
  --Seed:Clients:4:DisplayName="Conformance suite" \
  --Seed:Clients:4:Type=Confidential \
  --Seed:Clients:4:Secret=conformance-secret-one \
  --Seed:Clients:4:RedirectUris:0="$suite/test/a/mystack/callback" \
  --Seed:Clients:4:RedirectUris:1="$suite/test/a/mystack/callback?dummy1=lorem&dummy2=ipsum" \
  --Seed:Clients:4:PostLogoutRedirectUris:0="$suite/test/a/mystack/post_logout_redirect" \
  --Seed:Clients:4:Scopes:0=email \
  --Seed:Clients:4:Scopes:1=profile \
  --Seed:Clients:4:Scopes:2=roles \
  --Seed:Clients:5:ClientId=conformance-2 \
  --Seed:Clients:5:DisplayName="Conformance suite (second client)" \
  --Seed:Clients:5:Type=Confidential \
  --Seed:Clients:5:Secret=conformance-secret-two \
  --Seed:Clients:5:RedirectUris:0="$suite/test/a/mystack/callback" \
  --Seed:Clients:5:Scopes:0=email \
  --Seed:Clients:5:Scopes:1=profile \
  --Seed:Clients:5:Scopes:2=roles
