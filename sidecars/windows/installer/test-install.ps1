[CmdletBinding()]
param([Parameter(Mandatory)][string] $Installer)
$ErrorActionPreference = 'Stop'
$pairing = Join-Path $env:ProgramData 'Optimisarr\Sidecar'
if ((Get-Service OptimisarrSidecar -ErrorAction SilentlyContinue) -or (Test-Path $pairing)) {
    throw 'Run installer smoke tests only on a disposable Windows VM with no existing sidecar or pairing.'
}
$installerPath = (Resolve-Path $Installer).Path
$logRoot = Join-Path $env:TEMP ('optimisarr-install-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $logRoot | Out-Null
function Invoke-Msi([string] $Action, [string] $Name) {
    $process = Start-Process msiexec.exe -ArgumentList @($Action, ('"'+$installerPath+'"'), '/qn', '/norestart', '/l*v', ('"'+$logRoot+'\'+$Name+'.log"')) -Wait -PassThru
    if ($process.ExitCode -notin @(0,3010)) { throw "MSI $Name failed: $($process.ExitCode). Logs: $logRoot" }
}
$installed = $false
try {
    Invoke-Msi '/i' 'install'
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
    # This is a test sentinel, never a real credential. The initial guard proves this directory is ours.
    $sentinel = Join-Path $pairing 'installer-test-sentinel.txt'
    'retain settings on uninstall' | Set-Content $sentinel
    Invoke-Msi '/x' 'uninstall'
    $installed = $false
    if (Get-Service OptimisarrSidecar -ErrorAction SilentlyContinue) { throw 'Service left behind' }
    if (Test-Path (Join-Path $directory 'Optimisarr.Sidecar.Tray.exe')) { throw 'Tray binary left behind' }
    if (!(Test-Path $sentinel)) { throw 'Uninstall removed retained data' }
    Remove-Item $sentinel
    Write-Output "Install, native rendering and uninstall passed. Evidence: $logRoot"
}
finally {
    if ($installed) { Invoke-Msi '/x' 'cleanup' }
}
