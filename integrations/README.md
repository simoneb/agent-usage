# Integrations

The Windows widget is one way to see this data. `agent-usage` is the other: a single
self-contained binary that prints the same readings, so whatever already draws a status bar on
your machine can show them without any of the Win32 code existing.

Get it from [the latest release](https://github.com/simoneb/agent-usage/releases/latest) —
`agent-usage-osx-arm64`, `agent-usage-linux-x64`, and so on — and put it on your `PATH`.

```console
$ agent-usage
work · claude · max
  session   1%   resets in 4h 16m
  week      0%   resets Aug 22, 11:59am

codex · codex · free  (measured 105d ago)
  week   stale

$ agent-usage --brief
work 0% · codex stale
```

Three output modes, and which one you want depends on how much room you have:

| | For |
|---|---|
| *(default)* | a human at a terminal |
| `--brief` | one line: menu bar title, tmux status, shell prompt |
| `--json` | anything that parses — the schema is documented in the main README |

## macOS — xbar / SwiftBar

Copy [`xbar/agent-usage.30s.sh`](xbar/agent-usage.30s.sh) into your plugin folder and
`chmod +x` it. Keep the `.30s.` in the filename: that is how both apps read the refresh interval.

No jq, no python, no runtime — the plugin only formats what the binary prints.

## Linux — waybar

Copy [`waybar/agent-usage-waybar.sh`](waybar/agent-usage-waybar.sh) into
`~/.config/waybar/scripts/`, `chmod +x` it, and merge
[`waybar/config.jsonc`](waybar/config.jsonc) into your waybar config. Needs `jq`, to translate
this schema into waybar's own.

## tmux

```tmux
set -g status-right "#(agent-usage --brief) | %H:%M"
set -g status-interval 60
```

## Starship

```toml
[custom.agent_usage]
command = "agent-usage --brief"
when = true
format = "[$output]($style) "
style = "dimmed white"
```

## Anything else

`--json` is a stable contract: fields get added, never repurposed, and `schemaVersion` moves if
one ever has to change meaning. Two things worth honouring whatever you build:

- **`percent` can be null, and that is information.** It means this limit cannot honestly produce
  a proportion — a count with no ceiling, or a window that has already reset. Rendering it as `0`
  turns "I don't know" into "you have used none of it".
- **`ageSeconds` matters for Codex.** Its numbers are read from the transcript Codex writes as
  you use it, so a perfectly successful reading can be hours old. Say so, the way the widget and
  these scripts do.

## Polling cost

Each **Claude** account spawns the Claude CLI, which takes roughly two and a half seconds. Each
**Codex** account reads a file and costs nothing measurable. Neither spends tokens: `/usage` is
handled locally by the CLI, and Codex's limits are already on disk. Poll accordingly — 30-60s is
sensible with Claude accounts in the mix, and a Codex-only setup can go as fast as you like.
