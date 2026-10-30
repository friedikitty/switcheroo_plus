---
name: build
description: Compile switcheroo_plus at very low CPU priority via build.ps1 so builds never starve the desktop
type: prompt
whenToUse: Whenever you need to compile or build this solution (Debug or Release) — always use build.ps1 instead of invoking MSBuild or build.bat directly
---

Build with the repo-root script:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1                    # Debug (default)
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Configuration Release
```

- **Never invoke MSBuild directly.** `build.ps1` runs MSBuild at Idle CPU
  priority with `UseSharedCompilation=false` (otherwise Roslyn's warm
  VBCSCompiler daemon compiles at normal priority) and no node reuse.
- Exit code is MSBuild's; output streams to the console.
- The script auto-locates VS2022 MSBuild (Community/Professional/Enterprise)
  or falls back to `msbuild.exe` on PATH.
- Kill any running `switcheroo_plus.exe` first — a running app locks the
  output DLLs and the build fails.
