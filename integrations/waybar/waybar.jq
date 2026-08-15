# Translates the agent-usage snapshot into waybar's own {text, tooltip, percentage, class}.
#
# Its own file rather than a heredoc inside the script so that CI can run it against a fixture:
# a typo in here would otherwise only show up as an empty module on someone's bar.

# An account contributes a number only when it has an honest one. A stale window or a failed
# probe shows as a marker, never as a percentage that happens to be lying around.
def marker: if .error then "!" elif .headline then .headline.display else "?" end;

def worst: [.accounts[] | .headlinePercent // empty] | if length == 0 then null else max end;

{
  text: ([.accounts[] | "\(.label) \(marker)"] | join(" · ")),

  tooltip: ([.accounts[] |
    "\(.label) · \(.provider)"
    + (if .error then "\n  \(.error)"
       else ([.limits[] | "\n  \(.label): \(.display)"] | join(""))
       end)
    + (if .ageSeconds != null and .ageSeconds > 90
       then "\n  measured \((.ageSeconds / 60 | floor))m ago"
       else "" end)
  ] | join("\n\n")),

  percentage: (worst // 0),

  class: (worst as $w
    | if $w == null then "unknown"
      elif $w >= 90 then "critical"
      elif $w >= 75 then "warning"
      else "ok" end),
}
