# Uses the sibling React checkout without editing its files or .env.
$ErrorActionPreference = 'Stop'
$frontendPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'wedding-online'
if (-not (Test-Path (Join-Path $frontendPath 'package.json'))) { throw 'Expected the React project at ../wedding-online.' }
$previousMode = $env:VITE_GIFTS_MODE
$previousApi = $env:VITE_API_BASE_URL
Push-Location $frontendPath
try {
    $env:VITE_GIFTS_MODE = 'api'
    $env:VITE_API_BASE_URL = 'http://localhost:5080'
    npm.cmd run dev -- --host localhost --port 5173 --strictPort
    if ($LASTEXITCODE -ne 0) { throw 'Frontend stopped with an error.' }
} finally {
    $env:VITE_GIFTS_MODE = $previousMode
    $env:VITE_API_BASE_URL = $previousApi
    Pop-Location
}
