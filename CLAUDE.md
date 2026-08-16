# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows desktop widget (`AgentUsageWidget.exe`) and a portable CLI (`agent-usage`) that report
how much of your Claude Code and Codex subscription limits you have used. Both read the same core,
so they can never disagree about a number.

## Commands

```powershell
dotnet build src/AgentUsage.Widget/AgentUsage.Widget.csproj   # fast loop; no AOT link

dotnet test tests/AgentUsage.Tests          # portable: parsers, limit model, reset times
dotnet test tests/AgentUsage.Widget.Tests   # Win32: autostart, update check, icon layout
dotnet test tests/AgentUsage.Tests --filter FullyQualifiedName~ParseLimitsTests   # one class
dotnet test tests/AgentUsage.Tests --filter "DisplayName~expired"                 # one test

# Release builds. vswhere must be on PATH or the NativeAOT link step fails.
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
dotnet publish .\src\AgentUsage.Widget -c Release -o .\dist
dotnet publish .\src\AgentUsage.Cli    -c Release -o .\dist-cli
```

Releases are a manual GitHub Actions dispatch that bumps the version, tags and publishes:
`gh workflow run release.yml -f bump=patch`. The version lives once, in `Directory.Build.props`;
the workflow rewrites that line. Never bump it in a normal commit.

## Testing a widget change on this machine

The widget holds a single-instance mutex (`Program.cs`), so a test run requires stopping the one
already running — which is the user's real widget. Stop it, test, and put it back in the same
step:

```powershell
Stop-Process -Name AgentUsageWidget -Force
# …run the build under test…
Copy-Item .\dist\AgentUsageWidget.exe "$env:LOCALAPPDATA\Programs\AgentUsage\AgentUsageWidget.exe" -Force
Start-Process "$env:LOCALAPPDATA\Programs\AgentUsage\AgentUsageWidget.exe"
```

Window behaviour can be exercised without a human: the widget's whole UI is one wndproc, so
`SendMessageW` with the real message (e.g. `WM_APP_TRAY` = `0x8002` with `lParam` = `WM_LBUTTONUP`
for a tray left-click, or `WM_KEYDOWN`/`VK_ESCAPE`) drives it exactly as the shell would, and
`IsWindowVisible`/`IsIconic` read the result.

## Architecture

**`src/AgentUsage.Core`** (`net10.0`, portable) — everything that decides what a number *is*.

- `Providers/IUsageProvider` is the extension point, with a hard rule stated in its doc comment:
  a provider may run a CLI the user installed or read a file that CLI wrote, **and nothing else**.
  No browser cookies, no credential files, no undocumented endpoints. This is the project's main
  design position; new providers that need any of those don't get added.
- `ClaudeProvider` shells out to `claude -p "/usage" --output-format json` and parses the text
  layout (`LimitLine()` is the regex that breaks when the CLI changes its output).
  `CodexProvider` reads the rate-limit records Codex writes into its own session transcript.
- `Status.cs` holds the limit model. `LimitRow.Percent` is deliberately nullable: a count with no
  ceiling, or a window that already reset (`Expired`), cannot honestly produce a proportion, and
  rendering it as `0` turns "I don't know" into "you have used none of it". Every consumer must
  skip nulls rather than default them.
- `UsageService.ProbeAllAsync` fans out over accounts concurrently and is what both front ends
  call. Anything that should hold for widget and CLI alike belongs here, not in either front end.

**`src/AgentUsage.Widget`** (`net10.0-windows`) — raw Win32 + GDI, no WinForms/WPF (NativeAOT
rules them out).

- `Widget.cs` is the whole app: one window class, one wndproc, timers for the poll and the clock,
  the tray icon, the context menu, and the update flow. State lives in fields on the single
  `Widget` instance; background probes post `WM_APP_*` messages back to the UI thread rather than
  touching it.
- `Renderer.cs` draws the panel into an off-screen 32bpp DIB, reused for the DWM iconic
  thumbnail and live preview — so hovering the taskbar button shows the real panel.
  `IconBuilder.cs` draws the tray/taskbar badge, whose layout changes with account count.
  `TaskbarProgress.cs` is the taskbar progress bar, `Updates.cs` the self-replacing updater,
  `Autostart.cs` the per-user Run key. `Native.cs` is the P/Invoke surface — add constants and
  imports there, not inline.
- The panel is `WS_POPUP` with `WS_EX_APPWINDOW`: no real caption, so `WM_NCHITTEST` returns
  `HTCAPTION` everywhere except the title-bar buttons to make the whole panel draggable.
- Close hides to the tray; minimise goes to the taskbar; exit only from the menus.

**`src/AgentUsage.Cli`** (`net10.0`) — `agent-usage`. An account that fails is an error *inside*
the output with exit code 0; non-zero exit means the run itself was impossible. `--json` is a
stability contract: fields get added, never repurposed, and `schemaVersion` moves if one has to
change meaning.

## Constraints that bite

- **NativeAOT everywhere.** No reflection-based serialisation — every type crossing a JSON
  boundary must be listed in `CoreJson.cs`'s source-generated context — and no dynamic type
  loading. Reflection JSON does not warn under AOT, it fails on the user's machine.
- **Format invariantly.** `LimitRow.Display` and everything in the JSON output use
  `CultureInfo.InvariantCulture`; a status bar parsing `$12,40` on an it-IT machine is a bug
  nobody finds quickly.
- **A parse that stops matching must produce an error, never a zero.** Both providers' inputs are
  unstable by nature; "could not parse" is the correct output, a confident wrong number is not.
- Exceptions must not cross back into Win32 — `WndProcStatic` swallows them for that reason.

## Conventions

Comments explain *why*, in full prose sentences, and are used generously on decisions that look
arbitrary otherwise. Commit subjects are the same voice: a sentence saying what the change does
("Name the running build at the top of the context menu"), not a conventional-commits prefix.
British spelling in user-facing strings ("Minimise to taskbar").

README.md is the user-facing documentation and is kept in step with behaviour changes; `docs/`
is the landing page.
