#requires -Version 5.1
param([string]$Configuration='Debug', [string]$Artifacts)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
if(-not $Artifacts) { $Artifacts=Join-Path $repo '.temp\watchdog-tests' }
$id=[guid]::NewGuid().ToString('N')
$root=Join-Path ([IO.Path]::GetFullPath($Artifacts)) $id
New-Item -ItemType Directory -Path $root -Force | Out-Null
$pipe="switcheroo_plus_watchdog_$id"
$corePipe="switcheroo_plus_automation_$id"
$exe=Join-Path $repo "Switcheroo\bin\$Configuration\switcheroo_plus_watchdog.exe"
Set-Content -LiteralPath (Join-Path $root 'config.jsonc') -Encoding UTF8 -Value '{"EnableHotKey":false,"AltTabHook":false,"PreviousAppEnabled":false,"TaskbarBadgesEnabled":false,"SwitchHighlightEnabled":false}'
function Request([string]$Name,[string]$Action,[string]$Kind='') {
    $client=New-Object IO.Pipes.NamedPipeClientStream('.', $Name, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $client.Connect(1500)
        $writer=New-Object IO.StreamWriter($client,(New-Object Text.UTF8Encoding($false)),4096,$true)
        $reader=New-Object IO.StreamReader($client,[Text.Encoding]::UTF8,$false,4096,$true)
        $writer.AutoFlush=$true
        $writer.WriteLine((@{action=$Action;kind=$Kind}|ConvertTo-Json -Compress))
        $read=$reader.ReadLineAsync()
        if(-not $read.Wait(2000)) { throw 'IPC response timeout' }
        return ($read.Result|ConvertFrom-Json)
    } finally { $client.Dispose() }
}
function State { Request $pipe 'status' }
function Wait-State([string]$Expected,[int]$Seconds=25) {
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        try { $s=State; if($s.state -eq $Expected) { return $s } } catch { }
        if($watchdog.HasExited) { throw "Watchdog exited early: $($watchdog.ExitCode)" }
        Start-Sleep -Milliseconds 200
    } while([DateTime]::UtcNow -lt $deadline)
    throw "Expected $Expected, got $($s|ConvertTo-Json -Depth 5 -Compress)"
}
function Assert($Condition,[string]$Message) { if(-not $Condition) { throw $Message }; Write-Host "PASS $Message" }
$watchdog=$null
try {
    $watchdog=Start-Process -FilePath $exe -ArgumentList @('--auto','--test-mode','--test-root',('"'+$root+'"'),'--test-id',$id) -PassThru -WindowStyle Hidden
    $s=Wait-State 'Running'
    Assert (-not $watchdog.HasExited) 'watchdog remains alive'
    $firstPid=$s.pid
    Assert (-not (Request ($pipe+'_ui') 'status').visible) 'control window hidden by default'
    Request ($pipe+'_ui') 'show'|Out-Null
    Start-Sleep -Milliseconds 400
    Assert ((Request ($pipe+'_ui') 'status').visible) 'control window opens'
    Request ($pipe+'_ui') 'hide'|Out-Null
    Request $pipe 'start'|Out-Null
    Start-Sleep -Milliseconds 400
    Assert ((State).pid -eq $firstPid) 'Start is idempotent'
    Request $corePipe 'fault' 'handled'|Out-Null
    Start-Sleep -Milliseconds 500
    Assert ((State).state -eq 'Running') 'handled exceptions are not crashes'
    foreach($kind in @('managed','background','native','failfast')) {
        try { Request $corePipe 'fault' $kind|Out-Null } catch { }
        $s=Wait-State 'Crashed' 50
        Assert (-not $watchdog.HasExited) "$kind crash leaves watchdog alive"
        $report=Get-Content -LiteralPath (Join-Path $s.incident 'report.json') -Raw|ConvertFrom-Json
        Assert ($report.dumpStatus -eq 'complete') "$kind dump finalized"
        $dump=Join-Path $s.incident 'core.dmp'
        $bytes=[IO.File]::ReadAllBytes($dump)
        Assert ([Text.Encoding]::ASCII.GetString($bytes,0,4) -eq 'MDMP') "$kind dump signature"
        $count=[BitConverter]::ToUInt32($bytes,8); $dir=[BitConverter]::ToUInt32($bytes,12)
        $types=@(); $dumpPid=0; $exceptionCode=0; $thread=0
        for($i=0;$i -lt $count;$i++) {
            $entry=$dir+$i*12; $type=[BitConverter]::ToUInt32($bytes,$entry); $size=[BitConverter]::ToUInt32($bytes,$entry+4); $rva=[BitConverter]::ToUInt32($bytes,$entry+8)
            if(($rva+$size) -gt $bytes.Length) { throw 'Invalid dump stream bounds' }
            $types+=$type
            if($type -eq 15) { $dumpPid=[BitConverter]::ToUInt32($bytes,$rva+8) }
            if($type -eq 6) { $thread=[BitConverter]::ToUInt32($bytes,$rva); $exceptionCode=[BitConverter]::ToUInt32($bytes,$rva+8) }
        }
        Assert (($types -contains 3) -and ($types -contains 4) -and (($kind -eq 'failfast') -or ($types -contains 6)) -and ($types -contains 9)) "$kind dump streams"
        Assert ($dumpPid -eq $report.pid -and (($kind -eq 'failfast') -or ($thread -eq $report.thread -and $exceptionCode -eq $report.exceptionCode))) "$kind dump matches incident"
        $cdb=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Debuggers\x64\cdb.exe'
        if(Test-Path $cdb) {
            $output=Join-Path $s.incident 'debugger.txt'
            $debugger=Start-Process -FilePath $cdb -ArgumentList @('-y',('"'+$root+'"'),'-sins','-z',('"'+$dump+'"'),'-c','".ecxr; ~; lm; q"') -RedirectStandardOutput $output -RedirectStandardError ($output+'.err') -PassThru -WindowStyle Hidden
            try {
                if(-not $debugger.WaitForExit(30000)) { throw 'CDB analysis timeout' }
                $text=Get-Content -LiteralPath $output -Raw
                Assert ($text -match 'switcheroo_plus' -and $text -match 'ecxr' -and $text -notmatch 'Could not open dump') "$kind CDB fault context"
            } finally { if(-not $debugger.HasExited) { $debugger.Kill(); $debugger.WaitForExit(3000)|Out-Null }; $debugger.Dispose() }
        } else { Write-Host 'SKIPPED CDB unavailable' }
        Request $pipe 'start'|Out-Null
        $s=Wait-State 'Running'
        Assert ($s.pid -ne $report.pid) "$kind explicit replacement"
    }
    Request $corePipe 'fault' 'recoverablehang'|Out-Null
    $s=Wait-State 'Unresponsive' 45
    $report=Get-Content -LiteralPath (Join-Path $s.incident 'report.json') -Raw|ConvertFrom-Json
    Assert ($report.kind -eq 'hang' -and $report.dumpStatus -eq 'complete') 'hang dump'
    $s=Wait-State 'Running' 25
    Assert ($s.pid -eq $report.pid) 'hang recovers without restart'
    Request $corePipe 'fault' 'hang'|Out-Null
    $s=Wait-State 'Unresponsive' 45
    Request $pipe 'restart'|Out-Null
    $s=Wait-State 'Running' 45
    Assert ($s.pid -ne $report.pid) 'permanent hang restart'
    Request $pipe 'stop'|Out-Null
    $s=Wait-State 'Stopped'
    Assert (-not $watchdog.HasExited) 'Stop leaves shell available'
    Request $pipe 'start'|Out-Null
    $s=Wait-State 'Running'
    Request $pipe 'restart'|Out-Null
    Start-Sleep -Milliseconds 500
    $s2=Wait-State 'Running'
    Assert ($s2.pid -ne $s.pid) 'Restart replaces healthy core'
    Write-Host "Artifacts: $root"
} finally {
    if($watchdog -and -not $watchdog.HasExited) {
        try { Request $pipe 'exit'|Out-Null } catch { }
        if(-not $watchdog.WaitForExit(12000)) { $watchdog.Kill(); $watchdog.WaitForExit(3000)|Out-Null }
    }
    if($watchdog) { $watchdog.Dispose() }
}
