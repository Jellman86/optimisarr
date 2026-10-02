[CmdletBinding()]
param([string] $SmokeScript = (Join-Path $PSScriptRoot 'test-install.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($SmokeScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$definition = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Msi' }, $true))
if ($definition.Count -ne 1) { throw 'MSI test runner missing.' }
. ([scriptblock]::Create($definition[0].Extent.Text))
Add-Type @'
public class OptimisarrInstallerTestProcess {
    public int ExitCode;
    public bool TimedOut, Killed, Disposed;
    public int WaitMilliseconds;
    public bool WaitForExit(int milliseconds) { WaitMilliseconds = milliseconds; return !TimedOut; }
    public void Kill() { Killed = true; }
    public void Dispose() { Disposed = true; }
}
'@
$logRoot = Join-Path $env:TEMP ('optimisarr-process-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $logRoot | Out-Null
$childPidFile = Join-Path $logRoot 'owned-child.pid'
$shell = (Get-Process -Id $PID).Path
function Encode([string] $Text) { [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Text)) }
$childCode = Encode ("[IO.File]::WriteAllText('$childPidFile', [string]`$PID); Start-Sleep -Seconds 60")
$parentCode = Encode ("Microsoft.PowerShell.Management\Start-Process '$shell' -ArgumentList @('-NoProfile', '-EncodedCommand', '$childCode') -PassThru | Out-Null; for (`$n=0; `$n -lt 100 -and !(Test-Path '$childPidFile'); `$n++) { Start-Sleep -Milliseconds 100 }; exit 0")
$realProcess = $false
$fake = $null
function Start-Process {
    param($FilePath, $ArgumentList, [switch] $Wait, [switch] $PassThru)
    if ($Wait) { throw 'The installer harness waits for long-running service descendants.' }
    if ($realProcess) {
        return Microsoft.PowerShell.Management\Start-Process $shell -ArgumentList @('-NoProfile', '-EncodedCommand', $parentCode) -PassThru
    }
    return $fake
}
try {
    # No MSI or service is touched. A real short parent launches an owned long-lived child.
    $realProcess = $true
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-Msi '/i' 'owned-process' 'unused-test.msi'
    if ($watch.Elapsed.TotalSeconds -gt 15 -or !(Test-Path $childPidFile)) { throw 'Parent-only wait did not complete promptly.' }
    $child = Get-Process -Id ([int](Get-Content $childPidFile)) -ErrorAction SilentlyContinue
    if (!$child) { throw 'Installer completion waited for or killed the worker descendant.' }
    $realProcess = $false
    foreach ($code in @(0,3010,1603)) {
        $fake = [OptimisarrInstallerTestProcess]::new()
        $fake.ExitCode = $code
        $observed = $null
        try { Invoke-Msi '/i' 'exit-code' 'unused-test.msi' } catch { $observed = $_.Exception.Message }
        if ($code -eq 1603 -and $observed -notlike '*1603*') { throw 'Installer failure was lost.' }
        if ($code -ne 1603 -and $observed) { throw $observed }
        if (!$fake.Disposed -or $fake.Killed -or $fake.WaitMilliseconds -le 0 -or $fake.WaitMilliseconds -gt 600000) { throw 'Installer exit wait is unbounded or leaks its process handle.' }
    }
    $fake = [OptimisarrInstallerTestProcess]::new()
    $fake.TimedOut = $true
    $observed = $null
    try { Invoke-Msi '/i' 'timeout' 'unused-test.msi' } catch { $observed = $_.Exception.Message }
    if ($observed -notlike '*timed out*' -or !$fake.Killed -or !$fake.Disposed) { throw 'Installer timeout did not stop its owned process and retain the failure.' }
    Write-Output 'Parent completion with a live child, installer exit codes and bounded timeout passed.'
}
finally {
    if (Test-Path $childPidFile) { Stop-Process -Id ([int](Get-Content $childPidFile)) -ErrorAction SilentlyContinue }
    Remove-Item $logRoot -Recurse -Force
}
