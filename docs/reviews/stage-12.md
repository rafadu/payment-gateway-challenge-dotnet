# .NET Code Review — Stage 12 (MongoDB-backed payments repository)

**Reviewer**: .NET Code Reviewer Agent
**Scope**: Stage 12 diff vs `main`. New: `src/Services/InMemoryPaymentsRepository.cs`, `src/Services/MongoPaymentsRepository.cs`, `src/Services/PaymentDocument.cs`, `test/.../Integration/PaymentsPersistenceIntegrationTests.cs`. Modified: `IPaymentsRepository.cs` (async API), `PaymentsController.cs` (`GetPayment` async), `PaymentsService.cs` (awaits `AddAsync`), `Program.cs` (`MongoPaymentsRepository` registration), `InMemoryPaymentsRepositoryTests.cs` (renamed from `PaymentsRepositoryTests.cs`, async tests), `PaymentsServiceTests.cs` (NSubstitute `AddAsync`), `PaymentsControllerTests.cs` (`new InMemoryPaymentsRepository()`), `AuditMiddlewareTests.cs` + `AuthControllerTests.cs` (Mongo fake override in every test factory), `README.md` (new "Inspecting the payments collection" section). Deleted: `src/Services/PaymentsRepository.cs` (in-memory production impl).
**Date**: 2026-09-27
**.NET version**: `net10.0`
**Build/test observed**: clean build, 0 warnings. `dotnet test` → `Passed! - Failed: 0, Passed: 158, Skipped: 7, Total: 165`. Skipped count went from 6 → 7 (the new `PaymentsPersistenceIntegrationTests` `[SkippableFact]`); 158 unit/component tests still pass.

---

## Standards

### S-001 · Important · Duplicated `RemoveAll<IPaymentsRepository>` + `AddSingleton` block in `AuditMiddlewareTests.cs:64-71` (must fix before commit)

**Location**: `test/PaymentGateway.Api.Tests/AuditMiddlewareTests.cs:64-71`
**Description**: Inside `Authorized_POST_writes_one_record_with_outcome_Authorized_and_masked_summary`, the `ConfigureServices` lambda has the same four-line block twice in a row, including its comment:

```csharp
// Payments repository needs an in-memory fake too — the production
// MongoPaymentsRepository would otherwise hang on a connection that isn't running.
services.RemoveAll<IPaymentsRepository>();
services.AddSingleton<IPaymentsRepository>(new InMemoryPaymentsRepository());
// Payments repository needs an in-memory fake too — the production
// MongoPaymentsRepository would otherwise hang on a connection that isn't running.
services.RemoveAll<IPaymentsRepository>();
services.AddSingleton<IPaymentsRepository>(new InMemoryPaymentsRepository());
```

Second `RemoveAll` finds nothing (the first already cleared it), the second `AddSingleton` overwrites the first with the same `new InMemoryPaymentsRepository()` instance — no observable behaviour change, so the test still passes. But it's a clear copy-paste artefact that a reader will have to de-tangle to convince themselves the second block is dead.

**Why it matters**: Stranded dead code in a test misleads readers into thinking the second block exists for a reason (e.g. some subtlety about DI registration order). It's the kind of cleanup that takes 30 seconds to fix now and would be a 10-minute `git blame` exercise during the next Mongo-touch incident.

**Suggested fix**: Delete lines 68-71. The body becomes:

```csharp
services.RemoveAll<IPaymentsRepository>();
services.AddSingleton<IPaymentsRepository>(new InMemoryPaymentsRepository());
```

(Compare with the three other factories in the same file — `FactoryWith` at line 25-35, `Declined_POST_…` at line 121-126, `Bank_unavailable_…` at line 200-205 — all carry the override exactly once.)

---

### S-002 · Important · `Program.cs:14` registers `MongoPaymentsRepository` inline instead of via an `AddXxx` extension (could address later)

**Location**: `src/PaymentGateway.Api/Program.cs:14`
(cf. `src/PaymentGateway.Api/Configuration/AuditServiceCollectionExtensions.cs:13-19`,
`src/PaymentGateway.Api/Configuration/CredentialCacheServiceCollectionExtensions.cs:13-33`,
`src/PaymentGateway.Api/Configuration/MongoServiceCollectionExtensions.cs:9-31`,
`src/PaymentGateway.Api/Configuration/BankClientServiceCollectionExtensions.cs:11-43`)

**Description**: Every other store in this codebase registers through a `Configuration/XxxServiceCollectionExtensions.cs` extension method called from `Program.cs`:

| Store | Extension |
|---|---|
| Mongo client + DB | `AddMongoDb(services, configuration)` |
| Credentials + cache | `AddCredentialCache(services, configuration)` |
| Audit | `AddAudit(services, configuration)` |
| Bank HTTP client | `AddBankClient(services, configuration)` |
| JWT validation | `AddJwtAuthentication(services, configuration)` |
| Token issuance | `AddTokenIssuance(services, configuration)` |
| Payment validation | `AddPaymentValidation(services, configuration)` |
| **Payments repository (new)** | **`AddSingleton<IPaymentsRepository, MongoPaymentsRepository>()` inline** |

