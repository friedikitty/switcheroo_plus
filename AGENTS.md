# Repository Guidelines

## Agent Safety Rules

### Commit every iteration
- At the end of **every** working iteration (feature, fix, or verification cycle), commit the changes. Do not leave completed work uncommitted.
- Feature/fix work happens on a development branch checked out in the git worktree (e.g. `G:\github\switcheroo_plus_dev`); the main checkout stays on clean `master`.
- Never commit to `master` directly unless the user explicitly asks.

### Running the GUI application
- **Never** start `switcheroo_plus.exe` (or any interactive Windows process) without a defined timeout and a reliable way to communicate with or terminate it.
- If the app is started for verification, always:
  1. Confirm the process is still alive after launch.
  2. Use an established IPC mechanism (named pipe, TCP, window message) to communicate; do not rely on the presence of a main window or taskbar icon.
  3. Kill the process cleanly after the test if it has not exited on its own.
- A hanging or crashed GUI process must be treated as a test failure, not left running indefinitely.

## Project Overview

Switcheroo is a Windows incremental-search task switcher (Alt+Space / Alt+Tab replacement). It enumerates all top-level windows, lets the user type a few characters of a window title, and switches to the selected window. Originally written by James Sulak (2009), maintained by Regin Larsen since 2014.

License: GNU GPL v3  
Distribution: GitHub Releases, Chocolatey (`choco install switcheroo`), portable ZIP, Inno Setup installer.

---

## Architecture & Data Flow

```
[User presses hotkey]
    │
    ▼
[HotKey / AltTabHook] ──► [MainWindow] (WPF overlay, borderless, topmost)
    │                           │
    │                           ▼
    │                   [TextBox] query ──► [WindowFilterer]
    │                                           │
    │                           ┌───────────────┼───────────────┐
    │                           ▼               ▼               ▼
    │                     [StartsWith]  [Contains]  [IndividualChars]
    │                     (score 4)     (score 2)   (score 1)
    │                           │               │               │
    │                           └───────────────┴───────────────┘
    │                                           │
    │                                           ▼
    │                            ObservableCollection<AppWindowViewModel>
    │                                           │
    │                                           ▼
    │                            [ListBox] with icon + highlighted title
    │                                           │
    │                            [Enter / double-click]
    │                                           ▼
    └───────────────────────────────► [AppWindow.SwitchToThisWindow()]
```

**Key modules**
- **`Core/`** — Window enumeration, filtering, matching, icon extraction, P/Invoke wrappers. Pure class library with no UI.
- **`Switcheroo/`** — WPF application shell, overlay window, options/about dialogs, hotkey registration, settings persistence.
- **`ManagedWinapi/`** — Third-party Win32 wrapper (`SystemWindow`, `SystemAccessibleObject`, etc.).
- **`Core.UnitTests/`** — Matcher and highlighter unit tests.

---

## Key Directories

| Directory | Purpose |
|-----------|---------|
| `Switcheroo/` | WPF app entry point, windows, view models, converters, settings provider |
| `Core/` | Domain logic: `AppWindow`, `WindowFinder`, `WindowFilterer`, matchers, `WinApi` |
| `ManagedWinapi/` | External dependency: Win32 window / accessibility wrappers |
| `Core.UnitTests/` | NUnit tests for matchers and `XamlHighlighter` |
| `Installer/` | Inno Setup script (`Installer.iss`) and MSBuild packaging (`Build.xml`) |
| `ChocolateyPackages/` | `nuspec` + install scripts for `switcheroo`, `switcheroo.install`, `switcheroo.portable` |

---

## Development Commands

Build (requires .NET Framework 4.5 + MSBuild or Visual Studio 2013+):

```powershell
# Build Debug
msbuild Switcheroo.sln /p:Configuration=Debug

# Build Release
msbuild Switcheroo.sln /p:Configuration=Release

# Build portable release
msbuild Installer\Build.xml /t:CreatePortable

# Build installer
msbuild Installer\Build.xml /t:CreateSetup
```

Run tests (requires NUnit 2.6.3 runner or compatible adapter):

```powershell
# Using NUnit console runner
packages\NUnit.2.6.3\tools\nunit-console.exe Core.UnitTests\bin\Debug\Switcheroo.Core.UnitTests.dll
```

Run application:

