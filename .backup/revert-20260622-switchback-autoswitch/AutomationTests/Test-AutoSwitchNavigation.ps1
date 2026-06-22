#requires -Version 5.1
<#
.SYNOPSIS
    End-to-end test for Alt-held navigation while auto-switch is enabled.

.DESCRIPTION
    Verifies that, once the auto-switch Alt+Tab overlay is shown:

    1. Alt+Down moves the selection through the list.
    2. Alt+1 directly switches to the item at index 1.
    3. Alt+A directly switches to the item at index 10.

    The test uses the automation pipe only to trigger the overlay and inspect
    state. The actual navigation keys are synthesized at the OS level while Alt
    is held, which exercises the same WPF input path as a real user.
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "Switcheroo\bin\Debug\switcheroo_plus.exe"
$pipeName = "switcheroo_plus_automation"
$timeoutMs = 5000
$tempDir = Join-Path $repoRoot ".temp\automation-autoswitch"

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
        return $reader.ReadLine()
    }
    finally { $client.Dispose() }
}

function Send-KeyDown([byte]$vk) {
    [SwitcherooTest.KeyboardApi]::keybd_event($vk, 0, 0, [System.UIntPtr]::Zero)
}

function Send-KeyUp([byte]$vk) {
    [SwitcherooTest.KeyboardApi]::keybd_event($vk, 0, 2, [System.UIntPtr]::Zero)
}

function Send-KeyTap([byte]$vk, [int]$delayMs = 60) {
    Send-KeyDown $vk
    Start-Sleep -Milliseconds $delayMs
    Send-KeyUp $vk
}

function Wait-ForPipe {
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        try {
            $testClient = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
            $testClient.Connect(500)
            $testClient.Dispose()
            return
        }
        catch {
            Start-Sleep -Milliseconds 400
        }
    }

    throw "Automation pipe did not become available within 10 seconds."
}

function Wait-ForSelectedIndex([int]$expectedIndex) {
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline) {
        $state = Send-Command '{"action":"getselectedindex"}' | ConvertFrom-Json
        if ($state.visible -and $state.selectedIndex -eq $expectedIndex) {
            return $state
        }
        Start-Sleep -Milliseconds 200
    }

    return Send-Command '{"action":"getselectedindex"}' | ConvertFrom-Json
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

function Show-AutoSwitchOverlay {
    $response = Send-Command '{"action":"triggeralttab","shift":false,"ctrl":false}' | ConvertFrom-Json
    if (-not $response.ok) {
        throw "triggeralttab failed."
    }

    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline) {
        $state = Send-Command '{"action":"getselectedindex"}' | ConvertFrom-Json
        if ($state.visible) {
            return
        }
        Start-Sleep -Milliseconds 200
    }

    throw "Overlay did not become visible."
}

function Release-AltIfNeeded {
    try { Send-KeyUp 0x12 } catch {}
}

if (-not (Test-Path $tempDir)) {
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
}

$windowFiles = @()
for ($i = 0; $i -le 10; $i++) {
    $path = Join-Path $tempDir ("switcheroo-autoswitch-{0}.txt" -f $i)
    Set-Content -Path $path -Value ("window-" + $i) -Encoding ASCII
    $windowFiles += $path
}

$notepads = @()
$proc = $null
$results = @()

Get-Process -Name "switcheroo_plus" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$configDir = Join-Path $env:USERPROFILE ".config\.switcheroo_plus"
if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }
$configPath = Join-Path $configDir "config.jsonc"
$config = @"
{
  "ActivateAlt": false, "ActivateCtrl": false, "ActivateShift": false,
  "ActivateWindowsKey": false, "ActivateHotKey": 32,
  "PreviousAppEnabled": false,
  "PreviousAppAlt": true, "PreviousAppCtrl": false, "PreviousAppShift": false,
  "PreviousAppWindowsKey": false, "PreviousAppCapsLock": false, "PreviousAppHotKey": 9,
  "AltTabHook": true, "AutoSwitch": true, "RunAsAdmin": false,
  "EnableHotKey": false, "RunOnStartup": false, "OpenUsageHintsByDefault": false,
  "WindowWidth": 0, "WindowHeight": 0, "MultiColumnEnabled": false, "AzShortcutsEnabled": true
}
"@
Set-Content -Path $configPath -Value $config -Encoding UTF8

