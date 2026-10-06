#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Wait-InstallationHealth {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][DateTime]$After,
        [ValidateRange(30,300)][int]$ObservationSeconds = 90,
        [ValidateRange(60,600)][int]$TimeoutSeconds = 300,
        [switch]$RequireTray,
        [Parameter(Mandatory)][scriptblock]$ReadSample,
        [scriptblock]$Clock = { [DateTime]::UtcNow },
        [scriptblock]$Delay = { Start-Sleep -Seconds 2 }
    )
    $deadline = (& $Clock).AddSeconds($TimeoutSeconds)
    $firstHealthy = $null; $process = $null; $lastBeat = $null; $checkIns = 0
    while ((& $Clock) -lt $deadline) {
        $sample = & $ReadSample
        $now = & $Clock
        if ($sample.Failures.Count) { throw 'Windows recorded a worker startup or application-control failure. See the retained update evidence.' }
        if ($sample.ServiceState -ne 'Running') { throw 'The worker stopped during the update check.' }
        if ($null -ne $process -and $sample.ProcessId -ne $process) { throw 'The worker restarted during the update check.' }
        if ($sample.ProcessStartedUtc -lt $After) { throw 'The previous worker is still running; the update was not verified.' }
        if ($RequireTray -and !$sample.TrayHealthy) { throw 'The tray stopped during the update check.' }
        if (!$sample.Worker.Draining -or $sample.Worker.HeldLeases -ne 0) { throw 'Keep this worker drained with no jobs until the update check finishes.' }
        if ($null -ne $sample.Monitor) {
            if ($sample.PipeProcessId -ne $sample.ProcessId) { throw 'The local monitor belongs to a different process.' }
            if ($sample.Monitor.Version -cne $ExpectedVersion) { throw 'The local worker build does not match the installer.' }
            if (@($sample.Monitor.Jobs).Count -ne 0 -or $sample.Monitor.ShutdownArmed) { throw 'The worker has work or an armed shutdown; leave it drained before updating.' }
            if ($sample.Monitor.State -in @('Faulted','Stopped','Unreachable')) { throw 'The worker cannot maintain its server connection.' }
            if ($sample.Monitor.State -eq 'Connected' -and (Get-MonitorWorkerId $sample.Monitor) -ne $sample.Worker.Id) {throw 'The selected server worker is not this local worker.'}
        }
        $beat = $sample.Worker.LastSeenUtc
        if ($null -ne $beat -and $beat -gt $now.AddSeconds(5)) { throw 'The server check-in time is ahead of this PC; check both clocks.' }
        if ($null -ne $lastBeat -and $beat -lt $lastBeat) { throw 'The worker check-in time went backwards.' }
        $healthy = $null -ne $sample.Monitor -and $sample.Monitor.State -eq 'Connected' `
            -and $sample.Worker.Version -ceq $ExpectedVersion -and $sample.Worker.Online `
            -and $null -ne $beat -and $beat -ge $sample.ProcessStartedUtc -and $beat -ge $After `
            -and $beat -ge $now.AddSeconds(-45)
        if ($healthy) {
            if ($null -eq $firstHealthy) { $firstHealthy=$now; $process=$sample.ProcessId }
            if ($null -eq $lastBeat -or $beat -gt $lastBeat) { $checkIns++; $lastBeat=$beat }
            if (($now-$firstHealthy).TotalSeconds -ge $ObservationSeconds -and $checkIns -ge 3) {
                return [pscustomobject]@{Version=$ExpectedVersion; ProcessId=$process; CheckIns=$checkIns; ObservedSeconds=($now-$firstHealthy).TotalSeconds; LastCheckInUtc=$lastBeat}
            }
        }
        elseif ($null -ne $firstHealthy) { throw 'The worker lost a fresh server connection during the update check.' }
        & $Delay
    }
    throw 'The update could not be verified: three fresh check-ins and sustained worker health were not observed. Keep the worker drained.'
}

function Get-SidecarWorkerStatus {
    param([Parameter(Mandatory)][string]$ServerAddress, [Parameter(Mandatory)][int]$WorkerId, [Security.SecureString]$ServerToken)
    $uri = [Uri]$ServerAddress
    if (!$uri.IsAbsoluteUri -or $uri.Scheme -notin @('http','https') -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) { throw 'Use a server address without credentials, query parameters or fragments.' }
    $arguments = @{Uri=($ServerAddress.TrimEnd('/')+'/api/workers'); TimeoutSec=10; ErrorAction='Stop'}
    if ($ServerToken) { $arguments.Authentication='Bearer'; $arguments.Token=$ServerToken }
    try { $workers = Invoke-RestMethod @arguments } catch { throw 'Cannot read the server worker status. Check its address, access token and connection.' }
    $worker = @($workers | Where-Object { $_.id -eq $WorkerId })
    if ($worker.Count -ne 1) { throw 'The selected worker was not found on this server.' }
    $w=$worker[0]
    # PowerShell can deserialize JSON timestamps into dates before this function sees them.
    # Re-parsing a date's formatted string can exchange day and month on regional hosts.
    $lastSeenUtc=if($w.lastSeenAt -is [DateTimeOffset]){$w.lastSeenAt.UtcDateTime}
        elseif($w.lastSeenAt -is [DateTime]){$w.lastSeenAt.ToUniversalTime()}
        elseif($w.lastSeenAt -is [string]){[DateTimeOffset]::Parse($w.lastSeenAt,[Globalization.CultureInfo]::InvariantCulture).UtcDateTime}
        elseif($null -eq $w.lastSeenAt){$null}
        else{throw 'The server returned an invalid worker check-in timestamp.'}
    [pscustomobject]@{ Id=$w.id; Version=$w.sidecarVersion; Online=[bool]$w.online; LastSeenUtc=$lastSeenUtc; Draining=($null -ne $w.drainRequestedAt); DrainId=$w.drainRequestedAt; HeldLeases=$w.heldLeases }
}

function Get-MonitorWorkerId {
    param($Monitor)
    # Monitor v1 identifies the authenticated heartbeat as "Worker <id>: ...".
    # Older recovery packages have this same format; unknown formats fail closed.
    $id=0
    if ($Monitor.Detail -notmatch '^Worker ([1-9][0-9]*):' -or ![int]::TryParse($Matches[1],[ref]$id)) {throw 'The local monitor did not identify its connected worker.'}
    return $id
}

function Read-SidecarMonitor {
    param([string]$PipeName='Optimisarr.Sidecar.Monitor.v1')
    if (!('OptimisarrInstallerPipe' -as [type])) {
        Add-Type @'
using System;
using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class OptimisarrInstallerPipe {
    [DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    public static object[] Read(string name) {
        using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous)) {
            pipe.Connect(3000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new IOException("Cannot identify monitor process.");
            var clock=Stopwatch.StartNew();
            pipe.WriteByte(0);
            var header=new byte[4]; ReadExactly(pipe,header,clock);
            int length=BitConverter.ToInt32(header,0);
            if (length<=0 || length>65536) throw new IOException("Invalid monitor frame.");
            var body=new byte[length]; ReadExactly(pipe,body,clock);
            pipe.WriteByte(0);
            return new object[]{(int)pid,new UTF8Encoding(false,true).GetString(body)};
        }
    }
    private static void ReadExactly(Stream pipe, byte[] buffer, Stopwatch clock) {
        int offset=0;
        while(offset<buffer.Length) {
            int remaining=3000-(int)clock.ElapsedMilliseconds;
            if(remaining<=0) throw new TimeoutException("Monitor response timed out.");
            var read=pipe.ReadAsync(buffer,offset,buffer.Length-offset);
            if(!read.Wait(remaining)) throw new TimeoutException("Monitor response timed out.");
            if(read.Result==0) throw new IOException("Monitor response ended early.");
            offset+=read.Result;
        }
    }
}
'@
    }
    $frame=[OptimisarrInstallerPipe]::Read($PipeName)
    [pscustomobject]@{ProcessId=$frame[0]; Snapshot=($frame[1] | ConvertFrom-Json -Depth 15)}
}

function Get-SidecarStartupFailures {
    param([DateTime]$After,[string]$Directory)
    $events=@()
    foreach ($log in @('Application','System','Microsoft-Windows-CodeIntegrity/Operational')) {
        # A missing/disabled provider leaves health unverified rather than silently passing.
        $info=Get-WinEvent -ListLog $log -ErrorAction Stop
        if (!$info.IsEnabled) { throw 'A required Windows diagnostic log is disabled; the update cannot be fully checked.' }
        $ticks=$After.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
        $filter="*[System[TimeCreated[@SystemTime >= '$ticks'] and (EventID=1026 or EventID=1000 or EventID=7031 or EventID=7034 or EventID=7000 or EventID=7009 or EventID=7023 or EventID=7024 or EventID=3033 or EventID=3077)]]"
        $queryErrors=@()
        $items=@(Get-WinEvent -LogName $log -FilterXPath $filter -MaxEvents 128 -ErrorAction SilentlyContinue -ErrorVariable queryErrors)
        if (@($queryErrors | Where-Object {$_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*'}).Count) { throw 'Windows startup diagnostics could not be read; the update cannot be fully checked.' }
        if ($items.Count -ge 128) {throw 'Windows startup diagnostics exceeded the collection limit; the update cannot be fully checked.'}
        foreach ($event in $items) {
            if ($event.Message.IndexOf($Directory,[StringComparison]::OrdinalIgnoreCase) -ge 0 -or $event.Message -match '(?<!\w)Optimisarr ?Sidecar(?!\w)') {
                $events += [pscustomobject]@{Log=$log; Id=$event.Id; TimeUtc=$event.TimeCreated.ToUniversalTime()}
            }
        }
    }
    return $events
}

function Get-InstallationSample {
    param([string]$Directory,[string]$ServerAddress,[int]$WorkerId,[Security.SecureString]$ServerToken,[DateTime]$After,[int[]]$TrayProcessIds=@())
    $service=Get-CimInstance Win32_Service -Filter "Name='OptimisarrSidecar'" -OperationTimeoutSec 5
    if (!$service -or $service.State -ne 'Running') {
        return [pscustomobject]@{ServiceState='Stopped'; ProcessId=0; ProcessStartedUtc=[DateTime]::MinValue; Failures=@(); TrayHealthy=$false; Worker=$null; Monitor=$null}
    }
    $process=Get-Process -Id $service.ProcessId -ErrorAction Stop
    if ([IO.Path]::GetFullPath($process.Path) -ine [IO.Path]::GetFullPath((Join-Path $Directory 'runtime/dotnet.exe'))) { throw 'The worker service is not using this installation runtime.' }
    $monitor=$null
    try { $monitor=Read-SidecarMonitor }
    catch {
        if ($_.Exception.InnerException -is [UnauthorizedAccessException]) {throw 'The local monitor denied access. Run the checked update in a local administrator PowerShell window.'}
    }
    $trayHealthy=$true
    foreach ($trayId in $TrayProcessIds) {
        $tray=Get-Process -Id $trayId -ErrorAction SilentlyContinue
        if (!$tray -or $tray.Path -ine (Join-Path $Directory 'Optimisarr.Sidecar.Tray.exe')) { $trayHealthy=$false }
    }
    if($monitor -and $monitor.Snapshot.ServerAddress){
        $reported=[Uri]$monitor.Snapshot.ServerAddress; $expected=[Uri]$ServerAddress
        if($reported.Scheme -ne $expected.Scheme -or $reported.Authority -ine $expected.Authority -or $reported.AbsolutePath.TrimEnd('/') -cne $expected.AbsolutePath.TrimEnd('/')) {throw 'The local worker is paired with a different server.'}
    }
    [pscustomobject]@{
        ServiceState=$service.State; ProcessId=[int]$service.ProcessId; ProcessStartedUtc=$process.StartTime.ToUniversalTime(); TrayHealthy=$trayHealthy
        PipeProcessId=if($monitor){$monitor.ProcessId}else{0}; Monitor=if($monitor){$monitor.Snapshot}else{$null}
        Worker=(Get-SidecarWorkerStatus $ServerAddress $WorkerId $ServerToken)
        Failures=@(Get-SidecarStartupFailures $After $Directory)
    }
}

function Invoke-SidecarMsi {
    param([string]$Path,[string]$Log,[string]$ExtractTo,[switch]$Uninstall,[switch]$Repair,[string]$InstallDirectory)
    $arguments=if($ExtractTo){@('/a',('"'+$Path+'"'),'/qn',('TARGETDIR="'+$ExtractTo+'"'))}else{@($(if($Uninstall){'/x'}elseif($Repair){'/fa'}else{'/i'}),('"'+$Path+'"'),'/qn','/norestart')}
    if($InstallDirectory -and !$ExtractTo -and !$Uninstall){$arguments+=('INSTALLFOLDER="'+$InstallDirectory+'"')}
    $process=Start-Process msiexec.exe -ArgumentList ($arguments+@('/l*v',('"'+$Log+'"'))) -PassThru
    try {
        if (!$process.WaitForExit(600000)) {
            $timeout=[TimeoutException]::new('The installer timed out. Keep the worker drained and inspect the retained MSI log before any recovery.')
            $timeout.Data['InstallerStillRunning']=$true
            throw $timeout
        }
        if ($process.ExitCode -notin @(0,3010)) { throw "The installer failed with Windows error $($process.ExitCode). See the retained MSI log." }
        return $process.ExitCode
    }
    finally {$process.Dispose()}
}

function Get-SidecarPackage {
    param([string]$Installer,[ValidatePattern('^[a-fA-F0-9]{64}$')][string]$Sha256,[string]$Root,[string]$Name)
    $copy=Join-Path $Root ($Name+'.msi')
    Copy-Item -LiteralPath $Installer -Destination $copy -ErrorAction Stop
    if ((Get-FileHash $copy -Algorithm SHA256).Hash -ine $Sha256) { throw 'An installer checksum does not match. No update has been started.' }
    $metadata=Get-SidecarMsiMetadata $copy
    $extract=Join-Path $Root ($Name+'-payload')
    New-Item -ItemType Directory $extract | Out-Null
    if ((Invoke-SidecarMsi $copy (Join-Path $Root ($Name+'-extract.log')) $extract) -ne 0) { throw 'Installer extraction requires a restart; no update has been started.' }
    $cores=@(Get-ChildItem $extract -Filter Optimisarr.Sidecar.Core.dll -Recurse -File)
    if ($cores.Count -ne 1) { throw 'The installer does not contain one complete worker payload.' }
    $payload=$cores[0].Directory.FullName
    $version=$cores[0].VersionInfo.ProductVersion
    foreach ($file in @('Optimisarr.Sidecar.Service.dll','Optimisarr.Sidecar.Tray.dll')) {
        if ((Get-Item (Join-Path $payload $file)).VersionInfo.ProductVersion -cne $version) { throw 'The installer contains inconsistent worker builds.' }
    }
    [pscustomobject]@{Installer=$copy; Payload=$payload; Version=$version; ProductCode=$metadata.ProductCode}
}

function Get-SidecarMsiMetadata {
    param([string]$Installer)
    $engine=New-Object -ComObject WindowsInstaller.Installer
    $database=$null; $values=@{}
    try {
        $database=$engine.OpenDatabase($Installer,0)
        foreach ($name in @('UpgradeCode','ProductCode','ProductName')) {
            $view=$database.OpenView(('SELECT `Value` FROM `Property` WHERE `Property`='''+$name+''''))
            try {
                [void]$view.Execute(); $record=$view.Fetch()
                if (!$record) { throw 'Missing installer identity.' }
                try {$values[$name]=$record.StringData(1)} finally {[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)}
            }
            finally {[void]$view.Close();[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)}
        }
        if ($values.UpgradeCode -ine '{53DAE213-E6B7-49EB-8FF5-C31F82210925}' -or $values.ProductName -cne 'Optimisarr Sidecar') { throw 'Use an Optimisarr Sidecar installer for update and recovery.' }
        return [pscustomobject]$values
    }
    finally {
        if ($database) {[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)}
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine)
    }
}

function Invoke-SidecarUpdateTransaction {
    param([scriptblock]$Install,[scriptblock]$Verify,[scriptblock]$Recover,[scriptblock]$RecordFailure)
    try { & $Install; return & $Verify }
    catch {
        $original=$_
        try { & $RecordFailure $original } catch { Write-Warning 'Some update diagnostics were unavailable. The original failure is retained.' }
        if ($original.Exception.Data['InstallerStillRunning'] -or $original.Exception.Data['RebootRequired'] -or $original.Exception.Data['RecoveryUnsafe']) { throw $original }
        try { & $Recover | Out-Null }
        catch {
            $failure=[InvalidOperationException]::new('The update failed and recovery could not be verified. Keep the worker drained and use the retained installer and evidence.', $original.Exception)
            $failure.Data['RecoveryFailure']=$_.Exception.Message
            throw $failure
        }
        throw [InvalidOperationException]::new('The update failed. The previous installer was restored and its worker health verified. Keep it drained until you review the failure.', $original.Exception)
    }
}

function Assert-SidecarPayload {
    param([string]$Payload,[string]$Directory)
    $count=0
    foreach ($file in Get-ChildItem $Payload -Recurse -File) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The recovery payload contains a link.' }
        $relative=[IO.Path]::GetRelativePath($Payload,$file.FullName)
        $target=Join-Path $Directory $relative
        if (!(Test-Path -LiteralPath $target -PathType Leaf) -or (Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint `
            -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw 'Installed files do not match the verified installer payload.' }
        $count++
    }
    if ($count -lt 100) { throw 'The installer payload is incomplete.' }
    return $count
}

Export-ModuleMember -Function Wait-InstallationHealth,Get-SidecarWorkerStatus,Read-SidecarMonitor,Get-SidecarStartupFailures,Get-InstallationSample,Invoke-SidecarMsi,Get-SidecarPackage,Assert-SidecarPayload,Invoke-SidecarUpdateTransaction,Get-MonitorWorkerId