```powershell
Switcheroo\bin\Debug\switcheroo.exe
```

---

## Code Conventions & Common Patterns

### Naming
- PascalCase for classes, methods, properties, public fields.
- `private` fields: camelCase (no `_` prefix observed).
- Test methods: `MethodName_Scenario_ExpectedResult` (e.g., `Evaluate_InputNull_ReturnsNonMatchingResult`).

### UI / Data Binding
- View models implement `INotifyPropertyChanged` manually (no MVVM framework).
- XAML uses `PriorityBinding` for async icon lookup (cached → fallback).
- Attached property `FormattedTextAttribute` parses XAML fragments into `TextBlock.Inlines`.
- Commands are `ICommand` implementations inside `MainWindow.xaml.cs` (no centralized command library).

### Window Enumeration & Filtering
- `WindowFinder.GetWindows()` enumerates toplevel windows via `SystemWindow.AllToplevelWindows` and filters with `AppWindow.IsAltTabWindow()`.
- `IsAltTabWindow()` applies Raymond Chen rules: visible, titled, not tool window, not `NOACTIVATE`, no visible owner, not `ITaskList_Deleted`, skips UWP `CoreWindow`, handles `ApplicationFrameWindow` cloak type.
- `WindowFilterer` runs four matchers in sequence and accumulates scores.
- Overlay list ordering (`MainWindow.LoadData` / `OrderWindows`): when `RecentWindowsOnTopEnabled` (default on), the top of the list is the previous app followed by the other most-recently-used windows (up to `RecentWindowsOnTopCount`, default 5) sorted alphabetically; all remaining windows are alphabetical by process name then title. When disabled, the legacy order applies: all MRU windows first in recency order, then the rest alphabetically. Recency is tracked by a 500 ms foreground poll in `SetUpForegroundTracking` plus every `ActivateAppWindow`. In both modes the foreground window is moved to the bottom so pressing Enter switches to the previous app.

### Matchers (score-based ranking)
All implement `IMatcher`:

| Matcher | Score | Behavior |
|---------|-------|----------|
| `StartsWithMatcher` | 4 | Prefix match |
| `SignificantCharactersMatcher` | 2 | Boundary-aware regex (word-start chars) |
| `ContainsMatcher` | 2 | Substring regex |
| `IndividualCharactersMatcher` | 1 | Fuzzy character sequence |

Return: `MatchResult` with `StringPart` segments (matched/unmatched).

### Async / Threading
- No `async/await` or `Task` usage.
- Icon extraction uses `SendMessageTimeout` (Win32) with a timeout fallback.
- `WindowCloser` uses a `BackgroundWorker`-style polling loop to detect closed windows.
- Window activation goes through `WinApi.ForceSwitchToWindow` (`AttachThreadInput` workaround); plain `SwitchToThisWindow` is denied by the foreground lock when another app received the most recent input. When there is no foreground window at all (disconnected session) the attach is impossible, so it falls back to `SetWindowPos(HWND_TOP)` to at least raise the window.
- The previous-app hook (`PreviousAppHook`) fires synchronously inside the `WH_KEYBOARD_LL` callback; the switch must be deferred with `Dispatcher.BeginInvoke` because `SetForegroundWindow` is denied while the intercepted keystroke is still being processed.

