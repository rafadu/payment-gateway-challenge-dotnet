# Handoff — Payment Gateway (mid-implementation, resuming at Stage 7)

**Repo:** `/home/rafadu/repos/payment-gateway-challenge-dotnet` · branch `main`
**As of:** 2026-09-27 · **Tests: 108 passing, 0 failing** · clean build, no warnings.
**Target framework:** .NET 10 (`net10.0`).

This replaces the original pre-implementation handoff. Stages 1–6 of
`docs/implementation-plan.md` are built, reviewed, and committed. This file is the context for the
next session, which starts at **Stage 7**.

---

## How we work (established workflow — keep doing this)

- **Driver:** the implementer (assistant) runs each stage directly with the **`tdd`** skill
  (red → green), NOT via the `Senior .NET TDD Developer` sub-agent (that agent hardcodes a
  `CLAUDE.md` source-of-truth + Clean-Architecture layout that fights this plan).
- **Spec = `docs/design.md` + in-scope ADRs + `docs/implementation-plan.md`.** Single project with
  informal `I...` ports is deliberate — do **not** split into Domain/Application/Infrastructure.
- **Review after every stage** with the `.NET Code Reviewer` agent; apply worthwhile findings
  (red-green for any new behavior), then **stop for the user's check-in** before the next stage.
- **The user commits each stage themselves** (one commit per stage, with a rationale message).
  Don't commit unless asked.
- Each stage ends green: `dotnet test` all passing, build clean.

## Environment gotchas (see also the auto-memory files)

- **Sandbox has no .NET SDK by default and no `jq`/`node`/`python3`; no Docker.** Reinstall the SDK
  each session: `curl -fsSL https://dot.net/v1/dotnet-install.sh | ...` → `--channel 10.0
  --install-dir ~/.dotnet`, then symlink `~/.dotnet/dotnet` into `~/.nvm/versions/node/<v>/bin/`.
- **Docker is unavailable here**, so the container steps (Mongo seed check, smoke run, integration
  tests) must be **run by the user** in their own terminal.
- `WebApplicationFactory`'s `ConfigureAppConfiguration` overrides do **not** reach config that is
  read before `builder.Build()` (e.g. inside `AddX` extension methods). To assert against the app's
  real config in a component test, read it back from `factory.Services.GetRequiredService<IConfiguration>()`.

---

## Current state by stage (all committed on `main`)

| Stage | What | Key types |
|---|---|---|
| 1 | Domain contracts + validation | `PaymentStatus` (Authorized/Declined only), `PostPaymentRequest`, `PaymentResponse` (last-four as `string`), `PostPaymentRequestValidator` (FluentValidation; injected `TimeProvider` for expiry) |
| 2 | Payments repository | `IPaymentsRepository` + `PaymentsRepository` (`ConcurrentDictionary<Guid,Payment>`); `Payment` domain model (immutable, **no MerchantId yet**); `Add` is a documented upsert |
| 3 | Acquiring bank client | `IAcquiringBankClient` + `AcquiringBankClient` (typed `HttpClient`); snake_case DTOs in `Models/Bank/`; `BankUnavailableException` (timeout/network/unreadable-body/non-2xx-except-400), `InvalidBankRequestException` (**400** = gateway built a bad request → 500, NOT 503); caller-cancellation propagates |
| 4 | Payments service orchestration | `IPaymentsService` + `PaymentsService`: request → `BankPaymentRequest` (full PAN, `expiry_date` `"MM/yyyy"`) → bank → `PaymentStatus` → `Payment` (new GUID, **last-four only**) → persist. Bank exception propagates, nothing persisted. Assumes an already-validated request |
| 5 | Mongo infra + credential cache | `MerchantCredential` record; `ICredentialStore` + `MongoCredentialStore` (MongoDB.Driver); `ICredentialCache` + `CredentialCache` (**`IMemoryCache`-backed**, 4h absolute TTL, not-found not cached, key-prefixed `merchant-cred:`); `docker-compose` `mongo:7` + `mongo-init/seed-merchants.js` |
| 6 | Token issuance + AuthController | `ITokenIssuanceService` + `TokenIssuanceService` (BCrypt verify + HMAC-SHA256 JWT, `sub`=merchantId, `jti`, absolute expiry via `TimeProvider`, timing-equalised, malformed-hash-safe); `AuthController` `POST /api/auth/token` (400 blank / uniform 401 / 200); `TokenRequest`/`TokenResponse` |

