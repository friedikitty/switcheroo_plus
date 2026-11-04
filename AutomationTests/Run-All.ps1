#requires -Version 5.1
param([ValidateSet('Debug','Release')][string]$Configuration='Debug',[switch]$SkipBuild,[switch]$SkipDesktop)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$artifacts=Join-Path $repo ('.temp\acceptance\'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $artifacts -Force|Out-Null
$results=New-Object Collections.Generic.List[object]
function Phase($name,[scriptblock]$action) {
 try { & $action; $results.Add([pscustomobject]@{name=$name;status='PASS'}) }
 catch { $results.Add([pscustomobject]@{name=$name;status='FAIL';reason=$_.ToString()}); throw }
 finally { $results|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding UTF8 }
}
Push-Location $repo
try {
 if(-not $SkipBuild) {
  Phase 'build' { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'build.ps1') -Configuration $Configuration; if($LASTEXITCODE -ne 0) { throw 'Build failed' } }
 }
 Phase 'unit-tests' {
  & (Join-Path $repo 'packages\NUnit.ConsoleRunner.3.17.0\tools\nunit3-console.exe') (Join-Path $repo "Core.UnitTests\bin\$Configuration\Switcheroo.Core.UnitTests.dll") "--result=$artifacts\unit-tests.xml"
  if($LASTEXITCODE -ne 0) { throw 'NUnit failed' }
 }
 Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access); [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);' -Name Desktop -Namespace SwitcherooAcceptance -ErrorAction SilentlyContinue
 $desktop=[SwitcherooAcceptance.Desktop]::OpenInputDesktop(0,$false,0x100)
 if($desktop -eq [IntPtr]::Zero) { $SkipDesktop=$true } else { [void][SwitcherooAcceptance.Desktop]::CloseDesktop($desktop) }
 if($SkipDesktop) { $results.Add([pscustomobject]@{name='interactive-desktop';status='SKIPPED';reason='No accessible interactive desktop or explicitly skipped'}) }
 $desktopArgs=@(); if($SkipDesktop) { $desktopArgs+= '-SkipDesktop' }
 Phase 'watchdog-crash-hang-ui' {
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-Watchdog.ps1') -Configuration $Configuration -Artifacts $artifacts @desktopArgs
  if($LASTEXITCODE -ne 0) { throw 'Watchdog acceptance tests failed' }
 }
 Phase 'startup-failures' {
  & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-Startup.ps1') -Configuration $Configuration -Artifacts $artifacts
  if($LASTEXITCODE -ne 0) { throw 'Startup failure tests failed' }
 }
 $results.Add([pscustomobject]@{name='UAC-consent';status='SKIPPED';reason='Requires an interactive user consent; the runner never bypasses UAC'})
 $results|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $artifacts 'results.json') -Encoding UTF8
 Write-Host "PASS acceptance suite; results and diagnostic artifacts: $artifacts"
} finally { Pop-Location }