### Taskbar badges (quick-switch key overlay)
- When `AzShortcutsEnabled` and `TaskbarBadgesEnabled` (both default on), each window's shortcut key is drawn at the top-left of its taskbar button. Rendering lives in a **separate helper process** (`BadgeOverlay/` -> `switcheroo_plus_badges.exe`, copied next to the main exe at build time) so badge/taskbar bugs can never crash the switcher. `Switcheroo/TaskbarBadgeOverlay.cs` is a thin **named-pipe client** (pipe `switcheroo_plus_badges`): it sends (key, title) pairs, the helper does everything else. The helper is also warmed ~2 s after the main app starts (cache-only scan, `DispatcherTimer` in `MainWindow`), is reused across shows, respawns automatically if killed, and exits when the main app exits (`Application.Exit` handler in `MainWindow`).
- Client calls are fire-and-forget (connect + write + close; only `getbadges` / `stats` read a response) and run on a thread-pool thread - a helper that dies mid-conversation must never hang the UI thread.
- **Taskbar geometry is cached in the helper.** A show whose taskbar signature (primary/secondary tray handles + rects) still matches the cache places every badge from the cached button rects with no UI Automation, so badges appear together with the overlay instead of ~1 s later. The UIA scan runs on a **background MTA thread** and only refreshes the cache (then re-places); the pipe loop and the dispatcher are never blocked by it - which is why `show` acknowledges before placement. Scans are throttled to at most one per 3 s (`MinScanIntervalMs`), so a slow UIA call cannot spin the CPU.
- Cache invalidation: a different tray signature (taskbar moved/resized, explorer restarted, monitor change), a taskbar that is not visible, an unknown title set, a title the last scan never saw (a window opened), or a cache older than 60 s triggers a background refresh. Closing a window needs no rescan - its badge simply has no match. The cache is keyed by the taskbar button name (UI Automation exposes only the title; process name is not available per button), so a retitled window receives its badge after the next scan while unchanged windows keep drawing instantly. Nothing is persisted: the helper is long-lived, and a restarted helper starts with an empty cache and warms itself 1 s after launch.
- Pipe actions: `show` (key+title pairs + timeout), `hide`, `warm` (cache-only scan + chip pre-creation, draws nothing), `stats` (cache size, placement/refresh timings, move counts, stale reason, skipped scans - test diagnostics), `getbadges`, `ping`, `exit`.
- The helper draws **one small opaque, click-through, topmost WPF window per badge**, pooled **per shortcut key** with the label set once at creation, 18x19. Two details matter for latency and were measured, not guessed: creating/showing a WPF window costs tens of ms, and re-rendering a label is not free, so chips are created ahead of time at `DispatcherPriority.Background` and parked far off-screen (`-30000`); a show only moves them, all moves in one `DeferWindowPos` batch (a chip move is ~2 ms, a `Show()` is ~25 ms+). **Do not add `SWP_NOREDRAW`** to those moves - it leaves parked-then-moved windows unpainted (verified by screenshot diff: chips vanish). A single full-screen transparent (layered) overlay renders nowhere on multi-monitor mixed-DPI setups, and small layered windows are clipped by the taskbar on Windows 10 - opaque non-layered windows are the only form verified to show over the taskbar. It also draws a clickable **X chip at the taskbar's top-right corner**; clicking it stops the helper.
- Badges auto-hide after `TaskbarBadgeTimeoutSeconds` (default 10, 0 = never); the timer lives in the helper, starts only once badges are actually placed for the current show, and suppressing a late scan result parks the chips so a hidden overlay never gets its badges back.
- Taskbar buttons are located via UI Automation: `FindAll(ControlType.Button)` over every `Shell_TrayWnd` / `Shell_SecondaryTrayWnd` toplevel window. `TB_BUTTONCOUNT`-style toolbar messages return 0 on ExplorerPatcher/reimplemented taskbars - UIA is the reliable path. Empty/failed scans retry up to 4 times at 500 ms intervals and keep the previous cache. Button->window matching is by exact title, else `"<title> - <digits>..."` (grouped-button suffix, localized), longest titles first. The whole lookup is best-effort: any failure just yields no (or stale) badges - a hung scan degrades to cached badges, never a frozen overlay.
- Shortcut-key presses resolve by the displayed `ShortcutKey` label (`SwitchToWindowByShortcutKey`), not by list index, so keys stay correct after filtering.
- **Packaging gap:** `Installer/Build.xml` and the portable zip do not yet include `switcheroo_plus_badges.exe` - badges silently do nothing in those packages until added.

### Error Handling
- Minimal try/catch; many P/Invoke calls assume success.
- `AppWindow` process-name resolution falls back to `Process.GetProcessById` on failure.

### Settings
- Standard `Properties.Settings` with `PortableSettingsProvider` injected for portable builds.
- Settings persisted to XML next to executable when `PORTABLE` compile constant is defined.

---

## Important Files