**DI/config already wired (in `Configuration/` extension methods, called from a thin `Program.cs`):**
`AddMongoDb`, `AddCredentialCache`, `AddTokenIssuance`. `appsettings.json` has `Mongo`,
`CredentialCache:TtlHours`, `Jwt:{SigningKey,ExpiryMinutes}`. Each extension fail-fasts on
missing/blank/invalid config and has unit tests (DI resolves + guard throws).

**Still registered but NOT yet wired for HTTP:** `IPaymentsRepository` (singleton). The bank client
(`IAcquiringBankClient`) and `IPaymentsService` are **not registered in `Program.cs` yet** — that
DI, plus the typed `HttpClient` + bounded timeout and JWT Bearer auth middleware, is Stage 9 (auth
middleware is Stage 7). `PaymentsController` is still the near-scaffold GET-only stub.

---

## Decisions locked (implemented; don't relitigate)

- **POST outcomes:** Authorized/Declined → `201` (persisted); validation-Rejected → `400` (not
  persisted, bank never called); bank-unavailable → `503` (not persisted, **no auto-retry** —
  ADR-0001). Bank **400** → `500` via `InvalidBankRequestException` (gateway-side defect, not a
  bank-availability problem — user-mandated, `design.md` "Bank integration" updated).
- **Auth (ADR-0010, a deliberate extension beyond the brief):** merchant JWT. `POST /api/auth/token`
  is unauthenticated; `POST`/`GET /api/payments` will require `Authorization: Bearer <jwt>`.
  **Uniform 401** for unknown-client vs wrong-secret (no existence leak); blank creds → 400.
  BCrypt hashes only; JWT HMAC-SHA256, `sub`=merchantId, 15-min expiry, no refresh.
- **Cache:** `IMemoryCache`, 4h **absolute** TTL (not sliding), not-found not cached (so a
  re-seed/rotation is picked up). `ICredentialCache` is the seam for the ADR-0011 Redis swap
  (documented, not built).
- **Persistence & data handling:** `ConcurrentDictionary` repo (in-memory, per the brief's test
  double). Full PAN/CVV never persisted or logged — only last-four reaches `Payment`/responses.
- **Out of scope / documented-not-built:** ADR-0004–0009, ADR-0011, `production-architecture.md`
  (modular monolith, RabbitMQ, metrics, ArchUnit/k6, CI/CD, Redis). Do not start these.

## Known limitations (intentional, keep documented — surface in README at Stage 9)

