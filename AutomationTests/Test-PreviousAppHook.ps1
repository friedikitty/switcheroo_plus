#requires -Version 5.1
<#
.SYNOPSIS
    End-to-end test of the "switch back to previous app" shortcut by injecting
    REAL keystrokes (keybd_event) through the low-level keyboard hook.

.DESCRIPTION
    This does NOT call the previous-app handler directly. It installs the actual
    PreviousAppHook (configured for Ctrl+Shift+E), then synthesizes Ctrl+Shift+E
    at the OS level and verifies the foreground window switches to the previous
    window and toggles back. This is the same path the user's real keypress takes.

    Flow:
      1. Start switcheroo_plus.exe --auto with PreviousAppEnabled + Ctrl+Shift+E.
      2. ping + installprevhook (installs the real WH_KEYBOARD_LL hook).
      3. Pick two distinct windows A and B from the overlay list.
      4. setforeground(A) then setforeground(B)  -> B is foreground, A is previous.
      5. resetprevwindow, then inject Ctrl+Shift+E.
      6. Assert foreground changed away from B (hook fired + Z-order switch worked).
      7. Inject Ctrl+Shift+E again -> assert foreground is back to B (toggle).
#>

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "Switcheroo\bin\Debug\switcheroo_plus.exe"
$pipeName = "switcheroo_plus_automation"
$timeoutMs = 5000

# --- keybd_event P/Invoke (synthesized input DOES pass through WH_KEYBOARD_LL) ---
$kbSig = @"
[DllImport("user32.dll")]
public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")]
public static extern System.IntPtr GetForegroundWindow();
"@
Add-Type -MemberDefinition $kbSig -Name "KB" -Namespace "SwitcherooTest" -ErrorAction SilentlyContinue

function Send-KeyCombo {
    # Ctrl(0x11) + Shift(0x10) + E(0x45)
    [SwitcherooTest.KB]::keybd_event(0x11, 0, 0, [System.UIntPtr]::Zero)   # Ctrl down
    Start-Sleep -Milliseconds 40
    [SwitcherooTest.KB]::keybd_event(0x10, 0, 0, [System.UIntPtr]::Zero)   # Shift down
    Start-Sleep -Milliseconds 40
    [SwitcherooTest.KB]::keybd_event(0x45, 0, 0, [System.UIntPtr]::Zero)   # E down
    Start-Sleep -Milliseconds 60
    [SwitcherooTest.KB]::keybd_event(0x45, 0, 2, [System.UIntPtr]::Zero)   # E up
    Start-Sleep -Milliseconds 30
    [SwitcherooTest.KB]::keybd_event(0x10, 0, 2, [System.UIntPtr]::Zero)   # Shift up
    [SwitcherooTest.KB]::keybd_event(0x11, 0, 2, [System.UIntPtr]::Zero)   # Ctrl up
}

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

function Get-ForegroundHwnd {
    return ("{0:X}" -f [SwitcherooTest.KB]::GetForegroundWindow().ToInt64())
}

# --- 1. Clean up any running instance ---
Get-Process -Name "switcheroo_plus" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

# --- 2. Write config: previous-app = Ctrl+Shift+E (VK_E = 0x45 = 69) ---
$configDir = Join-Path $env:USERPROFILE ".config\.switcheroo_plus"
if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }
$configPath = Join-Path $configDir "config.jsonc"
$config = @"
{
  "ActivateAlt": true, "ActivateCtrl": false, "ActivateShift": false,
  "ActivateWindowsKey": false, "ActivateHotKey": 32,
  "PreviousAppEnabled": true,
  "PreviousAppAlt": false, "PreviousAppCtrl": true, "PreviousAppShift": true,
  "PreviousAppWindowsKey": false, "PreviousAppCapsLock": false, "PreviousAppHotKey": 69,
  "AltTabHook": false, "AutoSwitch": false, "RunAsAdmin": false,
  "EnableHotKey": false, "RunOnStartup": false, "OpenUsageHintsByDefault": false,
  "WindowWidth": 0, "WindowHeight": 0, "MultiColumnEnabled": false, "AzShortcutsEnabled": true
}
"@
Set-Content -Path $configPath -Value $config -Encoding UTF8

# --- 3. Start app under --auto ---
$proc = Start-Process -FilePath $exe -ArgumentList "--auto" -PassThru
Write-Host "Started switcheroo_plus (PID $($proc.Id))"
Start-Sleep -Seconds 3

$pipeReady = $false
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline -and -not $pipeReady) {
    try {
        $t = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
        $t.Connect(500); $t.Dispose(); $pipeReady = $true
    } catch { Start-Sleep -Milliseconds 400 }
}
if (-not $pipeReady) { throw "Automation pipe not available within 10s." }

