# .NET Code Review — 2026-09-27 (#2)

**Reviewer**: .NET Code Reviewer Agent
**Scope**: Item 2 — Observability + custom metrics (ADR-0007, commit `2787f72`) and Item 3 — Architecture tests + R-005 fix (ADR-0008, commit `e89d2a9`), diffed against base `98b1a4a`. Item 1 (namespace refactor) already reviewed and excluded.
**Date**: 2026-09-27
**.NET version**: net8.0 (JwtBearer/HttpClient stack), OpenTelemetry 1.19.1 / Prometheus exporter 1.19.1-beta.1
**Overall impression**: High-quality, test-first work. The metric instrumentation is vendor-neutral, cardinality-conscious, and the tricky bank-call outcome mapping (including the caller-cancellation edge case) is correct and explicitly covered by tests. The architecture suite is genuinely non-vacuous — a rare thing — with a populated-namespace guard and a positive "teeth" test. The one finding worth acting on before this ships is that the unauthenticated `/metrics` endpoint is swept into the audit trail, which both pollutes the forensic log (ADR-0004) and puts continuous write load on Mongo.

---

## Summary

| Priority | Count |
|---|---|
| 🔴 Blocker | 0 |
| 🟡 Suggestion | 3 |
| 💭 Nit | 4 |
| **Total** | **7** |

---

## Findings

### R-001 · 🟡 Suggestion · Architecture / Observability

**Location**: `src/PaymentGateway.Api/Program.cs` (lines 68, 78) → `AuditMiddleware` interaction with `MapPrometheusScrapingEndpoint`
**Description**: `AuditMiddleware` is registered at the top of the pipeline with no path filter, so it wraps every downstream endpoint — including the `GET /metrics` scraping endpoint. Every Prometheus scrape therefore writes an `AuditRecord` to MongoDB.
**Why it matters**:

`/metrics` is scraped continuously (typically every 10–30s per instance). Each scrape produces an audit record with `MerchantId=""`, `Method=GET`, `Path=/metrics`, `Outcome=Unknown` — thousands per day per pod of pure infrastructure noise in the forensic trail that ADR-0004 exists to keep clean, plus a sustained MongoDB write load that scales with instance count and scrape frequency. It also calls `context.Request.EnableBuffering()` on every scrape. This is not hypothetical: `PrometheusEndpointTests.Factory()` has to swap `IAuditStore` for an in-memory one *specifically because* hitting `/metrics` otherwise drives the Mongo-backed audit store — direct evidence the scrape path is audited.

**Suggested fix**:

```csharp
// In AuditMiddleware.InvokeAsync — skip non-business infrastructure endpoints.
public async Task InvokeAsync(HttpContext context)
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/metrics") || path.StartsWithSegments("/health"))
    {
        await _next(context);
        return;
    }
    // ... existing buffering + audit logic
}
```

Prefer a small, explicit allow/deny path set (or endpoint metadata / a marker attribute) over a broad regex, so the audit scope stays intentional. This is the highest-priority item in the review.

---

### R-002 · 🟡 Suggestion · Observability semantics

**Location**: `src/PaymentGateway.Api/Clients/AcquiringBankClient.cs` → `ProcessPaymentAsync()` (400 branch → `Record(BankCallOutcome.Error)`)
**Description**: A bank `400 Bad Request` (`InvalidBankRequestException`) is tagged `outcome=error` on `payments.bank.call.duration`, the same bucket as `503`/unreachable/unreadable-body failures.
**Why it matters**:

ADR-0007 states this histogram's `outcome` dimension is the failure-rate signal "ADR-0001 requires before a circuit breaker's thresholds could be sized." A `400` is a *client-side* defect — the gateway sent a malformed request; retrying or tripping a breaker would never help. Folding it into `error` conflates "the bank is unhealthy" (availability) with "we have an integration bug" (correctness). Anyone sizing a breaker or alerting on bank availability off the `error` rate will be misled by a spike that is actually our own bad requests. The timeout/error split is deliberately careful elsewhere; the 400 case deserves the same care.

**Suggested fix**:

```csharp
public enum BankCallOutcome { Success, Timeout, Error, InvalidRequest }

// in the 400 branch:
Record(BankCallOutcome.InvalidRequest);
```

