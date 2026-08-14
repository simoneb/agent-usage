# Claude Usage Widget

A Windows desktop panel showing your Claude subscription limit usage, for one or many accounts.
It also owns a taskbar button that carries the headline number as its icon, a coloured progress
bar, and a full custom preview on hover.

Usage is read by running the Claude Code CLI:

```
claude -p "/usage" --output-format json
```

That slash command is handled locally by the CLI — it reports `num_turns: 0` and
`total_cost_usd: 0`, so polling costs no tokens. The CLI owns authentication and token refresh;
this app never reads `.credentials.json`, never touches a browser session, and never calls an
Anthropic endpoint directly.

## Download

Grab a binary from [the latest release](https://github.com/simoneb/claude-usage-widget/releases/latest):

| | Run it directly | Zipped, with README and licence |
|---|---|---|
| **Intel / AMD** | `ClaudeUsageWidget-win-x64.exe` | `…-win-x64.zip` |
| **Arm** | `ClaudeUsageWidget-win-arm64.exe` | `…-win-arm64.zip` |

One self-contained file, about 2.3 MB. No installer and no .NET runtime. It is unsigned, so
SmartScreen asks once; `SHA256SUMS.txt` accompanies every release.

## Requirements

- Windows. The UI is raw Win32 and GDI — there is no cross-platform layer, so there is no
  macOS or Linux build. Built and tested on Windows 11; nothing in it needs Windows 11 except
  the rounded window corners, so Windows 10 should work, untested.
- Claude Code installed and on `PATH` (`winget install Anthropic.ClaudeCode`)
- .NET 10 SDK and the MSVC toolchain (Visual Studio with the C++ workload) to build

## Build

```powershell
# vswhere must be on PATH or the NativeAOT link step fails
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"

dotnet publish .\ClaudeUsageWidget.csproj -c Release -o .\dist
```

Produces a single self-contained `ClaudeUsageWidget.exe` of about 2.3 MB. No runtime install
needed on the target machine.

The project is NativeAOT, which rules out WinForms and WPF — the UI is raw Win32 and GDI.
Anything added here must stay AOT-safe: no reflection-based serialisation (use the
`JsonContext` source generator), no dynamic type loading.

Tests cover the `/usage` parser, which is the part that breaks when the CLI changes:

```powershell
dotnet test tests/ClaudeUsageWidget.Tests
```

## Configuration

First run writes `%APPDATA%\ClaudeUsageWidget\config.json`:

```json
{
  "pollSeconds": 30,
  "claudePath": null,
  "alwaysOnTop": true,
  "windowX": null,
  "windowY": null,
  "accounts": [
    { "label": "default", "configDir": null }
  ]
}
```

- `pollSeconds` — poll interval, 30 by default. Each poll spawns the CLI (~2.5 s), so anything
  below 10 seconds is clamped to 10; below that the widget spends most of its life starting
  processes.
- `claudePath` — explicit path to `claude.exe`. `null` resolves from `PATH`.
- `windowX` / `windowY` — panel position, saved automatically when you drag it. `null` means
  "lower-right of the work area". Nullable rather than a `-1` sentinel because monitors placed
  left of or above the primary one have genuinely negative coordinates.
- `accounts[].configDir` — the `CLAUDE_CONFIG_DIR` for that account. `null` uses whatever
  profile the CLI picks by default.

Right-click the panel or the tray icon for **Edit config…**, which opens the file in whatever
editor handles `.json` (Notepad if nothing does), then **Reload config** to apply it.

## Multi-account

Setting up multiple Claude Code profiles is on you — this app only consumes them. Each account
needs its own config directory, logged in once:

```powershell
$env:CLAUDE_CONFIG_DIR = "$env:USERPROFILE\.claude-accounts\work"
claude auth login
```

Then list them:

```json
{
  "pollSeconds": 30,
  "accounts": [
    { "label": "work",     "configDir": "C:\\Users\\you\\.claude-accounts\\work" },
    { "label": "personal", "configDir": "C:\\Users\\you\\.claude-accounts\\personal" }
  ]
}
```

Accounts are probed concurrently as separate processes, so wall time stays around one CLI
startup regardless of how many you list. The panel grows to fit and the taskbar shows the
worst percentage across all of them.

Note that a config directory holds more than credentials — skills, plugins, settings, MCP
config, and session history all live there. A fresh profile starts empty.

## Interaction

- **Minimise button** (top right) sends the panel to the taskbar. The taskbar button stays
  live — hover it for the full panel as a DWM preview.
- **Close button** hides the panel to the tray. It does not exit.
- **Exit** lives in the tray icon's right-click menu, and in the panel's own right-click menu.
- **Drag anywhere** else on the panel to move it. Position is saved.
- **Double-click** to refresh immediately.
- **Right-click** the panel or the tray icon for: show panel, refresh, always-on-top toggle,
  minimise, **Edit config…**, **Reload config**, exit.
- Colours: green below 75%, amber below 90%, red at or above 90%. The taskbar progress bar
  uses the matching normal/paused/error state.

Windows 11 hides newly registered tray icons by default. Click the `^` chevron in the
notification area and drag the badge out to pin it.

## Known limits

- The `/usage` text layout is not a stable contract. If the CLI changes it, the widget reports
  "could not parse" rather than showing a wrong number — see `UsageProbe.LimitLine()` for the
  pattern to adjust.
- Percentages are account-level, so usage from claude.ai and Claude Desktop that draws on the
  same subscription pool is already included. The "what's contributing" breakdown that `/usage`
  prints is machine-local only, and this widget does not display it.
- Separate accounts you legitimately hold are ordinary use; rotating between accounts to work
  past a hit limit is against the Anthropic usage policy.

## Autostart

```powershell
$exe = "D:\dev\claude-usage-widget\dist\ClaudeUsageWidget.exe"
Set-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
  -Name ClaudeUsageWidget -Value $exe
```
