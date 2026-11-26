# Wedding gifts API

ASP.NET Core 10 API for one couple's symbolic cash gifts. The public HTTP contract is [spec.md](spec.md); [openapi.json](openapi.json) is generated from the endpoints. The existing React client works with `VITE_GIFTS_MODE=api` and `VITE_API_BASE_URL=https://api.example.com` (no `/api` suffix).

## Build and test

Install the .NET 10 SDK system-wide and a Linux Docker engine. Run the standard `dotnet` command; `global.json` selects .NET 10 rather than the .NET 11 preview.

```sh
dotnet restore Wedding.sln
dotnet build Wedding.sln --no-restore
dotnet test Wedding.sln --no-restore
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project src/Wedding.Infrastructure --startup-project src/Wedding.Api
dotnet run --project src/Wedding.Api -- openapi "$PWD/openapi.json"
docker build -t wedding-api:local .
```

On PowerShell, `./scripts/smoke.ps1` builds the image, runs migrations and health/catalog checks against disposable containers, verifies a non-root runtime and custom `PORT`, then removes only its test containers/network.

Unit tests require no services. Integration tests start and destroy PostgreSQL 17 via Testcontainers, apply real migrations, and replace only the payment provider. They never contact Mercado Pago. Alternatively, set `WEDDING_TEST_DATABASE` to an **exclusively disposable** PostgreSQL database; migrations and test data will be written there. The database is not automatically dropped when supplied externally.

The CI workflow builds Release, runs both suites, checks EF model drift and generated OpenAPI drift, and builds the Linux image. Unit/provider fixtures are documented in `tests/Wedding.UnitTests/Fixtures/README.md`.

## Local configuration

With Docker running, start the local API from PowerShell:

```powershell
./scripts/start-local.ps1
```

This runs `setup-local.ps1`, starts PostgreSQL on `127.0.0.1:55432`, applies migrations, provisions two development accounts if absent, and starts the API at **http://localhost:5080**. OpenAPI is at **http://localhost:5080/openapi/v1.json**. Setup is repeatable and preserves the database volume.

Local passwords are generated once and saved in ignored `.local/accounts.json`. The database password is stored in ignored `.env` (read by Docker Compose) and `src/Wedding.Api/appsettings.Development.Local.json` (read by the API only in Development). These files are excluded from the Docker build context. Do not commit or reuse them in production. Keep `.env`, local JSON, credentials, and the database volume together; deleting credentials does not reset existing accounts/passwords.

In another PowerShell terminal, connect the sibling React project without modifying its `.env`:

```powershell
./scripts/start-frontend-local.ps1
```

The frontend runs at **http://localhost:5173**, with API mode and `VITE_API_BASE_URL=http://localhost:5080` set only for that process. Its dependencies must already be installed. Use `localhost` consistently for the browser and API cookies. Stop each foreground process with Ctrl+C; stop the database with `docker compose stop db` (data is preserved). For the background instances initially launched during setup, run `./scripts/stop-local.ps1 -StopDatabase`; it checks the recorded process identities before stopping their process trees. Logs for those instances are in `.local/api.out.log`, `.local/api.err.log`, `.local/frontend.out.log`, and `.local/frontend.err.log`.

Mercado Pago is deliberately disabled in the local configuration. Catalog, login, administration, and contribution creation work with no provider credentials. Checkout returns 503 until real sandbox settings are supplied and validated; no payment is simulated. Production still requires real provider settings, database TLS, and a Data Protection certificate.

For local webhook delivery, build `tools/Wedding.WebhookRelay`, run `scripts/start-webhook-relay.ps1`, then run `scripts/start-webhook-tunnel.ps1` in another terminal. The tunnel targets the loopback relay on port 5081, which forwards only `POST /api/webhooks/mercadopago` to the API; auth, admin, and catalog routes are not forwarded. Ngrok request inspection is disabled. Configure the resulting HTTPS URL plus `/api/webhooks/mercadopago` in Mercado Pago's Test webhook settings and in local `MercadoPago.NotificationUrl`. Save the provider's secret as local `MercadoPago.WebhookSecret`, then restart the API. An unsigned webhook must return 401. The tunnel URL can change when ngrok restarts.

For manual configuration, use .NET user secrets or environment variables named as in [.env.example](.env.example). Environment variables override the local JSON. The API does not automatically load `.env` files. Example placeholders are not working credentials.

Use a local PostgreSQL database. Set `ASPNETCORE_ENVIRONMENT=Development`, configure both connection strings, then run:

```sh
dotnet run --project src/Wedding.Api -- db migrate
dotnet run --project src/Wedding.Api -- users create bride@example.com "Bride Name"
dotnet run --project src/Wedding.Api -- users create groom@example.com "Groom Name"
dotnet run --project src/Wedding.Api
```

