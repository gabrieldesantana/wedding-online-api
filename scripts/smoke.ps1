param([string]$Image = 'wedding-api:local', [switch]$SkipBuild)
# Windows PowerShell 5 reports ordinary Docker progress on stderr as NativeCommandError.
# Check native exit codes explicitly instead of treating all stderr as failure.
$ErrorActionPreference = 'Continue'
if (-not $SkipBuild) {
    docker build -t $Image .
    if ($LASTEXITCODE -ne 0) { throw 'Container build failed' }
}
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$network = "wedding-smoke-$suffix"
$databaseContainer = "wedding-smoke-db-$suffix"
$apiContainer = "wedding-smoke-api-$suffix"
$password = [Guid]::NewGuid().ToString('N')
try {
    docker network create $network | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create smoke network' }
    docker run -d --name $databaseContainer --network $network -e POSTGRES_DB=wedding -e POSTGRES_USER=wedding -e "POSTGRES_PASSWORD=$password" postgres:17-alpine | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create disposable PostgreSQL' }
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        docker exec $databaseContainer pg_isready -U wedding *> $null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Seconds 1
    }
    $connection = "Host=$databaseContainer;Database=wedding;Username=wedding;Password=$password"
    $environmentArguments = @(
        '-e', 'ASPNETCORE_ENVIRONMENT=Development',
        '-e', "ConnectionStrings__Default=$connection", '-e', "ConnectionStrings__Migrations=$connection",
        '-e', 'Frontend__PublicOrigin=https://example.test', '-e', 'Cors__AllowedOrigins__0=https://example.test',
        '-e', 'MercadoPago__AccessToken=smoke-only', '-e', 'MercadoPago__WebhookSecret=smoke-only',
        '-e', 'MercadoPago__ReceiverId=smoke-only', '-e', 'MercadoPago__ApplicationId=smoke-only',
        '-e', 'MercadoPago__NotificationUrl=https://example.test/api/webhooks/mercadopago', '-e', 'PORT=8090'
    )
    docker run --rm --network $network @environmentArguments $Image dotnet Wedding.Api.dll db migrate
    if ($LASTEXITCODE -ne 0) { throw 'Container migration failed' }
    docker run -d --name $apiContainer --network $network -p '127.0.0.1::8090' @environmentArguments $Image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot start smoke API' }
    $inspection = docker inspect $apiContainer | ConvertFrom-Json
    $publishedPort = $inspection[0].NetworkSettings.Ports.'8090/tcp'[0].HostPort
    $runtimeUser = $inspection[0].Config.User
    if ($runtimeUser -in @('', 'root', '0')) { throw 'Container must run without root privileges' }
    $ready = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            $response = Invoke-RestMethod "http://127.0.0.1:$publishedPort/health/ready" -ErrorAction Stop
            if ($response.status -eq 'ok') { $ready = $true; break }
        } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { docker logs $apiContainer --tail 10; throw 'Container readiness failed' }
    $catalog = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$publishedPort/api/gifts" -ErrorAction Stop
    if ($catalog.StatusCode -ne 200 -or $catalog.Content.Trim() -ne '[]') { throw 'Empty catalog contract failed' }
    Write-Output "Smoke passed: user $runtimeUser, migrations, PORT=8090, readiness, empty catalog."
} finally {
    # Only the uniquely named resources created by this run are removed.
    docker rm -f $apiContainer $databaseContainer 2>$null | Out-Null
    docker network rm $network 2>$null | Out-Null
}
