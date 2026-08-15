#!/bin/bash
#
# <xbar.title>Agent Usage</xbar.title>
# <xbar.version>v1.0</xbar.version>
# <xbar.author>simoneb</xbar.author>
# <xbar.author.github>simoneb</xbar.author.github>
# <xbar.desc>Claude Code and Codex subscription limits in the macOS menu bar.</xbar.desc>
# <xbar.dependencies>agent-usage</xbar.dependencies>
# <xbar.abouturl>https://github.com/simoneb/agent-usage</xbar.abouturl>
#
# Install: copy into your xbar (or SwiftBar) plugin folder, keeping the ".30s." in the name —
# that is how both apps decide the refresh interval. Then `chmod +x` it.
#
# No jq, no python, no runtime: agent-usage is a single self-contained binary and this script
# only formats what it prints.

export PATH="/opt/homebrew/bin:/usr/local/bin:$HOME/.local/bin:$PATH"

if ! command -v agent-usage >/dev/null 2>&1; then
  echo "⚠️ agent-usage"
  echo "---"
  echo "agent-usage is not on PATH"
  echo "Download it | href=https://github.com/simoneb/agent-usage/releases/latest"
  exit 0
fi

# The title line: one line is all the menu bar gets.
agent-usage --brief

echo "---"

# The dropdown: every account, every window, with reset times.
agent-usage | while IFS= read -r line; do
  echo "${line} | font=Menlo size=12"
done

echo "---"
echo "Refresh | refresh=true"
