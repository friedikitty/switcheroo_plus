Change log
----------
### 2026-11-06: current master (after v0.11.4) ###
- Switch feedback: soft 48 px radial-gradient center dot with per-pixel alpha and a fade-out over `SwitchHighlightDotFadeMs`; frame edges re-assert topmost.
- Recent windows pinned in recency order, plus a `Recent` / `A–Z` toggle.
- Force-English quick-jump keys: the IME cannot swallow `0-9` / `a-z` / `A-Z` while the result list has focus.
- Test-only `flashhwnd`, `showpassive` and `optionspassive` hooks; `checkui` captures only the staged target and never steals focus.
- README screenshots staged with real apps and harmless demo content.

### 2026-11-05: after v0.11.4 ###
- Switch feedback: blue frame pulse and center dot; mixed-DPI frame conversion.
- Watchdog: contextless dump fallback and log-rotation fix.
- Recent-on-top pinned ordering, sort toggle and force-English quick-jump.
- Per-monitor layered-pixel probe for the feedback dot.

### 2026-11-04: after v0.11.4 ###
- Switch feedback border pulse on every switch path.
- Hidden watchdog shell: tray control, start/stop/restart, crash and hang dumps, incident retention, log rotation.
- Isolated acceptance suite and new automation-pipe actions.

### 2026-11-04: v0.11.4 ###
- CI installs the NUnit console runner before running tests.

### 2026-11-04: v0.11.3 ###
- CI runs the NUnit step under cmd.

### 2026-11-04: v0.11.2 ###
- Docs point at the real exe name and installer output path.

### 2026-11-03: v0.11.1 ###
- Badge cache and helper review fixes.

### 2026-11-02: v0.11.0 ###
- Badge rendering isolated into `switcheroo_plus_badges.exe`.
- Badge auto-hide timeout (default 10 s, 0 = never).
- PID-based process lookup removes the 1-2 s popup delay.

### 2026-10-30: v0.10.0 ###
- Recent-on-top ordering, taskbar shortcut badges and hotkey conflict probe.
- Badges drawn as small opaque click-through chips (transparent overlays did not render).
- Alt+Tab hook swallows the key instantly; low-priority `build.ps1`; raise the target when no foreground window exists.

### 2026-05-18 .. 2026-07-27: pre-v0.10.0 fork groundwork ###
- .NET Framework 4.8, NUnit 3 and tooling modernization.
- JSONC config at `~/.config/.switcheroo_plus` with legacy migration.
- Configurable activation and previous-app hotkeys; `0-9` / `a-z` / `A-Z` jump keys; previous-app toggle.
- Overlay UX: list focus, `/` search, menu button, tray double-click, resizable popup, multi-column layout, case-sensitive jump keys up to 62.
- CapsLock+Tab hook, AutoStart fix, `switcheroo_plus.exe` rename and build scripts.
- Named-pipe automation API and `--auto` test mode.
- Hotkey validation/persistence fixes and reliable switch-back/MRU ordering.
- Upstream 2018 features: Alt+Tab auto-switch, RunAsAdmin, multi-select, UWP process names, Alt+X close and Alt+J/K navigation.

### 2015-08-18: v0.9.2 ###
- Chocolatey packages updated to 0.9.2.

### 2015-06-16: v0.9.1 ###
- Null pointer check before disposing the window closer (#46).

### 2015-04-28: v0.9.0 ###
- Much improved window closing: Switcheroo now stays open, so you can close several windows faster. Thanks to @HellBrick for proposing this idea and sending a pull request! :+1: (#25)
- The focused window is now at the bottom of the list. This makes it faster to switch to the next window as you just need to press Alt+Enter, Enter. No more need to press Arrow Down. This makes Alt+Enter and Alt+Tab work in the same way (#24)
- Add help information to Switcheroo. Features should be more easily discoverable. Just click the question mark in the overlay (#32)
- Add Ctrl+W as a shortcut to close a window. This shortcut fells more intuitive than Ctrl+Enter (#22)
- Allow using Tab and Shift+Tab to navigate the window list (#31)
- Small look and feel adjustments. More prettiness! (#30)
- Deactivate the System Menu for Switcheroo. Because it often gets accidentally activated when using the default Alt+Tab shortcut (#29)
- Key presses in Switcheroo can be sent to other windows. Key presses are now contained within Switcheroo (#34)
- Fix Switcheroo window turning black on activate/dismiss (#30)
- More work around missing Alt+Tab windows. No windows should be forgotten (#36)
- Fix missing scrollbar if list is taller than screen (#37)
- More compatible way of closing windows (#42)

### 2015-01-15: v0.8.3 ###
- Crashes on launch in Windows 10 or when .NET 4.6 Preview is installed (#20)

### 2014-10-15: v0.8.2 ###
- Use icons from the taskbar (#19)

### 2014-10-15: v0.8.1 ###
- Fix crash when opening the Options window while the hotkey is already in use (#18)

### 2014-09-03: v0.8.0 ###
- Activate Switcheroo instead of the native task switcher with Alt+Tab [You need to enable this feature under Options] (#16)
- Option whether to start Switcheroo automatically on startup or not (#3)
- Ensure that the input field has a minimum width (#1)
- Remember key bindings and other settings when upgrading (#14)
- The Windows included are closer to those in the native Alt+Tab task switcher (#17)

### 2014-04-18: v0.7.3 ###
- Portable version of Switcheroo (#10)
- Icons are now shown for admin processes as well (#11)
- Decrease flickering when closing a window with CTRL+Enter (#12)

### 2014-03-04: v0.7.2 ###
- New Switcheroo icon
- Allow circling in the window list (@ovesen)
- Align filtering and highlighting algorithms
- Fix crash when pressing key up or down while the window list is empty (@ovesen)
- Fix potential crash in update check

### 2014-01-30: v0.7.1 ###
- Fix crash if process icon could not be found

### 2014-01-24: v0.7 ###
- Faster load time and filtering
- Grabs focus right away
- Highlights matching characters
- Included windows should be closer to the default alt+tab
- Informs you when a new version of Switcheroo is available
- Requires .NET 4.5

### 2014-01-13: v0.6 ###
- Development continued by Regin Larsen
- Shows process icon and process title in addition to window title
- No window chrome
- Simple scoring algorithm when filtering
- Support for ReSharper like filtering, e.g. hc for HipChat
- New default key binding `Alt + Space` (Windows 8 is using `Win + W`)

### 2010-07-18: v0.5 ###
- Hotkey now hides Switcheroo window in addition to showing it (Issue 4)
- Double-clicking on item now activates that window (Issue 4)
- Added mutex to ensure only one instance is running
- Attempted bugfix of Windows 7 64-bit window-switching bug (Issue 3).

### 2010-05-03: v0.4.1 ###
- Long windows titles are now truncated.

### 2010-02-07: v0.4 ###
- Window now resizes to match height and width of all entries
- Window exception list is now user-editable.  
- Tested on 32-bit Windows 7.

### 2009-11-09: v0.3 ###
- Added ctrl-enter functionality.
- Mostly migrated to using the Managed Windows API instead of custom window class.

### 2009-11-01: v0.2 ###

### 2009-10-11: v0.1 ###
