#requires -Version 5.1
param([string]$Configuration='Debug',[string]$Artifacts)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if(-not $Artifacts) { $Artifacts=Join-Path $repo '.temp\startup-tests' }
function Request($name,$action) {
 $pipe=New-Object IO.Pipes.NamedPipeClientStream('.', $name, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
 try {
  $pipe.Connect(500)
  $writer=New-Object IO.StreamWriter($pipe,(New-Object Text.UTF8Encoding($false)),4096,$true)
  $reader=New-Object IO.StreamReader($pipe,[Text.Encoding]::UTF8,$false,4096,$true)
  $writer.AutoFlush=$true; $writer.WriteLine((@{action=$action}|ConvertTo-Json -Compress))
  $read=$reader.ReadLineAsync(); if(-not $read.Wait(1000)) { throw 'response timeout' }
  return ($read.Result|ConvertFrom-Json)
 } finally { $pipe.Dispose() }
}
foreach($scenario in @('startup-crash','startup-timeout','missing-core','unwritable-diagnostics')) {
 $id=[guid]::NewGuid().ToString('N'); $root=Join-Path ([IO.Path]::GetFullPath($Artifacts)) $id
 New-Item -ItemType Directory -Path $root -Force|Out-Null
 Set-Content -LiteralPath (Join-Path $root 'config.jsonc') -Encoding UTF8 -Value '{"EnableHotKey":false,"AltTabHook":false,"PreviousAppEnabled":false,"TaskbarBadgesEnabled":false,"SwitchHighlightEnabled":false}'
 $pipeName="switcheroo_plus_watchdog_$id"; $corePipe="switcheroo_plus_automation_$id"
 $oldCrash=$env:SWITCHEROO_TEST_STARTUP_CRASH; $oldHang=$env:SWITCHEROO_TEST_STARTUP_HANG
 $watchdog=$null
 try {
  $env:SWITCHEROO_TEST_STARTUP_CRASH=if($scenario -eq 'startup-crash'){'1'}else{''}
  $env:SWITCHEROO_TEST_STARTUP_HANG=if($scenario -eq 'startup-timeout'){'1'}else{''}
  if($scenario -eq 'unwritable-diagnostics') { Set-Content -LiteralPath (Join-Path $root 'crashes') -Value 'intentional test obstruction' }
  $arguments=@('--auto','--test-mode','--test-root',('"'+$root+'"'),'--test-id',$id)
  if($scenario -eq 'missing-core') { $arguments+=@('--test-core',('"'+(Join-Path $root 'missing.exe')+'"')) }
  $watchdog=Start-Process -FilePath (Join-Path $repo "Switcheroo\bin\$Configuration\switcheroo_plus_watchdog.exe") -ArgumentList $arguments -PassThru -WindowStyle Hidden
  $deadline=[DateTime]::UtcNow.AddSeconds(50); $passed=$false; $faultSent=$false
  do {
   try {
    $s=Request $pipeName 'status'
    if($scenario -eq 'unwritable-diagnostics' -and $s.state -eq 'Running' -and -not $faultSent) { $faultSent=$true; Request $corePipe 'fault'|Out-Null }
    if($scenario -eq 'startup-crash' -and $s.state -eq 'Crashed') {
      $report=Get-Content -LiteralPath (Join-Path $s.incident 'report.json') -Raw|ConvertFrom-Json
      if($report.dumpStatus -ne 'complete') { throw 'Startup dump incomplete' }; $passed=$true
    }
    if($scenario -eq 'startup-timeout' -and $s.state -eq 'Stopped' -and $s.error -match 'startup timed out') { $passed=$true }
    if($scenario -eq 'missing-core' -and $s.state -eq 'Stopped' -and $s.error -match 'not found') { $passed=$true }
    if($scenario -eq 'unwritable-diagnostics' -and $s.state -eq 'Crashed' -and $s.error) { $passed=$true }
   } catch { }
   if($watchdog.HasExited) { throw "$scenario watchdog exited early" }
   if($passed) { break }
   Start-Sleep -Milliseconds 200
  } while([DateTime]::UtcNow -lt $deadline)
  if(-not $passed) { throw "$scenario failed: $($s|ConvertTo-Json -Depth 5 -Compress)" }
  Write-Host "PASS $scenario remains controllable and reports its outcome"
 } finally {
  $env:SWITCHEROO_TEST_STARTUP_CRASH=$oldCrash; $env:SWITCHEROO_TEST_STARTUP_HANG=$oldHang
  if($watchdog -and -not $watchdog.HasExited) {
   try { Request $pipeName 'exit'|Out-Null } catch { }
   if(-not $watchdog.WaitForExit(12000)) { $watchdog.Kill(); $watchdog.WaitForExit(3000)|Out-Null }
  }
  if($watchdog) { $watchdog.Dispose() }
 }
}
