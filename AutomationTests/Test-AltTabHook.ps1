#requires -Version 5.1
# One-off verification for the AltTabHook fix: hook callback must swallow
# Alt+Tab instantly and defer the heavy UI work, so the OS switcher never appears.
# Requires an UNLOCKED desktop (keybd_event injection reaches the session).
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "Switcheroo\bin\Debug\switcheroo_plus.exe"
$pipeName = "switcheroo_plus_automation"

$api = @"
[DllImport("user32.dll")]
public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);
[DllImport("user32.dll")]
public static extern System.IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
"@
Add-Type -MemberDefinition $api -Name "U32" -Namespace "SwtAltTabTest" -ErrorAction SilentlyContinue

if ([SwtAltTabTest.U32]::OpenInputDesktop(0, $false, 0x100) -eq [System.IntPtr]::Zero) {
    Write-Host "[SKIP] input desktop not accessible (machine locked?)" -ForegroundColor Yellow
    exit 2
}

function Send-Command($json) {
    $client = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $client.Connect(5000)
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $writer = New-Object System.IO.StreamWriter($client, $utf8, 4096, $true)
        $reader = New-Object System.IO.StreamReader($client, $utf8, $true, 4096, $true)
        $writer.WriteLine($json); $writer.Flush()
        return $reader.ReadLine()
    }
    finally { $client.Dispose() }
}
function Send-AltTabTap {
    [SwtAltTabTest.U32]::keybd_event(0x12, 0, 0, [System.UIntPtr]::Zero)  # Alt down
    Start-Sleep -Milliseconds 40
    [SwtAltTabTest.U32]::keybd_event(0x09, 0, 0, [System.UIntPtr]::Zero)  # Tab down
    Start-Sleep -Milliseconds 60
    [SwtAltTabTest.U32]::keybd_event(0x09, 0, 2, [System.UIntPtr]::Zero)  # Tab up
    Start-Sleep -Milliseconds 30
    [SwtAltTabTest.U32]::keybd_event(0x12, 0, 2, [System.UIntPtr]::Zero)  # Alt up
}
function Send-AltTabHold([int]$tabs) {
    [SwtAltTabTest.U32]::keybd_event(0x12, 0, 0, [System.UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    for ($i = 0; $i -lt $tabs; $i++) {
        [SwtAltTabTest.U32]::keybd_event(0x09, 0, 0, [System.UIntPtr]::Zero)
        Start-Sleep -Milliseconds 70
        [SwtAltTabTest.U32]::keybd_event(0x09, 0, 2, [System.UIntPtr]::Zero)
        Start-Sleep -Milliseconds 70
    }
    [SwtAltTabTest.U32]::keybd_event(0x12, 0, 2, [System.UIntPtr]::Zero)
}
function Check($name, $cond, $detail) {
    Write-Host ("[{0}] {1}{2}" -f $(if ($cond) { "PASS" } else { "FAIL" }), $name, $(if ($detail) { "  ($detail)" } else { "" }))
    return $cond
}

$configDir = Join-Path $env:USERPROFILE ".config\.switcheroo_plus"
if (-not (Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }
$configPath = Join-Path $configDir "config.jsonc"
$configBackup = $null
if (Test-Path $configPath) { $configBackup = [IO.File]::ReadAllText($configPath) }

$allOk = $true
$proc = $null
try {
    Set-Content -Path $configPath -Encoding UTF8 -Value @'
{"ActivateAlt":true,"ActivateCtrl":false,"ActivateShift":false,"ActivateWindowsKey":false,"ActivateHotKey":32,"PreviousAppEnabled":false,"PreviousAppAlt":true,"PreviousAppCtrl":false,"PreviousAppShift":false,"PreviousAppWindowsKey":false,"PreviousAppCapsLock":false,"PreviousAppHotKey":9,"AltTabHook":true,"AutoSwitch":false,"RunAsAdmin":false,"EnableHotKey":true,"RunOnStartup":false,"OpenUsageHintsByDefault":false,"WindowWidth":0,"WindowHeight":0,"MultiColumnEnabled":false,"RecentWindowsOnTopEnabled":true,"RecentWindowsOnTopCount":5,"AzShortcutsEnabled":true,"TaskbarBadgesEnabled":false}
'@
    Get-Process -Name "switcheroo_plus" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
    $proc = Start-Process -FilePath $exe -ArgumentList "--auto" -PassThru
    $ready = $false
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline -and -not $ready) {
        try { if ((Send-Command '{"action":"ping"}') -like '*"ok":true*') { $ready = $true } }
        catch { Start-Sleep -Milliseconds 400 }
    }
    if (-not $ready) { throw "automation pipe not available" }

    # 1) repeated Alt+Tab taps: overlay must appear and own foreground every time
    $selBase = -1
    for ($i = 1; $i -le 5; $i++) {
        Send-Command '{"action":"hide"}' | Out-Null
        Start-Sleep -Milliseconds 400
        Send-AltTabTap
        Start-Sleep -Milliseconds 800
        $vis = ((Send-Command '{"action":"ping"}') | ConvertFrom-Json).visible
        $fg = ((Send-Command '{"action":"getforeground"}') | ConvertFrom-Json).foreground
        $allOk = (Check "tap $i shows overlay with foreground" ($vis -eq $true -and $fg.process -eq "switcheroo_plus") ("vis=$vis fg=$($fg.process)/$($fg.title)")) -and $allOk
        if ($i -eq 1) {
            $w0 = ((Send-Command '{"action":"getwindows"}') | ConvertFrom-Json).windows
            for ($k = 0; $k -lt $w0.Count; $k++) { if ($w0[$k].selected -eq "True") { $selBase = $k; break } }
        }
    }

    # 2) hold Alt, Tab x3, release: overlay shows, selection advanced by 3 from baseline
    Send-Command '{"action":"hide"}' | Out-Null
    Start-Sleep -Milliseconds 400
    Send-AltTabHold 3
    Start-Sleep -Milliseconds 900
    $vis = ((Send-Command '{"action":"ping"}') | ConvertFrom-Json).visible
    $w = ((Send-Command '{"action":"getwindows"}') | ConvertFrom-Json).windows
    $sel = -1
    for ($k = 0; $k -lt $w.Count; $k++) { if ($w[$k].selected -eq "True") { $sel = $k; break } }
    $allOk = (Check "hold-alt x3: overlay visible" ($vis -eq $true) ("vis=$vis")) -and $allOk
    $allOk = (Check "hold-alt x3: selection advanced by 3" ($selBase -ge 0 -and $sel -eq ($selBase + 3)) ("base=$selBase now=$sel")) -and $allOk

    # 3) no OS switcher: foreground must never be a shell switcher window after any press
    $fg = ((Send-Command '{"action":"getforeground"}') | ConvertFrom-Json).foreground
    $allOk = (Check "no OS Alt+Tab switcher jamming" ($fg.title -notmatch 'Task Switching' -and $fg.process -notmatch 'explorer|ShellExperienceHost|SearchHost') ("fg=$($fg.process)/$($fg.title)")) -and $allOk
}
finally {
    try { Send-Command '{"action":"exit"}' | Out-Null } catch {}
    Start-Sleep -Seconds 1
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Get-Process -Name "switcheroo_plus" -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($configBackup -ne $null) { [IO.File]::WriteAllText($configPath, $configBackup) }
    Write-Host "Cleaned up (config restored)."
}
if ($allOk) { Write-Host "`nAll AltTabHook checks passed." -ForegroundColor Green } else { exit 1 }
