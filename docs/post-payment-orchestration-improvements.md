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

## 7. Update — §2 implemented (2026-09-28)

§2 (extract handler + pipeline) is now in place on `main` (uncommitted, awaiting the user's
check-in commit). The shape landed:

- **`IPaymentsService` → `IPaymentsHandler`** (port renamed; controller still depends on the
  abstraction only — architecture test verifies).
- **`PaymentsService` → `ProcessPaymentHandler`** (the use-case orchestrator, now narrowed to:
  build bank request → call bank → `Payment.FromBankOutcome` → persist). No metric, no audit
  side-channel — those moved out.
- **`Payment.FromBankOutcome(merchantId, request, bankResponse)`** factory on the domain
  (`src/PaymentGateway.Api/Models/Payment.cs`). 7 unit tests under
  `test/.../Domain/PaymentFactoryTests.cs`, no I/O, no DI.
- **`BankPaymentRequest.FromMerchantRequest(request)`** factory for the bank wire-format
  conversion (`src/PaymentGateway.Api/Models/Bank/BankPaymentRequest.cs`). 2 unit tests under
  `test/.../Domain/BankPaymentRequestFactoryTests.cs`, including the zero-pad / year-not-truncated
  formatting theory.
- **Decorator chain composed in DI** (`Configuration/PaymentsHandlerServiceCollectionExtensions.cs`,
  called from `Program.cs` as `AddPaymentsHandler()`):
  `AuditOutcomeDecorator → MetricsDecorator → ProcessPaymentHandler` (outer to inner). Each link
  is registered as itself so a partial chain is testable without the full graph. Lifetime is
  `Scoped` (preserves the ADR-0001 / `ServiceLifetimeTests` rationale that captured
  `IAcquiringBankClient` must not be pinned for the process).
- **`MetricsDecorator`** records `payments.processed.count` on success; bank failure propagates
  untouched and no metric is recorded. 5 unit tests in `MetricsDecoratorTests`.
- **`AuditOutcomeDecorator`** stamps `HttpContext.Items[AuditConventions.OutcomeItemKey]` with
  the adjudicated `PaymentStatus` name on success; skips on bank failure; skips if no active
  `HttpContext`. 5 unit tests in `AuditOutcomeDecoratorTests`.
- **`PaymentsController`** now takes `IPaymentsHandler` (no more `IPaymentsService`); the
  `HttpContext.Items[...]` audit side-channel is gone from the controller (moved into the
  decorator). Validation, `RecordRejection`, exception→status-code mapping, and the
  `CreatedAtAction` response shape stay in the controller (per the user's design choice:
  validation stays where the 400 response shape lives).

**Test status after §2**: 215 passing, 0 failing, 7 skipped integration. Architecture tests: 24
passing. Three pre-existing analyzer warnings (CS8619 in `PaymentValidationServiceCollectionExtensionsTests.cs`
+ two CS4014 in `ProcessPaymentHandlerTests.cs`) are unchanged from baseline — none introduced by
this refactor.

**Code review**: `docs/reviews/code-review-2026-09-28.md`. 0 blockers, 1 suggestion (R-001:
drop abstraction→implementation crefs in `IPaymentsHandler.cs` — applied), 4 nits applied
(doc-crefs in decorators normalized to prose; `using` directives added to the three new
`Services` files; stale historical doc references to the old names swept in
`design.md`/ADR-0006/ADR-0007/ADR-0008/`implementation-plan.md`/`production-architecture.md`).

**What §3 looks like now**: the inner `ProcessPaymentHandler` is intentionally narrow
(4 lines of body), so §3.2 (outbox/pending), §3.3 (circuit breaker), §3.4 (Mongo retry) all
slot in as additional decorators around it without rewriting it. Each becomes one testable
class — the shape §2 was meant to enable. The recommended order in §5 stands.

**Open questions carried forward from §6** (unresolved, still relevant for §3):
- Is a `Pending` `PaymentStatus` acceptable? §3.2.
- Does the bank simulator support a status-lookup / reference query? §3.2.
- Is `IMemoryCache` an acceptable idempotency-key backing? §3.4.
- Is Polly already in the dependency graph? §3.3. (Spot-check: it's NOT in `PaymentGateway.Api.csproj`
  or the test csproj at the time of this writing — would need to be added.)

## 8. Plan — §3.2 (Option B / mongo-backed outbox) + ADR-0012 (Option E / bank-side Idempotency-Key)

Both options ship together — complementary gaps (B closes "gateway crashed silently";
E closes "merchant retries after gateway 5xx"). E's signature change to
`IPaymentsHandler.ProcessPaymentAsync` has to land before B's body change so we don't
revisit the signature twice. E first because it's more self-contained (one header, one
parameter, one simulator stub); B is the bigger architectural lift and benefits from a
settled handler signature.

### End-state shape

```
POST /api/payments (Idempotency-Key: K)
   │
   ▼
PaymentsController
   │  validates, maps 400/503/500
   │  merchantId  = sub claim
   │  bankKey     = Idempotency-Key header (forwarded verbatim — R-4)
   ▼
IPaymentsHandler (AuditOutcomeDecorator → MetricsDecorator → ProcessPaymentHandler)
   │
   │  handler.ProcessPaymentAsync(req, merchantId, bankKey=K)
   │  ┌── B: insert BankIntent { Id=paymentId, Status=Pending, Request=sanitizedSnapshot }
   │  ├── E: bankClient.ProcessPaymentAsync(bankReq, idempotencyKey=K)
   │  ├── B: update BankIntent { Status=Authorized/Declined, Response=... }
   │  ├── B: insert Payment { Id=paymentId }
   │  └── B: update BankIntent { Status=Reconciled }
   │
   ▼
BankIntents collection (durable outbox)
   └── hosted BankIntentReconciler polls every Ns:
       for each stale intent where status ∈ {Pending, Authorized, Declined}:
         • Authorized/Declined + no Payment:
             rebuild Payment, persist, mark Reconciled
         • Pending (old):
             increment Attempts, log alert (R-7: leave Pending, ops investigates)
```

