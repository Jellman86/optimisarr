[CmdletBinding()]
param([string] $SmokeScript = (Join-Path $PSScriptRoot 'test-install.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($SmokeScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$protectedRun = @($ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] })
if ($protectedRun.Count -ne 1 -or $protectedRun[0].CatchClauses.Count -ne 1) {
    throw 'Installer failures must capture diagnostics before cleanup.'
}
$body = $protectedRun[0].CatchClauses[0].Body.Extent.Text
$diagnostics = [scriptblock]::Create($body.Substring(1, $body.Length - 2))
$root = Join-Path $env:TEMP ('optimisarr-evidence-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
try {
    # Simulated SCM failures exercise only the actual catch block. No service, MSI,
    # event log or pairing is touched, so this is safe on an installed worker host.
    function Get-CimInstance {
        param($ClassName, $Filter)
        [pscustomobject]@{ Name='OptimisarrSidecar'; State='Stopped'; ExitCode=1053 }
    }
    function Get-WinEvent {
        param($FilterHashtable, $MaxEvents, $ErrorAction)
        if ($diagnosticUnavailable) { throw 'Diagnostic provider unavailable' }
        [pscustomobject]@{ TimeCreated=$testStarted; Id=7000; ProviderName='Service Control Manager'; Message='Fabricated startup failure' }
    }
    foreach ($diagnosticUnavailable in @($false, $true)) {
        $logRoot = Join-Path $root $diagnosticUnavailable.ToString()
        New-Item -ItemType Directory $logRoot | Out-Null
        $testStarted = [DateTime]::Now
        $observed = $null
        try {
            try { throw 'Original service startup failure' }
            catch { & $diagnostics }
        }
        catch { $observed = $_.Exception.Message }
        if ($observed -ne 'Original service startup failure') {
            throw "Diagnostics swallowed or replaced the original failure: $observed"
        }
        $state = Get-Content (Join-Path $logRoot 'service-state.json') -Raw | ConvertFrom-Json
        if ($state.ExitCode -ne 1053) { throw 'Service failure state was not retained.' }
        if (!$diagnosticUnavailable) {
            foreach ($name in @('System', 'Application')) {
                $events = Get-Content (Join-Path $logRoot ($name+'-events.json')) -Raw | ConvertFrom-Json
                if ($events.Message -ne 'Fabricated startup failure') { throw 'Startup event evidence was not retained.' }
            }
        }
    }
    Write-Output 'Installer failure evidence and original-error preservation passed.'
}
finally { Remove-Item $root -Recurse -Force }