try {
    $shell = New-Object -ComObject WScript.Shell
    foreach ($file in $windowFiles) {
        $notepads += Start-Process -FilePath "notepad.exe" -ArgumentList $file -PassThru
    }
    Start-Sleep -Seconds 2

    $lastTitle = [System.IO.Path]::GetFileName($windowFiles[$windowFiles.Count - 1])
    [void]$shell.AppActivate($lastTitle)
    Start-Sleep -Milliseconds 500

    $proc = Start-Process -FilePath $exe -ArgumentList "--auto" -PassThru
    Start-Sleep -Seconds 3
    Wait-ForPipe

    Show-AutoSwitchOverlay
    $initialState = Send-Command '{"action":"getselectedindex"}' | ConvertFrom-Json
    $results += [PSCustomObject]@{
        Name = "overlay_visible_for_autoswitch"
        Passed = [bool]$initialState.visible
        Detail = ($initialState | ConvertTo-Json -Compress)
    }

    Send-KeyDown 0x12
    Start-Sleep -Milliseconds 80

    Send-KeyTap 0x28
    $afterDown = Wait-ForSelectedIndex 1
    $results += [PSCustomObject]@{
        Name = "alt_down_moves_selection"
        Passed = ($afterDown.selectedIndex -eq 1)
        Detail = ($afterDown | ConvertTo-Json -Compress)
    }

    $windows = (Send-Command '{"action":"getwindows"}' | ConvertFrom-Json).windows
    if ($windows.Count -lt 11) {
        throw "Expected at least 11 windows in the overlay; found $($windows.Count)."
    }

    $digitTarget = $windows[1]
    Send-KeyTap 0x31
    Start-Sleep -Milliseconds 400
    $afterDigit = Wait-ForForegroundHwnd $digitTarget.hwnd
    $results += [PSCustomObject]@{
        Name = "alt_1_switches_to_index_1"
        Passed = ($afterDigit.hwnd -eq $digitTarget.hwnd)
        Detail = (($afterDigit | ConvertTo-Json -Compress) + "; target=" + ($digitTarget | ConvertTo-Json -Compress))
    }

    Release-AltIfNeeded
    Start-Sleep -Milliseconds 200

    [void]$shell.AppActivate($lastTitle)
    Start-Sleep -Milliseconds 500
    Show-AutoSwitchOverlay
    Send-KeyDown 0x12
    Start-Sleep -Milliseconds 80

    $windows = (Send-Command '{"action":"getwindows"}' | ConvertFrom-Json).windows
    $letterTarget = $windows[10]
    Send-KeyTap 0x41
    Start-Sleep -Milliseconds 400
    $afterLetter = Wait-ForForegroundHwnd $letterTarget.hwnd
    $results += [PSCustomObject]@{
        Name = "alt_a_switches_to_index_10"
        Passed = ($afterLetter.hwnd -eq $letterTarget.hwnd)
        Detail = (($afterLetter | ConvertTo-Json -Compress) + "; target=" + ($letterTarget | ConvertTo-Json -Compress))
    }
}
catch {
    $results += [PSCustomObject]@{ Name = "exception"; Passed = $false; Detail = $_.ToString() }
}
finally {
    Release-AltIfNeeded
    try { Send-Command '{"action":"exit"}' | Out-Null } catch {}
    Start-Sleep -Seconds 1
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    foreach ($np in $notepads) {
        if ($np -and -not $np.HasExited) { Stop-Process -Id $np.Id -Force -ErrorAction SilentlyContinue }
    }
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
    Write-Host "AUTO-SWITCH NAVIGATION PASSED." -ForegroundColor Green
    exit 0
}

Write-Host "AUTO-SWITCH NAVIGATION FAILED." -ForegroundColor Red
exit 1