Provisioning reads passwords from a hidden interactive prompt, or from the temporary `WEDDING_TEMP_PASSWORD` environment secret. Never pass passwords as command arguments. Remove temporary secrets from the parent environment/Render configuration immediately after use. Passwords require at least 12 characters and Identity's default complexity rules. Provisioning permits at most two accounts and grants `Couple`; there is no public registration or demo login.

For HTTP development use a Vite same-origin `/api` proxy. Only Development allows a non-Secure cookie. HTTPS origins are mandatory outside Development. Use distinct database and payment credentials for sandbox and production.

## Persistence and security

`Wedding.Api` owns HTTP and administrative commands; Application owns contracts and validation; Domain owns entities/states; Infrastructure owns EF, Identity, provider access, sessions, and durable processing. Financial amounts use checked integer cents and exact decimal parsing. Catalog changes never rewrite contribution snapshots.

Cookies are host-only, HttpOnly, Secure outside Development, and expire absolutely after eight hours. Every authenticated request reads its server session and checks the current security stamp. Logout removes its session; password reset removes all user sessions and changes the security stamp. Browser writes require the antiforgery cookie plus `X-CSRF-Token`, including anonymous creation and login. Obtain a fresh token after login/logout. Webhooks use signatures instead of cookies or antiforgery.

Data Protection keys and encrypted tickets live in PostgreSQL. Production requires `DataProtection__CertificateBase64` containing a PFX with its private key and `DataProtection__CertificatePassword`; `DataProtection__ApplicationName` must match across web, cron, migrations, and administrative processes. Protected tracking tokens allow exact idempotent replay without storing a plaintext response cache. Checkout request snapshots are also encrypted because return URLs contain tracking tokens.

To rotate certificates, deploy the new PFX as the active certificate and retain each old PFX/password under `DataProtection__PreviousCertificates__N__Base64` / `__Password`. Keep old decryption certificates for as long as any retained key/token/session needs them. Back up the database **and** certificates; losing either can make tracking tokens unrecoverable. Do not rely on Render's ephemeral filesystem.

Logs deliberately omit request bodies, raw URLs/query strings, EF command details, cookies, credentials, and payment/tracking headers. Operational failures use sanitized codes and internal IDs. Do not enable HTTP body/header logging or EF sensitive-data logging in production.

Rate limits are per process: login 5/min/IP, contribution creation 10/min/IP, checkout 10/min/IP/contribution, private reads 30/min/IP/contribution. Identity also locks accounts after five failures for 15 minutes. Configure overrides with `RateLimits__login`, `__create`, `__checkout`, and `__private`. If deploying multiple instances, apply aggregate limits at a trusted edge as well. Trust only explicit proxy IPs/CIDRs via `ForwardedHeaders__KnownProxies__N` / `__KnownNetworks__N`; never trust arbitrary forwarded IP headers.

## Mercado Pago sandbox and activation

