# Acceptance tests

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File AutomationTests\Run-All.ps1
```

The runner builds Debug, runs NUnit, and launches isolated watchdog/core processes.
Use `-Configuration Release`, `-SkipBuild`, or `-SkipDesktop` when appropriate.
Results go to `.temp/acceptance/<timestamp>/results.json`; incident reports,
logs, screenshots, debugger output and dumps are in GUID subdirectories.

The tests exercise real managed, background-thread, native and FailFast crashes;
validate dump headers, streams, process ID and exception/thread identity; and open
each dump with CDB when Windows Debugging Tools is installed. CDB uses local symbol
paths with a 30-second timeout. Missing CDB is explicitly reported as skipped.
FailFast is checked for memory/thread/module streams but may lack exception context.

Other scenarios cover hidden startup, the tray control command, closing-to-hide,
Start/Stop/Restart, handled exceptions, startup crash and readiness timeout, missing
core, dump write failure and timeout, inaccessible diagnostics, recoverable and
permanent hangs, incident retention, log rotation, and core cleanup when the
watchdog dies. Desktop checks exercise options grouping, dependencies, OK/Cancel,
scaled content, isolated startup shortcuts and windowed/fullscreen dot pixels on
every attached monitor, fading, focus, click-through and cleanup.

Each test instance uses its own GUID pipe/mutex namespace, config, startup shortcut
and diagnostics directory. Fault injection requires `--test-mode --test-root <path>
--test-id <guid>` on the watchdog. A five-minute test watchdog deadline prevents
orphaned tests. Each runner also uses bounded IPC waits and cleans up only its own
watchdog PID, whose process job owns the core and badge helper. Live settings and
existing running instances are not changed.

Interactive-desktop tests are skipped explicitly in CI or when no input desktop
is available. Desktop pixel checks additionally need displays that are awake and
a capture-visible desktop: GDI capture returns black on monitors whose scanout is
idle (powered off, asleep, unswitched KVM), and streaming/remote-play tools that
virtualize the desktop make captures show wallpaper only. Frame placement is also
asserted geometrically against the target's window rect, which needs no pixels.
UAC consent remains an explicit manual check; tests never bypass it.
Fullscreen tests use borderless WPF windows; exclusive fullscreen games, secure
desktops and protected graphics surfaces still need hardware-specific checks.
Dumps stay local and are excluded from CI artifact uploads; they can be large and
contain private process memory.

`Run-AutomationTests.ps1` delegates to the same runner. The older hotkey scripts
also use isolated settings and watchdog ownership, and can be run separately for
keyboard regression checks.
