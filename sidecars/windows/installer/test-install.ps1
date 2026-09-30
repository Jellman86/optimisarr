[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Installer,
    [Parameter(Mandatory)][string] $UpgradeInstaller
)
$ErrorActionPreference = 'Stop'
$pairing = Join-Path $env:ProgramData 'Optimisarr\Sidecar'
if ((Get-Service OptimisarrSidecar -ErrorAction SilentlyContinue) -or (Test-Path $pairing)) {
    throw 'Run installer smoke tests only on a disposable Windows VM with no existing sidecar or pairing.'
}
$installerPath = (Resolve-Path $Installer).Path
$upgradePath = (Resolve-Path $UpgradeInstaller).Path
$logRoot = Join-Path $env:TEMP ('optimisarr-install-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $logRoot | Out-Null
function Invoke-Msi([string] $Action, [string] $Name, [string] $Path) {
    $process = Start-Process msiexec.exe -ArgumentList @($Action, ('"'+$Path+'"'), '/qn', '/norestart', '/l*v', ('"'+$logRoot+'\'+$Name+'.log"')) -Wait -PassThru
    if ($process.ExitCode -notin @(0,3010)) { throw "MSI $Name failed: $($process.ExitCode). Logs: $logRoot" }
}
function Get-ProductCode([string] $Path) {
    $engine = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    try {
        $database = $engine.OpenDatabase($Path, 0)
        $view = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property`=''ProductCode''')
        $view.Execute()
        $record = $view.Fetch()
        try { return $record.StringData(1) }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
    }
    finally {
        if ($view) { $view.Close(); [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
        if ($database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine)
    }
}
function Wait-ForCheckIn([string] $Evidence, [DateTime] $After) {
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Get-Service OptimisarrSidecar).Status -eq 'Running' -and (Test-Path $Evidence)) {
            $beats = @(Get-Content $Evidence | Where-Object { [DateTime]::Parse($_).ToUniversalTime() -gt $After })
            if ($beats.Count -gt 0) { return }
        }
        Start-Sleep -Seconds 1
    }
    throw "Worker did not check in after upgrade/start. Evidence: $logRoot"
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
    $directory = Join-Path $env:ProgramFiles 'Optimisarr Sidecar'
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
        Wait-ForCheckIn $heartbeats ([DateTime]::UtcNow)
        Write-Output "$($upgrade.Name): service restarted and checked in; pairing unchanged. Started $before"
    }
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
    Write-Output "Fresh install, unpaired/paired/repeated upgrades, check-in, native rendering and uninstall passed. Evidence: $logRoot"
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
