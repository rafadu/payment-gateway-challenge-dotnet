# Payment Gateway — staged implementation plan

## Context

All design work is finished and written to `docs/` (`design.md`, ADR-0001–0004, ADR-0010,
ADR-0011). The scaffold is still close to empty: `PaymentsController` only has a GET stub
returning `200` unconditionally, `PaymentsRepository` is a non-thread-safe `List<T>`,
`PostPaymentRequest` has the wrong shape, `CardNumberLastFour` is typed `int` (drops leading
zeros), there's no validation, no auth, no Mongo, and `docker-compose.yml` only runs the bank
simulator. `GetPaymentResponse` is a dead duplicate of `PostPaymentResponse`.

The user explicitly wants to avoid a one-shot "build everything" prompt. This plan exists purely
to sequence implementation into small, independently reviewable stages, each ending in a
check-in before the next one starts. No design decisions are made or changed here — see the ADRs
for rationale; this file only orders the work.

**Auth-vs-core-flow ordering decision**: build the auth-agnostic core payment logic first
(stages 1–4, fully unit-testable with zero HTTP surface), then build the auth module as its own
reviewable vertical slice (stages 5–6, login provably works on its own), then wire
`PaymentsController` already auth-required from day one (stage 7). This avoids writing
`PaymentsControllerTests.cs` twice — once unauthenticated, once rewritten for auth — which would
otherwise be the single messiest, least reviewable stage in the whole plan. `Payment.MerchantId`
is deliberately introduced in stage 7, not stage 1, since adding it earlier would be a speculative
field with no owner yet.

Note: "vertical slice" here describes delivery *sequencing* only (build auth end-to-end, review,
before wiring it in) — it is not a code-structure decision. ADR-0006's modular monolith / ports
and adapters (separate projects, enforced module boundaries, architecture tests) remains out of
scope for this submission; interfaces like `ICredentialCache` and `IAcquiringBankClient` act as
informal ports within the single existing project, not as physically separated modules.

## Working agreement for each stage

- Each stage is one focused session, driven by the **`tdd`** skill (red-green-refactor).
- Each stage ends with: tests passing, a short summary of what changed, and a stop for review —
  no stage silently continues into the next.
- Git commits happen only when explicitly asked, per existing working agreement.
- New NuGet packages needed as stages are reached (none pre-installed today): FluentValidation,
  a mocking library (Moq or NSubstitute — the scaffold currently has neither), MongoDB.Driver,
  BCrypt.Net-Next, Microsoft.AspNetCore.Authentication.JwtBearer.

## Stages

**1. Domain contracts + validation**
- `PaymentStatus` enum (Authorized/Declined only — `Rejected` never persisted, per design.md).
- Fixed `PostPaymentRequest` (real `CardNumber`/`Cvv` fields, replacing the scaffold's wrong
  `CardNumberLastFour`-only shape).
- Single `PaymentResponse` (`CardNumberLastFour` as `string`) replacing the
  `PostPaymentResponse`/`GetPaymentResponse` duplication.
- FluentValidation validator: card length/numeric, expiry-in-future, currency allow-list from
  config, `Amount > 0` (ADR-0002), CVV.
- **Done when**: one test per validation rule (valid/boundary/invalid), all green. No controller,
  no DI, no I/O touched.

**2. Payments repository**
- `IPaymentsRepository` + `ConcurrentDictionary<Guid, Payment>` implementation, replacing the
  scaffold's `List<T>`.
- **Done when**: add/get/get-missing unit tests pass.

**3. Acquiring bank client**
- `IAcquiringBankClient`, snake_case bank DTOs (`card_number`, `expiry_date` as `"MM/yyyy"`,
  `currency`, `amount`, `cvv`), typed `HttpClient` via `IHttpClientFactory`, bounded timeout from
  `BankSimulator:TimeoutSeconds` (ADR-0001), `BankUnavailableException` on non-success/timeout/
  network failure.
- **Done when**: unit tests against a faked `HttpMessageHandler` cover authorized/declined/
  non-2xx/timeout. No docker needed — real wire-contract conformance is proven later, in stage 10.

**4. Payments service orchestration**
- `IPaymentsHandler`: validated request → bank DTO → `IAcquiringBankClient` → map result →
  persist via `IPaymentsRepository` (Authorized/Declined only).
- **Done when**: unit tests with mocked bank client + repository cover Authorized (persisted),
  Declined (persisted), bank-unavailable (exception propagates, nothing persisted).
- **Checkpoint**: the entire payment decision logic is proven correct with zero HTTP/auth surface.

**5. Mongo infrastructure + credential cache**
- `docker-compose.yml` gains a `mongo` service + seed script (`docker-entrypoint-initdb.d`)
  creating the `merchants` collection (`merchantId`, `clientId`, `hashedSecret`, `createdAt`).
- `ICredentialCache`: `ConcurrentDictionary`-backed, read-through over Mongo, 4h absolute TTL
  from write time (ADR-0010), expired-on-read treated as a miss.
