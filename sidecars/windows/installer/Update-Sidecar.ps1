#Requires -Version 7.4
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$Sha256,
    [Parameter(Mandatory)][string]$PreviousInstaller,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$PreviousSha256,
    [Parameter(Mandatory)][string]$ServerAddress,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$WorkerId,
    [Security.SecureString]$ServerToken,
    [string]$InstallDirectory=(Join-Path $env:ProgramFiles 'Optimisarr Sidecar'),
    [string]$EvidenceDirectory=(Join-Path $env:ProgramData ('Optimisarr/Updates/'+[Guid]::NewGuid().ToString('N'))),
    [ValidateRange(30,300)][int]$ObservationSeconds=90
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'InstallationHealth.psm1') -Force
if(![IO.Path]::IsPathFullyQualified($InstallDirectory) -or $InstallDirectory -match '["\r\n]'){throw 'Use the full path to the existing installation folder.'}
$InstallDirectory=[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\','/')
if($InstallDirectory -eq [IO.Path]::GetPathRoot($InstallDirectory).TrimEnd('\','/') -or !(Test-Path -LiteralPath $InstallDirectory -PathType Container)){throw 'Use the existing sidecar installation folder, not a drive root.'}
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Use a new evidence directory for each update.' }
New-Item -ItemType Directory -Path $EvidenceDirectory | Out-Null
$acl=[Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true,$false)
foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
}
Set-Acl -LiteralPath $EvidenceDirectory -AclObject $acl
$receipt=@{State='Preparing'; PairingUnchanged=$false; DrainNotChangedByUpdater=$true}
$pairingFile=Join-Path $env:ProgramData 'Optimisarr/Sidecar/pairing.dat'
$trayContext=$null
function Save-Receipt { $receipt | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDirectory 'result.json') }
function Assert-Pairing {
    if (!(Test-Path -LiteralPath $pairingFile) -or (Get-FileHash -LiteralPath $pairingFile).Hash -ne $pairingHash) { throw 'Pairing changed during the update. Keep the worker drained and inspect the retained encrypted backup before recovery.' }
    $receipt.PairingUnchanged=$true
}
function Stop-UpdateTray {
    if ($trayContext) {
        Get-Process -Name Optimisarr.Sidecar.Tray -ErrorAction SilentlyContinue |
            Where-Object {$_.Path -ieq (Join-Path $InstallDirectory 'Optimisarr.Sidecar.Tray.exe') -and $_.SessionId -eq $trayContext.SessionId} |
            Stop-Process -Force
    }
}
function Start-UpdateTray {
    if (!$trayContext) { return @() }
    $exe=Join-Path $InstallDirectory 'Optimisarr.Sidecar.Tray.exe'
    if ([Security.Principal.WindowsIdentity]::GetCurrent().Name -ieq $trayContext.Owner -and (Get-Process -Id $PID).SessionId -eq $trayContext.SessionId) {
        return @((Start-Process -FilePath $exe -WorkingDirectory $InstallDirectory -PassThru).Id)
    }
    $task='OptimisarrUpdate-'+[Guid]::NewGuid().ToString('N')
    try {
        $action=New-ScheduledTaskAction -Execute $exe -WorkingDirectory $InstallDirectory
        $principal=New-ScheduledTaskPrincipal -UserId $trayContext.Owner -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $task -Action $action -Principal $principal | Out-Null
        Start-ScheduledTask -TaskName $task
        for ($n=0;$n -lt 15;$n++) {
            Start-Sleep -Seconds 2
            $tray=@(Get-Process -Name Optimisarr.Sidecar.Tray -ErrorAction SilentlyContinue | Where-Object {$_.Path -ieq $exe -and $_.SessionId -eq $trayContext.SessionId})
            if ($tray.Count -eq 1) {return @($tray[0].Id)}
        }
        throw 'The updated tray did not reopen in its original signed-in session.'
    }
    finally {Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue}
}
function Verify-Package($Package,[DateTime]$After,[int[]]$TrayIds) {
    Assert-Pairing
    $files=Assert-SidecarPayload $Package.Payload $InstallDirectory
    $ui=Start-Process -FilePath (Join-Path $InstallDirectory 'runtime/dotnet.exe') -ArgumentList @(('"'+(Join-Path $InstallDirectory 'Optimisarr.Sidecar.Tray.dll')+'"'),'--verify-popover') -PassThru
    try {
        if (!$ui.WaitForExit(30000)) { $ui.Kill($true); throw 'The installed tray check timed out.' }
        if ($ui.ExitCode -ne 0) {throw 'The installed tray could not run its window check.'}
    }
    finally {$ui.Dispose()}
    $read={
        Assert-Pairing
        $sample=Get-InstallationSample $InstallDirectory $ServerAddress $WorkerId $ServerToken $After $TrayIds
        if ($sample.Worker -and $sample.Worker.DrainId -ne $drainId) {
            $e=[InvalidOperationException]::new('The server drain changed during the update. Keep this worker drained until it is reviewed.')
            $e.Data['RecoveryUnsafe']=$true
            throw $e
        }
        return $sample
    }
    $health=Wait-InstallationHealth -ExpectedVersion $Package.Version -After $After -ObservationSeconds $ObservationSeconds -RequireTray:($TrayIds.Count -gt 0) -ReadSample $read
    # A live check is followed by another payload check, including locked/replaced files.
    Assert-Pairing
    [void](Assert-SidecarPayload $Package.Payload $InstallDirectory)
    [pscustomobject]@{Health=$health; CheckedFiles=$files; TrayChecked=$true; TrayRestored=($TrayIds.Count -gt 0)}
}
try {
    if (!(Test-Path -LiteralPath $pairingFile)) {throw 'Use the normal MSI and pair this PC before using the checked update route.'}
    $pairingHash=(Get-FileHash -LiteralPath $pairingFile).Hash
    Copy-Item -LiteralPath $pairingFile -Destination (Join-Path $EvidenceDirectory 'pairing-before.dat')
    $previous=Get-SidecarPackage $PreviousInstaller $PreviousSha256 $EvidenceDirectory 'previous'
    $next=Get-SidecarPackage $Installer $Sha256 $EvidenceDirectory 'new'
    $receipt.PreviousVersion=$previous.Version; $receipt.NewVersion=$next.Version
    if (!(Test-Path ('HKLM:/SOFTWARE/Microsoft/Windows/CurrentVersion/Uninstall/'+$previous.ProductCode))) {throw 'The previous installer does not match the installed MSI product.'}
    [void](Assert-SidecarPayload $previous.Payload $InstallDirectory)
    $baseline=Get-InstallationSample $InstallDirectory $ServerAddress $WorkerId $ServerToken ([DateTime]::UtcNow) @()
    if ($baseline.ServiceState -ne 'Running' -or !$baseline.Monitor -or $baseline.PipeProcessId -ne $baseline.ProcessId `
        -or $baseline.Monitor.Version -cne $previous.Version -or $baseline.Monitor.State -ne 'Connected' `
        -or @($baseline.Monitor.Jobs).Count -ne 0 -or $baseline.Monitor.ShutdownArmed `
        -or !$baseline.Worker.Draining -or $baseline.Worker.HeldLeases -ne 0 -or !$baseline.Worker.Online `
        -or $baseline.Worker.Version -cne $previous.Version -or $baseline.Worker.LastSeenUtc -lt [DateTime]::UtcNow.AddSeconds(-45)) {
        throw 'Drain the selected worker on the server, wait for all jobs, and check that its current worker is connected before updating.'
    }
    if((Get-MonitorWorkerId $baseline.Monitor) -ne $WorkerId){throw 'The selected server worker is not this local worker.'}
    $drainId=$baseline.Worker.DrainId
    $trays=@(Get-Process -Name Optimisarr.Sidecar.Tray -ErrorAction SilentlyContinue | Where-Object {$_.Path -ieq (Join-Path $InstallDirectory 'Optimisarr.Sidecar.Tray.exe')})
    if ($trays.Count -gt 1) {throw 'Close the extra tray sessions before updating this PC.'}
    if ($trays.Count -eq 1) {
        $owner=Invoke-CimMethod -InputObject (Get-CimInstance Win32_Process -Filter "ProcessId=$($trays[0].Id)") -MethodName GetOwner
        if ($owner.ReturnValue -ne 0) {throw 'Cannot identify the signed-in tray user; no update has been started.'}
        $trayContext=[pscustomobject]@{SessionId=$trays[0].SessionId; Owner=($owner.Domain+'\'+$owner.User)}
    }
    $receipt.State='Updating'; Save-Receipt
    Write-Output "Checking update. The previous MSI, encrypted pairing backup and evidence are retained in $EvidenceDirectory"
    $newStarted=[DateTime]::UtcNow
    $result=Invoke-SidecarUpdateTransaction -Install {
        $worker=Get-SidecarWorkerStatus $ServerAddress $WorkerId $ServerToken
        if(!$worker.Draining -or $worker.DrainId -ne $drainId -or $worker.HeldLeases -ne 0){$e=[InvalidOperationException]::new('The worker drain changed before installation. No update was started.');$e.Data['RecoveryUnsafe']=$true;throw $e}
        Stop-UpdateTray
        $code=Invoke-SidecarMsi $next.Installer (Join-Path $EvidenceDirectory 'update.log') -InstallDirectory $InstallDirectory
        if ($code -eq 3010) {$error=[InvalidOperationException]::new('Windows requires a restart. This update is not yet verified; keep the worker drained.');$error.Data['RebootRequired']=$true;throw $error}
    } -Verify {
        $ids=@(Start-UpdateTray)
        Verify-Package $next $newStarted $ids
    } -RecordFailure {
        param($Failure)
        $receipt.UpdateFailure=$Failure.Exception.Message
        $receipt.StartupFailures=@(Get-SidecarStartupFailures $newStarted $InstallDirectory)
        Save-Receipt
    } -Recover {
        $receipt.State='Recovering'; Save-Receipt
        Assert-Pairing
        $worker=Get-SidecarWorkerStatus $ServerAddress $WorkerId $ServerToken
        if(!$worker.Draining -or $worker.DrainId -ne $drainId -or $worker.HeldLeases -ne 0){throw 'Recovery cannot start while this worker drain has changed or jobs are held.'}
        Stop-UpdateTray
        $recoveryStarted=[DateTime]::UtcNow
        if (Test-Path ('HKLM:/SOFTWARE/Microsoft/Windows/CurrentVersion/Uninstall/'+$next.ProductCode)) {
            if ((Invoke-SidecarMsi $next.Installer (Join-Path $EvidenceDirectory 'remove-failed-update.log') -Uninstall) -ne 0) {throw 'Recovery removal requires a restart.'}
        }
        if ((Invoke-SidecarMsi $previous.Installer (Join-Path $EvidenceDirectory 'restore.log') -InstallDirectory $InstallDirectory -Repair:(Test-Path ('HKLM:/SOFTWARE/Microsoft/Windows/CurrentVersion/Uninstall/'+$previous.ProductCode))) -ne 0) {throw 'Recovery installation requires a restart.'}
        Assert-Pairing
        Start-Service OptimisarrSidecar
        $ids=@(Start-UpdateTray)
        $receipt.Recovery=Verify-Package $previous $recoveryStarted $ids
        $receipt.State='Recovered'; Save-Receipt
    }
    $receipt.Verification=$result; $receipt.State='Verified'; Save-Receipt
    Write-Output 'Update verified: files match, the worker stayed healthy and checked in at least three times. You can now remove its server drain.'
    exit 0
}
catch {
    if ($receipt.State -ne 'Recovered') {$receipt.State=if($_.Exception.Data['RebootRequired']){'RebootRequired'}else{'Unverified'}}
    $receipt.Failure=$_.Exception.Message
    if($_.Exception.Data['RecoveryFailure']){$receipt.RecoveryFailure=$_.Exception.Data['RecoveryFailure']}
    Save-Receipt
    Write-Error -Message ($_.Exception.Message+" Evidence: $EvidenceDirectory") -ErrorAction Continue
    if ($receipt.State -eq 'RebootRequired') {exit 3010}
    exit 1
}