The adapter uses [Checkout Pro Orders](https://www.mercadopago.com.br/developers/en/docs/checkout-pro-orders/create-order?scope=prod), never Preferences. Provider calls have a total ten-second budget; HTTP requests have an 18-second deadline, below the frontend's 20 seconds. A cancelled request leaves its committed attempt/key recoverable through the expiring lease. Orders are correlated using the persisted attempt UUID in `external_reference`; the provider idempotency UUID is separate from the browser's key.

`MercadoPago__CheckoutEnabled=false` is the default. Before enabling it:

1. Confirm that the receiver account/application can use Orders and configure signed `order` webhooks at `MercadoPago__NotificationUrl` in the provider dashboard. This setting documents/validates the callback; it does not register the webhook automatically. Verify receiver ID, application ID, and test/production environment.
2. Capture sanitized account-specific create/get/signature fixtures. Confirm response state/timestamps, sandbox identification, amount/currency, Pix/card methods, and associated payment financial identifiers. The adapter uses authenticated user metadata when Orders omits `live_mode`; failure to verify it blocks financial updates.
3. Set `MercadoPago__ValidatedConfigJson` to the provider-accepted `config` object. It must exclude `ticket` (boleto), permit Pix/credit, and avoid seller-funded or interest-free installment settings. The application overwrites `online.success_url`, `pending_url`, `failure_url`, and `retries.allowed`; browser input cannot supply them. Validate the actual payer-funded installment behavior in checkout rather than assuming an account default.
4. Obtain the provider's idempotency retention guarantee and set `MercadoPago__IdempotencyWindowMinutes` conservatively within it. There is no invented retention default. An unresolvable attempt past that window requires manual reconciliation.
5. Complete the acceptance checks below, then set `CheckoutEnabled=true`. If the expected flow or metadata is unavailable, leave it disabled and resolve the provider integration; do not substitute another API or simulate payments.

The parser follows the documented [Orders get response](https://www.mercadopago.com.br/developers/en/reference/online-payments/checkout-pro/get-order/get), including transaction refunds and chargebacks. Net/fee amounts remain unknown when absent. Where Orders exposes a compatible numeric payment reference, the adapter consults its associated payment resource and verifies its relationship before using financial details. Unsupported reference shapes remain unknown and require sandbox adapter validation; no fee estimates are used.

Webhook authentication follows the [official notification flow](https://www.mercadopago.com.br/developers/pt/docs/checkout-pro-orders/notifications?scope=prod) and [official .NET signature canonicalization](https://github.com/mercadopago/sdk-dotnet/blob/master/src/MercadoPago/Webhook/WebhookSignatureValidator.cs). Signed identifiers retain their case. A valid notification is persisted before 200; only an authoritative provider query changes financial state. Unsupported authenticated topics are retained as ignored; add separately validated adapters if the account requires additional refund/chargeback topics.

`MercadoPago__SandboxSingleAttempt=true` permits checkout without a retention guarantee when `MercadoPago__Environment=test`, including on Render with `ASPNETCORE_ENVIRONMENT=Production`. Hosting and payment environments are independent. The adapter verifies that the credential belongs to the configured test account before creating an order. Creation is attempted once; uncertainty requires manual intervention and never triggers another creation request. This option cannot enable live payments. Use one installment for the initial card test; Pix and payer-funded installments still require separate acceptance. Sandbox Orders status is authoritative after account/reference/amount verification; legacy Payments financial projections are not queried in sandbox because observed metadata uses incompatible environment and order identifiers. Missing fee/net values remain unknown.

For a hosted sandbox, keep the production certificate, HTTPS, CORS, and database settings and set:

```dotenv
ASPNETCORE_ENVIRONMENT=Production
MercadoPago__Environment=test
MercadoPago__CheckoutEnabled=true
MercadoPago__SandboxSingleAttempt=true
MercadoPago__IdempotencyWindowMinutes=0
MercadoPago__ValidatedConfigJson={"payment_method":{"not_allowed_types":["ticket"],"max_installments":1}}
```

Supply test account credentials and configure the test dashboard webhook to the deployed `/api/webhooks/mercadopago` endpoint. An uncertain creation is blocked; do not bypass it by creating a fresh contribution until the existing provider outcome has been investigated. On Render Free, the worker stops while the service sleeps; delayed notifications and polling must be tested, and reconciliation needs a separate scheduled runner.

After sandbox acceptance, live activation uses environment values: disable checkout while configuring `Environment=production`, `SandboxSingleAttempt=false`, production access token/signature/receiver/application, production dashboard webhook, accepted payment config, and a verified positive idempotency retention window. Use a separate Neon database/branch and matching migrations/couple provisioning so test contributions never enter live totals. Keep the production Data Protection certificate backed up and consistent across workers. Do not claim live readiness until production financial projections, installments, refunds and chargebacks pass acceptance.

With local credentials, webhook configuration, and this sandbox mode configured, run `./scripts/create-sandbox-checkout.ps1`. It creates a R$10 gift/contribution through the local API and stores reusable request keys and the checkout link in ignored `.local/sandbox-checkout.json`. Rerunning reuses that contribution. Complete checkout with an official test card, then verify the authoritative order and signed webhook processing before considering the test successful.

## Jobs and recovery

The web worker drains authenticated inbox items and reconciles up to five due payment attempts per polling cycle. Each successful provider synchronization schedules the next read five minutes later, including across process restarts. Missing webhook deliveries therefore no longer require manual intervention while the web process is running. Render Free may sleep; keep the five-minute external reconciliation schedule for recovery independent of web uptime. The same leases coordinate web and cron workers, and uncertain sandbox creations are never automatically recreated.

Webhook lifecycle logs identify accepted inbox IDs and successfully processed provider order IDs without recording signatures, request bodies, tracking tokens, or guest information. A missing inbox record means there is no accepted delivery; use Mercado Pago's delivery history and HTTP response to distinguish no delivery from pre-inbox rejection. Do not diagnose missing delivery solely from a pending contribution.

Run commands from an authorized Render shell or a secure terminal using the deployed image and secrets:

```sh
dotnet Wedding.Api.dll db migrate
dotnet Wedding.Api.dll users create <email> "Full Name"
dotnet Wedding.Api.dll users reset-password <email>
dotnet Wedding.Api.dll jobs reconcile
dotnet Wedding.Api.dll jobs reconcile --id <contribution-uuid>
dotnet Wedding.Api.dll jobs reconcile --from 2026-11-01T00:00:00Z --to 2026-11-22T23:59:59Z
dotnet Wedding.Api.dll jobs reprocess <inbox-uuid>
```

The web worker processes inbox events every five seconds. Render cron reconciles every five minutes. Both claim work through short PostgreSQL row transactions and expiring leases; no lock remains open during provider HTTP. Processing defaults: `Processing__PollSeconds=5`, `__LeaseSeconds=60`, `__MaxAttempts=10`. Retries use exponential backoff with jitter and stop for manual intervention after ten failures.

Reconciliation covers uncertain creation, pending payments, approvals created within the last 30 days, and refunds/chargebacks with unresolved financial amounts. Explicit ID/period targeting can revisit older known orders. Each run processes bounded batches; excess work remains durable for the next run. Webhooks remain accepted for older payments.

For an uncertain checkout, inspect its internal attempt ID, original provider key, error code, and lease. Let reconciliation retry within the verified provider idempotency window. **Never delete the attempt, change its key, or create a replacement payment to resolve uncertainty.** If the window expires, locate the existing order in Mercado Pago using the attempt's external reference. Bind and reconcile that authoritative order through:

```sh
dotnet Wedding.Api.dll jobs reconcile --id <contribution-uuid> --order-id <existing-provider-order-id>
```

This command verifies the attempt reference, receiver, application, environment, amount, and currency before linking anything. If no authoritative outcome can be established, keep the attempt blocked and investigate with Mercado Pago. Confirmed rejected/cancelled payments require a new contribution from the catalog. Reprocessing an inbox item resets its retry budget only when no active lease exists; inspect the original cause first.

## Neon and Render deployment

Use the [Neon pooled connection](https://neon.com/docs/connect/connection-pooling) for application traffic and its direct connection for `ConnectionStrings__Migrations`. Production requires `SSL Mode=VerifyFull` and does not accept bypassed certificate validation. The default pool limit is ten per process; size the total across web instances, cron, and maintenance. Migrations are explicit and never run on ordinary startup.

The [Render blueprint](render.yaml) creates an always-on paid web service and a five-minute cron job. Create/populate the shared `wedding-shared` environment group in Render with every required setting in `.env.example` before deployment. Secrets are deliberately absent from the YAML: Render ignores `sync: false` inside environment groups, so configure them in the dashboard. Set the actual trusted proxy network(s) from your deployment; `/api` requests reject plaintext connections outside Development.

Both services have automatic deploys disabled. Deploy the **same source commit/image version** to web and cron; the web pre-deploy command runs `db migrate` once over the direct connection. For byte-identical release artifacts, publish one immutable image digest and configure both Render services to pull that digest. Run account provisioning separately after migrations. Docker uses an unprivileged user and binds `0.0.0.0` to Render's `PORT`.

Prefer `site.example.com` and `api.example.com` with `SameSite=Lax`, or proxy `/api` through the frontend origin. Configure exact CORS origins. For distinct sites, use `Authentication__CookieSameSite=None` and test real browsers: third-party cookie blocking can prevent sessions regardless of CORS settings.

`/health/live` checks the process. `/health/ready` checks database connectivity and pending migrations; neither calls the payment provider or exposes internal diagnostics. After deployment, verify HTTPS readiness, CORS, CSRF/login/logout, catalog, and a private contribution in the deployed browser configuration.

Rollback web and cron together to the previous compatible image, retaining the same key ring and decryption certificates. Do not run a destructive down-migration over financial data. Use additive/backward-compatible migrations for subsequent releases; restore from protected backups only through an incident plan that accounts for payments created after the backup. No automatic financial-history deletion is configured.

## Monitoring and release acceptance

The `Wedding.Payments` meter emits provider failures, financial mismatches, uncertain-checkout events/count, reconciliation failures, inbox depth/oldest age, and intervention count. Connect these instruments through your .NET metrics collector; use sanitized Error-level logs for immediate alerts. Alert on any financial mismatch/intervention, uncertainty lasting beyond a reconciliation cycle, inbox age above five minutes, repeated reconciliation errors, or elevated provider failures. Do not place guest names/emails or secrets in metric labels.

Before production activation, complete `spec.md` section 11 against isolated sandbox credentials/database and the real frontend domains:

- Both provisioned accounts, CSRF rotation, private-token isolation, logout/reset revocation, and restart persistence.
- Pix and credit checkout, boleto exclusion, payer-funded installments, and verified gross/net financial data.
- Pending redirect followed by authoritative approval, private links/new tabs, polling, and network retries without duplicate charges.
- Catalog edits/deactivation preserving history; duplicate/out-of-order notifications and webhook-before-create-response behavior.
- Supported refund/chargeback scenarios, including partial refunds leaving approved totals.
- HTTPS smoke test, job execution, alert delivery, backups, and a rehearsed uncertain-attempt recovery procedure.

Automated tests and container builds do not replace this account-specific acceptance. No live provider credentials or deployment account are included in this repository.
