#!/usr/bin/env bash
# Starts (or stops) the OpenID conformance suite from its own checkout, overlaid with this
# repo's networking override. Usage:
#   ./run-suite.sh            # up -d
#   ./run-suite.sh down       # stop it
#   ./run-suite.sh pull       # refresh the prebuilt images
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
suite_dir="${CONFORMANCE_SUITE_DIR:-$HOME/Repos/conformance-suite}"

if [[ ! -f "$suite_dir/docker-compose-prebuilt.yml" ]]; then
  echo "conformance suite not found at $suite_dir" >&2
  echo "  git clone --depth 1 https://gitlab.com/openid/conformance-suite.git \"$suite_dir\"" >&2
  echo "  (or set CONFORMANCE_SUITE_DIR)" >&2
  exit 1
fi

cmd=("${@:-up}")
[[ "${cmd[0]}" == "up" ]] && cmd=(up -d)

exec docker compose \
  -f "$suite_dir/docker-compose-prebuilt.yml" \
  -f "$here/compose.override.yml" \
  "${cmd[@]}"
