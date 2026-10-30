#requires -Version 5.1
# Low-priority build wrapper: compiles the solution without starving the desktop.
# - MSBuild runs at Idle CPU priority (child csc.exe processes inherit it)
# - UseSharedCompilation=false: otherwise Roslyn hands work to a warm
#   VBCSCompiler daemon running at NORMAL priority, defeating the point
# - no /m (single node), /nr:false (no lingering build nodes)
param(
    [string]$Configuration = "Debug",
    [string]$Solution = "Switcheroo.sln"
)
$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

$msbuild = $null
foreach ($edition in "Community", "Professional", "Enterprise") {
    $candidate = "${env:ProgramFiles}\Microsoft Visual Studio\2022\$edition\MSBuild\Current\Bin\MSBuild.exe"
    if (Test-Path $candidate) { $msbuild = $candidate; break }
}
if (-not $msbuild) {
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { $msbuild = $cmd.Source }
}
if (-not $msbuild) { throw "MSBuild not found (looked for VS2022 editions and PATH)" }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $msbuild
$psi.Arguments = "`"$Solution`" /p:Configuration=$Configuration /p:UseSharedCompilation=false /v:m /nologo /nr:false"
$psi.WorkingDirectory = $repoRoot
$psi.UseShellExecute = $false   # inherit console: output streams through

$p = [System.Diagnostics.Process]::Start($psi)
try { $p.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::Idle } catch {}
$p.WaitForExit()
exit $p.ExitCode
