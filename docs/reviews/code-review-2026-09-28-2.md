# .NET Code Review — 2026-09-28 (#2) — Full-codebase quality pass

**Reviewer**: Claude Opus 4.8 (via Claude Code), at the developer's request
**Scope**: The entire `src/PaymentGateway.Api` codebase (all 65 source files; ~90% read in full, trivial DTOs/enums skimmed), reviewed against **three lenses only**:
1. **Functional correctness** — does it do the right thing?
2. **Necessary complexity** — is anything complex without cause?
3. **Comment usefulness** — do the comments genuinely help a developer?

This is *not* a standards/style or spec-conformance review. It is a snapshot of `main` after the outbox (B1–B5), the background cleanup service, and the CI work.
**Build / tests**: `dotnet build -c Release` clean (1 pre-existing nullability warning in a test). Full suite **271 passed, 17 skipped, 0 failed**.
**Overall impression**: Senior-level, well-engineered code. The architecture is sound (outbox, `IPaymentsHandler` decorator chain, read-through credential cache, resource-filter idempotency), and the security hygiene is genuinely strong (timing-equalised auth, `HS256` algorithm pinning, PCI-safe snapshots). Comments are, as a rule, the *good* kind — they explain *why* and cite ADRs. **No functional defects were found.** The findings below are all minor; the dominant risk in a codebase this comment-heavy is not bugs or complexity but a handful of **comments that have drifted out of sync with the code** — which for this project is the thing most likely to mislead the next developer.

---

## Summary

| Priority | Count |
|---|---|
| 🔴 Blocker | 0 |
| 🟡 Suggestion | 4 |
| 💭 Nit | 4 |
| **Total** | **8** |

By axis: **Functional** 0 defects (2 robustness watch-items) · **Complexity** 1 · **Comments** 5 · **Cross-cutting** 2.

---

## What's strong (worth preserving)

- **Security.** `TokenIssuanceService` runs BCrypt against a dummy hash for unknown clients (timing-equalised, anti-enumeration), returns a uniform 401, and treats a corrupt stored hash as a 401 not a 500. `JwtAuthentication` pins `HS256` (blocks `alg=none`/confusion), validates lifetime + signing key, and disables inbound claim mapping. Never persists PAN/CVV anywhere (`Payment`, `BankIntentRequest`, audit summary).
- **Bank client** correctly separates a caller-requested cancellation from a client-timeout, and maps bank `400` to an internal error (gateway-built-bad-request) rather than a retriable 503.
- **Idempotency** at two layers is coherent and well-reasoned (merchant→gateway replay in `IdempotencyResourceFilter` + gateway→bank dedupe forwarding).
- **Outbox** recovery is idempotent by construction (shared intent/Payment id + "does the Payment already exist?" check), and the reconciler never re-calls the bank, so it cannot double-charge.
- **Comments** across the codebase explain rationale and cite ADRs — e.g. the `CreatedAt`-vs-`UpdatedAt` explanation in `DeletePendingOlderThanAsync`, the 404-not-403 ownership decision, and the metric cardinality safeguards. This is the standard to hold the drifted comments (below) to.

---

## Findings

### R-001 · 🟡 Suggestion · Comment accuracy (stale doc)

**Location**: `src/PaymentGateway.Api/Services/BankIntentCleanupService.cs` — class `<summary>` (lines 8–21) vs. `CleanupOnceAsync` (line 104)
**Description**: The class summary states the service deletes **Reconciled** intents only ("periodically deletes … `Reconciled` intents … Pending / Authorized / Declined / Cancelled intents are deliberately untouched"). The implementation also deletes **Pending** intents via `DeletePendingOlderThanAsync`. The doc was not updated when Pending-deletion was added.
**Why it matters**: This is the most misleading drift in the codebase. A developer reading the class doc would conclude Pending intents live forever, and could build monitoring or recovery logic on that false premise. The method-level docs on the interface are correct — only the class summary lies.
**Suggested fix**: Update the summary to state both deletions and the shared retention, mirroring the accurate rationale already in `IBankIntentsRepository.DeletePendingOlderThanAsync`.

---

### R-002 · 🟡 Suggestion · Unnecessary complexity (dead registrations)

**Location**: `src/PaymentGateway.Api/Configuration/PaymentsHandlerServiceCollectionExtensions.cs` (lines 27–28, factory 30–39)
**Description**: `AddScoped<MetricsDecorator>()` and `AddScoped<AuditOutcomeDecorator>()` are registered, but the factory constructs both with `new` and never resolves them from the provider. Verified: no code in `src/` or `test/` resolves either type from DI. Only `ProcessPaymentHandler` is genuinely resolved (`sp.GetRequiredService<ProcessPaymentHandler>()`).
**Why it matters**: Two registrations that do nothing. The comment justifies them as "each link registered as itself so a future test can construct a partial chain," but no such test exists — it's speculative (YAGNI) and mildly misleading, since it implies a capability the composition doesn't actually use.
**Suggested fix**: Delete the two unused `AddScoped` lines, *or* make the factory resolve them (`sp.GetRequiredService<MetricsDecorator>()`) so the registrations are real. Deleting is the smaller change and matches how the chain is actually built. Keep the `ProcessPaymentHandler` registration.

---

### R-003 · 🟡 Suggestion · Robustness (startup behavior asymmetry)

