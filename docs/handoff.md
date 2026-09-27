# Handoff: Payment Gateway challenge — ready to start coding

**Repo:** `/home/rafadu/repos/payment-gateway-challenge-dotnet` (branch `rafael-gervasio/my-branch-only`)
**Context:** This is a take-home coding challenge (Checkout.com-style .NET payment gateway) for a
Software Engineer job application. The prior session was a pure design conversation — **no
implementation code has been written yet**, only documentation. This handoff exists so the next
session can go straight to writing code, test-first.

## What the next session is for
Leading the model through actually implementing the gateway, following the plan and decisions
below. Read the referenced docs first — they contain the full reasoning; this file only indexes
them and states what's settled vs. still open.

## Read these first (do not re-derive — the reasoning is already written down)
- `README.md` and `docs/resources/assessment.md` — original challenge instructions/requirements.
- `docs/design.md` — the actual design for what gets built: architecture, request/response
  contracts, status-code/persistence mapping, validation rules, bank integration, concurrency,
  configuration, testing strategy. **This is the spec to implement.**
- `docs/adr/0001-bank-call-resiliency-timeout-only.md`
- `docs/adr/0002-minimum-amount-validation.md`
- `docs/adr/0003-idempotency-key.md`
  — these three ADRs are **in scope for implementation**, same as `docs/design.md`.
- `docs/adr/0004` through `0009`, and `docs/production-architecture.md` — **NOT in scope for
  implementation**. These document a forward-looking "if this went to production" narrative
  (MongoDB, RabbitMQ, modular monolith, metrics, ArchUnitNET, BenchmarkDotNet+k6, GitHub Actions
  CI/CD) that the user deliberately chose to document only, not build, to avoid over-engineering
  the actual submission. Don't let a fresh agent "helpfully" start implementing these.
- `imposters/bank_simulator.ejs` and `docker-compose.yml` — the real bank simulator's wire
  contract (snake_case JSON, `expiry_date` as `"MM/yyyy"`, card-number-ending-digit → outcome
  rules). Integration tests must run against this real container, not a mock (explicit user
  choice — see "Decisions already locked" below).

## Existing scaffold (already in the repo, needs fixing, not just extending)
- `src/PaymentGateway.Api/Controllers/PaymentsController.cs` — only has a GET stub.
- `src/PaymentGateway.Api/Services/PaymentsRepository.cs` — uses a plain `List<T>`, **not
  thread-safe**, needs replacing with a `ConcurrentDictionary`-backed implementation.
- `src/PaymentGateway.Api/Models/Responses/PostPaymentResponse.cs` /
  `GetPaymentResponse.cs` — `CardNumberLastFour` is typed as `int`, which **silently drops a
  leading zero**; must become `string`.
- `src/PaymentGateway.Api/Models/Requests/PostPaymentRequest.cs` — currently wrong shape
  (has `CardNumberLastFour` instead of a full card number field); needs rebuilding per
  `docs/design.md`'s contract section.
- `test/PaymentGateway.Api.Tests/PaymentsControllerTests.cs` — existing GET tests, written
  against a `WebApplicationFactory<PaymentsController>` pattern; extend rather than replace.

## Decisions already locked (don't re-litigate — just implement)
- POST `/api/payments`: `201 Created` for Authorized/Declined (persisted), `400 Bad Request` for
  Rejected/invalid input (not persisted, bank never called), `503 Service Unavailable` for
  bank-unreachable/timeout (not persisted, **no automatic retry** — non-idempotent bank API).
- GET `/api/payments/{id}`: `200 OK` or `404 Not Found`.
- FluentValidation for request validation; rules mirror the assessment's table plus "amount must
  be > 0" (ADR-0002, a deliberate extension beyond the literal spec).
- Currency allow-list is config-driven (`SupportedCurrencies` in appsettings), not hardcoded.
- Bank client: typed `HttpClient` via `IHttpClientFactory`, bounded timeout from config
  (ADR-0001), snake_case DTOs separate from the merchant-facing contract.
- `Idempotency-Key` header support (ADR-0003): opt-in, `IAsyncActionFilter` on the POST action
  only, in-memory `IIdempotencyStore`, claim/complete/release semantics (release, don't cache, on
  a `503` so a genuine retry can still reach the bank).
- Test strategy: fast unit tests (mocked `IAcquiringBankClient`) + component tests
  (`WebApplicationFactory`, non-bank paths: validation 400s, GET 200/404) + integration tests
  against the **real** Mountebank container via `docker-compose up` (explicit user choice over
  mocking the bank client for these specific scenarios).
- Proposed folder structure and the 10-step TDD implementation order are both written out in the
  prior session's transcript; if not otherwise reconstructed, a reasonable order is: domain &
  contracts → validation → repository → bank client → service → idempotency → controller → DI
  wiring (`Program.cs`) → component tests → integration tests.

## Nothing is currently open/undecided
All of the judgment calls for the in-scope implementation were resolved during the design
conversation (rejection status code, persistence scope, bank-failure handling, validation
library, idempotency behavior). The next session should not need to ask the user to re-decide any
of this — just implement per the docs above. If a genuinely new question comes up during coding
that the docs don't answer, that's a real gap worth surfacing, not something to guess past.

## Suggested skills for the next session
- **`tdd`** — primary skill to invoke. The whole plan is explicitly test-first (unit tests per
  validation rule, service tests with a mocked bank client, etc.); this skill should drive the
  red-green-refactor loop for the .NET implementation.
- **`domain-modeling`** — only if implementation surfaces a new architectural decision worth
  recording as an ADR (continue numbering from `0009`). Not needed just to write the planned code.
- **`code-review`** — worth invoking once a meaningful slice of the implementation exists, to get
  an independent pass before considering the submission done.

## Not redacted
No secrets, credentials, or PII appeared in this design conversation — nothing needed redaction.
