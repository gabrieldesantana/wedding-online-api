# Calls only the local application. Provider creation remains subject to its verified test-account guard.
# Re-running replays the same contribution keys; it does not invent a new attempt after uncertainty.
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
$session = $null
function Invoke-LocalPost([string]$Path, $Body, [string]$Csrf, [string]$Key = '', [string]$Tracking = '') {
    $headers = @{ 'X-CSRF-Token' = $Csrf }
    if ($Key) { $headers['Idempotency-Key'] = $Key }
    if ($Tracking) { $headers['X-Contribution-Token'] = $Tracking }
    try {
        Invoke-RestMethod ("http://localhost:5080" + $Path) -Method Post -WebSession $script:session -ContentType 'application/json' -Headers $headers -Body ($Body | ConvertTo-Json -Depth 5) -TimeoutSec 20
    } catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'unavailable' }
        throw "Local operation $Path returned HTTP $status. The persisted attempt must be inspected before any new payment creation."
    }
}
try {
    $config = Get-Content 'src/Wedding.Api/appsettings.Development.Local.json' -Raw | ConvertFrom-Json
    if ($config.MercadoPago.Environment -ne 'test' -or -not $config.MercadoPago.CheckoutEnabled -or -not $config.MercadoPago.SandboxSingleAttempt) { throw 'Local single-attempt sandbox mode must be configured first.' }
    $statePath = '.local/sandbox-checkout.json'
    $csrf = Invoke-RestMethod 'http://localhost:5080/api/auth/csrf' -SessionVariable session
    $script:session = $session
    $accounts = Get-Content '.local/accounts.json' -Raw | ConvertFrom-Json
    Invoke-LocalPost '/api/auth/login' @{ email = $accounts[0].email; password = $accounts[0].password } $csrf.token | Out-Null
    $csrf = Invoke-RestMethod 'http://localhost:5080/api/auth/csrf' -WebSession $session
    if (Test-Path -LiteralPath $statePath) {
        $state = Get-Content $statePath -Raw | ConvertFrom-Json
    } else {
        $gift = Invoke-LocalPost '/api/admin/gifts' @{ title = 'Teste Checkout Pro (sandbox)'; description = 'Presente de teste local; sem pagamento real.'; imageUrl = $null; amountCents = 1000; active = $true } $csrf.token
        $state = [pscustomobject]@{ GiftId = $gift.id; CreateKey = [Guid]::NewGuid().ToString(); CheckoutKey = [Guid]::NewGuid().ToString(); ContributionId = $null; CheckoutUrl = $null }
        $state | ConvertTo-Json | Set-Content $statePath -Encoding UTF8
    }
    $receipt = Invoke-LocalPost '/api/contributions' @{ giftId = $state.GiftId; guestName = 'Convidado de teste'; email = 'sandbox@example.com'; message = 'Validacao do Checkout Pro em ambiente de teste.' } $csrf.token $state.CreateKey
    $state.ContributionId = $receipt.id
    $state | ConvertTo-Json | Set-Content $statePath -Encoding UTF8
    # The tracking token stays in memory only; future runs recover the same receipt through idempotent replay.
    $checkout = Invoke-LocalPost ("/api/contributions/" + $receipt.id + '/checkout') @{} $csrf.token $state.CheckoutKey $receipt.trackingToken
    $state.CheckoutUrl = $checkout.checkoutUrl
    $state | ConvertTo-Json | Set-Content $statePath -Encoding UTF8
    Write-Output "Sandbox contribution: $($state.ContributionId)"
    Write-Output "Checkout URL: $($state.CheckoutUrl)"
} finally {
    if ($session) {
        try {
            $csrf = Invoke-RestMethod 'http://localhost:5080/api/auth/csrf' -WebSession $session
            Invoke-LocalPost '/api/auth/logout' @{} $csrf.token | Out-Null
        } catch { }
    }
    Pop-Location
}
