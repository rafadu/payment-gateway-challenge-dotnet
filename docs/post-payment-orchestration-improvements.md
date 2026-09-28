# POST /api/payments — orchestration improvements (discussion)

**Status:** working note. Not an ADR; an open discussion for future sessions to make decisions in, then promote to ADRs as each item is taken up.
**Written:** 2026-09-27, after the Stage 12 review session.
**Scope:** the `POST /api/payments` request lifecycle — what can be hardened, restructured, or made safer. Read alongside `docs/handoff.md`, `docs/design.md`, and `docs/adr/0001-bank-call-resiliency-timeout-only.md` (no retry on bank calls) and `docs/adr/0003-idempotency-key.md` (already shipped — see §1).

---

## 1. What's already in place (do not relitigate)

These are deliberate decisions, already implemented or locked in via ADR:

- **Idempotency-Key (ADR-0003).** `POST /api/payments` dedupes on a client-supplied header via `IAsyncResourceFilter`. Same key + same body → cached response replayed, **bank is not called twice**. Different body, same key → `422`. Concurrent in-flight → `409`. The known limitation: this only protects the merchant→gateway boundary; if the *gateway's* call to the bank times out, the bank may have already authorised and we have no record. (See §3.2.) — Store is in-memory only; production needs a TTL'd persistent store.
- **No automatic bank retry (ADR-0001).** Retrying a non-idempotent authorisation risks double-charging. `AcquiringBankClient.ProcessPaymentAsync` makes exactly one attempt.
- **Bank failure classification** is intentional: bank `400` → `InvalidBankRequestException` → `500` (gateway defect); everything else (non-2xx, timeout, network, unreadable body) → `BankUnavailableException` → `503`.
- **Persistence only after a definitive bank answer.** `PaymentsService` returns no `Payment` and writes nothing if the bank throws. A rejected request (validation failure) is never persisted.
- **Last-four only.** Full PAN and CVV never reach `Payment`, the response, or logs.
- **Merchant scoping (ADR-0010).** `sub` claim → `Payment.MerchantId` → `GET /api/payments/{id}` returns `404` (not `403`) for cross-merchant access.

## 2. The architectural smell

`PaymentsController.ProcessPayment` (`src/PaymentGateway.Api/Controllers/PaymentsController.cs:43-93`) is the entire pipeline in one method:

1. Validate (`IValidator<PostPaymentRequest>`)
2. Extract merchant id from JWT
3. Call `IPaymentsService.ProcessPaymentAsync`
4. Stamp the audit outcome on `HttpContext.Items`
5. Map domain → DTO → `201 CreatedAtAction`
6. Map two exception types to status codes

The service (`src/PaymentGateway.Api/Services/PaymentsService.cs:29-64`) is similarly fat: build bank request → call bank → build domain → persist → metric.

This works at the current scale but is the single biggest reason later improvements (idempotency-key, correlation id, retries, timeouts) cost more than they should.

### Direction (open)

- **Extract `ProcessPaymentHandler`** as the single use-case. The controller becomes bind → call handler → shape response.
- **Move cross-cutting concerns** (metrics, audit outcome, validation, logging) onto a pipeline (MediatR) or as decorators (`MetricsDecorator(IPaymentsService)`, etc.). Each is one testable class.
- **Move domain construction onto `Payment`** as a factory (`Payment.FromBankOutcome(merchantId, request, bankResponse)`). The service no longer knows field-by-field shape.

These are structural; they enable §3 without rewriting it twice.

## 3. Open problems (priority order)

### 3.1 Double-charging via the merchant→gateway boundary — closed (modulo persistence)

ADR-0003's resource filter handles retries. **Caveat:** the dedupe store is in-memory, so a gateway restart drops in-flight keys. Acceptable for the assessment; flag in production-readiness discussion.

### 3.2 Bank-authorised-but-gateway-forgot — **the remaining gap**