**Location**: `src/PaymentGateway.Api/Services/BankIntentReconciler.cs` (lines 49–72) vs. `BankIntentCleanupService.cs` (lines 51–55)
**Description**: The reconciler *reconciles first, then sleeps* — its first Mongo poll fires immediately at startup with no delay. The cleanup service *sleeps first, then acts*. The reconciler's immediate first pass is precisely the cold-start Mongo hit that made the `WebApplicationFactory` component tests hang (now mitigated for tests by the `BankIntents:RunBackgroundServices` flag, but production still eats an immediate poll during host warm-up).
**Why it matters**: The two hosted services behave inconsistently for no stated reason, and the reconciler's eager first pass adds Mongo contention exactly when the process is least ready. Retention/stale windows are seconds-to-days, so a one-interval delay changes nothing functionally.
**Suggested fix**: Give the reconciler a short initial delay (or adopt the cleanup service's sleep-first loop shape) so both services warm up symmetrically. Optional; behavior-affecting, so treat as its own decision.

---

### R-004 · 🟡 Suggestion · Robustness (blocking I/O in constructor)

**Location**: `src/PaymentGateway.Api/Persistence/MongoBankIntentsRepository.cs` (constructor line 38 → `EnsureIndexes` line ~150)
**Description**: The constructor calls `EnsureIndexes()`, a *synchronous* `Indexes.CreateOne` network round-trip. When Mongo is unreachable it blocks ~30s on the driver's server-selection timeout before its best-effort catch swallows the failure. Because it's best-effort it never throws — so it degrades to a silent 30s stall rather than an error.
**Why it matters**: Construction-time blocking network I/O couples object creation to infrastructure availability. It surfaced as a ~30s-per-test stall in the suite (already worked around in tests), and in production it delays first construction of the singleton. Index creation is not on the request hot path and doesn't need to be synchronous with construction.
**Suggested fix**: Move index creation off the constructor — e.g. a `IHostedService`/startup task that runs it once with a bounded timeout, or lazily on first use. Keep the best-effort semantics. Optional; behavior-affecting.

---

### R-005 · 💭 Nit · Comment accuracy (broken doc reference)

**Location**: `src/PaymentGateway.Api/Services/ProcessPaymentHandler.cs` (line ~19)
**Description**: References `docs/post-payment-orchestration-improments.md` — a typo (missing "ve"). The file is `docs/post-payment-orchestration-improvements.md`; 7 other source files reference it correctly.
**Why it matters**: A dead cross-reference in a comment. Minor, but it's a broken link a developer might chase.
**Suggested fix**: `improments` → `improvements`.

---

### R-006 · 💭 Nit · Comment quality (test rationale in production code)

**Location**: `src/PaymentGateway.Api/Services/BankIntentCleanupService.cs` (lines 46–50)
**Description**: The "sleep first, then run cleanup" comment justifies the production behavior partly by test artifacts: *"firing a DeleteMany on Mongo immediately would contend with the integration fixture's probe and with parallel test-host startups."* Now that hosted services are disabled under test via `BankIntents:RunBackgroundServices=false`, that rationale is both leaky (production explained by test concerns) and partly obsolete.
**Why it matters**: A future reader can't tell which part of the rationale is a real production constraint. The genuine production reason is also present (avoid a cold-start thundering herd; retention is days so deferring one interval is harmless) — that's the part to keep.
**Suggested fix**: Drop the fixture/test-host sentence; keep the production rationale.

---

### R-007 · 💭 Nit · Comment accuracy (aspirational)

**Location**: `src/PaymentGateway.Api/Services/IntentReconciliationLogic.cs` — `Pending` branch of `ReconcileOneAsync`
**Description**: The comment reads *"Without bank-side idempotency (ADR-0012), retrying an unknown bank state risks double-charging."* This implies that *with* ADR-0012 the reconciler could safely retry a Pending. As built it cannot: the intent persists neither the idempotency key nor the full PAN, and the bank caches only 2xx — so a reconciler retry has no key to replay and no request body to resend.
**Why it matters**: Reads as a green light for a future change ("just wire up ADR-0012 and retry") that would actually require persisting sensitive data the PCI-safe snapshot deliberately omits.
**Suggested fix**: Clarify that reconciler-side retry is out of scope regardless of ADR-0012, because the intent stores neither the key nor the PAN needed to replay safely.

---

### R-008 · 💭 Nit · Comment placement + minor style

**Location**: (a) `src/PaymentGateway.Api/Abstractions/IIdempotencyStore.cs` (lines 34–40); (b) `src/PaymentGateway.Api/Persistence/MongoPaymentsRepository.cs` (lines 32 vs 41)
**Description**: (a) The `IIdempotencyStore` *interface* doc describes an implementation detail ("In-memory store … `ConcurrentDictionary.TryAdd`"). That belongs on `InMemoryIdempotencyStore`, not the abstraction. (b) `AddAsync` filters with a typed lambda (`Eq(d => d.Id, …)`) while `GetAsync` uses a string field name (`Eq("_id", …)`) — same target, inconsistent style.
**Why it matters**: (a) An interface doc should describe the contract, not one implementation; it's confusing if a second implementation (e.g. Redis) is added. (b) Purely cosmetic consistency.
**Suggested fix**: (a) Move the impl paragraph to `InMemoryIdempotencyStore`'s doc; keep the interface doc contract-only. (b) Use the typed lambda in both.

---

## Recommended order of work

Comment/dead-code cleanups are quick and directly serve the "comments should help developers" goal:

1. **R-001** — fix the cleanup-service class summary (highest-value: it's actively misleading).
2. **R-002** — remove the two dead decorator registrations.
3. **R-005** — fix the broken doc link.
4. **R-006, R-007** — de-drift the two rationale comments.
5. **R-008** — tidy the interface doc + filter style.

Behavior-affecting, treat separately (own decision each):

6. **R-003** — reconciler initial-delay symmetry.
7. **R-004** — move `EnsureIndexes` off the constructor.

None of these block shipping. Items 1–5 are safe, mechanical, and self-contained; 6–7 change runtime startup behavior and deserve their own commit + test.
