<#
.SYNOPSIS
    Fetches the FFmpeg this sidecar ships with, and refuses to accept one that cannot do the job.

.DESCRIPTION
    The sidecar bundles FFmpeg rather than hoping the machine has one. A worker is only ever offered
    work matching what it proved it can do, so an absent or feature-poor FFmpeg does not fail
    loudly — it produces a worker that is quietly never given anything, which is far harder to
    diagnose than a broken build.

    Windows is fetched rather than compiled, unlike macOS. Building FFmpeg on Windows means
    MSYS2/MinGW64, and the capabilities that matter here are all present in BtbN's GPL builds
    already. Optimisarr is GPL-3.0, so a GPL build is licence-compatible.

    GPU VMAF is deliberately absent and cannot be fixed by choosing a different build. libvmaf_cuda
    requires libvmaf compiled with CUDA and FFmpeg configured --enable-nonfree, and a nonfree binary
    cannot be redistributed by anyone. This sidecar therefore reports CPU VMAF; NVENC encoding and
    CUDA decoding are unaffected and both work.

.NOTES
    The release is pinned and hash-checked. BtbN's 'latest' tag moves, and a bundled toolchain that
    changes underneath a release is a capability set nobody chose.
#>
[CmdletBinding()]
param(
    # Where ffmpeg.exe and ffprobe.exe are put. Matches the macOS sidecar's vendor/ convention.
    [string] $Destination = (Join-Path $PSScriptRoot '..\vendor'),
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Release = 'autobuild-2026-09-14-13-17'
$Asset   = 'ffmpeg-n8.1.2-52-g5a03dfa0f6-win64-gpl-8.1.zip'
$Sha256  = 'F42DCA81BDCCE7CCAB00BDE91CECEB96685BEAB97C42A77C003C90771B47EB17'
$Url     = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$Release/$Asset"

# Every one of these was verified present in the pinned build. The check below is fatal: a
# capability silently missing is a worker that is never offered the work it exists to do, and the
# macOS sidecar shipped two broken releases because an equivalent check was only advisory.
$RequiredEncoders = @('hevc_nvenc', 'h264_nvenc', 'av1_nvenc', 'libx265', 'libsvtav1')
$RequiredFilters  = @('libvmaf')
$RequiredDecoders = @('hevc_cuvid', 'h264_cuvid')

$Destination = [System.IO.Path]::GetFullPath($Destination)
$ffmpeg = Join-Path $Destination 'ffmpeg.exe'

if ((Test-Path $ffmpeg) -and -not $Force) {
    Write-Host "Already present: $ffmpeg (use -Force to refetch)"
    exit 0
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("optimisarr-ffmpeg-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    $zip = Join-Path $work 'ffmpeg.zip'
    Write-Host "Fetching $Asset ..."
    $previous = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'   # the progress bar makes this many times slower
    try {
        Invoke-WebRequest -Uri $Url -OutFile $zip -TimeoutSec 600
    }
    finally {
        $ProgressPreference = $previous
    }

    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($actual -ne $Sha256) {
        throw "Downloaded archive does not match the pinned hash.`n  expected $Sha256`n  actual   $actual"
    }

    Expand-Archive -Path $zip -DestinationPath $work -Force
    $found = Get-ChildItem -Path $work -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
    if (-not $found) {
        throw "The archive contained no ffmpeg.exe."
    }

    $bin = $found.Directory.FullName
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($tool in @('ffmpeg.exe', 'ffprobe.exe')) {
        Copy-Item -Path (Join-Path $bin $tool) -Destination (Join-Path $Destination $tool) -Force
    }

    # Retain the upstream copyright notices, licences and build documentation alongside the
    # binaries. Corresponding source archives are supplied separately with public releases.
    $noticeDirectory = Join-Path $Destination 'media-notices'
    New-Item -ItemType Directory -Force -Path $noticeDirectory | Out-Null
    Get-ChildItem -LiteralPath $found.Directory.Parent.FullName | Where-Object { $_.Name -ne 'bin' } |
        Copy-Item -Destination $noticeDirectory -Recurse -Force

    # Proved by running it, never read off a listing of what the build was meant to contain.
    Write-Host "Verifying capabilities ..."
    $encoders = & $ffmpeg -hide_banner -encoders 2>&1 | Out-String
    $filters  = & $ffmpeg -hide_banner -filters  2>&1 | Out-String
    $decoders = & $ffmpeg -hide_banner -decoders 2>&1 | Out-String

    $missing = @()
    $missing += $RequiredEncoders | Where-Object { $encoders -notmatch "\b$_\b" }
    $missing += $RequiredFilters  | Where-Object { $filters  -notmatch "\b$_\b" }
    $missing += $RequiredDecoders | Where-Object { $decoders -notmatch "\b$_\b" }

    if ($missing.Count -gt 0) {
        Remove-Item -Path $Destination -Recurse -Force -ErrorAction SilentlyContinue
        throw ("This FFmpeg cannot do what the sidecar needs. Missing: " + ($missing -join ', ') +
               "`nThe bundle has been removed rather than left in place: a sidecar that ships a " +
               "toolchain it cannot use is a worker the server never offers anything to.")
    }

    $version = (& $ffmpeg -hide_banner -version 2>&1 | Select-Object -First 1)
    @(
        "release: $Release"
        "asset:   $Asset"
        "sha256:  $Sha256"
        "version: $version"
        "fetched: $([DateTimeOffset]::UtcNow.ToString('u'))"
        "proved:  " + (($RequiredEncoders + $RequiredFilters + $RequiredDecoders) -join ', ')
        "note:    GPU VMAF (libvmaf_cuda) is absent by necessity, not oversight: it needs"
        "         --enable-nonfree, and a nonfree binary cannot be redistributed. CPU VMAF,"
        "         NVENC encoding and CUDA decoding are all present."
    ) | Set-Content -Path (Join-Path $Destination 'BUILD-INFO.txt') -Encoding utf8

    Write-Host "Bundled $version"
    Write-Host "  -> $Destination"
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
