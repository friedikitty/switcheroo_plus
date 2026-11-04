#requires -Version 5.1
<#
.SYNOPSIS
    End-to-end hotkey verification using Notepad plus Paint.

.DESCRIPTION
    Verifies both user-facing hotkey paths under automation:

    1. The default previous-app shortcut Alt+Tab toggles between two different
       windows without showing the overlay.
    2. The regular activation shortcut brings switcheroo_plus.exe itself to the
       foreground, proving no other application handled the shortcut first.
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "Initialize-IsolatedTest.ps1")
$exe = Join-Path $repoRoot "Switcheroo\bin\Debug\switcheroo_plus_watchdog.exe"
$pipeName = "switcheroo_plus_automation_$testId"
$timeoutMs = 5000
$tempDir = Join-Path $repoRoot ".temp\automation-hotkeys"
$fileA = Join-Path $tempDir "switcheroo-hotkey-a.txt"

$kbSig = @"
[DllImport("user32.dll")]
public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
"@
Add-Type -MemberDefinition $kbSig -Name "KeyboardApi" -Namespace "SwitcherooTest" -ErrorAction SilentlyContinue

function Send-Command($json) {
    $client = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect($timeoutMs)
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $writer = New-Object System.IO.StreamWriter($client, $utf8, 4096, $true)
        $reader = New-Object System.IO.StreamReader($client, $utf8, $true, 4096, $true)
        $writer.WriteLine($json)
        $writer.Flush()
        $read = $reader.ReadLineAsync()
        if (-not $read.Wait(5000)) { throw "Pipe response timeout" }
        return $read.Result
    }
    finally { $client.Dispose() }
}

function Send-KeyDown([byte]$vk) {
    [SwitcherooTest.KeyboardApi]::keybd_event($vk, 0, 0, [System.UIntPtr]::Zero)
}

function Send-KeyUp([byte]$vk) {
    [SwitcherooTest.KeyboardApi]::keybd_event($vk, 0, 2, [System.UIntPtr]::Zero)
}

function Send-ActivationHotkey {
    Send-KeyDown 0x11 # Ctrl
    Start-Sleep -Milliseconds 40
    Send-KeyDown 0x10 # Shift
    Start-Sleep -Milliseconds 40
    Send-KeyDown 0x7A # F11
    Start-Sleep -Milliseconds 60
    Send-KeyUp 0x7A
    Start-Sleep -Milliseconds 30
    Send-KeyUp 0x10
    Send-KeyUp 0x11
}

function Send-AltTab {
    Send-KeyDown 0x12 # Alt
    Start-Sleep -Milliseconds 40
    Send-KeyDown 0x09 # Tab
    Start-Sleep -Milliseconds 60
    Send-KeyUp 0x09
    Start-Sleep -Milliseconds 30
    Send-KeyUp 0x12
}

function Wait-ForPipe {
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        try {
            $testClient = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
            $testClient.Connect(500)
            $testClient.Dispose()
            return
        } catch {
            Start-Sleep -Milliseconds 400
        }
    }

    throw "Automation pipe did not become available within 10 seconds."
}

function Wait-ForForegroundHwnd([string]$expectedHwnd) {
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline) {
        $fg = (Send-Command '{"action":"getforeground"}' | ConvertFrom-Json).foreground
        if ($fg.hwnd -eq $expectedHwnd) {
            return $fg
        }
        Start-Sleep -Milliseconds 200
    }

    return (Send-Command '{"action":"getforeground"}' | ConvertFrom-Json).foreground
}

function Wait-ForMainWindowHandle([int]$processId) {
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($proc -and $proc.MainWindowHandle -ne 0) {
            return "{0:X}" -f $proc.MainWindowHandle
        }
        Start-Sleep -Milliseconds 200
    }

    throw "Process $processId never produced a main window handle."
}