### Decisions locked

- **R-4 (key forwarding)**: Forward the merchant's `Idempotency-Key: K` verbatim to the
  bank. The merchant→gateway filter (ADR-0003) already requires the key to be unique
  enough; bank's namespace is opaque to merchants. No namespacing.
- **R-7 (stale Pending)**: Reconciler increments `Attempts` and logs/alert; status stays
  `Pending`. Ops investigates via the bank's transaction report. No auto-cancel, no metric
  spike — the `Attempts` counter is the visible signal.
- **Simulator cache scope** (E): only 2xx responses are cached under the Idempotency-Key.
  4xx/5xx never are — a retry after a transient bank failure must re-attempt, not replay
  the failure.
- **Mongo transactions**: not used. The 4-write flow in `ProcessPaymentHandler` is
  sequential, accepting eventual consistency. Single-node Mongo in `docker-compose.yml`
  doesn't support transactions; production replica set would, but the reconciler is the
  consistency mechanism by design — no extra value from transactions.
- **`BankIntent.Id == Payment.Id`**: outbox record and the eventual Payment share an id.
  If the gateway crashes mid-flow, the sweeper-materialized Payment has the same id the
  cached Idempotency-Key response would have used. No id-mismatch bugs.
- **`BankIntentRequest` is sanitized**: stores last-four, expiry, currency, amount. Never
  PAN, never CVV. The PCI safety net (ADR-0008 architecture test) still holds.
- **`BankPaymentResponse.AuthorizationCode`** stays on the wire but is not persisted in
  the `BankIntent` either (mirrors the existing `Payment` policy from `design.md`).

### TDD slices (per-slice review + check-in; user commits each)

| # | Slice | What's added/changed | Tests added | What review covers |
|---|---|---|---|---|
| **E1** | Bank client header | `IAcquiringBankClient` +1 param, `AcquiringBankClient` sends header | 2 | Header sent when key provided; absent when not |
| **E2** | Handler signature | `IPaymentsHandler` +1 param, decorator forwarding | 1 + 4 updated | Parameter threaded through chain; existing handler tests still pass |
| **E3** | Controller wiring | `PaymentsController` reads `Idempotency-Key` | 1 | Header → handler (only when non-blank) |
| **E4** | Simulator | `imposters/bank_simulator.ejs` cache stub + behaviors | 1 integration | First POST under K → cache miss; second → cache hit |
| **E5** | ADR + docs | `docs/adr/0012-bank-side-idempotency-key.md`, `docs/design.md` | — | Design rationale recorded |
| **B1** | Domain + in-memory repo | `BankIntent`, `BankIntentRequest`, `BankIntentStatus`, `IBankIntentsRepository`, `InMemoryBankIntentsRepository` | ~7 | Factory sanitizes PAN; repo CRUD; `FindStaleAsync` |
| **B2** | Handler outbox body | `ProcessPaymentHandler` 4-write flow, `Payment.FromBankOutcome` optional id | 4 + updated | One test per crash-point |
| **B3** | Reconciler logic | `IntentReconciliationLogic` | ~4 | Stale Authorized/Declined → create Payment; stale Pending → increment; existing Payment → mark Reconciled |
| **B4** | Reconciler host + Mongo + DI | `BankIntentReconciler` `BackgroundService`, `MongoBankIntentsRepository`, `Configuration/BankIntentsServiceCollectionExtensions`, `Program.cs`, `appsettings.json` | ~3 | Lifetime correct; index `(Status, UpdatedAt)` created on startup |
| **B5** | Integration test + ADR | `Integration/BankIntentReconciliationIntegrationTests`, ADR-0013 (outbox) | 1 integration | End-to-end: crash mid-handler → sweeper materializes Payment within poll interval |

### Combined Idempotency-Key semantics (three layers)

| Layer | Owns | Cache | Survives restart? |
|---|---|---|---|
| Merchant → Gateway | `IdempotencyResourceFilter` (`IIdempotencyStore`) | In-memory `ConcurrentDictionary` (ADR-0003: 2xx+400 cached, 5xx released) | No |
| Gateway → Bank | Mountebank `state.idemCache` | In-memory; 2xx only | No |
| Internal recovery | `BankIntents` + reconciler | Mongo | Yes |

### Risks tracked (none blocking; documented where relevant)

- **R-1** (architectural): sweeper-side bug could create intents without payments or vice
  versa. Mitigated by B5's integration test, mandatory.
- **R-2** (simulator state): idempotency cache lost on simulator restart. ADR-0012
  documents; not solved (matches the reality of any real bank's cache persistence story
  being external to the gateway).
- **R-5** (Mongo transactions): sequential writes, no transactions. ADR-0013 will
  document as a deliberate design choice.
- **R-6** (storing the merchant's `Idempotency-Key` on `BankIntent`): NOT in scope.
  ADR-0013 notes as a follow-up — the sweeper retries bank calls for stale Pending without
  a key, accepting the very-small double-charge risk in the un-bank-resolvable case.
  Adding it later is one field + one parameter.

### Size

- New files: 14
- Modified files: 17 (production + tests + docs)
- Tests added: ~35; tests updated: ~6
- Wall-clock estimate (TDD red-green-review-check-in per slice): 3-4 working sessions.