The new payments-repo registration breaks the convention. `Program.cs:14` reads:

```csharp
builder.Services.AddSingleton<IPaymentsRepository, MongoPaymentsRepository>();
```

— a single line, no `AddXxx` method, no XML doc explaining why this store is the only one registered directly.

**Why it matters**: Handoff.md calls out that the established extension-method shape exists precisely so `Program.cs` stays a "thin list of features" (`MongoServiceCollectionExtensions.cs:5-7`). The Stage 11 review (S-007) already flagged that `AddAudit` ignores its `IConfiguration` parameter — and was deliberately left to a "could address later" because the extension exists and can be retro-fitted later. The payments repo doesn't even have the extension to retrofit into. Future extension-hook consumers (e.g. someone wanting to read a `Payments:CollectionName` config section, or a future `AddPaymentsRepositoryInMemoryForTests` extension) will reinvent the convention instead of following it.

**Suggested fix**: Move the registration into a new `Configuration/PaymentsServiceCollectionExtensions.cs` parallel to `AuditServiceCollectionExtensions.cs`:

```csharp
namespace PaymentGateway.Api.Configuration;

/// <summary>
/// Registers the MongoDB-backed <see cref="IPaymentsRepository"/> (ADR-0004, audit half
/// precedent — Stage 12). Requires <see cref="MongoServiceCollectionExtensions.AddMongoDb"/>
/// to have registered the <see cref="MongoDB.Driver.IMongoDatabase"/> this collection
/// writes to.
/// </summary>
public static class PaymentsServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentsRepository(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IPaymentsRepository, MongoPaymentsRepository>();
        return services;
    }
}
```