function Wait-ForWindowByTitleFragment([string]$titleFragment) {
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        Send-Command '{"action":"show"}' | Out-Null
        Start-Sleep -Milliseconds 400
        $wins = (Send-Command '{"action":"getwindows"}' | ConvertFrom-Json).windows
        $match = $wins | Where-Object { $_.title -like ("*" + $titleFragment + "*") } | Select-Object -First 1
        if ($match) {
            return $match
        }
        Start-Sleep -Milliseconds 300
    }

    throw "Could not find a window title containing '$titleFragment'."
}

function Wait-ForWindowByProcess([string]$processName) {
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        Send-Command '{"action":"show"}' | Out-Null
        Start-Sleep -Milliseconds 400
        $wins = (Send-Command '{"action":"getwindows"}' | ConvertFrom-Json).windows
        $match = $wins | Where-Object { $_.process -eq $processName } | Select-Object -First 1
        if ($match) {
            return $match
        }
        Start-Sleep -Milliseconds 300
    }

    throw "Could not find a window owned by process '$processName'."
}

function Wait-ForForegroundMatch([string]$expectedProcess, [string]$titleFragment) {
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline) {
        $fg = (Send-Command '{"action":"getforeground"}' | ConvertFrom-Json).foreground
        $processMatches = [string]::IsNullOrWhiteSpace($expectedProcess) -or $fg.process -eq $expectedProcess
        $titleMatches = [string]::IsNullOrWhiteSpace($titleFragment) -or $fg.title -like ("*" + $titleFragment + "*")
        if ($processMatches -and $titleMatches) {
            return $fg
        }
        Start-Sleep -Milliseconds 200
    }

    return (Send-Command '{"action":"getforeground"}' | ConvertFrom-Json).foreground
}

if (-not (Test-Path $tempDir)) {
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
}
Set-Content -Path $fileA -Value "A" -Encoding ASCII

$notepadA = $null
$paintWindow = $null
$proc = $null
$results = @()

Start-Sleep -Seconds 1

$configDir = $isolatedConfigRoot
if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }
$configPath = Join-Path $configDir "config.jsonc"
$config = @"
{
  "ActivateAlt": false, "ActivateCtrl": true, "ActivateShift": true,
  "ActivateWindowsKey": false, "ActivateHotKey": 122,
  "PreviousAppEnabled": true,
  "PreviousAppAlt": true, "PreviousAppCtrl": false, "PreviousAppShift": false,
  "PreviousAppWindowsKey": false, "PreviousAppCapsLock": false, "PreviousAppHotKey": 9,
  "AltTabHook": false, "AutoSwitch": false, "RunAsAdmin": false,
  "EnableHotKey": true, "RunOnStartup": false, "OpenUsageHintsByDefault": false,
  "WindowWidth": 0, "WindowHeight": 0, "MultiColumnEnabled": false, "AzShortcutsEnabled": true
}
"@
Set-Content -Path $configPath -Value $config -Encoding UTF8

