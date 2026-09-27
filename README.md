# Instructions for candidates

This is the .NET version of the Payment Gateway challenge. If you haven't already read this [README.md](https://github.com/cko-recruitment/) on the details of this exercise, please do so now. 

## Template structure
```
src/
    PaymentGateway.Api - a skeleton ASP.NET Core Web API
test/
    PaymentGateway.Api.Tests - an empty xUnit test project
imposters/ - contains the bank simulator configuration. Don't change this

.editorconfig - don't change this. It ensures a consistent set of rules for submissions when reformatting code
docker-compose.yml - configures the bank simulator
PaymentGateway.sln
```

Feel free to change the structure of the solution, use a different test library etc.

## How to call the API

The assessment's documented request/response shapes omit the `Authorization` header — but the
challenge build itself adds merchant authentication (ADR-0010), so `POST` / `GET /api/payments`
will return `401 Unauthorized` without a bearer token. This section walks through the full flow,
end-to-end, against the seeded demo merchant.

### Prerequisites

- .NET 10 SDK (`dotnet --version` should report `10.x`).
- Docker + Docker Compose, to run the bank simulator and MongoDB together.

### 1. Start the supporting services

```bash
docker-compose up -d bank_simulator mongo
```

The Mongo container seeds a single merchant on first startup
(`mongo-init/seed-merchants.js`). Wait until the seed has run before the gateway hits the
credential store:

```bash
docker exec payment_gateway_mongo mongosh payment_gateway --quiet \
  --eval 'db.merchants.find().toArray()'
```

You should see one document with `clientId: "demo-merchant"`.

### 2. Start the API

```bash
dotnet run --project src/PaymentGateway.Api
```

The API listens on `http://localhost:5067` / `https://localhost:7092` (the dev profile in
`Properties/launchSettings.json`). All examples below use the HTTP form.

### 3. Get a bearer token

```bash
curl -s -X POST http://localhost:5067/api/auth/token \
  -H 'Content-Type: application/json' \
  -d '{ "clientId": "demo-merchant", "clientSecret": "demo-secret" }'
```

A successful response looks like:

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "tokenType": "Bearer",
  "expiresIn": 900
}
```

Save the `accessToken` value for the next two calls. Tokens expire after 15 minutes
(`Jwt:ExpiryMinutes`); re-authenticate when one expires. Wrong secrets and unknown clients both
return `401` — the response is identical so callers can't tell which case they hit.

### 4. Process a payment (Authorized)

```bash
curl -s -i -X POST http://localhost:5067/api/payments \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
    "cardNumber": "2222405343248877",
    "expiryMonth": 12,
    "expiryYear": 2030,
    "currency": "GBP",
    "amount": 100,
    "cvv": "123"
  }'
```

A successful payment returns `201 Created`, a `Location` header pointing at
`/api/payments/{id}`, and the stored payment (only the last four card digits are ever returned).
The bank simulator (Mountebank) decides Authorized vs Declined by the **last digit** of
`card_number`: odd (1/3/5/7/9) → Authorized, even (2/4/6/8) → Declined, `0` → 503 (bank
unavailable). The example card `2222405343248877` ends in `7`, so it returns Authorized — see
`imposters/bank_simulator.ejs` for the full predicate.

Validation failures (e.g. short card number, past expiry, unsupported currency) return `400 Bad
Request` with a `ProblemDetails` body, and the bank is not called.

The bank simulator being down (e.g. `docker-compose stop bank_simulator`) makes the call fail
with `503 Service Unavailable`; the gateway does not persist anything in that case and does not
auto-retry (ADR-0001). Retry once the bank is back up.

### 5. Idempotency-Key (optional)

For safe retries, send an `Idempotency-Key` header with the same UUID for every retry of a
particular purchase attempt:

```bash
curl -s -X POST http://localhost:5067/api/payments \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Idempotency-Key: 11111111-1111-1111-1111-111111111111' \
  -H 'Content-Type: application/json' \
  -d '{ ...same body... }'
```

A second POST with the same key + body returns the cached response from the first call, **without
calling the bank again** — the safety property the feature exists for (ADR-0003). Reusing the
key with a different body returns `422 Unprocessable Entity`. Reusing the key while the first
request is still in flight returns `409 Conflict`. Omitting the header means no caching and no
behaviour change.

### 6. Retrieve a payment

```bash
curl -s -H "Authorization: Bearer $TOKEN" \
  http://localhost:5067/api/payments/{id}
```

`200 OK` with the payment when the caller's merchant owns it, `404 Not Found` when the payment
doesn't exist **or** belongs to a different merchant (ADR-0010 — never reveal whether another
merchant's payment exists, so cross-merchant access is `404`, not `403`). `401 Unauthorized`
without a valid bearer token.

### Demo credentials

| Field | Value |
|---|---|
| `clientId` | `demo-merchant` |
| `clientSecret` | `demo-secret` |
| `merchantId` | `11111111-1111-1111-1111-111111111111` |

The plaintext `demo-secret` exists only in `mongo-init/seed-merchants.js` and the README — the
collection stores only its BCrypt hash (ADR-0010). Replace before any non-local deployment.

`Jwt:SigningKey` in `appsettings.json` is also a dev-only placeholder; production must override
via env var / secret store. The in-memory credential cache (`CredentialCache:TtlHours`) and
in-memory idempotency-key store are intentionally non-persistent — both are cleared on process
restart. Both are documented limitations, not solved problems (see ADR-0011 for the cache's
planned Redis evolution; ADR-0003 for the idempotency store's known limitations).