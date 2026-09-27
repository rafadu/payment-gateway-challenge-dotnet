# .NET Code Review — 2026-09-27

**Reviewer**: .NET Code Reviewer Agent
**Scope**: Structural/organizational refactor of `src/PaymentGateway.Api/Services/` (commit `bb827ea` vs `98b1a4a`) — split of 24 files under one `PaymentGateway.Api.Services` namespace into `Abstractions/`, `Services/`, `Persistence/`, `Clients/`, `Exceptions/`, `Middleware/`, `Filters/`, and additions to `Models/`, plus consumer/test using updates.
**Date**: 2026-09-27
**.NET version**: net10.0 (`Nullable` enabled; `TreatWarningsAsErrors`/`GenerateDocumentationFile` not set)
**Overall impression**: A clean, disciplined refactor. Every folder has a matching namespace, the code-level dependency direction is correct (Abstractions imports only `Models`; every implementation folder points inward at Abstractions), and consumer/test using-directives were updated precisely with no residual references to the old flat namespace. The only issues are XML-doc `cref`s that no longer resolve because the referenced type moved to a namespace the file no longer imports — cosmetic (no warnings, no behavior impact) but worth a cleanup pass, and one of them (an abstraction documenting its implementations) is a mild layering smell worth rewording.

---

## Summary

| Priority | Count |
|---|---|
| 🔴 Blocker | 0 |
| 🟡 Suggestion | 2 |
| 💭 Nit | 4 |
| **Total** | **6** |

---

## Layering verdict (explicitly requested)

**Code-level dependency direction is clean.** Nothing in `Abstractions/` depends on `Services`, `Persistence`, `Clients`, `Middleware`, `Filters`, or `Exceptions` in *code* — every interface imports only `PaymentGateway.Api.Models` (or `Models.Bank` / `Models.Requests`), and `IIdempotencyStore.cs` imports nothing external. The deliberate placement of `AuditRecord` and `IssuedToken` in `Models/` (not `Persistence/`) achieves the stated goal: Abstractions depends on Models, never on Persistence. Implementation folders (`Services`, `Persistence`, `Clients`, `Middleware`, `Filters`) all point inward at `Abstractions` + `Models`. `PaymentDocument` and `MerchantDocument` are `internal` and correctly confined to `Persistence`. The planned architectural unit tests (which analyze real code dependencies, not doc-crefs) should pass as-is — the findings below are all in XML documentation and will not register as code dependencies.

---

## Findings

### R-001 · 🟡 Suggestion · Architecture / Maintainability

**Location**: `src/PaymentGateway.Api/Abstractions/IPaymentsRepository.cs` → `IPaymentsRepository` (type-level XML doc, lines 5–10)
**Description**: The interface's XML doc `<see cref="MongoPaymentsRepository"/>` and `<see cref="InMemoryPaymentsRepository"/>` — both now in `PaymentGateway.Api.Persistence`, which this file does not import, so both crefs are unresolved.
**Why it matters**: Two problems in one. (1) The crefs are broken, so IDE navigation/tooltips won't resolve them. (2) More importantly, an abstraction documenting its concrete implementations is a mild inward→outward doc coupling. The wrong fix is to add `using PaymentGateway.Api.Persistence;` — that would introduce an Abstractions→Persistence reference (even if only in a doc, it muddies the layer boundary you're about to enforce with architecture tests). The right fix keeps the knowledge but drops the hard reference.

**Suggested fix**:

```csharp
// Before — abstraction crefs its implementations (unresolved + inward-pointing)
/// The MongoDB-backed implementation
/// (<see cref="MongoPaymentsRepository"/>) is the production registration; the in-memory
/// <see cref="InMemoryPaymentsRepository"/> exists as a unit-test fixture. Both are
/// registered as singletons and safe under concurrent access.

// After — describe the contract without naming/cref-ing concretes
/// The production registration is MongoDB-backed; an in-memory implementation exists as a
/// unit-test fixture. Both are registered as singletons and safe under concurrent access.
```

**Reference**: Dependency Inversion Principle — abstractions should not know their implementers.

---

### R-002 · 🟡 Suggestion · Maintainability

**Location**:
- `src/PaymentGateway.Api/Abstractions/IAcquiringBankClient.cs` → `ProcessPaymentAsync` (`<exception>` tags, lines 13/18)
- `src/PaymentGateway.Api/Abstractions/IPaymentsService.cs` → `ProcessPaymentAsync` (`<exception>` tags, lines 19/22)
- `src/PaymentGateway.Api/Services/PaymentsService.cs` → `PaymentsService` (type doc, lines 10–12)

**Description**: These files carry `<exception cref="BankUnavailableException">` / `<exception cref="InvalidBankRequestException">` doc tags, but the two exception types moved to `PaymentGateway.Api.Exceptions` and none of these three files imports that namespace, so every one of these crefs is now unresolved.
**Why it matters**: The `<exception>` documentation on the public port surface is genuinely useful (it tells callers what to catch), and it silently degraded in the move — no warning is emitted because `GenerateDocumentationFile` is off, so this only surfaces as dead tooltips/navigation. Since these are the exact contracts a consumer reasons about, keeping them resolvable has real readability value. `Clients/AcquiringBankClient.cs` already imports `PaymentGateway.Api.Exceptions` (it references the types in code), so it is the correct precedent.

**Suggested fix**:

```csharp
// Add to each of the three files:
using PaymentGateway.Api.Exceptions;
```