try {
    $shell = New-Object -ComObject WScript.Shell
    $notepadA = Start-Process -FilePath "notepad.exe" -ArgumentList $fileA -PassThru
    $paintWindow = Start-Process -FilePath "mspaint.exe" -PassThru
    Start-Sleep -Seconds 2

    $proc = Start-Process -FilePath $exe -ArgumentList "--auto" -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 3
    Wait-ForPipe

    $mainHotkey = Send-Command '{"action":"installmainhotkey"}' | ConvertFrom-Json
    $results += [PSCustomObject]@{ Name = "installmainhotkey"; Passed = [bool]$mainHotkey.active; Detail = ($mainHotkey | ConvertTo-Json -Compress) }

    $prevHook = Send-Command '{"action":"installprevhook"}' | ConvertFrom-Json
    $results += [PSCustomObject]@{ Name = "installprevhook"; Passed = [bool]$prevHook.active; Detail = ($prevHook | ConvertTo-Json -Compress) }

    if (-not $mainHotkey.active) { throw "Activation hotkey was not installed." }
    if (-not $prevHook.active) { throw "Previous-app hook was not installed." }

    $winA = Wait-ForWindowByTitleFragment "switcheroo-hotkey-a"
    $winB = Wait-ForWindowByProcess "mspaint"

    Send-Command '{"action":"hide"}' | Out-Null
    [void]$shell.AppActivate($winB.title)
    $fgNotepad = Wait-ForForegroundMatch "mspaint" ""
    $results += [PSCustomObject]@{
        Name = "focus_other_app_before_activation"
        Passed = ($fgNotepad.process -eq "mspaint")
        Detail = ($fgNotepad | ConvertTo-Json -Compress)
    }

    Send-ActivationHotkey
    Start-Sleep -Milliseconds 900
    $activationFg = (Send-Command '{"action":"getforeground"}' | ConvertFrom-Json).foreground
    $visible = (Send-Command '{"action":"ping"}' | ConvertFrom-Json).visible
    $results += [PSCustomObject]@{
        Name = "activation_routes_to_switcheroo"
        Passed = ($activationFg.process -eq "switcheroo_plus" -and $visible)
        Detail = (($activationFg | ConvertTo-Json -Compress) + "; visible=" + $visible)
    }

    Send-Command '{"action":"hide"}' | Out-Null
    Start-Sleep -Milliseconds 300

    [void]$shell.AppActivate($winA.title)
    $fgA = Wait-ForForegroundMatch "notepad" "switcheroo-hotkey-a"
    [void]$shell.AppActivate($winB.title)
    $fgB = Wait-ForForegroundMatch "mspaint" ""
    Send-Command '{"action":"resetprevwindow"}' | Out-Null
    Start-Sleep -Milliseconds 200

    Send-AltTab
    $afterFirstAltTab = Wait-ForForegroundMatch "notepad" "switcheroo-hotkey-a"
    $results += [PSCustomObject]@{
        Name = "default_alt_tab_switches_to_previous_app"
        Passed = ($afterFirstAltTab.process -eq "notepad" -and $afterFirstAltTab.title -like "*switcheroo-hotkey-a*")
        Detail = ($afterFirstAltTab | ConvertTo-Json -Compress)
    }

    Send-AltTab
    $afterSecondAltTab = Wait-ForForegroundMatch "mspaint" ""
    $results += [PSCustomObject]@{
        Name = "default_alt_tab_toggles_back"
        Passed = ($afterSecondAltTab.process -eq "mspaint" -and $afterSecondAltTab.hwnd -eq $fgB.hwnd)
        Detail = ($afterSecondAltTab | ConvertTo-Json -Compress)
    }
}
catch {
    $results += [PSCustomObject]@{ Name = "exception"; Passed = $false; Detail = $_.ToString() }
}
finally {
    try { Send-Command '{"action":"exit"}' | Out-Null } catch {}
    Start-Sleep -Seconds 1
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    if ($notepadA -and -not $notepadA.HasExited) { Stop-Process -Id $notepadA.Id -Force -ErrorAction SilentlyContinue }
    if ($paintWindow -and -not $paintWindow.HasExited) { Stop-Process -Id $paintWindow.Id -Force -ErrorAction SilentlyContinue }
}

$allPass = $true
foreach ($r in $results) {
    $tag = if ($r.Passed) { "PASS" } else { "FAIL" }
    if (-not $r.Passed) { $allPass = $false }
    $color = if ($r.Passed) { "Green" } else { "Red" }
    $line = "[$tag] $($r.Name)"
    if ($r.Detail) { $line += "  ($($r.Detail))" }
    Write-Host $line -ForegroundColor $color
}

Write-Host ""
if ($allPass) {
    Write-Host "HOTKEY AUTOMATION PASSED." -ForegroundColor Green
    exit 0
}

Write-Host "HOTKEY AUTOMATION FAILED." -ForegroundColor Red
exit 1
