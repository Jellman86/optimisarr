[CmdletBinding()]
param(
    [string] $Version = '',
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts'),
    [string] $Python = 'python'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be X.Y.Z' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$work = Join-Path ([IO.Path]::GetTempPath()) ('optimisarr-msi-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
New-Item -ItemType Directory -Force -Path $payload, $output | Out-Null
$runtimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-win-x64.zip'
$runtimeHash = '844fa99e16fd6f44e0a7c29def7a82d7846902334d6a955248a9519a4dddb3f5acceb9c9223bef69f8c83b8ae2417537e5b76dddf79fb7117dc85b5039bc1297'
$wixVersion = '4.0.6'
function Assert-Exit([string] $Operation) {
    if ($LASTEXITCODE -ne 0) { throw "$Operation failed with exit code $LASTEXITCODE" }
}
try {
    dotnet publish (Join-Path $repo 'sidecars\windows\src\Optimisarr.Sidecar.Service\Optimisarr.Sidecar.Service.csproj') -c Release -warnaserror -r win-x64 --self-contained false -p:UseAppHost=false "-p:Version=$Version" -o $payload
    Assert-Exit 'Service publish'
    dotnet publish (Join-Path $repo 'sidecars\windows\src\Optimisarr.Sidecar.Tray\Optimisarr.Sidecar.Tray.csproj') -c Release -warnaserror -r win-x64 --self-contained false -p:AppHostRelativeDotNet=runtime "-p:Version=$Version" -o $payload
    Assert-Exit 'Tray publish' 
    # Fetch afresh into the owned staging directory: a pre-existing developer vendor directory
    # is not evidence that a redistributable binary matches the pinned archive.
    & (Join-Path $PSScriptRoot '..\scripts\fetch-ffmpeg.ps1') -Destination $payload -Force
    Assert-Exit 'Media tool staging'
    $zip = Join-Path $work 'runtime.zip'
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest $runtimeUrl -OutFile $zip -TimeoutSec 600
    if ((Get-FileHash $zip -Algorithm SHA512).Hash -ne $runtimeHash) { throw 'Runtime checksum mismatch' }
    Expand-Archive $zip (Join-Path $payload 'runtime')
    $desktopZip = Join-Path $work 'desktop-runtime.zip'
    Invoke-WebRequest 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-x64.zip' -OutFile $desktopZip -TimeoutSec 600
    if ((Get-FileHash $desktopZip -Algorithm SHA512).Hash -ne '8b79f679c348aa08a10ee4e1ca75be54b135ae951b7328400fea8499f9eb8450ccb18f9f708af6182b2bcd88fcca719aa282939e907b017422f519de8dd42534') { throw 'Desktop runtime checksum mismatch' }
    Expand-Archive $desktopZip (Join-Path $payload 'runtime') -Force
    & (Join-Path $payload 'runtime\dotnet.exe') --list-runtimes
    Assert-Exit 'Private runtime validation'
    Copy-Item (Join-Path $PSScriptRoot 'Getting started.html') $payload
    Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $payload 'LICENSE.txt')
    @"
Optimisarr Sidecar ${Version}: GPL-3.0. Source: https://github.com/Jellman86/optimisarr
FFmpeg: GPL build pinned in BUILD-INFO.txt. Copyright the FFmpeg contributors.
Build scripts and corresponding upstream build-source release:
https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-09-14-13-17
https://github.com/BtbN/FFmpeg-Builds
https://ffmpeg.org/legal.html
.NET runtime 10.0.12: Microsoft and contributors. MIT and third-party licences
are included inside runtime/LICENSE.txt and runtime/ThirdPartyNotices.txt.
Runtime source: https://github.com/dotnet/runtime/tree/v10.0.12
This Windows preview MSI is unsigned. Upstream licences are in media-notices.
The matching release supplies checksums and exact corresponding media sources:
https://github.com/Jellman86/optimisarr/releases/tag/v${Version}
"@ | Set-Content (Join-Path $payload 'THIRD-PARTY-NOTICES.txt')
    $license = Get-Content (Join-Path $repo 'LICENSE') -Raw
    $rtf = '{\rtf1\ansi\deff0 {\fonttbl {\f0 Segoe UI;}}\f0\fs18 ' + $license.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}').Replace("`n", '\par ') + '}'
    $licensePath = Join-Path $work 'license.rtf'
    $rtf | Set-Content $licensePath -Encoding ascii
    $generated = Join-Path $work 'Payload.wxs'
    & $Python (Join-Path $PSScriptRoot 'generate_payload.py') $payload $generated
    Assert-Exit 'Payload generation'
    $tool = Join-Path $work 'tools'
    dotnet tool install wix --version $wixVersion --tool-path $tool --allow-roll-forward
    Assert-Exit 'WiX setup'
    $wix = Join-Path $tool 'wix.exe'
    & $wix extension add "WixToolset.UI.wixext/$wixVersion"
    Assert-Exit 'WiX UI setup'
    $msi = Join-Path $output "OptimisarrSidecar-$Version-win-x64.msi"
    & $wix build (Join-Path $PSScriptRoot 'Package.wxs') $generated -arch x64 -ext WixToolset.UI.wixext -d "Version=$Version" -d "Payload=$payload" -d "LicenseRtf=$licensePath" -o $msi
    Assert-Exit 'MSI build and validation'
    (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($msi) | Set-Content "$msi.sha256"
    Write-Host "Built $msi"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
