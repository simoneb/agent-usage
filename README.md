# Agent Usage

How much of your week is left, for Claude Code and Codex.

A Windows desktop panel showing your AI coding subscription limits, for one or many accounts.
It also owns a taskbar button that carries the headline number as its icon, a coloured progress
bar, and a full custom preview on hover.

It ships with `agent-usage`, a portable CLI that prints the same readings on macOS and Linux —
for a menu bar, a status bar, or a shell prompt. Same code underneath, so the two can never
disagree about what your usage is.

## Where the numbers come from

Every figure here comes from a tool you installed, or a file that tool wrote. Nothing else.

**Claude Code** is asked directly:

```
claude -p "/usage" --output-format json
```

That slash command is handled locally by the CLI — it reports `num_turns: 0` and
`total_cost_usd: 0`, so polling costs no tokens. The CLI owns authentication and token refresh;
this app never reads `.credentials.json`, never touches a browser session, and never calls an
Anthropic endpoint directly.

**Codex** has no equivalent command yet ([openai/codex#15281](https://github.com/openai/codex/issues/15281)
asks for one), but it records the rate limits the API returns into its own session transcript on
every turn. Reading the last of those costs no process, no tokens and no credentials. The catch
is honest staleness: that number is only as fresh as your last Codex turn, so it is always
reported with its age, and a window that has since reset reads `stale` rather than showing a
percentage that describes a week which is over.

### What is deliberately not here

The other tools in this space cover a dozen providers each, and they get there by reading browser
session cookies, scraping dashboards, or calling undocumented internal endpoints. That is the
whole difference in coverage, and it is a trade this project doesn't make: every one of those is
one silent change away from confidently reporting a wrong number, and none of them can be
audited by the person running them.

So GitHub Copilot is absent, despite being the obvious next candidate. Its documented billing
endpoint doesn't serve individual quota, its in-CLI `/usage` is
[known to undercount](https://github.com/github/copilot-cli/issues/1582) and to
[emit nonsense once you're over](https://github.com/github/copilot-cli/issues/2797), and the
working approach everyone else uses is an internal API behind a device-flow token. It goes in the
day the Copilot CLI can report its own usage.

## Download

Grab a binary from [the latest release](https://github.com/simoneb/agent-usage/releases/latest).

**The widget**, Windows only:

| | Run it directly | Zipped, with README and licence |
|---|---|---|
| **Intel / AMD** | `AgentUsageWidget-win-x64.exe` | `…-win-x64.zip` |
| **Arm** | `AgentUsageWidget-win-arm64.exe` | `…-win-arm64.zip` |

**The CLI**, everywhere: `agent-usage-osx-arm64`, `agent-usage-linux-x64`,
`agent-usage-linux-arm64`, `agent-usage-win-x64.exe`. The `.tar.gz` of each carries the xbar and
waybar integrations alongside the binary.

Every one of these is a single self-contained file of under 3 MB. No installer and no .NET
runtime. They are unsigned, so SmartScreen asks once on Windows and Gatekeeper wants
`xattr -d com.apple.quarantine` once on macOS; `SHA256SUMS.txt` accompanies every release.

## Requirements

- **The widget needs Windows.** Its UI is raw Win32 and GDI with no cross-platform layer, and
  the taskbar progress bar and hover preview have no counterpart on macOS or Linux to port to.
  Built and tested on Windows 11; nothing in it needs Windows 11 except the rounded window
  corners, so Windows 10 should work, untested.
- **The CLI runs anywhere** — macOS, Linux, Windows.
- Claude Code installed and on `PATH` (`winget install Anthropic.ClaudeCode`) for Claude
  accounts; Codex accounts need nothing but a `~/.codex` that has been used at least once.
- .NET 10 SDK to build. The widget additionally needs the MSVC toolchain (Visual Studio with the
  C++ workload) for its NativeAOT link step.

## The CLI

```console
$ agent-usage
work · claude · max
  session   1%   resets in 4h 16m
  week      0%   resets Aug 22, 11:59am

codex · codex · plus  (measured 3h ago)
  session  12%   resets in 1h 4m
  week      3%   resets Aug 18, 2pm

$ agent-usage --brief
work 0% · codex 3%
```

| Flag | |
|---|---|
| *(none)* | the report above, for a human at a terminal |
| `--brief` | one line, for a menu bar title, tmux status or shell prompt |
| `--json` | the full snapshot, for anything that parses |
| `--provider claude\|codex` | only accounts of that provider |
| `--config <path>` | a config file other than the default |
| `--config-path` | print where config is read from, and exit |

An account that fails is reported as an error *inside* the output, and the exit code stays 0 — a
status bar polling this needs something to render either way. A non-zero exit means the run
itself was impossible: bad arguments, or an unreadable config.

See [`integrations/`](integrations/) for ready-made xbar (macOS) and waybar (Linux) plugins,
plus tmux and Starship snippets.

### The JSON contract

`--json` is meant to be depended on. Fields get added, never repurposed, and `schemaVersion`
moves if one ever has to change meaning.

```json
{
  "schemaVersion": 1,
  "generatedAt": "2026-08-15T13:33:53+02:00",
  "accounts": [
    {
      "provider": "claude",
      "label": "work",
      "loggedIn": true,
      "email": "you@example.com",
      "plan": "max",
      "measuredAt": "2026-08-15T13:33:52+02:00",
      "ageSeconds": 1,
      "headlinePercent": 42,
      "headline": { "label": "Current week (all models)", "kind": "percent", "value": 42, "percent": 42, "display": "42%" },
      "limits": [
        {
          "label": "Current week (all models)",
          "kind": "percent",
          "value": 42,
          "max": 100,
          "percent": 42,
          "display": "42%",
          "resets": "Aug 22, 11:59am (Europe/Rome)",
          "resetsAt": "2026-08-22T09:59:00+00:00"
        }
      ]
    }
  ]
}
```

Two things to honour if you build on it:

- **`percent` can be null, and that is information.** It means this limit cannot honestly produce
  a proportion — a count with no ceiling, or a window that has already reset (`expired: true`).
  Rendering it as `0` turns "I don't know" into "you have used none of it".
- **`ageSeconds` matters.** A Codex reading can be hours old and still be the best available. Say
  so, the way the widget and the bundled scripts do.

`kind` is `percent`, `count` or `currency`, because providers genuinely disagree about what a
limit *is*: a proportion of a rolling window, a number of requests per month, money against a
cap. `display` is preformatted, invariantly, for the common case where you just want to print it.

## Configuration

First run writes the config, and where depends on the platform's own convention:

| | |
|---|---|
| Windows | `%APPDATA%\agent-usage\config.json` |
| macOS | `~/Library/Application Support/agent-usage/config.json` |
| Linux | `~/.config/agent-usage/config.json` (or `$XDG_CONFIG_HOME`) |

`agent-usage --config-path` prints the one in effect.

```json
{
  "pollSeconds": 30,
  "claudePath": null,
  "alwaysOnTop": true,
  "checkForUpdates": true,
  "autoUpdate": false,
  "iconLimit": null,
  "windowX": null,
  "windowY": null,
  "accounts": [
    { "label": "default", "provider": "claude", "configDir": null }
  ]
}
```

- `pollSeconds` — poll interval, 30 by default. Each Claude account spawns the CLI (~2.5 s), so
  anything below 10 seconds is clamped to 10; below that the widget spends most of its life
  starting processes. Codex accounts are a file read and cost nothing.
- `claudePath` — explicit path to the Claude CLI. `null` resolves from `PATH`.
- `checkForUpdates` — whether to ask GitHub once a day whether a newer release exists. `false`
  means the app makes no network requests of its own at all; the panel and the right-click menu
  simply never mention updates.
- `autoUpdate` — whether finding a newer release should also install it, rather than waiting for
  you to click. Off by default: an app that replaces itself and restarts while you are reading it
  is a surprise, and the notice is one click either way. Ignored when `checkForUpdates` is off.
- `iconLimit` — which limit the icon, taskbar bar and tooltip report, matched against a row
  label: `"session"`, `"all models"`, a model name. `null` shows everything the icon has room
  for. Easier set from the **Icon shows** menu, which lists the rows your tools actually reported.
- `windowX` / `windowY` — panel position, saved automatically when you drag it. `null` means
  "lower-right of the work area". Nullable rather than a `-1` sentinel because monitors placed
  left of or above the primary one have genuinely negative coordinates.
- `accounts[].provider` — `"claude"` or `"codex"`. Absent means Claude, so every config written
  before there was a second provider keeps working untouched.
- `accounts[].configDir` — that provider's home directory: `CLAUDE_CONFIG_DIR` for Claude,
  `CODEX_HOME` for Codex. `null` uses whatever profile the tool picks by default.

Right-click the panel or the tray icon for **Edit config…**, which opens the file in whatever
editor handles `.json` (Notepad if nothing does), then **Reload config** to apply it.

## Multiple accounts and tools

Setting up multiple profiles is on you — this app only consumes them. Each Claude account needs
its own config directory, logged in once:

```powershell
$env:CLAUDE_CONFIG_DIR = "$env:USERPROFILE\.claude-accounts\work"
claude auth login
```

Then list whatever mix you want:

```json
{
  "accounts": [
    { "label": "work",     "provider": "claude", "configDir": "C:\\Users\\you\\.claude-accounts\\work" },
    { "label": "personal", "provider": "claude", "configDir": "C:\\Users\\you\\.claude-accounts\\personal" },
    { "label": "codex",    "provider": "codex" }
  ]
}
```

Accounts are probed concurrently, so wall time stays around one CLI startup regardless of how
many you list. The panel grows to fit and the taskbar shows the worst percentage across all of
them.

Note that a config directory holds more than credentials — skills, plugins, settings, MCP
config, and session history all live there. A fresh profile starts empty.

## Interaction

- **Minimise button** (top right) sends the panel to the taskbar. The taskbar button stays
  live — hover it for the full panel as a DWM preview.
- **Close button** hides the panel to the tray. It does not exit.
- **Exit** lives in the tray icon's right-click menu, and in the panel's own right-click menu.
- **Drag anywhere** else on the panel to move it. Position is saved.
- **Double-click** to refresh immediately.
- **Right-click** the panel or the tray icon for: show panel, refresh, **Icon shows ▸**,
  always-on-top toggle, minimise, **Edit config…**, **Reload config**, exit.
- Colours: green below 75%, amber below 90%, red at or above 90%. The taskbar progress bar
  uses the matching normal/paused/error state.
- The title bar shows how stale the reading is — `just now`, `12s ago`, `3m ago` — and, when a
  newer release exists, `update to v0.8.0` on the right. That is a button: clicking it downloads
  the build for this machine, checks it against the `SHA256SUMS.txt` published with the release,
  puts it in place of the running binary and restarts into it — no installer, no browser, no
  admin prompt, and nothing replaced unless the checksum matches. The tray menu carries the same
  action plus **Release notes…**. Set `autoUpdate` to `true` to have it happen without the click,
  or `checkForUpdates` to `false` to switch the whole thing off.
- An account whose figures are older than the poll — which is the normal state of a Codex
  account — says so under its name: `Codex · measured 3h ago`.
- Reset times are grouped by moment, so the weekly and model windows that share one share a
  line. Anything resetting within 24 hours is counted down (`session resets in 1h 32m`);
  further out, the date is the clearer answer (`week resets Aug 21, 8:59pm`).

## What the icon shows

Two things can stop you working, on different horizons: the session window, which resets in
hours, and the weekly one, which locks you out for days. Both belong in the icon, and a 16px
tray icon has room for only so much, so detail is dropped in a fixed order as accounts are
added:

| accounts | icon |
|---|---|
| 1 | week digits, with the session as a strip along the top edge |
| 2 | a bar pair per account — session above week, matching the panel's row order |
| 3–4 | one bar per account, week only |
| 5+ | digits for the worst week |

Colour always means severity and never identity. Accounts are told apart by position, in config
order, which makes the panel the icon's legend. **Icon shows ▸** overrides all of this with a
single chosen limit, one bar per account.

An account with no honest number — signed out, failed, or every window expired — contributes no
bar at all rather than an empty one, because an empty track reads as "none used".

The icon is drawn at whatever size the shell asks for on the monitor the taskbar is on — 16px at
100%, 24px at 150% — and the bars run edge to edge of it. Both matter more than they sound: a
fixed 16px icon is drawn smaller than its neighbours on a scaled display, and rows that round
down individually leave a couple of the sixteen pixels unused, which is enough to read as a
smaller icon than everything beside it.

Windows 11 hides newly registered tray icons by default. Click the `^` chevron in the
notification area and drag the badge out to pin it.

## Build

```powershell
# vswhere must be on PATH or the NativeAOT link step fails
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

dotnet publish .\src\AgentUsage.Widget -c Release -o .\dist       # the widget
dotnet publish .\src\AgentUsage.Cli    -c Release -o .\dist-cli   # the CLI
```

NativeAOT cannot cross-compile between operating systems, so the macOS and Linux CLI binaries
are built on their own CI runners — and run there too, which is the only hands-on testing those
platforms get from a Windows-only machine.

Three projects:

| | |
|---|---|
| `src/AgentUsage.Core` | portable: config, providers, the limit model. `net10.0`. |
| `src/AgentUsage.Cli` | the `agent-usage` binary. `net10.0`. |
| `src/AgentUsage.Widget` | the Windows widget: Win32, GDI, tray, taskbar, autostart. `net10.0-windows`. |

Everything is NativeAOT, which rules out WinForms and WPF — the UI is raw Win32 and GDI.
Anything added must stay AOT-safe: no reflection-based serialisation (add types to the
`CoreJson` source-generated context), no dynamic type loading.

Tests cover the parsers, which are the parts that break when a CLI changes what it prints, and
the rules that decide when a number is not fit to show:

```powershell
dotnet test tests/AgentUsage.Tests          # portable — also runs on Linux and macOS in CI
dotnet test tests/AgentUsage.Widget.Tests   # Win32: autostart, update check, icon layout
```

The version lives once, in `Directory.Build.props`, and the release workflow rewrites that line.

## Known limits

- The `/usage` text layout is not a stable contract. If the CLI changes it, the widget reports
  "could not parse" rather than showing a wrong number — see `ClaudeProvider.LimitLine()` for the
  pattern to adjust.
- Codex's transcript records are internal, not a published contract, and are read on the same
  terms: a shape that stops matching produces an error, never a zero.
- A Codex reading only refreshes when you use Codex. Left long enough, its window resets and the
  reading is discarded rather than shown — so a Codex account you haven't touched this week
  shows `stale`, not `0%`.
- Percentages are account-level, so usage from claude.ai and Claude Desktop that draws on the
  same subscription pool is already included. The "what's contributing" breakdown that `/usage`
  prints is machine-local only, and this widget does not display it.
- Separate accounts you legitimately hold are ordinary use; rotating between accounts to work
  past a hit limit is against the provider's usage policy.

## Autostart

Right-click the panel or the tray icon and tick **Start with Windows**. It writes the running
exe's own path to the per-user Run key, so it needs no elevation and appears in Task Manager's
**Startup apps**, where it can be disabled without knowing this app put it there.

The tick reflects the registry rather than a saved preference, so turning it off in Task Manager
shows up here too. It also reads as off when the entry points at a copy that has since moved —
ticking it then repairs the path instead of leaving a checkmark on something that starts
nothing. Move the exe, tick it again.
