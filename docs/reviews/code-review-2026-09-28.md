# .NET Code Review — 2026-09-28

**Reviewer**: .NET Code Reviewer Agent
**Scope**: §2 refactor of `POST /api/payments` orchestration (per `docs/post-payment-orchestration-improvements.md`): extract `ProcessPaymentHandler` as the single use case, move cross-cutting concerns onto decorator classes (`MetricsDecorator`, `AuditOutcomeDecorator`) that compose via DI, and move domain construction onto `Payment.FromBankOutcome` / `BankPaymentRequest.FromMerchantRequest` factories. Renames `IPaymentsService` → `IPaymentsHandler`, `PaymentsService` → `ProcessPaymentHandler`; controller drops the audit-outcome side-channel (it moves into the decorator). Validated against the architecture-test suite (24 passing) and the full test suite (215 passing, 0 failing, 7 skipped integration).
**Date**: 2026-09-28
**.NET version**: net10.0 (`Nullable` enabled; `TreatWarningsAsErrors`/`GenerateDocumentationFile` not set)
**Overall impression**: This is a textbook "extract handler + compose decorators" refactor. The rename lands cleanly (no residual `IPaymentsService`/`PaymentsService` references anywhere in code), the two decorators are each a single-purpose, ~30-line class with their own focused unit tests, the factories are pure and individually tested, and the layering remains sound: the controller's import set narrows (drops `AuditConventions`), and the abstractions stay unidirectional. The only items worth attention are XML-doc `<see cref>` references that were unresolved by the rename (same family of issue as code-review-2026-09-27 R-001–R-004), two cosmetic consistencies, and the now-stale historical design-doc references to the old type names.

---

## Summary

| Priority | Count |
|---|---|
| 🔴 Blocker | 0 |
| 🟡 Suggestion | 1 |
| 💭 Nit | 4 |
| **Total** | **5** |

---

## Layering verdict (explicitly requested)

**Code-level dependency direction is clean — the rename and decorator extraction did not break any architectural invariant.**

Walk-through against the architecture tests (all 24 still pass per the user):