Three paths reach this state today:

- Bank returns `200 {authorized: true}`, then `_repository.AddAsync` throws (Mongo down / timeout) → merchant is charged at the bank, the gateway has no record.
- Client disconnects after the bank call has started but before the gateway has persisted → bank call still completes server-side; gateway drops the response.
- Gateway process restart between the bank response and the persist → same outcome.

The idempotency filter does **not** cover this because the bank call has already been made when the failure occurs — replaying would double-charge. ADR-0003 calls this out as a known limitation.

**Where:** `src/PaymentGateway.Api/Services/PaymentsService.cs:57` (`await _repository.AddAsync(payment, ct)`) is the only point where the bank outcome becomes durable. Everything before it is volatile.

**Options to discuss (next session):**

- **Outbox / pending-write.** Write a `Payment { Status = Pending }` (or a separate `BankIntents` collection) *before* the bank call, with a generated bank reference. Update to `Authorized`/`Declined` after the bank responds. A sweeper reconciles any `Pending` rows older than N seconds by re-querying the bank (if the simulator supports a status lookup — currently it does not).
- **Two-phase commit semantics.** Not realistic against an external bank; out of scope.
- **Bank-side idempotency key.** Out of scope — the bank simulator has no equivalent of `Idempotency-Key`. Real acquirers (Stripe, Adyen) support it; if we ever integrate one, this gap closes for real.

**Decide:** is a `Pending` status worth introducing? It bloats the state machine (`PaymentStatus` currently has only `Authorized`/`Declined`, per `design.md`) and the public `PaymentResponse`. Alternative: separate `BankIntents` collection, `Payment` stays binary.

### 3.3 Bank as a synchronous dependency

`PaymentsService` awaits `IAcquiringBankClient.ProcessPaymentAsync` inline. With a degraded bank, every request waits for the HTTP timeout (`HttpClient` configured at registration, ADR-0001) before returning `503`. The gateway falls over from latency pile-up before it falls over from errors.

**Where:** `src/PaymentGateway.Api/Services/PaymentsService.cs:43`.

**Options to discuss:**

- **Circuit breaker** (Polly `IAsyncPolicy<BankPaymentResponse>`) keyed on the bank client — open after N consecutive failures, half-open after cooldown, fail fast with `503` instead of timing out.
- **Bulkhead** — `SemaphoreSlim` or a dedicated thread pool so a slow bank can't starve validation or persistence.
- **Per-merchant concurrency cap** — protects other merchants from one runaway merchant.

ADR-0006 mentions a future multi-acquirer router; a circuit breaker on the bank client is a prerequisite for that anyway.

### 3.4 Mongo write has no timeout, no retry

`MongoPaymentsRepository.AddAsync` (`src/PaymentGateway.Api/Persistence/MongoPaymentsRepository.cs:26-36`) is a `ReplaceOneAsync` with no timeout / no transient-error retry. A hung Mongo will hang the request thread even though the bank has already responded authoritatively.

**Where:** same file. The Mongo client is registered in `src/PaymentGateway.Api/Configuration/MongoServiceCollectionExtensions.cs` — add a `serverSelectionTimeout` / `connectTimeout` here and a Polly retry on transient Mongo error codes.

### 3.5 Persistence semantics — the upsert is a footgun

`ReplaceOneAsync(..., IsUpsert = true)` was kept to honour the in-memory `ConcurrentDictionary` indexer semantics (ADR per stage-12 review). For a payment ledger it's a hazard: a programming bug that reuses an `Id` silently overwrites an earlier outcome. GUID collisions are not realistic, but a `Payment.Id` ever becoming non-unique (e.g. someone adds an int id elsewhere) would let a Declined overwrite an Authorized.

**Where:** `src/PaymentGateway.Api/Persistence/MongoPaymentsRepository.cs:31-35`.

**Options:**