Update `Program.cs:14` to `builder.Services.AddPaymentsRepository(builder.Configuration);`. The `IConfiguration` parameter is unused today, but the parameter keeps the call-site shape uniform with the other six extensions (and Stage 11 S-007's fix — `TryAddSingleton` — applies symmetrically).

This is a low-effort cleanup that brings the new repo into line with the codebase's pattern. Two existing files, no behavioural change.

---

### S-003 · Important · `PaymentDocument.cs` is `internal sealed record` but `MerchantDocument.cs` is `internal sealed class` — pick a side (could address later)

**Location**: `src/PaymentGateway.Api/Services/PaymentDocument.cs:13`
(cf. `src/PaymentGateway.Api/Services/MongoCredentialStore.cs:36-37` — `internal sealed class MerchantDocument`)

**Description**: Two BSON document shapes now live in this codebase, with different `class`/`record` choices:

- `PaymentDocument` (`PaymentDocument.cs:13`) — `internal sealed record` with `init`-only properties, `FromDomain` / `ToDomain` static mappers
- `MerchantDocument` (`MongoCredentialStore.cs:36-37`) — `internal sealed class` with `set` properties (not `init`), no mappers (the store constructs and reads the document directly)

The Stage 11 review (S-016) already flagged the inverse problem — `MongoAuditStore` hand-builds `BsonDocument` instead of using a typed `[BsonElement]` DTO like `MongoCredentialStore`. The Stage 12 author fixed that for the new repo by introducing `PaymentDocument`, but didn't update `MerchantDocument` for consistency.

**Why it matters**: Two stores, two conventions. A new typed-DTO store will look at `PaymentDocument` and copy the `record` shape; a refactor of `MerchantDocument` will look at the existing `class` shape and stay. The codebase now has no canonical "typed BSON document" pattern — it has two patterns, each with a single example.

**Suggested fix**: Pick `record` (matches the rest of the repo's `sealed record` convention: `MerchantCredential`, `IssuedToken`, `CachedResponse`, `Entry`, the integration tests' `IdempotencyClaim`, `AuditRecord` is the documented outlier from Stage 11 S-001). Convert `MerchantDocument` to `internal sealed record` with `init`-only properties; remove the manual `new MerchantDocument { MerchantId = ..., ... }` construction in `MongoCredentialStore.FindByClientIdAsync` (replace with a `static MerchantDocument FromCredential(MerchantCredential)` mapper that mirrors `PaymentDocument.FromDomain`).

Or — defensible alternative — keep both as-is and add a one-line comment to each explaining why (e.g. "matches `PaymentDocument` record shape from Stage 12" / "matches `MongoCredentialStore`'s original `class` shape from Stage 5; conversion is out of scope for this stage"). The current state — two stores, no comment, no convention — is the worst of the three options.

---

### S-004 · Nit · `MongoPaymentsRepository.GetAsync` filter uses string-literal `"_id"` while `AddAsync` uses a lambda (could address later)

**Location**: `src/PaymentGateway.Api/Services/MongoPaymentsRepository.cs:31, 40`

**Description**: `AddAsync` (line 31) filters by lambda:

```csharp
Builders<PaymentDocument>.Filter.Eq(d => d.Id, document.Id),
```

`GetAsync` (line 40) filters by string literal:

```csharp
Builders<PaymentDocument>.Filter.Eq("_id", id.ToString())
```

**Why it matters**: Mixed style in a 44-line file. The lambda form is type-safe (renaming `PaymentDocument.Id` would surface as a compile error at the `AddAsync` site; the `GetAsync` site stays silently broken). Pick one.

**Suggested fix**: Change line 40 to:

```csharp
var document = await _collection
    .Find(Builders<PaymentDocument>.Filter.Eq(d => d.Id, id.ToString()))
    .FirstOrDefaultAsync(cancellationToken);
```

(`PaymentDocument.Id` is `string`, so `id.ToString()` is the type-correct value. Same as the `AddAsync` site.)

---

### S-005 · Nit · `IPaymentsRepository.cs` + `PaymentsRepositoryTests.cs` lost their trailing newline (could address later)

**Location**: `src/PaymentGateway.Api/Services/IPaymentsRepository.cs:23`, `test/PaymentGateway.Api.Tests/Services/PaymentsRepositoryTests.cs:66`

**Description**: Both files end with `}` without a trailing newline (the diff shows `\ No newline at end of file`). The editorconfig on line 22 explicitly says `insert_final_newline = false` — so technically not a violation — but most files in the same folders DO end with a newline (`MongoCredentialStore.cs`, `PaymentsService.cs`, `Program.cs`, `IdempotencyResourceFilter.cs`, `AcquiringBankClient.cs`, `AuditRecord.cs`, …).

**Why it matters**: Mixed convention; a `dotnet format` or `dotnet format whitespace` sweep would silently normalize these. If you prefer "no trailing newline", it's editorconfig-correct today; if you prefer "trailing newline on all source files", then these two are the outliers. Pick a side — or normalize via a one-line `printf '\n' >>` — so a future diff doesn't keep introducing the inconsistency.

**Suggested fix**: Append a trailing newline to both files. (Or: ignore — the editorconfig permits the current state.)

---

### S-006 · Nit · `PaymentDocument.FromDomain` stamps `CreatedAt = DateTime.UtcNow` at write time, not at domain-creation time (could address later)

**Location**: `src/PaymentGateway.Api/Services/PaymentDocument.cs:52`

**Description**: `PaymentDocument.FromDomain(payment)` sets `CreatedAt = DateTime.UtcNow` at the moment of persistence, not at the moment of `new Payment { Id = Guid.NewGuid(), ... }`. The `Payment` domain model itself has no `CreatedAt` field — only the persisted document does. So the timestamp reflects "when was this written to Mongo", which for an upsert on a duplicate id can be *later* than the original write.

**Why it matters**: Low — a non-issue for the happy path (each payment is written once). It IS an issue for the documented `ReplaceOneAsync` upsert contract: a re-write of an existing payment overwrites `CreatedAt` with the new wall-clock time. The README (line 199) describes the field as "the time the payment was created" — semantically `CreatedAt` should be set once and never re-stamped. The current behaviour would surprise a future "give me all payments created in the last 7 days" query that pulled a re-written payment and saw a fresh timestamp.

**Suggested fix**: Either (a) add a `CreatedAt` to the `Payment` domain model and set it once in `PaymentsService.ProcessPaymentAsync` (the same way `Id` and `MerchantId` are set), then pass it through `FromDomain` — or (b) document the current behaviour in the README: "the document's `createdAt` reflects the last write to Mongo, not the original payment creation time". Option (a) is cleaner; option (b) is one line.

The non-Mongo `InMemoryPaymentsRepository` ignores `CreatedAt` entirely (`PaymentDocument` isn't constructed on the in-memory path), so adding a domain field is a single-direction change.

---

### S-007 · Nit · No MongoDB index on `payments` collection (could address later)

**Location**: `src/PaymentGateway.Api/Services/MongoPaymentsRepository.cs:22` (collection creation),
`README.md:189, 193` (the `mongosh` queries)

**Description**: Same finding as Stage 11's S-017. The two README `mongosh` queries both filter or sort on un-indexed fields:

```bash
db.payments.find().sort({_id:-1}).limit(10)              # sort by _id desc
db.payments.find({merchantId: "..."}).sort({_id:-1})     # filter by merchantId, sort by _id desc
```

`_id` is auto-indexed by the driver, but the second query's `merchantId` filter is a full-collection scan.

**Why it matters**: A "give me all payments for merchant X in the last 30 days" query (the natural follow-on to a per-merchant GET — there is no GET endpoint for it today, but the README documents the operational surface) is a full scan from day one. The query that ships in the README today does this implicitly via the `sort({_id:-1})` after `find({merchantId: ...})` — the filter is un-indexed.

**Suggested fix**: Same shape as Stage 11 S-017. Add `CreateIndexesAsync` in the ctor:

```csharp
var keys = Builders<PaymentDocument>.IndexKeys
    .Ascending(d => d.MerchantId).Descending(d => d.Id);
await _collection.Indexes.CreateOneAsync(
    new CreateIndexModel<PaymentDocument>(keys, new CreateIndexOptions { Name = "merchantId_id_desc" }));
```

(Don't index `_id` — the driver does that automatically.)

---

### No findings on

- **`IPaymentsRepository` async API shape**: `Task AddAsync(Payment, CancellationToken = default)` and `Task<Payment?> GetAsync(Guid, CancellationToken = default)`. Both parameters use the default-value-of-`CancellationToken` idiom (matches `IAuditStore.WriteAsync`, `ICredentialStore.FindByClientIdAsync`, `IAcquiringBankClient.ProcessPaymentAsync`). The `CancellationToken cancellationToken = default` convention is followed everywhere.
- **`[BsonElement("…")]` attribute use**: every field has a snake-style lowercase BSON name (`merchantId`, `cardNumberLastFour`, `expiryMonth`, etc.) that matches the established `audit_records` shape. PCI-DSS: no `cvv`, no full `cardNumber`, only `cardNumberLastFour` — matches the audit half's masking rules exactly.
- **`MongoPaymentsRepository` constructor DI**: takes `IMongoDatabase` (the abstraction registered by `AddMongoDb`), not `MongoClient`. Same shape as `MongoCredentialStore` and `MongoAuditStore`. Lazy connection by design (MongoClient is thread-safe, owns the connection pool, connects lazily — `MongoServiceCollectionExtensions.cs:25-27`).
- **`PaymentsController.GetPayment` async wiring**: `[HttpGet("{id:guid}")] public async Task<ActionResult<PaymentResponse>> GetPayment(Guid id, CancellationToken cancellationToken)` — passes `cancellationToken` through to `GetAsync`, matching the established pattern in `ProcessPayment` (line 41-43). The 404 ownership-check logic is preserved exactly as it was in the synchronous version.
- **`PaymentsService.ProcessPaymentAsync` cancellation propagation**: `await _repository.AddAsync(payment, cancellationToken)` (line 52) — matches the `IAcquiringBankClient.ProcessPaymentAsync` propagation at line 38. Both threads of async I/O carry the request's `CancellationToken` through to Mongo.
- **`MongoPaymentsRepository.AddAsync` upsert semantics**: `ReplaceOneAsync` with `IsUpsert = true` matches the `IPaymentsRepository.AddAsync` XML doc contract ("replacing any existing payment stored under the same id") — preserves the in-memory `ConcurrentDictionary[indexer]` "replace on duplicate" behaviour verbatim. The XML doc on line 12-18 explains this is "the store's defined contract rather than a scenario the callers rely on", which is honest about the GUID-keyed design.
- **`MongoPaymentsRepository.GetAsync` `null` semantics**: `document?.ToDomain()` returns `null` for a missing document (consistent with the original `ConcurrentDictionary.TryGetValue` returning `null` for an unknown id). The controller's `if (payment is null || payment.MerchantId != CallerMerchantId()) return NotFound();` continues to work unchanged.
- **`InMemoryPaymentsRepository` async wrapper**: `AddAsync` returns `Task.CompletedTask` (synchronous `ConcurrentDictionary` indexer assigned); `GetAsync` returns `Task.FromResult(...)`. Both compile to the same shape as a true async method, so the production code can't tell the difference. Singleton-safe (`ConcurrentDictionary` is thread-safe).
- **`PaymentsRepositoryTests.cs` → `InMemoryPaymentsRepositoryTests.cs` rename**: matches the `MongoAuditStore` / `InMemoryAuditStore` split naming convention established in Stage 11. Class header now reads `public class InMemoryPaymentsRepositoryTests` — correct.
- **`APayment` test helper update**: added `MerchantId = "merchant-42"` to `PaymentsRepositoryTests.cs:15`. Matches the existing `TestMerchantId = "merchant-42"` constant in `PaymentsServiceTests.cs:34` and the domain model's mandatory `MerchantId` field (added in Stage 7). Without this addition, the test data wouldn't compile against the post-Stage-7 `Payment` model.
- **NSubstitute `AddAsync` rewrite in `PaymentsServiceTests.cs`**: every `_repository.When(r => r.Add(...))` and `_repository.Received(...).Add(...)` correctly migrated to the two-argument async form `Arg.Any<Payment>(), Arg.Any<CancellationToken>()`. The `.Do(ci => persisted = ci.Arg<Payment>())` callback signature is unchanged (still receives the `Payment`, ignoring the `CancellationToken`).
- **`PaymentsControllerTests.cs` `new PaymentsRepository()` → `new InMemoryPaymentsRepository()` migration**: every test factory and every inline `new PaymentsRepository()` correctly replaced. The single renamed file `PaymentsRepositoryTests.cs` → `InMemoryPaymentsRepositoryTests.cs` is the only place the class is referenced.
- **Test-substitution pattern in every `WebApplicationFactory` factory**: `AuditMiddlewareTests.cs` (5 sites), `AuthControllerTests.cs` (1 site), and the existing `PaymentsControllerTests.cs` factories all follow the same `services.RemoveAll<IPaymentsRepository>(); services.AddSingleton<IPaymentsRepository>(new InMemoryPaymentsRepository());` idiom. The comment on each block explains *why* (production `MongoPaymentsRepository` would hang on a connection that isn't running in unit tests). Pattern matches what Stage 11 did for `IAuditStore → InMemoryAuditStore` and Stage 5 did for `ICredentialStore → Substitute.For<ICredentialStore>()`.
- **`PaymentsPersistenceIntegrationTests` collection-fixture pattern**: `[Collection("Integration")]`, `[Trait("Category", "Integration")]`, `[SkippableFact]`, `Skip.IfNot(_fixture.ServicesAvailable, ...)` — textbook xunit, byte-identical to `AuditPersistenceIntegrationTests.cs:28-30, 36-40`. Minting the JWT directly with `new JsonWebTokenHandler().CreateToken(...)` matches `BankSimulatorIntegrationTests.cs:90-104` and `AuditPersistenceIntegrationTests.cs:43-59` — the established "integration tests bypass `/api/auth/token` because the fixture only proves one half at a time" pattern.
- **`PaymentsPersistenceIntegrationTests` BSON shape assertions**: `merchantId == "merchant-mongo-it"`, `status == "Authorized"`, `cardNumberLastFour == "8871"`, `currency == "GBP"`, `amount == 100`, `expiryMonth == 12`, no `cvv`, no `cardNumber` — matches `design.md:107-110` ("only the last four digits of the card number reach the domain model (`Payment`) or any response body") and ADR-0004's "Important clarification that affects scope" block. PCI-DSS-compliant end-to-end against real Mongo.
- **`README.md` new section**: "Inspecting the payments collection" (lines 180-199) parallels the existing "Inspecting the audit trail" section (lines 158-178). Same shape — prose paragraph + two `mongosh` examples + a one-sentence field-list. The "card `2222405343248877`" example card is consistent with the existing walkthrough (lines 86-93).
- **`InMemoryPaymentsRepository` XML doc comment** (lines 7-11): explicitly says "Used by unit tests as a fake — production code uses `MongoPaymentsRepository`". Identifies the production vs test split clearly. Same self-documenting approach as the existing `InMemoryIdempotencyStore` doc.
- **`MongoPaymentsRepository` `sealed class` modifier**: matches `MongoAuditStore`, `MongoCredentialStore`, `InMemoryAuditStore`, `InMemoryPaymentsRepository`, `InMemoryIdempotencyStore` — every store in this codebase is sealed. No exception taken.
- **`PaymentDocument` `internal` access modifier**: same as `MerchantDocument`. `internal` is correct here — the document shape is an infrastructure concern of `MongoPaymentsRepository` and isn't part of the public domain surface.
- **`Program.cs:14` lifetime**: `AddSingleton` is correct — both `MongoPaymentsRepository` and `InMemoryPaymentsRepository` are stateless apart from their backing store reference (Mongo connection pool for the former, `ConcurrentDictionary` instance for the latter). Singleton = one store per process. Matches every other store registration in `Program.cs`.
- **`dotnet test` count**: 158 unit/component tests pass (unchanged), 7 integration tests skipped (was 6). Stage 12 added one `[SkippableFact]` to the integration suite. The InMemoryPaymentsRepositoryTests rename + async-update is net-zero on test count (4 tests, same as `PaymentsRepositoryTests` had).
- **Build**: clean, 0 warnings under `TreatWarningsAsErrors=true`. No new packages added (MongoDB.Driver 3.5.0 was already pinned from Stage 5).

---

## Spec

### P-001 · Important · `design.md:135-139` "Concurrency" section is now stale — must fix before commit (explicit user request)

**Location**: `docs/design.md:135-139`
(cf. `README.md:184-185` — already correctly updated for Stage 12)

**Description**: `design.md` still reads:

> ## Concurrency
>
> - `IPaymentsRepository` is backed by `ConcurrentDictionary<Guid, Payment>`, not the original
>   scaffold's plain `List<T>`, since ASP.NET Core serves requests concurrently by default and a
>   `List<T>.Add` is not thread-safe.

The repository is no longer `ConcurrentDictionary`-backed in production — it's Mongo-backed. `PaymentsRepository.cs` is deleted; `MongoPaymentsRepository.cs` is registered as the production impl (`Program.cs:14`); `InMemoryPaymentsRepository.cs` exists only as a test fake.

The README correctly captured this in the Stage 12 change (line 184-185): *"…written by `MongoPaymentsRepository`, which replaced the previous in-memory `ConcurrentDictionary`…"* — but `design.md` was not touched.

**Why it matters**: `design.md` is the spec source-of-truth per handoff.md ("Spec = `docs/design.md` + in-scope ADRs + `docs/implementation-plan.md`"). A reviewer reading the spec will form the wrong mental model: they'll think the repo is still in-memory. This is the same docs-drift pattern Stage 11 caught (P-001 in stage-11 review: *"`design.md:191-197` 'Explicitly out of scope' line still claims audit persistence is out of scope"*). The user explicitly asked for this to be flagged.

**Suggested fix**: Rewrite the section to describe the actual production impl and call out the test-only fake. Minimum viable edit:

```diff
 ## Concurrency

-- `IPaymentsRepository` is backed by `ConcurrentDictionary<Guid, Payment>`, not the original
-  scaffold's plain `List<T>`, since ASP.NET Core serves requests concurrently by default and a
-  `List<T>.Add` is not thread-safe.
+- `IPaymentsRepository`'s production impl is `MongoPaymentsRepository` (`ReplaceOneAsync` with
+  `IsUpsert = true` for idempotent writes; `Find().FirstOrDefaultAsync` for reads). The driver
+  owns the connection pool and the operations are thread-safe.
+- The in-memory `InMemoryPaymentsRepository` exists only as a unit-test fixture; it's
+  `ConcurrentDictionary<Guid, Payment>`-backed (the scaffold's plain `List<T>` was not
+  thread-safe — see ADR for the original reasoning).
 - The idempotency store (ADR-0003) uses the same `ConcurrentDictionary`-based approach, with
   `TryAdd` used to atomically claim a key and avoid a check-then-act race between two concurrent
   requests carrying the same key.
```

Also: the architecture diagram in `design.md:25` still says *"IPaymentsRepository (in-memory, ConcurrentDictionary)"*. Same one-line fix needed there:

```diff
-└──────────────► IPaymentsRepository (in-memory, ConcurrentDictionary)
+└──────────────► IPaymentsRepository (MongoDB; in-memory fake for tests)
```

---

### P-002 · Pass · `payments` collection shape mirrors `audit_records` (masked PAN, no CVV, async API, idempotent Add) — exact match to user's request and ADR-0004 audit half

**Location**: `src/PaymentGateway.Api/Services/PaymentDocument.cs:13-53`, `src/PaymentGateway.Api/Services/MongoPaymentsRepository.cs:25-43`
(cf. `src/PaymentGateway.Api/Services/MongoAuditStore.cs:21-40`, ADR-0004 "Update — audit trail half now implemented (Stage 11)", user's request: *"Transfer this process to MongoDB, using the audit collection as reference to insert and get payments"*)

**Description**: The `payments` collection uses the same BSON-document conventions the audit half established:

| Property | `payments` collection | `audit_records` collection | Match? |
|---|---|---|---|
| Document model | Typed `[BsonElement]`-driven (`PaymentDocument`) | Hand-built `BsonDocument` (Stage 11 S-016 noted this divergence) | ⚠️ — payments is *more* type-safe; see S-003 |
| `_id` type | `string` (GUID `.ToString()`) | `string` (GUID `.ToString()`) | ✓ |
| Field naming | camelCase BSON elements (`merchantId`, `status`, `cardNumberLastFour`, `expiryMonth`, `expiryYear`, `currency`, `amount`, `createdAt`) | camelCase BSON elements (`merchantId`, `method`, `path`, `statusCode`, `outcome`, `durationMs`, `requestSummary`) | ✓ |
| PAN handling | Last four only (`CardNumberLastFour`), no `cardNumber` field | Masked in `requestSummary.cardNumberLastFour`, no `cardNumber` field | ✓ |
| CVV handling | **Not stored** (no field on `Payment` or `PaymentDocument`) | **Not stored** (masked out of `requestSummary`) | ✓ |
| Async API | `Task AddAsync` / `Task<Payment?> GetAsync` | `Task WriteAsync` | ✓ |
| Idempotent Add | `ReplaceOneAsync` with `IsUpsert = true` (matches the original `ConcurrentDictionary[indexer]` semantics) | `InsertOneAsync` (one-time write; no idempotency needed for audit) | ✓ for payments (replace-on-duplicate is in the `IPaymentsRepository` contract) |
| `CancellationToken` | Propagated to both `AddAsync` and `GetAsync` | Propagated to `WriteAsync` | ✓ |

**Status**: Pass. The user's request to use the audit collection "as reference" is honoured in shape (BSON document, masked PAN, no CVV, async API, idempotent Add), even if the *typed-vs-hand-built* DTO convention diverges (which is the S-003 finding — payments does it better, audit should follow).

---

### P-003 · Pass · README "Inspecting the payments collection" section is accurate against the persisted shape

**Location**: `README.md:180-199`

**Description**: The new section's field list (`_id`, `merchantId`, `status`, `cardNumberLastFour`, `expiryMonth`, `expiryYear`, `currency`, `amount`, `createdAt`) matches `PaymentDocument.cs` exactly. The PCI-DSS guarantee ("The full PAN and CVV are never stored") matches the domain model's invariant (`Payment.CardNumberLastFour` is the only card data field on the persisted shape). The two `mongosh` examples are syntactically valid against the BSON shape the integration test (`PaymentsPersistenceIntegrationTests.cs:79-92`) confirms.

The example card `2222405343248871` (last digit `1` → Authorized) matches the `bank_simulator.ejs` predicate already documented in the README's "How to call the API" section (lines 98-101).

**Status**: Pass.

---

### P-004 · Pass · `MongoPaymentsRepository` replace-on-duplicate matches `IPaymentsRepository` contract

**Location**: `src/PaymentGateway.Api/Services/MongoPaymentsRepository.cs:25-35`, `src/PaymentGateway.Api/Services/IPaymentsRepository.cs:13-19`

**Description**: The interface XML doc at `IPaymentsRepository.cs:13-18` says:

> Persists a payment, keyed by its `Payment.Id`, replacing any existing payment stored under the same id. In normal operation ids are gateway-generated GUIDs, so a collision does not occur; this replace-on-duplicate behaviour is the store's defined contract rather than a scenario the callers rely on.

`MongoPaymentsRepository.AddAsync` implements exactly this:

```csharp
await _collection.ReplaceOneAsync(
    Builders<PaymentDocument>.Filter.Eq(d => d.Id, document.Id),
    document,
    new ReplaceOptions { IsUpsert = true },
    cancellationToken);
```

The in-memory `InMemoryPaymentsRepository.AddAsync` does the same thing via `_payments[payment.Id] = payment` (line 18) — a `ConcurrentDictionary` indexer assignment that overwrites any existing key. The two impls are behaviourally equivalent on the documented contract, which is what the `InMemoryPaymentsRepositoryTests.AddAsync_with_a_duplicate_id_replaces_the_existing_payment` test (`PaymentsRepositoryTests.cs:42-52`) verifies.

**Status**: Pass. The `ReplaceOptions { IsUpsert = true }` is correct here — without it, a duplicate id would throw `MongoWriteException` instead of replacing.

---

### P-005 · Pass · `PaymentsPersistenceIntegrationTests` follows the established integration-test convention end-to-end

**Location**: `test/PaymentGateway.Api.Tests/Integration/PaymentsPersistenceIntegrationTests.cs:1-98`

**Description**: The new integration test exercises the full POST → Mongo → GET round-trip against real Mongo (ADR-0004) and real Mountebank (ADR-0001). It follows the established pattern from `AuditPersistenceIntegrationTests.cs` and `AuthFlowIntegrationTests.cs` byte-for-byte:

- `[Collection("Integration")]` + `[Trait("Category", "Integration")]` + `[SkippableFact]` + `Skip.IfNot(_fixture.ServicesAvailable, ...)` — exact pattern.
- Mints the JWT directly with `new JsonWebTokenHandler().CreateToken(...)` — decoupled from `/api/auth/token` (the fixture only proves the Mongo half, not the auth flow).
- Uses `factory.Services.GetRequiredService<IMongoClient>()` to reach the real Mongo client, then queries `payment_gateway.payments` via `BsonDocument` — the same shape `AuditPersistenceIntegrationTests.cs:76-81` uses for `audit_records`.
- Card `2222405343248871` (last digit `1` → Authorized) matches the bank-simulator predicate documented at `README.md:98-101`.
- Asserts no `cvv`, no `cardNumber` on the persisted document — PCI-DSS verification end-to-end against real Mongo.

The BSON-shape assertions cover exactly the properties the new `PaymentDocument` defines, so a regression in the `[BsonElement]` attributes or in `FromDomain` would surface here.

**Status**: Pass.

---

### P-006 · Pass · Status / persistence mapping unchanged by Stage 12

**Location**: `src/PaymentGateway.Api/Controllers/PaymentsController.cs:60-84`, `docs/design.md:80-95`

**Description**: `design.md:80-95` defines the persistence contract: Authorized/Declined → 201 + persisted; validation-rejected → 400 + not persisted; bank-unavailable → 503 + not persisted. Stage 12 changes only the *backend* the controller's `PaymentsService` writes to (Mongo instead of in-memory), not the mapping. The integration test's `response.StatusCode.Should().Be(HttpStatusCode.Created)` + `getResponse.StatusCode.Should().Be(HttpStatusCode.OK)` proves both halves of the contract (write + read) are honoured end-to-end.

**Status**: Pass.

---

### P-007 · Pass · `IPaymentsRepository` async rename propagates through every call site

**Location**: `src/PaymentGateway.Api/Controllers/PaymentsController.cs:90`, `src/PaymentGateway.Api/Services/PaymentsService.cs:52`,
`test/PaymentGateway.Api.Tests/Services/PaymentsServiceTests.cs:45, 50, 59, 108, 125, 158`

**Description**: Every call site of the old sync `Add(Payment)` / `Get(Guid)` now uses the async form with a propagated `CancellationToken`:

| Site | Form |
|---|---|
| `PaymentsController.GetPayment` (line 90) | `await _paymentsRepository.GetAsync(id, cancellationToken)` |
| `PaymentsService.ProcessPaymentAsync` (line 52) | `await _repository.AddAsync(payment, cancellationToken)` |
| `PaymentsServiceTests` `When(...).Do(...)` (4 sites) | `_repository.When(r => r.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>()))` |
| `PaymentsServiceTests` `Received/DidNotReceive` (2 sites) | `_repository.Received(1).AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())` / `DidNotReceive().AddAsync(...)` |

The compile would fail if any site were missed. Build is clean and 158 tests pass — proves every call site migrated.

**Status**: Pass.

---

## Findings summary

| ID | Severity | Category | File |
|---|---|---|---|
| S-001 | Important | Standards — duplicated `RemoveAll`+`AddSingleton` block | `test/PaymentGateway.Api.Tests/AuditMiddlewareTests.cs:64-71` |
| S-002 | Important | Standards — payments-repo registered inline, not via `AddXxx` extension | `src/PaymentGateway.Api/Program.cs:14` |
| S-003 | Important | Standards — `PaymentDocument` is `record`, `MerchantDocument` is `class` | `src/PaymentGateway.Api/Services/PaymentDocument.cs:13`, `MongoCredentialStore.cs:36-37` |
| S-004 | Nit | Standards — `GetAsync` filter uses string `"_id"` vs `AddAsync` lambda | `src/PaymentGateway.Api/Services/MongoPaymentsRepository.cs:40` |
| S-005 | Nit | Standards — `IPaymentsRepository.cs` + `PaymentsRepositoryTests.cs` lost trailing newline | `src/PaymentGateway.Api/Services/IPaymentsRepository.cs:23`, `test/.../PaymentsRepositoryTests.cs:66` |
| S-006 | Nit | Standards — `PaymentDocument.CreatedAt` stamped at write, not at creation | `src/PaymentGateway.Api/Services/PaymentDocument.cs:52` |
| S-007 | Nit | Standards — no MongoDB index on `payments` collection | `src/PaymentGateway.Api/Services/MongoPaymentsRepository.cs:22`, `README.md:189, 193` |
| P-001 | Important | Spec — `design.md:135-139` "Concurrency" section is now stale (explicit user flag) | `docs/design.md:135-139` |
| P-002 | Pass | Spec — `payments` collection shape mirrors `audit_records` (masked PAN, no CVV, async API, idempotent Add) | `PaymentDocument.cs`, `MongoPaymentsRepository.cs`, `MongoAuditStore.cs` |
| P-003 | Pass | Spec — README "Inspecting the payments collection" accurate against persisted shape | `README.md:180-199` |
| P-004 | Pass | Spec — replace-on-duplicate matches `IPaymentsRepository` contract | `MongoPaymentsRepository.cs:25-35`, `IPaymentsRepository.cs:13-19` |
| P-005 | Pass | Spec — integration test follows established convention end-to-end | `PaymentsPersistenceIntegrationTests.cs:1-98` |
| P-006 | Pass | Spec — status / persistence mapping unchanged by Stage 12 | `PaymentsController.cs:60-84`, `design.md:80-95` |
| P-007 | Pass | Spec — async rename propagates through every call site | `PaymentsController.cs:90`, `PaymentsService.cs:52`, `PaymentsServiceTests.cs:45-158` |

---

**Spec verdict**: Stage 12 covers the user's request *"Transfer this process to MongoDB, using the audit collection as reference to insert and get payments"* (P-002): masked PAN (last-four only), no CVV anywhere, async API end-to-end, idempotent Add via `ReplaceOneAsync + IsUpsert`, typed `[BsonElement]` BSON document with the same field-shape conventions as the audit half. The one spec gap is `design.md:135-139` (P-001, explicit user flag): the "Concurrency" section still describes the in-memory `ConcurrentDictionary` repo, which is now test-fixture-only — same docs-drift pattern Stage 11 caught for audit persistence. The architecture diagram at `design.md:25` also needs a one-line update to drop the "(in-memory, ConcurrentDictionary)" parenthetical.

**Standards verdict**: One clear copy-paste bug (S-001, AuditMiddlewareTests.cs:64-71 duplicated `RemoveAll + AddSingleton` block — easy 4-line deletion). One convention break (S-002, payments repo registered inline in `Program.cs` instead of via a `AddPaymentsRepository` extension parallel to `AddAudit`/`AddCredentialCache`). One inconsistency with the existing typed-DTO store (S-003, `PaymentDocument` is `record`, `MerchantDocument` is `class`). Everything else is nits (S-004 through S-007). The structural design — async API, `CancellationToken` propagation, `IMongoDatabase`-based DI, replace-on-duplicate matching the documented contract, BSON shape conventions matching the audit half, integration test following the established `[SkippableFact]` + `[Collection("Integration")]` pattern — is sound and consistent with the codebase's established conventions.

**Recommendation**:

- **Must fix before commit**: S-001 (delete the duplicated `RemoveAll`/`AddSingleton` block in `AuditMiddlewareTests.cs:64-71`), P-001 (rewrite `design.md:135-139` "Concurrency" section + update the architecture diagram at line 25 to reflect the Mongo-backed production impl).
- **Could address later** (clean follow-ups): S-002 (`AddPaymentsRepository` extension in `Configuration/`, matching `AddAudit` etc.), S-003 (pick a side on `record` vs `class` for the typed BSON DTOs), S-007 (MongoDB index on `{merchantId: 1, _id: -1}` for the payments collection).
- **Pure nits** (sweep in a later cleanup or with `dotnet format`): S-004 (use lambda filter in `GetAsync`), S-005 (trailing-newline consistency), S-006 (`CreatedAt` source — write-time vs creation-time).

The must-fix-before-commit set is two small edits totaling well under 10 lines of code/docs change. The structural design is sound, the test coverage is complete (158 unit/component + 1 new integration test), the build is clean, and the BSON shape mirrors the audit half exactly as the user asked.