$results = @()
try {
    $r = Send-Command '{"action":"ping"}'
    Write-Host "ping -> $r"

    # install the REAL low-level hook with the configured combo
    $r = Send-Command '{"action":"installprevhook"}'
    $obj = $r | ConvertFrom-Json
    Write-Host "installprevhook -> $r"
    if (-not $obj.active) { throw "Previous-app hook did not install." }
    $results += [PSCustomObject]@{ Name="installprevhook"; Passed= [bool]$obj.active }

    # refresh the window list
    $r = Send-Command '{"action":"show"}'
    Start-Sleep -Milliseconds 400
    $r = Send-Command '{"action":"getwindows"}'
    $obj = $r | ConvertFrom-Json
    $wins = $obj.windows
    Write-Host "getwindows -> $($wins.Count) windows"

    # Need at least 2 distinct real windows to switch between
    $cands = @()
    foreach ($w in $wins) {
        if ($w.title -ne "" -and $w.hwnd -ne "0" -and $w.hwnd) { $cands += $w }
    }
    if ($cands.Count -lt 2) {
        throw "Need >=2 real windows for the switch test; found $($cands.Count). Open another window and rerun."
    }
    $A = $cands[0]
    $B = $cands[1]
    Write-Host ("A = hwnd={0} title='{1}' proc={2}" -f $A.hwnd, $A.title, $A.process)
    Write-Host ("B = hwnd={0} title='{1}' proc={2}" -f $B.hwnd, $B.title, $B.process)

    # establish: A foreground, then B foreground -> B on top, A is previous
    Send-Command ('{"action":"setforeground","hwnd":"' + $A.hwnd + '"}') | Out-Null
    Start-Sleep -Milliseconds 500
    Send-Command ('{"action":"setforeground","hwnd":"' + $B.hwnd + '"}') | Out-Null
    Start-Sleep -Milliseconds 700
    $fgBefore = Get-ForegroundHwnd
    Write-Host "foreground after setting B = $fgBefore (B=$($B.hwnd))"

    # force the Z-order path (no tracked previous window)
    Send-Command '{"action":"resetprevwindow"}' | Out-Null
    Start-Sleep -Milliseconds 200

    # --- inject Ctrl+Shift+E (the real user path) ---
    Send-KeyCombo
    Start-Sleep -Milliseconds 900
    $fgAfter1 = Get-ForegroundHwnd
    $switchedAway = ($fgAfter1 -ne $fgBefore)
    Write-Host "after 1st Ctrl+Shift+E: foreground=$fgAfter1 (was $fgBefore) switchedAway=$switchedAway"
    $results += [PSCustomObject]@{ Name="switch_away_from_B"; Passed=$switchedAway; Detail="fg $fgBefore -> $fgAfter1" }

    # --- inject Ctrl+Shift+E again -> should toggle back to B ---
    Send-KeyCombo
    Start-Sleep -Milliseconds 900
    $fgAfter2 = Get-ForegroundHwnd
    $toggledBack = ($fgAfter2 -eq $fgBefore)
    Write-Host "after 2nd Ctrl+Shift+E: foreground=$fgAfter2 (expected back to $fgBefore) toggledBack=$toggledBack"
    $results += [PSCustomObject]@{ Name="toggle_back_to_B"; Passed=$toggledBack; Detail="fg $fgAfter1 -> $fgAfter2 (expected $fgBefore)" }

    # diagnostics
    $st = (Send-Command '{"action":"prevstate"}') | ConvertFrom-Json
    Write-Host "prevstate -> $($st.state | ConvertTo-Json -Compress)"
}
catch {
    Write-Host "TEST ERROR: $_" -ForegroundColor Red
    $results += [PSCustomObject]@{ Name="exception"; Passed=$false; Detail=$_.ToString() }
}
finally {
    try { Send-Command '{"action":"exit"}' | Out-Null } catch {}
    Start-Sleep -Seconds 1
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}

# --- report ---
$allPass = $true
foreach ($r in $results) {
    $tag = if ($r.Passed) { "PASS" } else { "FAIL" }; if (-not $r.Passed) { $allPass = $false }
    $color = if ($r.Passed) { "Green" } else { "Red" }
    $line = "[$tag] $($r.Name)"; if ($r.Detail) { $line += "  ($($r.Detail))" }
    Write-Host $line -ForegroundColor $color
}
Write-Host ""
if ($allPass) { Write-Host "PREVIOUS-APP HOOK TEST PASSED." -ForegroundColor Green; exit 0 }
else { Write-Host "PREVIOUS-APP HOOK TEST FAILED." -ForegroundColor Red; exit 1 }
