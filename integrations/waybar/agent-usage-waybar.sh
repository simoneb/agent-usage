#!/bin/bash
#
# Waybar custom module for agent-usage.
#
# Waybar wants its own JSON shape — {text, tooltip, percentage, class} — so this translates ours
# into that, using the filter in waybar.jq beside it. One agent-usage call, not one per field:
# each Claude account costs a CLI launch, and calling twice would double a poll that already
# takes a couple of seconds.
#
# Needs jq. If you would rather not have that dependency, the text line alone is available
# without it:  agent-usage --brief
#
# See config.jsonc in this directory for the waybar side.

set -euo pipefail

export PATH="$HOME/.local/bin:$PATH"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if ! command -v agent-usage >/dev/null 2>&1; then
  echo '{"text":"agent-usage ?","tooltip":"agent-usage is not on PATH","class":"error"}'
  exit 0
fi

agent-usage --json | jq -c -f "$here/waybar.jq"
