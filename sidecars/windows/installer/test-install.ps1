[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Installer,
    [Parameter(Mandatory)][string] $UpgradeInstaller
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'InstallationHealth.psm1') -Force
$pairing = Join-Path $env:ProgramData 'Optimisarr\Sidecar'
if ((Get-Service OptimisarrSidecar -ErrorAction SilentlyContinue) -or (Test-Path $pairing)) {
    throw 'Run installer smoke tests only on a disposable Windows VM with no existing sidecar or pairing.'
}
$installerPath = (Resolve-Path $Installer).Path
$upgradePath = (Resolve-Path $UpgradeInstaller).Path
$testStarted = [DateTime]::Now
# CI uploads runner.temp; TEMP can point at the user profile and silently lose evidence.
$evidenceRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$logRoot = Join-Path $evidenceRoot ('optimisarr-install-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $logRoot | Out-Null
$directory = Join-Path $logRoot 'Custom install'
Write-Output "Installer evidence: $logRoot"
function Invoke-Msi([string] $Action, [string] $Name, [string] $Path) {
    Write-Output "MSI $($Name): starting. Logs: $logRoot"
    # Start-Process -Wait also waits for descendants. An upgraded worker must stay alive.
    # Wait only for this msiexec instance, with a deadline, and inspect its own exit code.
    $arguments=@($Action, ('"'+$Path+'"'), '/qn', '/norestart', '/l*v', ('"'+$logRoot+'\'+$Name+'.log"'))
    if($Action -eq '/i' -and (Get-Variable directory -ErrorAction SilentlyContinue)){$arguments+=('INSTALLFOLDER="'+$directory+'"')}
    $process = Start-Process msiexec.exe -ArgumentList $arguments -PassThru
    try {
        if (!$process.WaitForExit(600000)) {
            try { $process.Kill() } catch { Write-Warning "Could not stop timed-out installer: $_" }
            throw "MSI $Name timed out. Logs: $logRoot"
        }
        if ($process.ExitCode -notin @(0,3010)) { throw "MSI $Name failed: $($process.ExitCode). Logs: $logRoot" }
        Write-Output "MSI $($Name): completed with $($process.ExitCode)."
    }
    finally { $process.Dispose() }
}
function Get-ProductCode([string] $Path) {
    $engine = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    try {
        $database = $engine.OpenDatabase($Path, 0)
        $view = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property`=''ProductCode''')
        [void]$view.Execute()
        $record = $view.Fetch()
        try { return $record.StringData(1) }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
    }
    finally {
        if ($view) { [void]$view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        if ($database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine)
    }
}
function Wait-ForCheckIn([string] $Evidence, [DateTime] $After) {
    $version=(Get-Item (Join-Path $directory 'Optimisarr.Sidecar.Core.dll')).VersionInfo.ProductVersion
    $read={Get-InstallationSample $directory $address 1 $null $After @()}
    Wait-InstallationHealth -ExpectedVersion $version -After $After -ObservationSeconds 30 -TimeoutSeconds 180 -ReadSample $read | ConvertTo-Json | Set-Content "$Evidence-health.json"
}
function Invoke-CheckedUpdate([string]$New,[string]$Previous,[string]$Name,[int]$ExpectedExit) {
    $root=Join-Path $logRoot $Name
    $args=@('-NoProfile','-File',('"'+(Join-Path $PSScriptRoot 'Update-Sidecar.ps1')+'"'),'-Installer',('"'+$New+'"'),'-Sha256',(Get-FileHash $New).Hash,
        '-PreviousInstaller',('"'+$Previous+'"'),'-PreviousSha256',(Get-FileHash $Previous).Hash,'-ServerAddress',$address,'-WorkerId','1','-ObservationSeconds','30','-EvidenceDirectory',('"'+$root+'"'),'-InstallDirectory',('"'+$directory+'"'))
    $child=Start-Process (Get-Process -Id $PID).Path -ArgumentList $args -PassThru -RedirectStandardOutput (Join-Path $logRoot ($Name+'.stdout')) -RedirectStandardError (Join-Path $logRoot ($Name+'.stderr'))
    try {
        if(!$child.WaitForExit(540000)){throw 'Checked update did not finish within its test deadline.'}
        if($child.ExitCode -ne $ExpectedExit){throw "Checked update returned $($child.ExitCode), expected $ExpectedExit. See $Name evidence."}
    }
    finally{$child.Dispose()}
    $result=Get-Content (Join-Path $root 'result.json') -Raw | ConvertFrom-Json
    # The guarded fixture retains diagnostics, not duplicate toolchains or its dummy pairing.
    foreach($item in @('previous-payload','new-payload','previous.msi','new.msi','pairing-before.dat')){Remove-Item (Join-Path $root $item) -Recurse -Force -ErrorAction SilentlyContinue}
    return $result
}
if ((Get-ProductCode $installerPath) -eq (Get-ProductCode $upgradePath)) {
    throw 'Upgrade fixture must have a distinct ProductCode: repair is not an upgrade test.'
}
$installed = $false
$currentInstaller = $installerPath
$listener = $null
$serverJob = $null
try {
    Invoke-Msi '/i' 'install' $installerPath
    $installed = $true
    $service = Get-CimInstance Win32_Service -Filter "Name='OptimisarrSidecar'"
    if (!$service -or $service.StartName -ne 'LocalSystem' -or $service.State -ne 'Stopped') { throw 'Unexpected service registration/state' }
    if (!(Test-Path (Join-Path $directory 'Optimisarr.Sidecar.Tray.exe'))) { throw 'Tray executable missing' }
    & (Join-Path $directory 'runtime\dotnet.exe') --list-runtimes
    if ($LASTEXITCODE -ne 0) { throw 'Bundled runtime failed' }
    & (Join-Path $directory 'runtime\dotnet.exe') (Join-Path $directory 'Optimisarr.Sidecar.Tray.dll') --render-monitor (Join-Path $logRoot 'ui')
    if ($LASTEXITCODE -ne 0) { throw 'Installed native UI failed' }
    foreach ($state in @('encoding', 'light-encoding', 'preview-fallback', 'two-jobs', 'light-two-jobs')) {
        $image = Join-Path $logRoot "ui\$state.png"
        if (!(Test-Path $image) -or (Get-Item $image).Length -lt 1000) { throw "Installed preview fixture missing: $state" }
    }
    & (Join-Path $directory 'runtime\dotnet.exe') (Join-Path $directory 'Optimisarr.Sidecar.Tray.dll') --verify-popover
    if ($LASTEXITCODE -ne 0) { throw 'Installed popover lost its anchor' }
    Invoke-Msi '/i' 'unpaired-upgrade' $upgradePath
    $currentInstaller = $upgradePath
    if ((Get-Service OptimisarrSidecar).Status -ne 'Stopped') { throw 'Unpaired upgrade started the worker' }

    # A loopback fixture issues only a disposable credential and always drains the worker.
    # Actual DPAPI pairing and the SCM-hosted service run; no production server is contacted.
    $portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $portProbe.Start()
    $port = $portProbe.LocalEndpoint.Port
    $portProbe.Stop()
    $address = "http://127.0.0.1:$port"
    $listener = [Net.HttpListener]::new()
    $listener.Prefixes.Add("$address/")
    $listener.Start()
    $credential = [guid]::NewGuid().ToString('N')
    $heartbeats = Join-Path $logRoot 'heartbeats.txt'
    $serverJob = Start-ThreadJob -FilePath (Join-Path $PSScriptRoot 'test-server.ps1') -ArgumentList $listener, $credential, $heartbeats
    $pair = [Diagnostics.Process]::new()
    $pair.StartInfo.FileName = Join-Path $directory 'runtime\dotnet.exe'
    $pair.StartInfo.UseShellExecute = $false
    $pair.StartInfo.RedirectStandardInput = $true
    $pair.StartInfo.RedirectStandardOutput = $true
    $pair.StartInfo.RedirectStandardError = $true
    foreach ($argument in @((Join-Path $directory 'Optimisarr.Sidecar.Service.dll'), '--pair', $address)) {
        $pair.StartInfo.ArgumentList.Add($argument)
    }
    try {
        [void]$pair.Start()
        $stdout = $pair.StandardOutput.ReadToEndAsync()
        $stderr = $pair.StandardError.ReadToEndAsync()
        $pair.StandardInput.WriteLine('00000000')
        $pair.StandardInput.Close()
        if (!$pair.WaitForExit(180000)) { $pair.Kill($true); throw 'Test pairing timed out' }
        $stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $logRoot 'pairing-output.txt')
        $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $logRoot 'pairing-error.txt')
        if ($pair.ExitCode -ne 0) { throw "Test pairing failed: $($pair.ExitCode)" }
    }
    finally { $pair.Dispose() }
    $pairingFile = Join-Path $pairing 'pairing.dat'
    if (!(Test-Path $pairingFile)) { throw 'DPAPI pairing was not saved' }
    $pairingHash = (Get-FileHash $pairingFile).Hash
    $started = [DateTime]::UtcNow
    Start-Service OptimisarrSidecar
    Wait-ForCheckIn $heartbeats $started

    foreach ($upgrade in @(
        @{ Path = $installerPath; Name = 'paired-upgrade' },
        @{ Path = $upgradePath; Name = 'repeat-paired-upgrade' }
    )) {
        $before = [DateTime]::UtcNow
        $oldPid = (Get-CimInstance Win32_Service -Filter "Name='OptimisarrSidecar'").ProcessId
        Invoke-Msi '/i' $upgrade.Name $upgrade.Path
        $currentInstaller = $upgrade.Path
        $service = Get-CimInstance Win32_Service -Filter "Name='OptimisarrSidecar'"
        if ($service.State -ne 'Running' -or $service.ProcessId -eq $oldPid) { throw 'Paired upgrade did not restart the service' }
        if ((Get-FileHash $pairingFile).Hash -ne $pairingHash) { throw 'Upgrade changed pairing' }
        # Require a heartbeat from the replacement process, not one during MSI shutdown.
        Wait-ForCheckIn $heartbeats $before
        Write-Output "$($upgrade.Name): service restarted and checked in; pairing unchanged. Started $before"
    }
    $tray=Start-Process (Join-Path $directory 'Optimisarr.Sidecar.Tray.exe') -PassThru
    Start-Sleep -Seconds 2
    if($tray.HasExited){throw 'Installed tray did not stay running before checked update.'}
    $success=Invoke-CheckedUpdate $installerPath $upgradePath 'checked-success' 0
    $currentInstaller=$installerPath
    if($success.State -ne 'Verified' -or $success.Verification.Health.CheckIns -lt 3 -or !$success.Verification.TrayRestored){throw 'Checked update did not verify sustained service, check-ins and the reopened tray.'}

    # Only the initial no-service/no-pairing guard reaches here. Stop this fixture's
    # replacement service after its first check-in, then exercise real MSI recovery.
    $oldPid=(Get-CimInstance Win32_Service -Filter "Name='OptimisarrSidecar'").ProcessId
    $fault=Start-ThreadJob -ArgumentList $oldPid,$heartbeats -ScriptBlock {
        param($oldPid,$heartbeats)
        $deadline=[DateTime]::UtcNow.AddMinutes(3)
        while([DateTime]::UtcNow -lt $deadline){
            $service=Get-CimInstance Win32_Service -Filter "Name='OptimisarrSidecar'"
            if($service.State -eq 'Running' -and $service.ProcessId -ne $oldPid){
                $process=Get-Process -Id $service.ProcessId
                $beats=@(Get-Content $heartbeats | Where-Object {[DateTime]::Parse($_).ToUniversalTime() -ge $process.StartTime.ToUniversalTime()})
                if($beats.Count){Start-Sleep -Seconds 6;Stop-Service OptimisarrSidecar;return 'Owned replacement stopped after first check-in.'}
            }
            Start-Sleep -Seconds 1
        }
        throw 'Delayed crash fixture did not find the replacement worker.'
    }
    try {
        $recovery=Invoke-CheckedUpdate $upgradePath $installerPath 'checked-recovery' 1
        if($recovery.State -ne 'Recovered' -or $recovery.Recovery.Health.CheckIns -lt 3 -or !$recovery.PairingUnchanged -or !$recovery.Recovery.TrayRestored){throw 'Failed update did not recover the whole previous MSI, pairing and sustained service/tray health.'}
        $faultEvidence=Receive-Job $fault -Wait -ErrorAction Stop
        if($faultEvidence -ne 'Owned replacement stopped after first check-in.'){throw 'Delayed failure was not exercised.'}
        $faultEvidence | Set-Content (Join-Path $logRoot 'delayed-failure.txt')
    }
    finally{Stop-Job $fault;Remove-Job $fault -Force}
    Get-Process -Name Optimisarr.Sidecar.Tray -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq (Join-Path $directory 'Optimisarr.Sidecar.Tray.exe')} | Stop-Process -Force
    # This is a test sentinel, never a real credential. The initial guard proves this directory is ours.
    $sentinel = Join-Path $pairing 'installer-test-sentinel.txt'
    'retain settings on uninstall' | Set-Content $sentinel
    Invoke-Msi '/x' 'uninstall' $currentInstaller
    $installed = $false
    if (Get-Service OptimisarrSidecar -ErrorAction SilentlyContinue) { throw 'Service left behind' }
    if (Test-Path (Join-Path $directory 'Optimisarr.Sidecar.Tray.exe')) { throw 'Tray binary left behind' }
    if (!(Test-Path $sentinel)) { throw 'Uninstall removed retained data' }
    if ((Get-FileHash $pairingFile).Hash -ne $pairingHash) { throw 'Uninstall changed retained pairing' }
    Remove-Item $sentinel
    Write-Output "Fresh install, sustained paired upgrades, delayed failure and complete MSI recovery, native rendering and uninstall passed. Evidence: $logRoot"
}
catch {
    $failure = $_
    # Capture before uninstall removes the registration. Only the guarded disposable test
    # reaches here; no production pairing or machine diagnostics are collected.
    try {
        Get-CimInstance Win32_Service -Filter "Name='OptimisarrSidecar'" |
            Select-Object Name, State, StartName, PathName, ProcessId, ExitCode |
            ConvertTo-Json | Set-Content (Join-Path $logRoot 'service-state.json')
        foreach ($eventLog in @(
            @{ Name = 'System'; Providers = @('Service Control Manager') },
            @{ Name = 'Application'; Providers = @('.NET Runtime', 'Application Error', 'OptimisarrSidecar') }
        )) {
            Get-WinEvent -FilterHashtable @{
                LogName = $eventLog.Name
                ProviderName = $eventLog.Providers
                StartTime = $testStarted
            } -MaxEvents 40 -ErrorAction SilentlyContinue |
                Select-Object TimeCreated, Id, ProviderName, Message |
                ConvertTo-Json -Depth 4 | Set-Content (Join-Path $logRoot ($eventLog.Name + '-events.json'))
        }
    }
    catch { Write-Warning "Could not capture every installer diagnostic: $_" }
    throw $failure
}
finally {
    try { if ($installed) { Invoke-Msi '/x' 'cleanup' $currentInstaller } }
    finally {
        if ($listener) { $listener.Close() }
        if ($serverJob) {
            Stop-Job $serverJob
            try { Receive-Job $serverJob -ErrorAction Continue | Out-File (Join-Path $logRoot 'test-server.log') }
            finally { Remove-Job $serverJob -Force }
        }
        # The initial guard proves this directory belonged only to this disposable test.
        if (!(Get-Service OptimisarrSidecar -ErrorAction SilentlyContinue)) {
            Remove-Item $pairing -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
