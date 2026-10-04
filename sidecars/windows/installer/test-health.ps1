[CmdletBinding()]
param([string]$Installer)
$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot 'InstallationHealth.psm1'
Import-Module $module -Force
if($Installer){
    $metadata=@(& (Get-Module InstallationHealth) {param($path) Get-SidecarMsiMetadata $path} $Installer)
    if($metadata.Count -ne 1 -or !$metadata[0].ProductCode -or $metadata[0].ProductName -ne 'Optimisarr Sidecar'){throw 'Real MSI metadata must return exactly one complete identity record.'}
    Write-Output 'Real MSI identity returned exactly one complete record.'
}
$start = [DateTime]::UtcNow
$simulation = @{ Now=$start; Tick=0; Mode='healthy' }
function Sample {
    $tick = $simulation.Tick
    $state = 'Running'; $process = 42; $pipeProcess = 42; $version = '1.2.3+test'
    $beat = $start.AddSeconds(5 * [Math]::Floor($tick / 5))
    $draining = $true; $jobs = @(); $tray = $true; $errors = @(); $identity='Worker 4: connected'
    switch ($simulation.Mode) {
        'late-crash' { if ($tick -ge 8) { $state='Stopped' } }
        'restart' { if ($tick -ge 8) { $process=43 } }
        'stale' { $beat=$start }
        'wrong-build' { $version='1.2.3+old' }
        'different-pipe-process' { $pipeProcess=43 }
        'no-drain' { $draining=$false }
        'job-started' { if ($tick -ge 8) { $jobs=@(@{JobId=1}) } }
        'tray-crash' { if ($tick -ge 8) { $tray=$false } }
        'blocked' { if ($tick -ge 8) { $errors=@('ApplicationControl') } }
        'missing-heartbeat' { $beat=$null }
        'future-heartbeat' { $beat=$simulation.Now.AddMinutes(1) }
        'counter-regression' { if ($tick -ge 8) { $beat=$start.AddSeconds(-1) } }
        'diagnostics-unavailable' { throw 'Cannot read Windows runtime diagnostics.' }
        'wrong-worker' {$identity='Worker 8: connected'}
        'missing-identity' {$identity='Ready'}
    }
    [pscustomobject]@{
        ServiceState=$state; ProcessId=$process; ProcessStartedUtc=$start; TrayHealthy=$tray; Failures=$errors; PipeProcessId=$pipeProcess
        Worker=[pscustomobject]@{Id=4;Version=$version; LastSeenUtc=$beat; Online=$true; Draining=$draining; HeldLeases=0}
        Monitor=[pscustomobject]@{Version=$version; Detail=$identity; State='Connected'; Jobs=$jobs; ShutdownArmed=$false }
    }
}
function Run-Simulation([string]$Mode) {
    $simulation.Mode=$Mode; $simulation.Now=$start; $simulation.Tick=0
    Wait-InstallationHealth -ExpectedVersion '1.2.3+test' -After $start.AddSeconds(-1) -ObservationSeconds 30 -TimeoutSeconds 75 -RequireTray `
        -ReadSample { Sample } -Clock { $simulation.Now } -Delay { $simulation.Tick++; $simulation.Now=$simulation.Now.AddSeconds(1) }
}
$good = Run-Simulation 'healthy'
if ($good.CheckIns -lt 3 -or $simulation.Tick -lt 30) { throw 'Upgrade accepted before sustained health and three fresh check-ins.' }
foreach ($mode in @('late-crash','restart','stale','wrong-build','different-pipe-process','no-drain','job-started','tray-crash','blocked','missing-heartbeat','future-heartbeat','counter-regression','diagnostics-unavailable','wrong-worker','missing-identity')) {
    $failure=$null
    try { Run-Simulation $mode | Out-Null } catch { $failure=$_.Exception.Message }
    if (!$failure) { throw "Unsafe upgrade accepted: $mode" }
}
Write-Output 'Sustained health, delayed crashes, fresh check-ins, build/process identity, drain and diagnostic failures passed.'
$actions=[Collections.Generic.List[string]]::new()
foreach ($mode in @('success','verify-failure','install-failure','diagnostic-failure','recovery-failure','installer-timeout','reboot-required','unsafe-recovery')) {
    $actions.Clear(); $failure=$null
    $install={
        $actions.Add('install')
        if($mode -eq 'install-failure'){throw 'Original install failure'}
        if($mode -in @('installer-timeout','reboot-required','unsafe-recovery')){
            $e=[InvalidOperationException]::new('Original install failure')
            $e.Data[$(if($mode -eq 'installer-timeout'){'InstallerStillRunning'}elseif($mode -eq 'unsafe-recovery'){'RecoveryUnsafe'}else{'RebootRequired'})]=$true
            throw $e
        }
    }
    $verify={$actions.Add('verify');if($mode -ne 'success'){throw 'Original verification failure'};return 'verified'}
    $recover={$actions.Add('recover');if($mode -eq 'recovery-failure'){throw 'Recovery failed'}}
    $capture={$actions.Add('capture');if($mode -eq 'diagnostic-failure'){throw 'Diagnostics failed'}}
    try {$result=Invoke-SidecarUpdateTransaction $install $verify $recover $capture} catch {$failure=$_}
    if($mode -eq 'success'){
        if($result -ne 'verified' -or $failure -or ($actions -join ',') -ne 'install,verify'){throw 'A successful update ran recovery or lost its result.'}
    }else{
        if(!$failure){throw 'A failed update reported success.'}
        if($mode -in @('installer-timeout','reboot-required','unsafe-recovery')){
            if($actions.Contains('recover')){throw 'Recovery ran while installation/restart was still pending.'}
        }elseif(!$actions.Contains('recover') -or !$failure.Exception.InnerException.Message.StartsWith('Original')){throw 'Recovery or the original error was lost.'}
        if($mode -eq 'recovery-failure' -and $failure.Exception.Data['RecoveryFailure'] -ne 'Recovery failed'){throw 'The recovery failure reason was lost.'}
    }
}
Write-Output 'Update/recovery ordering, original-error preservation, failed recovery, installer timeout and restart handling passed.'
$healthModule=Get-Module InstallationHealth
try {
    & $healthModule {
        function script:Get-WinEvent {
            param($ListLog,$LogName,$FilterXPath,$MaxEvents,$ErrorAction,$ErrorVariable)
            if($ListLog){return [pscustomobject]@{IsEnabled=($script:DiagnosticMode -ne 'disabled')}}
            if($script:DiagnosticMode -eq 'unavailable'){throw 'Provider failed'}
            if($script:DiagnosticMode -eq 'limit'){return 1..128 | ForEach-Object {[pscustomobject]@{Message='Unrelated service';Id=7031;TimeCreated=[DateTime]::UtcNow}}}
            if($script:DiagnosticMode -eq 'display-name'){
                if($LogName -eq 'System'){return [pscustomobject]@{Message='The Optimisarr Sidecar service terminated unexpectedly';Id=7031;TimeCreated=[DateTime]::UtcNow}}
                return
            }
            if($LogName -ne 'Microsoft-Windows-CodeIntegrity/Operational'){return}
            [pscustomobject]@{Message=$(if($script:DiagnosticMode -eq 'related'){'Blocked C:/fixture/Optimisarr.Core.dll'}else{'Unrelated program'});Id=3077;TimeCreated=[DateTime]::UtcNow}
        }
    }
    foreach($mode in @('related','display-name','unrelated','disabled','unavailable','limit')){
        & $healthModule {param($mode) $script:DiagnosticMode=$mode} $mode
        $failure=$null; $events=@()
        try{$events=@(Get-SidecarStartupFailures $start 'C:/fixture')}catch{$failure=$_}
        if($mode -eq 'related' -and ($failure -or $events.Count -ne 1 -or $events[0].Id -ne 3077)){throw 'Related application-control failure was not retained.'}
        if($mode -eq 'display-name' -and ($failure -or $events.Count -ne 1 -or $events[0].Id -ne 7031)){throw 'SCM failure using the service display name was not retained.'}
        if($mode -eq 'unrelated' -and ($failure -or $events.Count)){throw 'Unrelated application failure was assigned to this worker.'}
        if($mode -in @('disabled','unavailable','limit') -and !$failure){throw 'Incomplete Windows diagnostics were accepted.'}
    }
}
finally{& $healthModule {Remove-Item Function:Get-WinEvent;Remove-Variable DiagnosticMode -Scope Script -ErrorAction SilentlyContinue}}
Write-Output 'Related application-control events, unrelated events, unavailable logs and bounded collection passed.'
Add-Type @'
public class OptimisarrFakeMsiProcess {
    public int ExitCode=0; public bool TimedOut=false,Disposed=false;
    public bool WaitForExit(int milliseconds){return !TimedOut;}
    public void Dispose(){Disposed=true;}
}
'@
$runner=[scriptblock]::Create((Get-Command Invoke-SidecarMsi).Definition)
$fake=[OptimisarrFakeMsiProcess]::new();$seen=@()
function Start-Process {param($FilePath,$ArgumentList,[switch]$PassThru) $script:seen=$ArgumentList;return $fake}
try{
    foreach($repair in @($false,$true)){
        [void](& $runner -Path 'fixture.msi' -Log 'fixture.log' -InstallDirectory 'C:\Custom worker' -Repair:$repair)
        if($seen -notcontains 'INSTALLFOLDER="C:\Custom worker"'){throw 'The checked update did not preserve a custom installation directory.'}
    }
    [void](& $runner -Path 'fixture.msi' -Log 'fixture.log' -InstallDirectory 'C:\Custom worker' -Uninstall)
    if(@($seen | Where-Object {$_ -like 'INSTALLFOLDER=*'}).Count){throw 'Uninstall changed the registered installation destination.'}
    $fake=[OptimisarrFakeMsiProcess]::new();$fake.TimedOut=$true;$failure=$null
    try{[void](& $runner -Path 'fixture.msi' -Log 'fixture.log')}catch{$failure=$_}
    if(!$failure.Exception.Data['InstallerStillRunning'] -or !$fake.Disposed){throw 'Installer timeout did not preserve its pending state and dispose the handle.'}
}
finally{Remove-Item Function:Start-Process}
Write-Output 'Custom MSI destination and non-competing timeout handling passed.'
