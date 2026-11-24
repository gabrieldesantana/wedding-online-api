# Stops only background launchers recorded by this workspace; never removes database data.
param([switch]$StopDatabase)
$projectRoot = Split-Path -Parent $PSScriptRoot
$statePath = Join-Path $projectRoot '.local/processes.json'
if (Test-Path -LiteralPath $statePath) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    foreach ($entry in @(
        @{ Id = $state.api; Script = 'start-local.ps1' },
        @{ Id = $state.frontend; Script = 'start-frontend-local.ps1' },
        @{ Id = $state.ngrok; Script = 'start-webhook-tunnel.ps1' },
        @{ Id = $state.webhookRelay; Script = 'start-webhook-relay.ps1' }
    )) {
        if (-not $entry.Id) { continue }
        $recordedProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $([int]$entry.Id)" -ErrorAction SilentlyContinue
        $expectedScript = Join-Path $PSScriptRoot $entry.Script
        if ($recordedProcess -and $recordedProcess.CommandLine.IndexOf($expectedScript, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            taskkill.exe /PID $entry.Id /T /F | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Cannot stop the recorded local process.' }
        }
    }
}
if ($StopDatabase) {
    Push-Location $projectRoot
    try { docker compose stop db; if ($LASTEXITCODE -ne 0) { throw 'Cannot stop local PostgreSQL.' } }
    finally { Pop-Location }
}
