# Repository Guidelines

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
| `Switcheroo/AltTabHook.cs` | Low-level keyboard hook (`WH_KEYBOARD_LL`) for Alt+Tab interception |
| `Core/AppWindow.cs` | Win32 window wrapper: title, process name, icon, UWP support, `SwitchToThisWindow` |
| `Core/WindowFinder.cs` | Enumerates toplevel windows, filters by `IsAltTabWindow` |
| `Core/WindowFilterer.cs` | Runs matchers and produces ranked `FilterResult<AppWindow>` list |
| `Core/WinApi.cs` | P/Invoke declarations (`user32.dll`, `kernel32.dll`) and constants |
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
