$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    dotnet run --project tools/Wedding.WebhookRelay --no-build --no-launch-profile
    if ($LASTEXITCODE -ne 0) { throw 'The local webhook relay stopped.' }
} finally { Pop-Location }