| File | Role |
|------|------|
| `Switcheroo/Program.cs` | Entry point (`[STAThread]`), single-instance mutex, admin elevation, portable provider injection |
| `Switcheroo/MainWindow.xaml.cs` | Overlay logic, hotkey handling, filtering, commands, list navigation |
| `Switcheroo/App.xaml` / `App.xaml.cs` | Minimal WPF app declaration |
| `Switcheroo/HotKey.cs` | Global hotkey registration via `RegisterHotKey`/`UnregisterHotKey` |
| `Switcheroo/HotkeySelection.cs` | `HotkeySelection` model + `HotkeySelectionValidator` (in-app conflict rules, run at key-selection time and on OK) |
| `Switcheroo/GlobalHotkeyProbe.cs` | System-level conflict probe: tries `RegisterHotKey` for a candidate shortcut to detect claims by other programs or Windows (e.g. Win+E). Blind to `WH_KEYBOARD_LL`-based hotkeys |
| `Switcheroo/AltTabHook.cs` | Low-level keyboard hook (`WH_KEYBOARD_LL`) for Alt+Tab interception |
| `Core/AppWindow.cs` | Win32 window wrapper: title, process name, icon, UWP support, `SwitchToThisWindow` |
| `Core/WindowFinder.cs` | Enumerates toplevel windows, filters by `IsAltTabWindow` |
| `Core/WindowFilterer.cs` | Runs matchers and produces ranked `FilterResult<AppWindow>` list |
| `Core/WinApi.cs` | P/Invoke declarations (`user32.dll`, `kernel32.dll`) and constants |
| `Switcheroo/TaskbarBadgeOverlay.cs` | Pipe client for the badge helper (spawn/reuse, fire-and-forget show/hide) |
| `BadgeOverlay/Program.cs` | Badge helper process (`switcheroo_plus_badges.exe`): pipe server, UIA taskbar lookup, chip windows, timeout, X close chip |
| `Core/Matchers/*.cs` | Four scoring matchers and `XamlHighlighter` |
| `Switcheroo/PortableSettingsProvider.cs` | Custom `SettingsProvider` for portable XML persistence |
| `Installer/Installer.iss` | Inno Setup installer script |
| `Installer/Build.xml` | MSBuild targets for setup + portable packaging |

---

## Runtime / Tooling Preferences

| Aspect | Current |
|--------|---------|
| Runtime | .NET Framework 4.5 (all projects) |
| Language | C# 5 (no `async/await`, no `?.`) |
| UI Framework | WPF (XAML + code-behind) |
| Build | MSBuild 12.0 / Visual Studio 2013+ solution (`Switcheroo.sln`) |
| Installer | Inno Setup 5 (bundled in `Installer/InnoSetup/`) |
| Packaging | MSBuild Community Tasks (`Build.xml`) |
| Package Manager | NuGet (`packages.config` style) |

**Legacy artifacts to be aware of**
- `Switcheroo.csproj` has `OldToolsVersion="3.5"` and `ProductVersion="9.0.30729"` (VS2008 residue).
- Bootstrapper packages for .NET 2.0/3.0/3.5 in `.csproj` (irrelevant for 4.5+ target).
- `.gitignore` has `packages/*` commented out with a TODO.

**Local tooling (untracked by design)**
- `.kimi-code/skills/build/` — low-priority build wrapper (`SKILL.md` + repo-root `build.ps1`).
- `.kimi-code/skills/call-codex/` — headless `codex exec` wrapper for second-opinion reviews: `bash .kimi-code/skills/call-codex/scripts/call-codex.sh --cwd <abs dir> --sandbox read-only "prompt"`. It was copied from `ai_command`'s `ai_tool/commands/call-codex` and is deliberately **not committed**; it is not part of the build.

---

## Testing & QA

- **Framework:** NUnit 2.6.3
- **Test files:**
  - `Core.UnitTests/ContainsMatcherTest.cs` — substring matching, case insensitivity, score
  - `Core.UnitTests/StartsWithMatcherTests.cs` — prefix matching, segmentation
  - `Core.UnitTests/IndividualCharactersMatcherTests.cs` — fuzzy character matching
  - `Core.UnitTests/XamlHighlighterTests.cs` — XAML bold-wrapping and escaping
- **Coverage gaps:** No tests for `AppWindow`, `WindowFinder`, `WindowFilterer`, `WinApi`, `KeyboardHelper`, `WindowIconFinder`, `SignificantCharactersMatcher`.
- **Patterns:** No mocking framework; each test directly instantiates the SUT. No `[SetUp]`/`[TearDown]`; flat namespace `Switcheroo.Core.UnitTests`.
- **Running:** Build `Core.UnitTests.csproj` then run with NUnit console runner or a VS test adapter that supports NUnit 2.x.