- `Jwt:SigningKey` is a **dev-only placeholder in `appsettings.json`** — production must override via
  env var / secret store. (JSON can't hold comments; document in README.)
- **No rate limiting / brute-force protection** on `/api/auth/token` (ADR-0010).
- JWT has **no `iss`/`aud`** (design scopes the token to signing-key + `sub` + expiry).
- Bank-timeout ambiguity (bank may have processed a call whose response was lost) is **not** solved;
  the Idempotency-Key feature (Stage 8) protects only the merchant→gateway boundary (ADR-0003).

---

## Open questions / things to decide

1. **User's own note (commit msg, Stage 4):** how to guarantee a payment is stored once the bank
   authorises, given the repo is an in-memory `ConcurrentDictionary` — i.e. the write-after-bank-
   success durability gap in a real system. Not a code change for this exercise; revisit if the
   user wants it written up (candidate ADR).
2. **Stage 7 claim mapping (reviewer hand-off):** when wiring JWT Bearer, the validation
   `TokenValidationParameters` **must** pin `ValidAlgorithms = ["HS256"]` (block `alg=none`/
   algorithm-confusion) and set `MapInboundClaims = false` so `sub` reads back directly (issuance
   emits a raw `sub`; the tests read it raw).
3. **Test token minting for Stage 7:** component tests need a valid bearer token. Plan says mint by
   signing directly with the test config's `Jwt:SigningKey` (decoupled from Stage 6/Mongo). Read the
   app's actual key back via `factory.Services` (see the WAF gotcha above), or override before build.

## Failed approaches / corrections (don't repeat)

- **Statusline (side task):** the `statusline-setup` agent shipped a `jq`-based script, but this
  box has no `jq`/`node`/`python`. Rewrote `~/.claude/statusline-command.sh` as pure bash + `grep -P`.
- **Transitive CVEs:** `MongoDB.Driver` pulls vulnerable `Snappier`/`SharpCompress`. First pin
  attempt (`Snappier 1.1.6`, `SharpCompress 0.39.0`) was still flagged; the working pins are
  **`Snappier 1.3.1`** + **`SharpCompress 0.50.4`** (compression is not enabled at runtime).
- **`CredentialCache` was originally a hand-rolled `ConcurrentDictionary`** (+ injected
  `TimeProvider`); switched to `IMemoryCache` per the user. Don't reintroduce the manual dictionary.
- **Component-test config override:** setting `Jwt:SigningKey` via `ConfigureAppConfiguration`
  did **not** reach `AddTokenIssuance` (reads config before `Build()`). Validate against the app's
  real key via `factory.Services` instead.
- Reviewer nit still open for Stage 7: rename `PaymentsController.GetPaymentAsync` → `GetPayment`
  (it's synchronous) during the rewrite.

---

## Concrete next steps — Stage 7: wire `PaymentsController` with auth gating

1. **Add `MerchantId` to `Payment`** (init-only string), set from the JWT `sub` in `PaymentsService`
   on POST. (Deferred from Stage 2 on purpose.) Thread the caller's merchant id into
   `IPaymentsService.ProcessPaymentAsync` (new param) and update Stage 4 tests.
2. **JWT Bearer middleware** (`Microsoft.AspNetCore.Authentication.JwtBearer`), a
   `AddJwtAuthentication`-style extension in `Configuration/`, reading the same `Jwt:SigningKey`.
   `TokenValidationParameters`: validate signature + lifetime, `ValidAlgorithms=["HS256"]`,
   `ValidateIssuer=false`, `ValidateAudience=false`, `MapInboundClaims=false`. Add
   `app.UseAuthentication()` before `UseAuthorization()`.
3. **`[Authorize]`** on both `PaymentsController` actions; rewrite the controller to its final,
   auth-aware form (read `sub` for the merchant id; rename `GetPaymentAsync`→`GetPayment`).
   **GET returns `404` (not `403`)** when the caller isn't the payment's owner (ADR-0010).
4. **Rewrite `PaymentsControllerTests.cs`** auth-aware (once): missing/invalid/expired-token → 401,
   validation → 400, GET 200/404 including **cross-merchant 404**. Mint tokens by signing with the
   test config's `Jwt:SigningKey`. POST happy-path isn't re-tested here (covered in Stage 4,
   re-verified end-to-end in Stage 10).
5. **Done when:** those component tests pass; full suite green; review; stop for check-in.

**Then:** Stage 8 (Idempotency-Key, ADR-0003), Stage 9 (finalise `Program.cs` DI incl. bank client
typed `HttpClient` + timeout, real `appsettings`, README "How to call the API" with demo creds
`demo-merchant`/`demo-secret`, manual `docker-compose up` smoke), Stage 10 (integration suite vs real
Mountebank + Mongo).

### Manual check still owed by the user (Docker unavailable in the sandbox)
```bash
docker-compose down -v            # only if an old mongo volume exists
docker-compose up -d mongo
docker exec payment_gateway_mongo mongosh payment_gateway --quiet \
  --eval 'db.merchants.find().toArray()'   # expect the demo-merchant doc
```

## Read these first (next session)
`docs/implementation-plan.md` (the 10 stages), `docs/design.md` (spec), `docs/adr/0001`–`0003`,
`docs/adr/0010`–`0011`, `docs/resources/assessment.md` + `README.md` (brief),
`imposters/bank_simulator.ejs` + `docker-compose.yml` (bank wire contract).
