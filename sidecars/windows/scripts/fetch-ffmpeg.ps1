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

    This redistributable GPL build provides CPU VMAF v1, NVENC encoding and CUDA decoding.
    Full v1 CUDA scoring is not implemented upstream: banding/chroma extractors are missing.
    Some CUDA build routes use --enable-nonfree and are not distributable; libvmaf_cuda itself
    does not inherently require that option. A future build needs a complete feature, artifact
    and licensing audit, rather than assuming any CUDA SDK combination is safe to ship.

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

$Release = 'autobuild-2026-09-30-13-08'
$Asset   = 'ffmpeg-n8.1.3-9-g29e619e767-win64-gpl-8.1.zip'
$Sha256  = '7B801CDD3A1A0BB54AE6F572187E68B4ED54F52086CFEE133FAB2180BFB429FA'
$Url     = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$Release/$Asset"
# Upstream autobuild releases expire. The older MSI is not this v1 toolchain; refuse it rather
# than silently downgrading measurement to the libvmaf 3.2.0 memory regression.

# Every one of these was verified present in the pinned build. The check below is fatal: a
# capability silently missing is a worker that is never offered the work it exists to do, and the
# macOS sidecar shipped two broken releases because an equivalent check was only advisory.
$RequiredEncoders = @('hevc_nvenc', 'h264_nvenc', 'av1_nvenc', 'libx265', 'libsvtav1')
$RequiredFilters  = @('libvmaf')
$RequiredDecoders = @('hevc_cuvid', 'h264_cuvid')

$Destination = [System.IO.Path]::GetFullPath($Destination)
$ffmpeg = Join-Path $Destination 'ffmpeg.exe'

if ((Test-Path $ffmpeg) -and -not $Force) {
    $buildInfo = Join-Path $Destination 'BUILD-INFO.txt'
    if (-not (Test-Path $buildInfo) -or (Get-Content $buildInfo -Raw) -notmatch [regex]::Escape($Sha256)) {
        throw 'Existing FFmpeg does not match the pinned v1 bundle. Re-run with -Force to upgrade.'
    }
    Write-Host "Already present: $ffmpeg (pinned bundle; use -Force to refetch)"
    exit 0
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("optimisarr-ffmpeg-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    $zip = Join-Path $work 'ffmpeg.zip'
    Write-Host "Fetching $Asset ..."
    $previous = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'   # the progress bar makes this many times slower
    $provenance = $Url
    try {
        try {
            Invoke-WebRequest -Uri $Url -OutFile $zip -TimeoutSec 600
        }
        catch {
            # A transient network error or hash mismatch must not silently select another input.
            if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
            throw 'Pinned VMAF v1 bundle expired upstream. Publish and pin a matching tool/source mirror; do not use the older v0 MSI.'
        }
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
        throw 'The verified package contained no ffmpeg.exe.'
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

    try {
        foreach ($model in @('vmaf_v1.0.16_3d0h', 'vmaf_v1.0.16_1d5h_2160')) {
            $scorePath = Join-Path $work "$model.json"
            $escapedLog = $scorePath.Replace('\', '/').Replace(':', '\\:')
            $graph = "[0:v][1:v]libvmaf=model='version=$model\:cambi.enc_width=320\:cambi.enc_height=240\:cambi.enc_bitdepth=10':n_threads=1:log_fmt=json:log_path=${escapedLog}:shortest=1:repeatlast=0"
            $arguments = @('-nostdin', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=s=320x240:r=24:d=0.25,format=yuv420p10le',
                '-f', 'lavfi', '-i', 'testsrc2=s=320x240:r=24:d=0.25,format=yuv420p10le', '-lavfi', $graph, '-f', 'null', '-')
            $probeOutput = & $ffmpeg @arguments 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path $scorePath)) { throw "VMAF model probe failed: $model`n$probeOutput" }
            $scores = Get-Content $scorePath -Raw | ConvertFrom-Json
            if ($scores.frames.Count -ne 6) {
                throw 'The bundle must produce complete frame scores for both VMAF v1 models.'
            }
            foreach ($frame in $scores.frames) {
                $score = [double]$frame.metrics.vmaf
                if ([double]::IsNaN($score) -or [double]::IsInfinity($score) -or $score -lt 0 -or $score -gt 100) {
                    throw "Non-finite or invalid VMAF probe score: $model"
                }
            }
        }
    }
    catch {
        Remove-Item -Path $Destination -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }

    $version = (& $ffmpeg -hide_banner -version 2>&1 | Select-Object -First 1)
    @(
        "release: $Release"
        "asset:   $Asset"
        "upstream archive sha256: $Sha256"
        "verified package: $provenance"
        "version: $version"
        "fetched: $([DateTimeOffset]::UtcNow.ToString('u'))"
        "proved:  " + (($RequiredEncoders + $RequiredFilters + $RequiredDecoders) -join ', ')
        "vmaf source: 86da14d0306a138fd3f01319860b905169746516 (v3.2.1 + 8 upstream commits)"
        "proved:  VMAF v1 HD/UHD, 10-bit measurement and finite frame scores"
        "note:    Full v1 CUDA scoring needs missing upstream feature extractors and a separate"
        "         redistributable artifact/license audit. CPU v1, NVENC and CUDA decode are present."
    ) | Set-Content -Path (Join-Path $Destination 'BUILD-INFO.txt') -Encoding utf8

    Write-Host "Bundled $version"
    Write-Host "  -> $Destination"
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