- **Abstractions → nothing else.** `IPaymentsHandler.cs` imports only `PaymentGateway.Api.Exceptions`, `PaymentGateway.Api.Models`, `PaymentGateway.Api.Models.Requests` (the last is a sibling sub-namespace of `Models`, not a separate layer). It has no code reference to `Services`/`Metrics`/anything outward-facing. The `<see cref="Services.*Decorator"/>` references in its `<remarks>` block are doc-only (see R-001) and are not picked up by `HaveDependencyOn`.
- **Services → Abstractions, Metrics, Models, Requests, Bank (all inward).** Both `MetricsDecorator` and `AuditOutcomeDecorator` are in `Services` and import only `Abstractions` (and `Metrics`, `Models` for the type they receive/return). The `<see cref="Middleware.AuditMiddleware"/>` in `AuditOutcomeDecorator.cs` is doc-only (R-002). NetArchTest's `Adapters_do_not_depend_on_web_or_configuration` only inspects IL/code references, not XML comments, so this passes.
- **Models → only its own sub-namespaces.** `Payment.cs` now imports `Models.Bank` and `Models.Requests` (for the factory signatures). These are sub-namespaces of `Models`, so the architecture test's `Models_depend_on_nothing_else_in_the_application` rule continues to pass — that rule asserts *no dependency on the other named layers*, and `Models.Bank`/`Models.Requests` are not in the forbidden list.
- **Controllers → Abstractions only.** `PaymentsController.cs` now imports `Abstractions`, `Exceptions`, `Metrics`, `Models`, `Models.Requests`, `Models.Responses` — no `Services`, no `Persistence`, no `Clients`, no `Middleware`, no `Filters`, no `Configuration`. The controller's direct coupling to `AuditConventions` (R-005 from the previous review) is now genuinely gone: the import of `Abstractions` remains but the controller no longer writes to `HttpContext.Items[...]`.
- **Configuration → everything.** `PaymentsHandlerServiceCollectionExtensions.cs` imports `Abstractions`, `Metrics`, `Services` — correct, because it is the composition root for the chain and explicitly composes across layers.
- **PCI safety net still holds.** The architecture test scans property/field *declarations* for sensitive names. The new factory methods in `Payment.FromBankOutcome` and `BankPaymentRequest.FromMerchantRequest` only *read* existing properties (the request DTO's `CardNumber`/`Cvv`); they do not introduce any new sensitive-named member on a type outside `{PostPaymentRequest, BankPaymentRequest}`. `request.CardNumber![^4..]` correctly keeps only the last four. The leading-zero regression test (`FromBankOutcome_preserves_a_leading_zero_in_the_last_four`) locks in that string-typed handling survives the move.

The decorator pattern was the right call here: with one use case (this is the only `IPaymentsHandler.ProcessPaymentAsync` consumer), a MediatR pipeline would have added a registration surface and a `IRequestHandler<TRequest, TResponse>` indirection for no behavioral payoff. The decorator chain is explicit (`AuditOutcomeDecorator` wrapping `MetricsDecorator` wrapping `ProcessPaymentHandler`), testable in isolation, and the `IPaymentsHandler` is still the only port the controller knows about.

---

## Findings

### R-001 · 🟡 Suggestion · Maintainability

**Location**: `src/PaymentGateway.Api/Abstractions/IPaymentsHandler.cs` (lines 14–18) → type-level `<remarks>` block
**Description**: The `<remarks>` block uses `<see cref="Services.MetricsDecorator"/>` and `<see cref="Services.AuditOutcomeDecorator"/>` — both are in `PaymentGateway.Api.Services`, which this file does not import, so both crefs are unresolved. It also recapitulates R-001 from `code-review-2026-09-27.md`: an abstraction documenting its concrete implementations is a mild inward→outward doc coupling (the doc now implies `Abstractions` knows which concrete decorators wrap it).

**Why it matters**: Two issues in one — the same pattern called out in the previous review for `IPaymentsRepository`. (1) IDE navigation/tooltips on those doc comments won't resolve; (2) the abstraction's doc enumerates the implementations by name, which is the precise smell the previous review called "mild layering smell" for `IPaymentsRepository`. The wrong fix is to add `using PaymentGateway.Api.Services;` — that would muddle the layer boundary even at the doc level. The right fix drops the hard references and keeps the knowledge in prose.

**Suggested fix**:

```csharp
// Before — abstraction crefs its implementations (unresolved + inward-pointing)
/// <remarks>
/// Cross-cutting concerns — the <c>payments.processed.count</c> metric and the audit-outcome
/// side-channel — are composed onto this interface as decorators
/// (<see cref="Services.MetricsDecorator"/>, <see cref="Services.AuditOutcomeDecorator"/>). The
/// controller depends only on this abstraction; the concrete handler is the inner-most link of the
/// DI-composed chain (Program.cs).
/// </remarks>

// After — describes the contract without naming the concrete decorators
/// <remarks>
/// Cross-cutting concerns — the <c>payments.processed.count</c> metric and the audit-outcome
/// side-channel — are composed onto this interface as decorators (one for the metric, one for the
/// audit-outcome key). The controller depends only on this abstraction; the concrete handler is
/// the inner-most link of the DI-composed chain (see <c>Program.cs</c>).
/// </remarks>
```

The previous review explicitly closed R-001 with "the right fix keeps the knowledge but drops the hard reference"; this is the same fix applied consistently.

---

### R-002 · 💭 Nit · Maintainability

**Location**: `src/PaymentGateway.Api/Services/AuditOutcomeDecorator.cs` (line 11) → type-level `<summary>` cref
**Description**: `<see cref="Middleware.AuditMiddleware"/>` refers to a type now in `PaymentGateway.Api.Middleware`, which this file does not import, so the cref is unresolved.

**Why it matters**: Doc-only — no behavior impact, no architecture-test impact (NetArchTest inspects IL). The cross-reference is genuine documentation value here (the decorator exists to feed the middleware's `HttpContext.Items` lookup), so unlike R-001 this cref *is* the right knowledge to keep — just expressed without a hard type reference.

**Suggested fix**: Drop the cref, keep the prose.

```csharp
// Before
/// <see cref="AuditConventions.OutcomeItemKey"/>) on the active <see cref="HttpContext"/> with
/// the adjudicated <see cref="Models.PaymentStatus"/> name. The audit middleware
/// (<see cref="Middleware.AuditMiddleware"/>) reads this to distinguish Authorized vs Declined

// After — same prose, doc link dropped to plain code font (the cref was the only thing that wouldn't render)
/// <see cref="AuditConventions.OutcomeItemKey"/>) on the active <see cref="HttpContext"/> with
/// the adjudicated <see cref="Models.PaymentStatus"/> name. The audit middleware reads this to
/// distinguish Authorized vs Declined
```

---

### R-003 · 💭 Nit · Maintainability

**Location**:
- `src/PaymentGateway.Api/Services/MetricsDecorator.cs` (line 26) → `Models.Requests.PostPaymentRequest`
- `src/PaymentGateway.Api/Services/AuditOutcomeDecorator.cs` (lines 29–30) → `Models.Payment`, `Models.Requests.PostPaymentRequest`
- `src/PaymentGateway.Api/Services/ProcessPaymentHandler.cs` (lines 22–23) → same fully-qualified `Models.Requests.PostPaymentRequest`

**Description**: The three new `Services` files fully qualify `PostPaymentRequest` and `Payment` instead of adding a `using` for `PaymentGateway.Api.Models.Requests`. Everywhere else in the codebase (e.g. `PaymentsController.cs`, `IAcquiringBankClient.cs`, the test files) these are imported with `using` directives and used unqualified.

**Why it matters**: Purely a consistency/readability nit. The fully-qualified form in `MetricsDecorator.ProcessPaymentAsync` makes the 4-line method read more like an FQN exercise than a one-line signature. Worth normalizing to the codebase convention while the change is fresh, since this is the moment the new files are being read most.

**Suggested fix**: Add a `using PaymentGateway.Api.Models.Requests;` to each of the three `Services` files (and confirm `using PaymentGateway.Api.Models;` is present for `Payment`), then drop the `Models.Requests.` and `Models.` prefixes in the method signatures. Test files that mirror these signatures should be normalized the same way if you also write more tests in this style.

---

### R-004 · 💭 Nit · Maintainability

**Location** (stale doc references introduced/left behind by the rename):
- `docs/design.md` line 22: `IPaymentsService (orchestration)` (component diagram label)
- `docs/design.md` line 122–125: prose still says "`PaymentsService` is registered **scoped**…"
- `docs/design.md` line 180: "`PaymentsService` orchestration logic with a mocked…"
- `docs/handoff.md` line 46 and lines 56, 124, 126: multiple references
- `docs/adr/0006-modular-monolith-multi-acquirer.md` line 47
- `docs/adr/0007-custom-metrics.md` line 48 (`from PaymentsService (Authorized/Declined)`)
- `docs/adr/0008-architecture-and-performance-testing.md` line 20
- `docs/implementation-plan.md` line 69
- `docs/production-architecture.md` line 39

**Description**: The rename to `IPaymentsHandler` / `ProcessPaymentHandler` is complete in code (verified — no `IPaymentsService`/`PaymentsService\b` references remain in `src/` or `test/`), but historical design docs still reference the old names. Several of these are recent (ADR-0007 still attributes the metric to `PaymentsService`; ADR-0008 lists `IPaymentsService` as an example of a port name).

**Why it matters**: These are reference docs readers consult when picking up the codebase, and the names disagreeing with code is exactly the kind of drift R-006 of `code-review-2026-09-27.md` warned about ("doc-cref drift"). Most are one-line s/`PaymentsService`/`ProcessPaymentHandler`/g edits. ADR-0008 in particular is now slightly misleading because it lists `IPaymentsService` as the type the arch-test's `typeof()` assertion points at — the assertion now points at `IPaymentsHandler`.

**Suggested fix**: No code change. A single sweep through the eight files above is enough. Lower priority than R-001 — the rename in code is correct and the docs were written as-of their respective ADRs — but flagging here so the sweep happens before someone reads ADR-0008 next and goes looking for `IPaymentsService`.

---

### R-005 · 💭 Nit · DI / Lifetime

**Location**: `src/PaymentGateway.Api/Configuration/PaymentsHandlerServiceCollectionExtensions.cs` (lines 26–39)
**Description**: The chain is registered as three scoped concrete types plus one scoped `IPaymentsHandler` factory that constructs `AuditOutcomeDecorator(MetricsDecorator(ProcessPaymentHandler))`. The factory calls `sp.GetRequiredService<ProcessPaymentHandler>()` to obtain the scoped core, then `new`s the two decorators around it.

**Why it matters**: This is the correct shape and worth calling out as a positive finding rather than a problem. The chain is scoped (matching the prior `PaymentsService` lifetime and preserving the rationale captured in `ServiceLifetimeTests.PaymentsHandler_chain_is_scoped_not_singleton` — a singleton would pin the typed `HttpClient` across the process). The decorator registration as concrete types (`AddScoped<ProcessPaymentHandler>()`, `AddScoped<MetricsDecorator>()`, `AddScoped<AuditOutcomeDecorator>()`) is a deliberate seam: a future test that wants a partial chain (e.g. just `MetricsDecorator(ProcessPaymentHandler)`) can resolve the inner link directly without having to compose the full DI graph. The doc comment on the extension makes this explicit ("Each link is also registered as itself so a future test (or alternative composition) can construct a partial chain without the full DI graph").

A related positive: `MetricsDecorator` captures `PaymentMetrics` (singleton) and `AuditOutcomeDecorator` captures `IHttpContextAccessor` (singleton) — both safe to capture in a scoped decorator (singleton→scoped is the safe direction; there is no captive-dependency issue).

**Suggested fix**: None. Flagged only because the lifetime choice and the rationale linking back to ADR-0001 are now spread across the extension method's XML doc, `ServiceLifetimeTests`, and the test doc-comment — a one-line cross-reference in the extension's `<remarks>` pointing at the test would make this easier to follow in future, but that's a polish item, not a finding.

---

## Praise / what's done well

- **The rename is clean.** Verified via `grep -rn "IPaymentsService\|PaymentsService\b"` across the repo: zero matches in `src/` and `test/`. Test imports, the architecture-test `Assembly Api` anchor, the `ServiceLifetimeTests` resolver, the controller constructor — every consumer was updated together. The architecture-test's `typeof(IPaymentsHandler).Assembly` is now the only anchor needed (one less thing to drift).
- **The factories are pure and individually tested.** `Payment.FromBankOutcome` (7 tests) and `BankPaymentRequest.FromMerchantRequest` (2 tests, including the MM/yyyy formatting theory) live in a new `test/PaymentGateway.Api.Tests/Domain/` folder with zero mocking and zero I/O — exactly the shape that survives future refactors of the surrounding use case. The leading-zero regression test in `PaymentFactoryTests` is the kind of "boring" test that pays for itself the day someone simplifies `[^4..]` to `.Substring(...)` without thinking.
- **The decorator tests are the right unit-of-decoupling.** Each decorator is tested with a `Substitute.For<IPaymentsHandler>()` as the inner — no DI, no `WebApplicationFactory`, no real `HttpContext`. The two tests that do need an `HttpContext` (`AuditOutcomeDecoratorTests.Does_not_stamp_the_outcome_key_when_the_inner_handler_throws`, `Does_not_throw_when_there_is_no_active_HttpContext`) construct a `DefaultHttpContext` directly rather than going through the test host — that's the correct lightweight seam.
- **`AuditOutcomeDecorator` correctly does not stamp the outcome on bank failure.** The `_httpContext.Items.ContainsKey(AuditConventions.OutcomeItemKey).Should().BeFalse()` test in `AuditOutcomeDecoratorTests` locks in the invariant that mirrors the pre-decorator behavior (controller only set the key on the success path), and the audit middleware then derives `BankUnavailable`/`InternalError` from the status code for the failure path. The behavior is provably unchanged.
- **`MetricsDecorator` carries forward the same "record only on success" invariant.** `Does_not_record_a_processed_payment_when_the_inner_handler_throws` is the lock-in. The previous `PaymentsService` had the same shape (the `RecordProcessed` call was *after* `await _repository.AddAsync`), so the metric semantics are preserved exactly.
- **The decorator-chain composition is explicit and the order is correct.** `AuditOutcomeDecorator(MetricsDecorator(core))` means audit-outcome stamping wraps the whole thing, including metric recording — which is what you want, because the audit middleware reads the outcome after the entire pipeline completes, and there's no scenario where the metric fires but the audit key isn't set (or vice versa) for the same adjudicated outcome.
- **`AddHttpContextAccessor()` belongs inside `AddPaymentsHandler()` rather than in `Program.cs`.** The accessor exists only to support `AuditOutcomeDecorator`; putting its registration in the extension method means the chain's "given a default DI graph, it works" contract is self-contained. `AddHttpContextAccessor` is internally idempotent (`TryAddSingleton`), so the placement is safe even if a future caller double-registers.
- **The `<see cref="AuditConventions.OutcomeItemKey"/>` cref in both decorators resolves correctly** — they both `using PaymentGateway.Api.Abstractions`, so the constant is in scope. That was the right cross-reference to keep (it is the *port* contract the decorator participates in, not the concrete implementation, so the layering is sound).
- **The `IPaymentsHandler` `<exception>` tags resolve correctly.** The interface imports `PaymentGateway.Api.Exceptions` (matching the pattern in `IAcquiringBankClient`), so both `<exception cref="BankUnavailableException"/>` and `<exception cref="InvalidBankRequestException"/>` produce the correct IntelliSense. This continues the doc-quality fix from the previous review (R-002).
- **No regression risk in the existing 215 tests.** The `PaymentsServiceTests` → `ProcessPaymentHandlerTests` rename was clean, the deleted metric tests (`Records_a_processed_payment_tagged_by_adjudicated_status_and_currency_on_success`, `Does_not_record_a_processed_payment_when_the_bank_fails`) were re-homed to `MetricsDecoratorTests` rather than dropped, and the bank-failure propagation test (`A_bank_failure_propagates_and_nothing_is_persisted`) is still in `ProcessPaymentHandlerTests` proving the inner handler still short-circuits before `AddAsync` (the inner-link behavior the controller and audit path both depend on).

---

## Decision-area spot-checks (not relitigated, verified intact)

- **Validation stays in the controller.** `PaymentsController.ProcessPayment` still calls `_validator.ValidateAsync` before the handler, still calls `_metrics.RecordRejection` on the 400 path (with the same `NormaliseCurrency` cardinality-bounded currency tag), still returns `ValidationProblemDetails`. The decorator chain is unaware of validation — correct per the decision.
- **Controller no longer touches `HttpContext.Items[AuditConventions.OutcomeItemKey]`.** Verified by diff: the line is gone, the `using PaymentGateway.Api.Abstractions` import remains (the controller still needs `IPaymentsHandler`, `IPaymentsRepository`, `AuditConventions` is no longer needed) — wait, the import *is* still needed for `IPaymentsHandler`/`IPaymentsRepository`, so it stays. The audit-key write moved to `AuditOutcomeDecorator`, which is the only place that touches it now. The `Controllers_do_not_depend_on_middleware_or_filters` architecture test continues to pass for the same reason as before.
- **`ProcessPaymentHandler` is intentionally narrow.** It does exactly four things: build bank request, ask bank, build domain via `Payment.FromBankOutcome`, persist. No metrics, no audit stamping, no logging — all moved out. This is the shape that lets §3.2 (outbox/pending status), §3.3 (circuit breaker), §3.4 (Mongo retry) slot in as additional decorators around `ProcessPaymentHandler` without rewriting it.
- **`BankPaymentResponse.AuthorizationCode` is still not persisted.** The factory uses only `bankResponse.Authorized`; the `AuthorizationCode` is discarded, matching `design.md` and `BankPaymentResponse`'s own XML doc ("not part of the merchant-facing contract and is not persisted").
