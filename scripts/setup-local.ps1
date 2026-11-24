# Creates development-only secrets and accounts. Existing database data is preserved.
$ErrorActionPreference = 'Continue'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$previousDefault = $env:ConnectionStrings__Default
$previousMigrations = $env:ConnectionStrings__Migrations
$previousPassword = $env:WEDDING_TEMP_PASSWORD
try {
    New-Item -ItemType Directory -Force -Path '.local' | Out-Null
    $localConfigPath = 'src/Wedding.Api/appsettings.Development.Local.json'
    if (-not (Test-Path -LiteralPath $localConfigPath)) {
        if (Test-Path -LiteralPath '.env') {
            $passwordLine = Get-Content -LiteralPath '.env' | Where-Object { $_ -match '^POSTGRES_PASSWORD=' } | Select-Object -First 1
            if (-not $passwordLine) { throw 'Existing .env has no POSTGRES_PASSWORD. Add it before running local setup.' }
            $databasePassword = $passwordLine.Substring('POSTGRES_PASSWORD='.Length)
        } else {
            $databasePassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
            Set-Content -LiteralPath '.env' -Value "POSTGRES_PASSWORD=$databasePassword" -Encoding ASCII
        }
        $connection = "Host=localhost;Port=55432;Database=wedding;Username=wedding;Password=$databasePassword;Maximum Pool Size=10"
        @{ ConnectionStrings = @{ Default = $connection; Migrations = $connection } } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $localConfigPath -Encoding UTF8
    }
    if (-not (Test-Path -LiteralPath '.local/accounts.json')) {
        @(
            @{ email = 'bride@localhost.test'; name = 'Local Bride'; password = ('Aa1!' + [Guid]::NewGuid().ToString('N')) },
            @{ email = 'groom@localhost.test'; name = 'Local Groom'; password = ('Aa1!' + [Guid]::NewGuid().ToString('N')) }
        ) | ConvertTo-Json | Set-Content -LiteralPath '.local/accounts.json' -Encoding UTF8
    }
    docker compose up -d --wait db
    if ($LASTEXITCODE -ne 0) { throw 'Local PostgreSQL failed to start. Check Docker and port 55432.' }
    dotnet restore Wedding.sln
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    dotnet build src/Wedding.Api --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'API build failed.' }
    $localConfig = Get-Content -LiteralPath $localConfigPath -Raw | ConvertFrom-Json
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ConnectionStrings__Default = $localConfig.ConnectionStrings.Default
    $env:ConnectionStrings__Migrations = $localConfig.ConnectionStrings.Migrations
    dotnet run --project src/Wedding.Api --no-build --no-launch-profile -- db migrate
    if ($LASTEXITCODE -ne 0) { throw 'Local migrations failed.' }
    foreach ($account in (Get-Content -LiteralPath '.local/accounts.json' -Raw | ConvertFrom-Json)) {
        # Emails generated above are restricted before use in the administrative lookup.
        if ($account.email -notmatch '^[a-z]+@localhost\.test$') { throw 'Unexpected local account email.' }
        $query = 'SELECT count(*) FROM "AspNetUsers" WHERE "Email" = ' + "'$($account.email)';"
        $count = $query | docker compose exec -T db psql -U wedding -d wedding -tA
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect local accounts.' }
        if (($count -join '').Trim() -eq '0') {
            $env:WEDDING_TEMP_PASSWORD = $account.password
            dotnet run --project src/Wedding.Api --no-build --no-launch-profile -- users create $account.email $account.name
            if ($LASTEXITCODE -ne 0) { throw 'Local account provisioning failed.' }
        }
    }
    Write-Output 'Local setup complete. API: http://localhost:5080. Private local credentials: .local/accounts.json.'
} finally {
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    $env:ConnectionStrings__Default = $previousDefault
    $env:ConnectionStrings__Migrations = $previousMigrations
    $env:WEDDING_TEMP_PASSWORD = $previousPassword
    Pop-Location
}