For the two `Abstractions/` interfaces this is a doc-only import (the exception types don't appear in the signatures), which is acceptable — `Exceptions` sits at the same low level as `Models` and importing it does not create an outward layer dependency (an exception the port throws is legitimately part of the port's contract). If you'd rather keep the interfaces' imports strictly to what the signatures use, the alternative is to drop the crefs to plain `<c>BankUnavailableException</c>`.

---

### R-003 · 💭 Nit · Maintainability

**Location**: `src/PaymentGateway.Api/Persistence/MongoCredentialStore.cs` → `MongoCredentialStore` (type doc, line ~13)
**Description**: `<see cref="CredentialCache"/>` refers to a type now in `PaymentGateway.Api.Services`, which this file does not import — unresolved cref.
**Why it matters**: Persistence documenting the caching layer above it is fine as prose, but the cref is dead. Note that `MongoPaymentsRepository.cs` deliberately uses plain `<c>MongoServiceCollectionExtensions</c>` (not a cref) for exactly this cross-namespace situation — following that convention here is the lowest-risk fix.

**Suggested fix**:

```csharp
// Before
/// This is the read-through source behind <see cref="CredentialCache"/>;
// After — plain code font, no cross-namespace cref to resolve
/// This is the read-through source behind the CredentialCache;
```

---

### R-004 · 💭 Nit · Maintainability

**Location**: `src/PaymentGateway.Api/Persistence/MongoAuditStore.cs` → `MongoAuditStore` (type doc, line ~13)
**Description**: `<see cref="MongoServiceCollectionExtensions"/>` refers to a type in `PaymentGateway.Api.Configuration`, not imported here — unresolved cref.
**Why it matters**: Same class of issue as R-003. Inconsistent with the sibling `MongoPaymentsRepository.cs`, which references the same extension type as plain `<c>MongoServiceCollectionExtensions</c>` and therefore reads correctly.

**Suggested fix**:

```csharp
// Before
/// per <see cref="MongoServiceCollectionExtensions"/>). Documents are BSON, ...
// After
/// per <c>MongoServiceCollectionExtensions</c>). Documents are BSON, ...
```

---

### R-005 · 💭 Nit · Architecture

**Location**: `src/PaymentGateway.Api/Controllers/PaymentsController.cs` (line 67) → uses `AuditMiddleware.OutcomeItemKey`; requires `using PaymentGateway.Api.Middleware;`
**Description**: The refactor makes an existing coupling more visible: the controller reaches into the `Middleware` namespace for the `AuditMiddleware.OutcomeItemKey` magic-string constant to set `HttpContext.Items[...]`. The `using` is correct and necessary — this is not a defect — but Controllers→Middleware is a slightly awkward direction now that folders name the layers.
**Why it matters**: Not introduced by the refactor (the constant predates it) and not a layering violation, but the shared `HttpContext.Items` key is a contract between the controller and the middleware. Hosting it on the middleware class means the producer (controller) depends on the consumer (middleware). Purely optional: a small neutral holder would decouple them.

**Suggested fix**:

```csharp
// Optional: a neutral constants holder both sides reference, e.g.
// Abstractions/ (or a small Auditing/ contract) instead of on AuditMiddleware:
public static class AuditItemKeys
{
    public const string Outcome = "Audit.Outcome";
}
```

Fine to defer — flagging only because the new folder layout surfaces the direction.

---

### R-006 · 💭 Nit · Maintainability

**Location**: Refactor-wide (documentation consistency)
**Description**: The refactor was executed with `git mv` (rename similarity 82–99% on all moved files), namespaces match folders 1:1, contract types (`CachedResponse`, `IdempotencyClaim`, `IdempotencyClaimOutcome`) sensibly stayed beside `IIdempotencyStore` in `Abstractions`, and no test or consumer retains a stale `using PaymentGateway.Api.Services;` for a moved type. This is a positive finding.
**Why it matters**: The only systematic gap is the doc-cref drift captured in R-001 through R-004. A single follow-up sweep for `cref=` targets whose type lives in an unimported namespace (five files total: the two `Abstractions` interfaces, `Services/PaymentsService.cs`, `Persistence/MongoCredentialStore.cs`, `Persistence/MongoAuditStore.cs`) would close it. Consider that sweep before the architecture-test work item so the docs match the enforced boundaries.

**Suggested fix**: No code change; address R-001–R-004 together, then optionally enable `GenerateDocumentationFile` on the API project so future cref drift surfaces as build warnings (`CS1574`).

---

## Praise / what's done well

- **Namespace-to-folder discipline is exact** across all eight folders — no mismatches.
- **The `Models/` placement of `AuditRecord` and `IssuedToken`** is the correct call: it keeps `Abstractions` free of any `Persistence` dependency and leaves `AuditRecord` free of BSON attributes (the `MongoAuditStore` maps to `BsonDocument` itself).
- **`internal` document types** (`PaymentDocument`, `MerchantDocument`) are correctly confined to `Persistence`.
- **Consumer usings were narrowed precisely** — e.g. `AuthController` imports only `Abstractions` + the request/response model namespaces; `PaymentsController` and `Program.cs` import exactly the folders they use, with no dead `using PaymentGateway.Api.Services;` left behind.
- **The old `AuditRecord.cs` three-way split** (record → `Models`, interface → `Abstractions`, in-memory impl → `Persistence`) landed each type in the right layer.
