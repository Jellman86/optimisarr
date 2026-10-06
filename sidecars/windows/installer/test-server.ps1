param(
    [Parameter(Mandatory)][Net.HttpListener] $Listener,
    [Parameter(Mandatory)][string] $Credential,
    [Parameter(Mandatory)][string] $HeartbeatEvidence
)
$ErrorActionPreference = 'Stop'
$lastCheckIn=$null
$version=$null
$drainId=[DateTime]::UtcNow.ToString('O')
while ($Listener.IsListening) {
    try { $context = $Listener.GetContext() }
    catch [Net.HttpListenerException] { break }
    catch [ObjectDisposedException] { break }
    try {
        $reader = [IO.StreamReader]::new($context.Request.InputStream)
        try { $body = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $response = $context.Response
        $response.ContentType = 'application/json'
        $json = '{}'
        if ($context.Request.HttpMethod -eq 'GET' -and $context.Request.Url.AbsolutePath -eq '/api/workers') {
            $json=ConvertTo-Json -InputObject @(@{id=1; sidecarVersion=$version; online=($null -ne $lastCheckIn); lastSeenAt=$lastCheckIn; heldLeases=0; drainRequestedAt=$drainId}) -Compress
        }
        elseif ($context.Request.HttpMethod -ne 'POST') {
            $response.StatusCode = 405
        }
        elseif ($context.Request.Url.AbsolutePath -eq '/api/workers/pair' -and ($body | ConvertFrom-Json).code -eq '00000000') {
            $json = @{ workerId = 1; credential = $Credential; protocolVersion = 4 } | ConvertTo-Json -Compress
        }
        elseif ($context.Request.Headers['Authorization'] -ne "Bearer $Credential") {
            $response.StatusCode = 401
        }
        elseif ($context.Request.Url.AbsolutePath -eq '/api/workers/heartbeat') {
            $lastCheckIn=[DateTime]::UtcNow.ToString('O')
            $version=($body | ConvertFrom-Json).sidecarVersion
            $json = @{ workerId = 1; protocolVersion = 4; serverTimeUtc = [DateTime]::UtcNow.ToString('O'); heartbeatIntervalSeconds = 5; draining = $true } | ConvertTo-Json -Compress
            [IO.File]::AppendAllText($HeartbeatEvidence, [DateTime]::UtcNow.ToString('O') + [Environment]::NewLine)
        }
        else { $response.StatusCode = 404 }
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $response.ContentLength64 = $bytes.Length
        $response.OutputStream.Write($bytes)
    }
    finally { $context.Response.Close() }
}
