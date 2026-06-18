#requires -Version 5.1
<#
.SYNOPSIS
    End-to-end automated test for Switcheroo+ using the named-pipe automation API.
.DESCRIPTION
    1. Kills any running switcheroo_plus.exe
    2. Writes a known config to ~/.config/.switcheroo_plus/config.jsonc
    3. Starts switcheroo_plus.exe
    4. Sends automation commands over the named pipe
    5. Verifies responses
    6. Shuts down the app
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "Switcheroo\bin\Debug\switcheroo_plus.exe"
$pipeName = "switcheroo_plus_automation"
$timeoutMs = 5000

function Send-Command($json) {
    $client = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect($timeoutMs)
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $writer = New-Object System.IO.StreamWriter($client, $utf8, 4096, $true)
        $reader = New-Object System.IO.StreamReader($client, $utf8, $true, 4096, $true)
        $writer.WriteLine($json)
        $writer.Flush()
        return $reader.ReadLine()
    }
    finally {
        $client.Dispose()
    }
}

function Assert-Contains($haystack, $needle, $message) {
    if ($haystack -notlike "*$needle*") {
        throw $message
    }
}

# 1. Clean up any running instance
Get-Process -Name "switcheroo_plus" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

# 2. Prepare config
$configDir = Join-Path $env:USERPROFILE ".config\.switcheroo_plus"
if (-not (Test-Path $configDir)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
}
$configPath = Join-Path $configDir "config.jsonc"
$config = @"
{
  // Main activation hotkey
  "ActivateAlt": true,
  "ActivateCtrl": false,
  "ActivateShift": false,
  "ActivateWindowsKey": false,
  "ActivateHotKey": 32,

  // Previous-app toggle hotkey (switch back / forth without overlay)
  "PreviousAppEnabled": false,
  "PreviousAppAlt": true,
  "PreviousAppCtrl": false,
  "PreviousAppShift": false,
  "PreviousAppWindowsKey": false,
  "PreviousAppCapsLock": false,
  "PreviousAppHotKey": 9,

  // Behaviour toggles
  "AltTabHook": false,
  "AutoSwitch": false,
  "RunAsAdmin": false,
  "EnableHotKey": true,
  "RunOnStartup": false,
  "OpenUsageHintsByDefault": false,
  "WindowWidth": 0,
  "WindowHeight": 0,
  "MultiColumnEnabled": false,

  // Show 0-9 / a-z / A-Z shortcuts in the overlay
  "AzShortcutsEnabled": true
}
"@
Set-Content -Path $configPath -Value $config -Encoding UTF8

# 3. Start app
$proc = Start-Process -FilePath $exe -ArgumentList "--auto" -PassThru
Write-Host "Started switcheroo_plus (PID $($proc.Id))"
Start-Sleep -Seconds 3

# Wait for automation pipe to become available
$pipeReady = $false
$pipeDeadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $pipeDeadline -and -not $pipeReady) {
    try {
        $testClient = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
        $testClient.Connect(500)
        $testClient.Dispose()
        $pipeReady = $true
    }
    catch {
        Start-Sleep -Milliseconds 500
    }
}

if (-not $pipeReady) {
    throw "Automation pipe did not become available within 10 seconds."
}

$allPassed = $true
$tests = @()

try {
    # 4. Run automation commands
    $resp = Send-Command '{"action":"ping"}'
    $tests += [PSCustomObject]@{ Name = "ping"; Response = $resp; Passed = $resp -like '*"ok":true*' }

    $resp = Send-Command '{"action":"show"}'
    $tests += [PSCustomObject]@{ Name = "show"; Response = $resp; Passed = $resp -like '*"ok":true*' }
    Start-Sleep -Milliseconds 300

    $resp = Send-Command '{"action":"setquery","query":"switcheroo"}'
    $tests += [PSCustomObject]@{ Name = "setquery"; Response = $resp; Passed = $resp -like '*"ok":true*' }
    Start-Sleep -Milliseconds 500

    $resp = Send-Command '{"action":"getwindows"}'
    $tests += [PSCustomObject]@{ Name = "getwindows"; Response = $resp; Passed = $resp -like '*"windows":*' }

    $resp = Send-Command '{"action":"hide"}'
    $tests += [PSCustomObject]@{ Name = "hide"; Response = $resp; Passed = $resp -like '*"ok":true*' }

    $resp = Send-Command '{"action":"getconfig"}'
    $tests += [PSCustomObject]@{ Name = "getconfig"; Response = $resp; Passed = $resp -like '*"config":*' }

    $resp = Send-Command '{"action":"previousapp"}'
    $tests += [PSCustomObject]@{ Name = "previousapp"; Response = $resp; Passed = $resp -like '*"ok":true*' }
}
catch {
    Write-Host "Automation error: $_" -ForegroundColor Red
    $allPassed = $false
}
finally {
    # 5. Shut down cleanly
    try {
        Send-Command '{"action":"exit"}' | Out-Null
    }
    catch {
        # If pipe is gone, process may already be exiting
    }
    Start-Sleep -Seconds 1
    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
}

# 6. Report
foreach ($t in $tests) {
    $color = if ($t.Passed) { "Green" } else { "Red" }
    Write-Host ("[{0}] {1}: {2}" -f $(if ($t.Passed) { "PASS" } else { "FAIL" }), $t.Name, $t.Response) -ForegroundColor $color
    if (-not $t.Passed) { $allPassed = $false }
}

if ($allPassed) {
    Write-Host "`nAll automation tests passed." -ForegroundColor Green
    exit 0
}
else {
    Write-Host "`nSome automation tests failed." -ForegroundColor Red
    exit 1
}