- **Done when**: cache hit/miss/expiry/re-fetch unit tests pass against a mocked Mongo dependency
  (fast, no docker), plus one manual `docker-compose up` check that the seed script actually
  creates a merchant document.
- **Infra note**: first appearance of Mongo in the actual build.

**6. Token issuance + AuthController**
- BCrypt secret verification, JWT HMAC issuance (`Jwt:SigningKey`, `sub` = merchantId, 15 min
  expiry), `POST /api/auth/token` — uniform `401` for bad secret or unknown `clientId`.
- **Done when**: unit tests for issuance logic (mocked cache) + `WebApplicationFactory` component
  tests for the `200`/`401` paths (credential store faked through DI, no real Mongo required).
- **Checkpoint**: auth is a fully working, independently reviewable slice before anything depends
  on it.

**7. Wire PaymentsController with auth gating**
- JWT Bearer middleware, `[Authorize]` on both actions, `Payment.MerchantId` set from the `sub`
  claim on POST, `GET` returns `404` (not `403`) for a non-owner (ADR-0010).
- Rewrite `PaymentsControllerTests.cs` into its final, auth-aware form (once, not twice).
- **Done when**: component tests cover missing/invalid/expired-token `401`s, validation `400`s,
  GET `200`/`404` (including cross-merchant `404`). Tokens minted by signing directly with the
  test config's `Jwt:SigningKey` (decoupled from stage 6/Mongo internals, already covered there).
  POST happy-path isn't re-tested here — covered in stage 4, re-verified end-to-end in stage 10.

**8. Idempotency-Key support**
- `IIdempotencyStore` (`ConcurrentDictionary`, `TryAdd` to atomically claim), `IAsyncResourceFilter`
  registered globally with a route-data gate limiting it to `POST /api/payments`
  (resource-filter shape was chosen over an action filter to avoid a body-swap interaction with
  `CreatedAtActionResult` in `TestHost`; documented in `IdempotencyResourceFilter`'s XML
  comment and ADR-0003), claim/complete/release semantics, request-hash comparison (ADR-0003).
- **Done when**: store unit tests + filter-level `WebApplicationFactory` tests cover: in-progress
  duplicate → `409`; same key+hash completed → cached replay, bank client verified not called
  again; same key+different hash → `422`. Bank client faked, no docker needed.

**9. Full DI/config wiring + manual smoke run**
- Finalize `Program.cs` (JWT Bearer auth, all services/clients, `HttpClient`, Mongo client), fill
  in real `appsettings.json` (`SupportedCurrencies`, `BankSimulator`, `Mongo`, `Jwt`,
  `CredentialCache`).
- Add a **"How to call the API"** section (README) documenting the `/api/auth/token` step and the
  seeded demo merchant's `clientId`/`clientSecret`, since the assessment's documented request/
  response shapes have no `Authorization` header at all — a reviewer following the assessment
  literally would otherwise hit an undocumented `401` on `POST`/`GET /api/payments`. Written here,
  not earlier, since this is the first stage where a seeded demo merchant actually exists to
  document credentials for.
- **Done when**: `docker-compose up` (bank simulator + Mongo together for the first time), then a
  manual curl walkthrough — following the new README section verbatim — `/api/auth/token` →
  bearer token → `POST /api/payments` → `GET /api/payments/{id}`, covering Authorized/Declined/
  bank-unavailable by hand.

**10. Integration test suite**
- Tagged integration tests (trait-filtered out of the default `dotnet test` run), requiring
  `docker-compose up`: Authorized/Declined/bank-unavailable against the real Mountebank wire
  contract, and the full `/api/auth/token` → bearer token → `/api/payments` flow against real
  Mongo-backed credentials (seeded test merchant, real token round-trip).
- **Done when**: integration suite green with both containers up.

## Critical files
- `docs/design.md`, `docs/adr/0010-merchant-jwt-authentication-mongodb-cache.md` — spec for all
  stages.
- `src/PaymentGateway.Api/Controllers/PaymentsController.cs` — GET-only stub today, rewritten in
  stages 7–8.
- `src/PaymentGateway.Api/Services/PaymentsRepository.cs` — `List<T>`-backed today, replaced in
  stage 2.
- `src/PaymentGateway.Api/Program.cs` — minimal today, finalized in stage 9.
- `docker-compose.yml` — bank simulator only today, gains Mongo in stage 5.
- `test/PaymentGateway.Api.Tests/PaymentsControllerTests.cs` — existing unauthenticated GET
  tests, rewritten in stage 7.

## Verification
Each stage's own unit/component tests are the acceptance check for that stage — run via
`dotnet test` (excluding the integration trait until stage 10). Stage 9 adds a manual, human-run
smoke test over real HTTP via `docker-compose up` + curl, since wiring gaps (DI registration
order, middleware order) don't surface through mocked/faked tests. Stage 10's integration suite
is the final end-to-end check against the real bank simulator and real Mongo.
