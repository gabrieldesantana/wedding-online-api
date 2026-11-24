param([switch]$SkipSetup)
$ErrorActionPreference = 'Stop'
if (-not $SkipSetup) { & "$PSScriptRoot/setup-local.ps1" }
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    dotnet run --project src/Wedding.Api --no-build --launch-profile Wedding.Api
    if ($LASTEXITCODE -ne 0) { throw 'API stopped with an error.' }
} finally { Pop-Location }