A fourth, low-cardinality tag value keeps availability alerting clean while still recording the call's latency. If a fourth value is unwanted for this submission, document explicitly in ADR-0007 that `error` includes bank-side 400s so dashboard authors exclude it from availability math.

---

### R-003 · 🟡 Suggestion · Arch test coverage

**Location**: `test/PaymentGateway.Api.Tests/Architecture/ArchitectureTests.cs` → dependency rules for `Middleware`/`Filters`
**Description**: Only `Controllers` are asserted to depend on ports and not on concrete adapters (`Controllers_depend_on_abstractions_not_on_concrete_adapters`). `Middleware` and `Filters` — which the layer model in the class doc treats as part of Web — are only checked for "must not depend on `Configuration`".
**Why it matters**:

A future `IdempotencyResourceFilter` or a new middleware could take a direct dependency on a concrete `Persistence`/`Clients`/`Services` type and no architecture test would catch it, even though that violates the same "Web depends on ports, not concrete adapters" invariant the controllers rule enforces. The suite otherwise closes its loops carefully, so this asymmetry stands out.

**Suggested fix**:

```csharp
[Fact]
public void Web_components_depend_on_abstractions_not_on_concrete_adapters()
{
    Passes(Types.InAssembly(Api)
        .That().ResideInNamespace(Controllers)
        .Or().ResideInNamespace(Middleware)
        .Or().ResideInNamespace(Filters)
        .ShouldNot().HaveDependencyOnAny(Services, Persistence, Clients)
        .GetResult());
}
```

---

### R-004 · 💭 Nit · Security

**Location**: `src/PaymentGateway.Api/Program.cs` → `app.MapPrometheusScrapingEndpoint()` (line 78)
**Description**: `/metrics` is unauthenticated and reachable anonymously (confirmed: `PrometheusEndpointTests` gets `200` with no bearer token, so there is no fallback `RequireAuthenticatedUser` policy blocking it).
**Why it matters**:

The decision is documented in ADR-0007 with an "restrict at ingress" caveat and the tags are aggregate-only (no PAN/CVV/PII by construction, and the PCI arch test backstops that), so the confidentiality risk is low. But metric labels still leak operational intelligence to anyone who can reach the pod — supported currencies, decline/rejection rates, bank error/timeout rates, retry volume. The mitigation lives entirely outside this repo (ingress/NetworkPolicy), so it is worth confirming that control is actually deployed. Optionally, bind the exporter to a separate management port so it is never co-exposed with the public API surface.

**Suggested fix**: No code change required for the submission. Track the ingress restriction as a deployment prerequisite, and consider a dedicated metrics port in prod hosting config.

---

### R-005 · 💭 Nit · PCI reflection test completeness

**Location**: `test/PaymentGateway.Api.Tests/Architecture/ArchitectureTests.cs` → `Raw_card_fields_exist_only_on_the_designated_request_dtos()`
**Description**: The PCI safety net matches on exact (case-insensitive) *property names* `CardNumber`/`Cvv`/`Pan`, over public instance properties with `DeclaredOnly`.
**Why it matters**:

It is a name-based heuristic, not a data-flow guarantee, and it has three blind spots: (1) public *fields* are not scanned (`GetProperties` only); (2) a PAN under a differently-spelled name (`PrimaryAccountNumber`, `AccountNumber`, `CardNo`, `FullPan`) passes; (3) `DeclaredOnly` skips inherited members (mitigated only if the base type is itself in the assembly). It is a sound, conservative tripwire as documented in ADR-0008 and correctly excludes `CardNumberLastFour`, but it should not be mistaken for proof the PAN can't escape.

**Suggested fix**: Optionally widen to fields and to a substring/regex match on PAN-like names, and assert against `GetProperties() | GetFields()`:

```csharp
var members = Api.GetTypes()
    .Where(t => !allowed.Contains(t))
    .SelectMany(t =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => (t, m.Name))
         .Concat(t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => (t, m.Name))));
```

---

### R-006 · 💭 Nit · Maintainability / dashboard semantics

