# Provider fixtures

`order-created.json` follows the public Checkout Pro Orders create-order example, with receiver, application, reference, and value replaced by test identifiers. The documented Orders response omits `live_mode`; tests must supply independently verified account metadata.

Sources checked 2026-11-22:

- https://www.mercadopago.com.br/developers/en/docs/checkout-pro-orders/create-order?scope=prod
- https://www.mercadopago.com.br/developers/en/reference/online-payments/checkout-pro/get-order/get
- https://raw.githubusercontent.com/mercadopago/sdk-dotnet/master/src/MercadoPago/Webhook/WebhookSignatureValidator.cs

Signature test vectors use the official SDK manifest (`id:...;request-id:...;ts:...;`) and synthetic secrets. They are reproducible test vectors, not claims of captured provider traffic. Signed IDs retain their case. No short timestamp tolerance is imposed because delivery retries can be delayed; durable deduplication prevents replay processing.

`order-sandbox-created.json` captures selected fields from an authenticated sandbox Orders GET on 2026-11-24 UTC after a successful R$10 create request. Order, receiver, application, and external reference identifiers are replaced. Payer data, checkout URLs, and private return tokens are excluded. Account metadata independently verified the test environment. This fixture validates creation parsing; it does not demonstrate an approved payment.

Production acceptance must additionally capture sanitized account-specific fixtures for Pix/card, installment settings, associated payment financial fields, refunds, chargebacks, and provider idempotency retention. Production checkout stays disabled until these pass.