- Drop the upsert; treat "row already exists" as a `DuplicateKeyException` and fail loudly (or translate to a domain-level "duplicate write" error).
- Add an integration test asserting `ReplaceOne` is **not** called when the `_id` already exists in the collection.

### 3.6 Observability — correlation id, structured logging

When a merchant reports "my payment is missing", today we have:

- AuditMiddleware writes a record keyed by JWT `sub` (see `src/PaymentGateway.Api/Middleware/AuditMiddleware.cs`).
- `AcquiringBankClient` records `payments.bank.call.duration` with an outcome tag (ADR-0007).
- No correlation id propagates from the merchant request → audit record → bank-call log → Mongo document.

**Options:**

- Add a `X-Correlation-Id` (or generated) header, push it into `HttpContext.TraceIdentifier`, stamp it on the audit record, the bank-call metric tags, and (optionally) a `correlationId` field on `PaymentDocument`.
- Introduce a typed `IAuditContext` populated by the handler instead of the current `HttpContext.Items[AuditConventions.OutcomeItemKey]` stringly-typed side channel at `PaymentsController.cs:73`.

### 3.7 JWT trust — defense in depth

`PaymentsController.CallerMerchantId()` (`src/PaymentGateway.Api/Controllers/PaymentsController.cs:117-120`) trusts the `sub` claim unconditionally. The JWT bearer middleware (configured in `src/PaymentGateway.Api/Configuration/JwtAuthenticationServiceCollectionExtensions.cs`) is the single point that validates the token; any future loosening (algorithm confusion, key rotation bug, missing `MapInboundClaims = false`) would let one merchant become another.

**Options:**

- Re-validate the `sub` claim against the merchant credential cache on first use per request (cheap, since the cache is already there).
- Confirm `ValidAlgorithms = ["HS256"]` is pinned and `MapInboundClaims = false` is set, both already called out in handoff.md open-questions.

## 4. Cross-cutting

- **Test seam changes.** `InMemoryPaymentsRepository` (`src/PaymentGateway.Api/Persistence/InMemoryPaymentsRepository.cs`) is the test fake; any of §3.2–3.5 needs it to grow (e.g. `TryAdd` semantics, transient-error simulation) before the Mongo impl changes.
- **Backward-compat story.** Stage 12 broke `IPaymentsRepository` from sync to async; any further interface change (idempotency tokens on the repo? bank-reference field on `Payment`?) is another breaking change. Plan it.
- **ADRs to write** as each item is taken up: §3.2 (outbox / pending status), §3.3 (circuit breaker / bulkhead), §3.4 (Mongo retry/timeout policy), §3.6 (correlation id).

## 5. How a future session picks this up

1. Read this file, `docs/handoff.md`, `docs/design.md`, and `docs/adr/0001` + `0003`.
2. Decide on §2 (extract handler + pipeline). This unlocks the rest cheaply; do it first.
3. Pick **one** item from §3 at a time. Each gets its own ADR. Don't batch — they each touch different files.
4. Recommended order if doing all: §2 → §3.2 → §3.3 → §3.4 → §3.6 → §3.5 → §3.7.
5. For each: red-green-refactor with the **`tdd`** skill, then `.NET Code Reviewer` review, then user check-in (per the workflow in `docs/handoff.md` "How we work").

## 6. Open questions for the next session

- Is introducing a `Pending` `PaymentStatus` (or a separate `BankIntents` collection) acceptable? It changes the public DTO if added to `PaymentStatus`.
- Does the bank simulator support any kind of status lookup / reference query? If yes, §3.2 closes cleanly via reconciliation. If no, the only defence is the outbox pattern with manual ops review.
- Is `IMemoryCache` an acceptable backing for the idempotency-key store in this codebase, or should it move to Mongo (consistent with §3.4's hardening) before any other change?
- For §3.3, is Polly already in the dependency graph? (Check `Directory.Packages.props` / `*.csproj`.)