**Location**: `src/PaymentGateway.Api/Metrics/PaymentMetrics.cs` → `RecordRejection()` (increments `payments.processed.count` with `status=Rejected`)
**Description**: `payments.processed.count` aggregates Authorized + Declined + **Rejected**, but a Rejected payment fails validation in the controller and never reaches the bank — it was not "processed" in the same sense as an adjudicated payment.
**Why it matters**:

This is intentional and matches ADR-0007's documented `status` tag set (Authorized/Declined/Rejected), so it is not a defect. But a dashboard author summing `payments_processed_count` for a throughput or conversion metric will over-count by the rejection volume unless they filter `status!="Rejected"`. Worth a one-line note in the ADR or a runbook so the aggregation isn't misread. (The instrument descriptions already help; a metric named `processed` carrying a never-processed state is the only friction.)

**Suggested fix**: No code change needed; document the semantics, or split rejections onto their own counter if the mixed meaning proves confusing in practice.

---

### R-007 · 💭 Nit · Minor consistency

**Location**: `src/PaymentGateway.Api/Controllers/PaymentsController.cs` → `NormaliseCurrency()`
**Description**: The rejected-path sentinel is lower-case `"unknown"` while real codes are upper-cased (`ToUpperInvariant`); the authorized/declined path emits `payment.Currency` from the validated allow-list (already canonical).
**Why it matters**:

Purely cosmetic — the sentinel and real ISO codes are disjoint value spaces so there is no collision, and cardinality is correctly bounded (this is a good, deliberate control against attacker-inflated tags). Flagged only so the mixed casing is a conscious choice rather than an oversight. The `char.IsAsciiLetter` + `Length: 3` guard is exactly the right shape.

**Suggested fix**: None required. If uniformity is preferred, `"UNKNOWN"` reads more consistently alongside upper-cased codes.

---

## What is done well (explicit praise)

- **Bank-call outcome mapping and the cancellation edge case are correct and provably so.** The `catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)` filter cleanly separates a client-side timeout (record `timeout`) from a genuine caller cancellation (propagate, record nothing), and `Does_not_record_a_bank_call_for_a_caller_initiated_cancellation` locks that behaviour in. No double-recording across the try/catch paths.
- **No request-path regression risk from instrumentation.** All record calls are `Counter.Add`/`Histogram.Record` on non-null string tags — they don't throw for valid input, and the audit write already fails safe.
- **DI is correct.** `TryAddSingleton<PaymentMetrics>` is idempotent; `AddMetrics()` supplies `IMeterFactory`; and because `PaymentMetrics` is a singleton, injecting it into the typed-client `AcquiringBankClient`, the scoped `IdempotencyResourceFilter`, the singleton `PaymentsService`, and the controller introduces **no captive-dependency problem** (a singleton is the longest lifetime, safe to capture anywhere).
- **Cardinality discipline throughout** — constant `acquirer`, bounded rule names, and `NormaliseCurrency` bounding attacker-controlled currency on the reject path; the "no PII in tags by construction" claim holds.
- **The architecture suite has teeth.** The 12-case populated-namespace theory kills the classic vacuous-rule failure mode, and `Controllers_are_built_against_the_abstractions` is a positive assertion that would fail if the detector were broken. The layer model matches the real single-project structure and the ADR-0008 update text honestly.
- **R-005 fix is clean.** Moving `OutcomeItemKey` into `Abstractions.AuditConventions` removes the controller→middleware coupling, and `Controllers_do_not_depend_on_middleware_or_filters` now enforces it permanently.

## Design-decision sanity check

- **(a) `acquirer = "simulator"` constant** — reasonable and consistent with ADR-0006 (multi-acquirer routing out of scope). The tag exists so the histogram is already dimensioned for the future without over-engineering now. ✅
- **(b) `NormaliseCurrency` on the reject path** — correct and well-justified: rejected currency is unvalidated attacker-controllable input, so bounding it to a real 3-letter code or `"unknown"` is the right cardinality defense (see R-007 for the cosmetic-only casing note). ✅

Both are documented accurately in the ADR-0007 "Update — implemented" section, and the ADR-0008 update honestly records the two deviations (NetArchTest vs ArchUnitNET; real namespaces vs Domain/Application/Infrastructure).
