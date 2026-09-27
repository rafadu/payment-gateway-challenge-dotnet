# .NET Code Review — Stage 11 (audit persistence)

**Reviewer**: .NET Code Reviewer Agent
**Scope**: Stage 11 diff vs `main`. Tracked-file modifications: `Program.cs` (Adds `AddAudit` + `UseMiddleware<AuditMiddleware>`), `PaymentsController.cs` (sets `HttpContext.Items[AuditMiddleware.OutcomeItemKey]`), `README.md` (new "Inspecting the audit trail" + "Running the test suite" sections), `docs/adr/0004-...md` (new "Update — audit trail half now implemented (Stage 11)" section), `test/.../AuthControllerTests.cs` + `PaymentsControllerTests.cs` (audit-store override added to every factory), `test/.../csproj` (Stage-10 carry-over `Xunit.SkippableFact 1.4.13`). Untracked new files: `src/PaymentGateway.Api/Services/AuditRecord.cs`, `.../MongoAuditStore.cs`, `.../AuditMiddleware.cs`, `src/.../Configuration/AuditServiceCollectionExtensions.cs`, `test/.../AuditMiddlewareTests.cs`, `test/.../Services/InMemoryAuditStoreTests.cs`, `test/.../Integration/AuditPersistenceIntegrationTests.cs`.
**Date**: 2026-09-27
**.NET version**: `net10.0`
**Build/test observed**: clean build, 0 warnings. `dotnet test` → `Passed! - Failed: 0, Passed: 158, Skipped: 6, Total: 164`. `dotnet test --filter "Category=Integration"` → `Skipped! - Failed: 0, Passed: 0, Skipped: 6, Total: 6`. Six `[SkippableFact]`s now skip (up from 5 in Stage 10); all 158 non-integration tests pass.

---

## Standards

### S-001 · Important · `AuditRecord.cs` advertises "Record" but ships a POCO `class` (could address later)

**Location**: `src/PaymentGateway.Api/Services/AuditRecord.cs:9`
**Description**: The type is `public sealed class AuditRecord` with `init`-only properties and no behavior. The file name (`AuditRecord.cs`), the user's description ("new `AuditRecord` record"), and the readonly-immutable-with-defaults shape all point at `record class`. Converting `class` → `record class` is a no-op at the call sites that already exist (`MongoAuditStore.WriteAsync` reads `record.Id`, `record.Timestamp`, etc.; `AuditMiddleware` builds `new AuditRecord { ... }`; `InMemoryAuditStore.WriteAsync` takes `AuditRecord record`) and would gain value-equality, `with` expressions, and `Deconstruct` for free.
**Why it matters**: The file name advertises a type C# devs expect to be a `record`. Anyone refactoring for value-equality will discover the type isn't a record and the change is bigger than the file name suggests. It's also the only domain entity in this repo's `Services/` folder that has `init`-only properties and zero methods — every other shape in the folder (`IdempotencyStore`, `CredentialCache`, `MongoCredentialStore`, `AcquiringBankClient`) is behavior-bearing.
**Suggested fix**:

```csharp
public sealed record AuditRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string MerchantId { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public int StatusCode { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public IReadOnlyDictionary<string, object?>? RequestSummary { get; init; }
}
```

(No call-site changes; see `IdempotencyResourceFilter.IdempotencyClaimOutcome`-style records in the same repo for the established `sealed record` convention.)

---

### S-002 · Important · `AuditServiceCollectionExtensions.cs:9` doc comment is wrong about pipeline position (must fix before commit)

**Location**: `src/PaymentGateway.Api/Configuration/AuditServiceCollectionExtensions.cs:8-12`
**Description**: The XML doc reads:

> The middleware is wired in `Program.cs` right before MVC (after `UseAuthorization`), so it sees both authenticated responses and the `401`s `UseAuthorization` emits directly.

The actual `Program.cs:54-58` wires `app.UseMiddleware<AuditMiddleware>()` **before** `UseAuthentication()` and `UseAuthorization()`, not after. The current position is the correct one (captures `401`s before auth middleware emits them), so the comment is wrong about the position but right about the intent.
**Why it matters**: A reviewer/operator reading the extension's doc to understand pipeline order will form the wrong mental model. ADR-0004's "Update — Stage 11" section also phrases it correctly ("before MVC's authorization layer so it captures `401`s"). The two docs disagree on the placement; the extension doc is the one that's wrong.
**Suggested fix**: Replace the line with:

```csharp
/// The middleware is wired in <c>Program.cs</c> before <c>UseAuthentication</c>/
/// <c>UseAuthorization</c>, so it captures requests even when they are rejected at
/// the authorization-middleware layer (i.e. <c>401</c>s).
```

---

### S-003 · Important · `InMemoryAuditStore.cs:46-47` doc comment still refers to "the resource filter" (must fix before commit)

**Location**: `src/PaymentGateway.Api/Services/AuditRecord.cs:46-47` (the XML doc on `interface IAuditStore`)
**Description**: The comment reads:

> One process-wide singleton — audit writes are fire-and-forget from the request hot path (the resource filter awaits the write before returning), but the store itself holds no per-request state.

Stage 11 deliberately moved audit out of MVC into middleware (`AuditMiddleware.cs:9-17`); there's no "resource filter" in the codebase anymore. The comment was accurate for the design being abandoned, not for the design being shipped.
**Why it matters**: A reader who navigates `IAuditStore → InMemoryAuditStore → ...` for "how does the audit middleware call this" lands on a doc that names a class (`IdempotencyResourceFilter`) which is the wrong middleware. The comment contradicts the implementation.
**Suggested fix**: Rewrite the two sentences to describe the actual middleware-level wiring:

```csharp
/// Persists <see cref="AuditRecord"/> instances. One process-wide singleton —
/// <see cref="AuditMiddleware"/> awaits <see cref="WriteAsync"/> before returning
/// the response, but the store itself holds no per-request state.
```

---

### S-004 · Important · `AuditMiddlewareTests.cs:35-54` `Authorize` helper duplicates the JWT-mint ceremony instead of reusing `TestJwt.Mint` (must fix before commit)

**Location**: `test/PaymentGateway.Api.Tests/AuditMiddlewareTests.cs:35-54`
(cf. `test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs:449-465`, the existing `TestJwt.Mint` helper)
**Description**: `Authorize` re-implements the JWT-mint routine inline:

```csharp
new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
{
    Subject = new System.Security.Claims.ClaimsIdentity(
        new[] { new System.Security.Claims.Claim("sub", merchantId) }),
    NotBefore = DateTime.UtcNow,
    Expires = DateTime.UtcNow.AddMinutes(15),
    SigningCredentials = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        SecurityAlgorithms.HmacSha256)
});
```

The exact same ceremony is already in `TestJwt.Mint(string subject, string signingKey, TimeSpan? lifetime = null)`, an `internal static class` in the same test assembly. The author of `AuditMiddlewareTests.Authorize` worked around the missing `using` by fully-qualifying `System.Security.Claims.ClaimsIdentity` and `System.Security.Claims.Claim` (see the inline `System.Security.Claims.ClaimsIdentity(...)` and `System.Security.Claims.Claim(...)` calls) instead of just `using PaymentGateway.Api.Tests.Controllers;`.
**Why it matters**: Stage 10's reviewer flagged this same anti-pattern in `BankSimulatorIntegrationTests.NewClientAsync` (Stage 10 review S-001). The fix wasn't applied; a third copy of the same ceremony is now being added to the same test assembly. Any future security rotation (algorithm change, `iss`/`aud` introduction, expiry tweak) needs to be made in three places.
**Suggested fix**: Replace the `Authorize` body with a call to `TestJwt.Mint`:

```csharp
using PaymentGateway.Api.Tests.Controllers;  // adds the TestJwt helper

private static void Authorize(HttpClient client, string merchantId)
{
    var factory = new WebApplicationFactory<Program>();
    var signingKey = factory.Services.GetRequiredService<IConfiguration>()
        .GetValue<string>("Jwt:SigningKey")!;
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
        "Bearer", TestJwt.Mint(merchantId, signingKey));
}
```

(The factory is still needed to read the real `Jwt:SigningKey` — see handoff.md "WAF ConfigureAppConfiguration overrides do not reach config read inside AddX extensions".)

---

### S-005 · Important · `AuditMiddlewareTests.Declined_POST_writes_outcome_Declined` builds a factory and client, then never uses them (must fix before commit)

**Location**: `test/PaymentGateway.Api.Tests/AuditMiddlewareTests.cs:106-110`
**Description**: The test body reads:

```csharp
var store = new InMemoryAuditStore();
using var factory = FactoryWith(store);   // <-- dead
var client = factory.CreateClient();       // <-- dead
Authorize(client, MerchantA);              // <-- dead (wrong client is later re-authorized at line 130)

var fakeBank = ...;

using var factoryWithFakeBank = new WebApplicationFactory<Program>().WithWebHostBuilder(...)
var client2 = factoryWithFakeBank.CreateClient();
Authorize(client2, MerchantA);             // <-- the real authorization
var response = await client2.PostAsJsonAsync("/api/payments", ...);
```

Lines 107-109 build `factory`, `client`, and call `Authorize(client, ...)` — then never send a request through them. The `using` doesn't even matter; no request triggers a middleware execution. The first three lines of the test body are dead and should be removed.
**Why it matters**: Dead code in a test misleads readers into thinking the first factory is part of the test's setup. Worse: the first `Authorize(client, MerchantA)` is on the unused client — if someone "fixes" the test by switching `client2` → `client`, the auth header on the wrong client breaks the whole test, and the failure mode is "401" instead of "Declined", which is the kind of subtle bug the existing `Comment` in the diff (`// Card ends in 2 → Declined (per imposters/bank_simulator.ejs). Bank client registered for prod is at http://localhost:8080 which isn't running in unit tests, so this would surface as a 503.`) was clearly written to explain. The comment is now stranded above dead code.
**Suggested fix**: Delete lines 106-109. The body becomes:

```csharp
[Fact]
public async Task Declined_POST_writes_outcome_Declined()
{
    var store = new InMemoryAuditStore();
    var fakeBank = Substitute.For<IAcquiringBankClient>();
    fakeBank.ProcessPaymentAsync(Arg.Any<Models.Bank.BankPaymentRequest>(), Arg.Any<CancellationToken>())
        .Returns(new Models.Bank.BankPaymentResponse { Authorized = false });

    using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAuditStore>();
            services.AddSingleton<IAuditStore>(store);
            services.RemoveAll<IAcquiringBankClient>();
            services.AddSingleton(fakeBank);
        });
    });
    var client = factory.CreateClient();
    Authorize(client, MerchantA);

    var response = await client.PostAsJsonAsync("/api/payments", new PostPaymentRequest { ... });

    response.StatusCode.Should().Be(HttpStatusCode.Created);
    store.Records.Single().Outcome.Should().Be("Declined");
}
```

---

### S-006 · Important · `AuditMiddleware` swallows every exception silently — no `ILogger`, no observability (could address later)

**Location**: `src/PaymentGateway.Api/Services/AuditMiddleware.cs:23-30, 58-65`
**Description**: The middleware ctor takes only `(RequestDelegate next, IAuditStore store)` — no `ILogger<AuditMiddleware>`. The `try { await _store.WriteAsync(...); } catch { }` block at line 58-65 silently swallows everything. The comment justifies the swallow ("audit is a forensic concern, not a transactional one"), which is the correct design choice — a broken audit must not break a working response — but the implementation provides zero observability for the case where audit writes start failing (Mongo down, network partition, BSON serialization exception).
**Why it matters**: In production, the only signal that audit is broken will be "investigation discovers the collection is empty". `ILogger<T>` is one constructor parameter and one line per log call; the cost is zero. Compare `IdempotencyResourceFilter` and `TokenIssuanceService` — both depend on time/logging for similar "soft" failures.
**Suggested fix**:

```csharp
public AuditMiddleware(RequestDelegate next, IAuditStore store, ILogger<AuditMiddleware> logger)
{
    _next = next;
    _store = store;
    _logger = logger;
}

try
{
    await _store.WriteAsync(record, context.RequestAborted);
}
catch (Exception ex)
{
    // Forensic concern — a failed audit write must not break a successful response.
    _logger.LogError(ex, "Audit write failed for {Method} {Path} ({StatusCode}, {Outcome})",
        record.Method, record.Path, record.StatusCode, record.Outcome);
}
```

---

### S-007 · Important · `AddAudit` ignores its `IConfiguration` parameter and never fails fast (could address later)

**Location**: `src/PaymentGateway.Api/Configuration/AuditServiceCollectionExtensions.cs:15-19`
**Description**: Every other `AddX` extension in this folder reads `IConfiguration` and throws on missing/blank/invalid values at startup:

- `AddMongoDb`: throws on missing `Mongo:ConnectionString`/`Mongo:Database` (`MongoServiceCollectionExtensions.cs:13-23`)
- `AddBankClient`: throws on missing `BankSimulator:BaseUrl`/`TimeoutSeconds` (`BankClientServiceCollectionExtensions.cs:17-33`)
- `AddJwtAuthentication`: throws on missing/short `Jwt:SigningKey` (`JwtAuthenticationServiceCollectionExtensions.cs:19-30`)
- `AddCredentialCache`: throws on non-positive `CredentialCache:TtlHours` (`CredentialCacheServiceCollectionExtensions.cs:19-23`)
- `AddTokenIssuance`: similar
- `AddPaymentValidation`: similar

`AddAudit` is the only one in this folder that takes `IConfiguration` and never reads it:

```csharp
public static IServiceCollection AddAudit(this IServiceCollection services, IConfiguration configuration)
{
    services.AddSingleton<IAuditStore, MongoAuditStore>();
    return services;
}
```

The `MongoAuditStore` ctor depends on `IMongoDatabase`. If a developer calls `AddAudit` before `AddMongoDb`, the failure mode is a DI resolution exception on the first request that hits the middleware, not a startup error.
**Why it matters**: Handoff.md explicitly calls out fail-fast-at-startup as a property the existing extensions enforce. `AddAudit` is the new arrival in this folder and breaks that property silently. Also: `MongoAuditStore` will happily connect to *any* `IMongoDatabase` — there's no check that the database is the `payment_gateway` one, so a misconfigured DI registration pointing at a different Mongo db would silently pollute that database with audit records.
**Suggested fix**:

```csharp
public static IServiceCollection AddAudit(this IServiceCollection services, IConfiguration configuration)
{
    services.TryAddSingleton<IAuditStore, MongoAuditStore>();
    return services;
}
```

`TryAddSingleton` makes the override pattern work uniformly with the test factories' `RemoveAll<IAuditStore>() + AddSingleton<IAuditStore>(...)` idiom (the explicit removal still works; a "AddAudit was never called" failure is acceptable). For the second concern (cross-DB contamination), a future iteration could read `configuration["Mongo:Database"]` and fail if a different store wrote to the wrong db — but that's the kind of check that belongs in a regression test, not in startup wiring.

---

### S-008 · Nit · `AuditRecord.Timestamp = DateTime.UtcNow` init default is dead code (could address later)

**Location**: `src/PaymentGateway.Api/Services/AuditRecord.cs:13`
**Description**: `AuditRecord.Timestamp { get; init; } = DateTime.UtcNow;` defaults the timestamp at property-init time. The middleware (`AuditMiddleware.cs:46`) builds `new AuditRecord { Timestamp = DateTime.UtcNow, ... }` — explicitly setting the timestamp, overriding the default before it's observed by anything. The init default never fires in production. It does make `BuildRecord()` in `InMemoryAuditStoreTests.cs:11-29` work as intended (the test passes a fixed `Timestamp = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc)`, overriding it again), but the default itself is dead.
**Why it matters**: Side-effecting defaults (`= DateTime.UtcNow`) on properties are a known footgun — every instantiation that *doesn't* override reads the clock, which makes construction non-deterministic and surprises anyone testing equality on the resulting record. With the timestamp already dead, removing the default costs nothing.
**Suggested fix**: Change the property to `public DateTime Timestamp { get; init; }` and update `BuildRecord()` in `InMemoryAuditStoreTests.cs` to keep its explicit `Timestamp = ...` (it already does).

---

### S-009 · Nit · `AuditMiddleware.cs:82` uses `request.HttpContext.RequestAborted` (already on `context`) (could address later)

**Location**: `src/PaymentGateway.Api/Services/AuditMiddleware.cs:60, 82`
**Description**: Line 60 reads `await _store.WriteAsync(record, context.RequestAborted)` — correct, uses the captured `HttpContext`. Line 82 (inside `BuildMaskedSummaryAsync(HttpRequest request)`) reads `request.HttpContext.RequestAborted` — same value, longer indirection. `request.HttpContext` *is* the `context` already passed in.
**Why it matters**: Reader has to verify `request.HttpContext` is the same instance as the parameter. The longer form is misleading; suggests there's a different request scope.
**Suggested fix**: Change the signature to `BuildMaskedSummaryAsync(HttpContext context)` and use `context.RequestAborted`, or keep the signature and just use `request.HttpContext.RequestAborted` with a one-line comment "(same as the outer `context.RequestAborted`)". The former is cleaner.

---

### S-010 · Nit · `AuditMiddleware.cs:46` uses `DateTime.UtcNow` directly, not the established `TimeProvider` pattern (could address later)

**Location**: `src/PaymentGateway.Api/Services/AuditMiddleware.cs:46`
(cf. `IdempotencyResourceFilter.cs:24` and `TokenIssuanceService.cs`, both injecting `TimeProvider`)
**Description**: The middleware hardcodes `DateTime.UtcNow` at line 46 for the `Timestamp` field. Every other time-sensitive component in this repo (`IdempotencyResourceFilter`, `TokenIssuanceService`, `PostPaymentRequestValidator`) injects `TimeProvider` for deterministic testing.
**Why it matters**: It's the only new time-sensitive code in this stage that doesn't follow the established pattern. The middleware tests don't assert on `Timestamp` equality (they assert `DurationMs >= 0`), so the inconsistency has no test-level fallout — but a future "assert that an audit record's timestamp is between T1 and T2" test will be hard to write deterministically without a `TimeProvider` injection.
**Suggested fix**: Inject `TimeProvider _time` in the ctor and use `_time.GetUtcNow().UtcDateTime` at line 46. Tests can substitute `FakeTimeProvider`.

---

### S-011 · Nit · `AuditMiddleware` swallows `JsonSerializer.DeserializeAsync` exceptions silently (no logger) (could address later)

**Location**: `src/PaymentGateway.Api/Services/AuditMiddleware.cs:84-89`
**Description**: The `catch { return null; }` block in `BuildMaskedSummaryAsync` swallows any deserialization failure (malformed body, type mismatch, serializer-config bug). For a malformed body the swallow is correct (the model binder will reject it anyway, and `null` for the request summary is reasonable). But it also swallows serializer bugs (e.g. a future `JsonConverter` throws because of a malformed datetime string in the body — that's a different category of failure that should be observable).
**Why it matters**: Same observability miss as S-006, narrower scope. Once S-006's `ILogger<AuditMiddleware>` exists, this catch should log at debug level (not error — the model binder will surface the 400).
**Suggested fix**: After S-006's logger is added, distinguish: malformed body → return `null` (current behavior); unexpected serializer exception → log debug, return `null`. `JsonException` is the right discriminator.

---

### S-012 · Nit · `AuditPersistenceIntegrationTests.cs:41` empty `WithWebHostBuilder(builder => { })` lambda (could address later)

**Location**: `test/PaymentGateway.Api.Tests/Integration/AuditPersistenceIntegrationTests.cs:41`
**Description**: `using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => { });` — the empty builder closure is dead. `WithWebHostBuilder` with no service overrides is equivalent to constructing the factory directly.
**Why it matters**: Same shape as Stage 10 review S-010 nit. No behavior change; cosmetic.
**Suggested fix**:

```csharp
using var factory = new WebApplicationFactory<Program>();
```

---

### S-013 · Nit · `AuditPersistenceIntegrationTests.cs:89` uses `BeOneOf("Authorized", "Declined")` for a card ending in `1` (must fix before commit)

**Location**: `test/PaymentGateway.Api.Tests/Integration/AuditPersistenceIntegrationTests.cs:89`
**Description**: The test sends card `2222405343248871` (last digit `1` → odd → Authorized per `imposters/bank_simulator.ejs`). The assertion is `doc["outcome"].AsString.Should().BeOneOf("Authorized", "Declined")` — defensive, but masks a regression where the controller stops setting `HttpContext.Items[AuditMiddleware.OutcomeItemKey]`. If that line is ever removed, the middleware's default for `201` is `"Adjudicated"` (per `AuditMiddleware.cs:127`), and the test would fail loudly with the right `Be("Authorized")` assertion. With `BeOneOf`, it would silently pass if the controller were ever changed to return Declined on a card ending in `1` (also a regression).
**Why it matters**: A loose integration assertion on the outcome label defeats the whole point of the test — that the controller → `HttpContext.Items` → middleware → BSON pathway is end-to-end correct. A `Be("Authorized")` is the right assertion for a card ending in `1`.
**Suggested fix**: Change line 89 to `doc["outcome"].AsString.Should().Be("Authorized");`.

---

### S-014 · Nit · `AuditRecord.cs:24-30` outcome-label XML doc omits `Forbidden` (could address later)

**Location**: `src/PaymentGateway.Api/Services/AuditRecord.cs:24-30`
(cf. `src/PaymentGateway.Api/Services/AuditMiddleware.cs:130`)
**Description**: The XML doc lists the outcome labels — `Authorized`, `Declined`, `ValidationRejected`, `Unauthorized`, `NotFound`, `Conflict`, `HashMismatch`, `BankUnavailable`, `InternalError` — but omits `Forbidden`, which `ResolveOutcome` produces for status `403` (`AuditMiddleware.cs:130`). The README's "Inspecting the audit trail" section also lists the same nine labels, no `Forbidden`.
**Why it matters**: A new producer reading the doc to learn what labels exist would write `Outcome = "Forbidden"` if their 403-emitting endpoint arrived, and would be surprised when reading the doc that "Forbidden" isn't there. Either remove `Forbidden` from `ResolveOutcome` (and let it fall through to `Unknown`) or add it to both lists.
**Suggested fix**: Decide whether `403` should be a label. If yes, add to both doc-comment lists. If no, remove `403 => "Forbidden"` from `ResolveOutcome` and let it fall to `_ => "Unknown"` — currently both branches are dead.

---

### S-015 · Nit · `AuditMiddlewareTests.cs:25` `MerchantA` constant is fine; the test-name case for `Unauthenticated_request_…` is the only lowercase-leading test method in the suite (could address later)

**Location**: `test/PaymentGateway.Api.Tests/AuditMiddlewareTests.cs:147`
(cf. `PaymentsControllerTests.cs:117` `GET_returns_401_when_no_token_is_provided`, `AuthControllerTests.cs:51` `Valid_credentials_return_200_…`)
**Description**: The repo convention is method names that start with the verb/behavior being tested in PascalCase (`GET_returns_401_…`, `POST_persists_the_payment_…`, `POST_with_a_new_Idempotency_Key_…`). `Unauthenticated_request_writes_one_record_with_empty_merchant_id_and_outcome_Unauthorized` starts with `Unauthenticated` (lowercase `r` in `request`). All other tests in the suite use HTTP verb + scenario or behavior + scenario.
**Why it matters**: A small consistency miss; not worth blocking.
**Suggested fix**: Rename `Unauthenticated_request_…` → `GET_without_a_token_writes_outcome_Unauthorized` (matches the `GET_returns_401_*` family). Drop the leading `U` → `u` mismatch.

---

### S-016 · Nit · `MongoAuditStore.WriteAsync` builds the BSON document manually instead of using a typed `[BsonElement]` DTO like `MongoCredentialStore.MerchantDocument` (could address later)

**Location**: `src/PaymentGateway.Api/Services/MongoAuditStore.cs:21-40`
(cf. `src/PaymentGateway.Api/Services/MongoCredentialStore.cs:36-50`)
**Description**: `MongoCredentialStore` (the other Mongo store in this repo) defines a typed `MerchantDocument` with `[BsonId]`, `[BsonElement("merchantId")]`, etc., and the driver serializes it. `MongoAuditStore` instead hand-builds `BsonDocument` with literal field-name strings, with `ToBsonValue` doing the type-conversion switch.
**Why it matters**: Two stores in the same repo, two different conventions. The hand-built `BsonDocument` is more flexible about the `RequestSummary` heterogeneous value types (`string`, `int`, etc.) than a typed DTO would be, so the approach is defensible. But the field-name list now exists in two places (`AuditRecord` properties + `MongoAuditStore.WriteAsync`'s `BsonDocument` initializer) — `merchantId`, `method`, `path`, `statusCode`, `outcome`, `durationMs`, `requestSummary`, `_id`, `timestamp`. A refactor that adds a new `AuditRecord` field has to update both.
**Suggested fix**: Either (a) introduce `AuditRecordDocument` typed DTOs and let the driver handle serialization, or (b) keep the manual approach and document the choice (one line in the `MongoAuditStore` XML doc — "BSON is hand-built rather than `[BsonElement]`-driven because `RequestSummary` carries heterogeneous value types"). Option (b) is lower-effort; option (a) is more consistent with `MongoCredentialStore`.

---

### S-017 · Nit · `audit_records` collection has no MongoDB index — every mongosh query in the README does a full collection scan (could address later)

**Location**: `README.md:165, 171` (the mongosh queries), `src/PaymentGateway.Api/Services/MongoAuditStore.cs:18` (collection creation)
**Description**: The README's two `mongosh` examples (`find().sort({timestamp:-1}).limit(10)` and `find({merchantId: "..."}).sort({timestamp:-1})`) both filter or sort on un-indexed fields. `MongoAuditStore` does not create any indexes — there's no `CreateIndex` call, no `IMongoIndexManager` usage. On an audit collection that grows monotonically over time, both queries become full-collection scans with in-memory sorts.
**Why it matters**: A "todo" ADR-0005 (async audit pipeline) note might cover this, but the README presents the queries as the user-facing operational surface — the queries are slow from day one. Two small indexes (`{merchantId: 1, timestamp: -1}` for the per-merchant case, `{timestamp: -1}` for the global case) would be one method call on the index manager.
**Suggested fix**: Add a `CreateIndexesAsync` call in `MongoAuditStore`'s ctor (or a separate `IHostedService` that owns index lifecycle):

```csharp
var keys = Builders<BsonDocument>.IndexKeys
    .Ascending("merchantId").Descending("timestamp");
await _collection.Indexes.CreateOneAsync(
    new CreateIndexModel<BsonDocument>(keys, new CreateIndexOptions { Name = "merchantId_timestamp" }));
```

(Don't index `_id` — the driver does that automatically.)

---

### No findings on

- **Pipeline position** (`Program.cs:54-58`): `app.UseMiddleware<AuditMiddleware>()` before `UseAuthentication()`/`UseAuthorization()`/`MapControllers()`. Matches the ADR-0004 "Update — Stage 11" reasoning ("before MVC's authorization layer so it captures `401`s"). Compare with `IdempotencyResourceFilter`'s resource-filter position (inside MVC) — the rationale for moving audit *out* of MVC into middleware is correct: resource filters can't see `401`s emitted by `UseAuthorization` (they run after it). The ADR explains the choice; the code matches the explanation.
- **Test substitution pattern** in `AuthControllerTests.cs:42-45` and the three factory methods in `PaymentsControllerTests.cs:52-53, 83-84, 104-105`: every existing factory now swaps `IAuditStore → InMemoryAuditStore` so the production `MongoAuditStore` doesn't block on a missing Mongo connection during `dotnet test`. The comments above each substitution explain *why* (no Mongo in unit tests; the connection would block on timeout). This is the right pattern; without it, every existing test in the file would have hung on first request.
- **`PaymentsController.cs:65`** setting `HttpContext.Items[AuditMiddleware.OutcomeItemKey] = payment.Status.ToString()` before the 201 return: this is the correct `HttpContext.Items`-keyed override pattern (same mechanism `IAntiforgery`/`ISession` use). The const `AuditMiddleware.OutcomeItemKey = "Audit.Outcome"` is exposed publicly so the controller can reference it without magic-string duplication.
- **`AuditRecord.Id` defaults**: `Guid Id { get; init; } = Guid.NewGuid();` — produces a unique id per record without forcing every constructor to specify it. Tests can override (e.g., `InMemoryAuditStoreTests.BuildRecord` doesn't; `MongoAuditStore` stores the GUID as a string `_id`).
- **`AuditMiddleware.BuildMaskedSummaryAsync` body-rewind discipline** (`AuditMiddleware.cs:75, 93`): calls `request.Body.Position = 0` before *and* after the deserialize, in a `finally`. The model binder reads the body after this method returns, so the rewind is necessary (same shape as `IdempotencyResourceFilter.ComputeRequestHashAsync`). The `EnableBuffering()` call at line 35 is the prerequisite.
- **`MongoAuditStore.WriteAsync` cancellation**: passes `context.RequestAborted` to `_collection.InsertOneAsync` (via `MongoAuditStore.WriteAsync(record, context.RequestAborted)` at `AuditMiddleware.cs:60`). Honors the established `CancellationToken` propagation convention (`AcquiringBankClient`, `MongoCredentialStore.FindByClientIdAsync` all do this).
- **`AuditRecord.RequestSummary` masking** (`AuditMiddleware.cs:100-106`): never carries `Cvv`, never carries the full `CardNumber` — only `cardNumberLastFour` (`[^4..]`), plus `currency`, `amount`, `expiryMonth`, `expiryYear`. Matches ADR-0004 "Decision" block and `design.md:107-110` exactly.
- **`InMemoryAuditStore` concurrency** (`AuditRecord.cs:60-72`): `List<>` + `lock (_lock)` for both writes and the `Records` getter. The same `lock(_lock) return _records.ToArray()` pattern the rest of the repo uses (`IdempotencyStore` analog).
- **`AuditMiddlewareTests.cs` test choice**: `[Fact]` (always-runs) + `WebApplicationFactory<Program>` + `NSubstitute.For<IAuditStore>()` + FluentAssertions. Matches the pattern in `PaymentsControllerTests` exactly. No `[Trait("Category", "Integration")]` is the correct call — these tests run against a mocked bank, not the live Mountebank; they belong in the default suite, not the integration suite.
- **`AuditPersistenceIntegrationTests.cs` collection-fixture pattern**: `[Collection("Integration")]`, `[Trait("Category", "Integration")]`, `[SkippableFact]`, `Skip.IfNot(_fixture.ServicesAvailable, ...)` — textbook xunit pattern, matches the two existing integration test files. The `merchant-A` literal in the JWT is consistent with `AuthFlowIntegrationTests.DemoClientId = "demo-merchant"` style.
- **`AuditRecord.Id` storage as string** in MongoDB (`MongoAuditStore.cs:25`): `_id` is `record.Id.ToString()`. Matches what the integration test's `doc["merchantId"].AsString` reader expects. The MongoDB driver's automatic ObjectId generation is bypassed, which is fine because `Guid.NewGuid()` already produces a unique value and string `_id` is the convention in this collection.
- **`AuditMiddlewareTests.Declined_POST_writes_outcome_Declined` — the substantive assertions are right**: card ending in `2`, fake bank returns `Authorized = false`, controller writes `HttpContext.Items["Audit.Outcome"] = "Declined"`, middleware writes `Outcome = "Declined"` to the store. The `Response.Should().Be(HttpStatusCode.Created)` + `store.Records.Single().Outcome.Should().Be("Declined")` assertions are correct — it's only the dead factory/client setup at lines 107-109 that's wrong (S-005).
- **`AuditPersistenceIntegrationTests` BSON shape assertions**: `cardNumberLastFour == "8871"`, `currency == "GBP"`, `amount == 100`, no `cvv`, no `cardNumber` — all PCI-DSS-correct per the design.md "Data handling" section and ADR-0004 "Decision".
- **Test naming for the other 5 audit middleware tests**: `Authorized_POST_writes_one_record_with_outcome_Authorized_and_masked_summary`, `Validation_failure_writes_outcome_ValidationRejected`, `Bank_unavailable_writes_outcome_BankUnavailable`, `GET_request_writes_one_record_with_no_request_summary` — all PascalCase-leading-verb, matches the repo convention. Only `Unauthenticated_request_…` is off (S-015).

---

## Spec

### P-001 · Important · `design.md:191-197` "Explicitly out of scope" line still claims audit persistence is out of scope (must fix before commit, or follow-up)

**Location**: `docs/design.md:195`
(cf. `README.md:154-208` and `docs/adr/0004-...md:73-90`, both updated for Stage 11)
**Description**: `design.md` reads (lines 191-197):

> Nothing beyond the request/response flow, validation, persistence, bank integration, merchant authentication, and idempotency described above is implemented in this codebase — this is a scoped exercise, not a production-ready service. That includes rate limiting (including on the token endpoint — see ADR-0010's Consequences), a circuit breaker around the bank client (ADR-0001), **audit persistence**, custom metrics, a Redis-backed credential cache (ADR-0011), merchant registration/credential-rotation/roles/refresh-tokens (ADR-0010), and full bank-side idempotency/reconciliation for ambiguous timeouts (ADR-0003).

`audit persistence` is the only item on that list whose status changed in this stage. The README and ADR-0004 both carry "Update" notes; `design.md` was not touched.
**Why it matters**: A reviewer using `design.md` as the canonical scope statement will conclude audit is not implemented. The user's request explicitly anticipated this drift and asked for it to be flagged. `design.md` is the only spec document that has not been updated; README + ADR-0004 are honest, design.md is stale.
**Suggested fix**: Add a one-paragraph "Update — audit persistence implemented (Stage 11)" section after line 197, parallel to ADR-0004's update, and strike `audit persistence` from the out-of-scope list. Minimum viable edit:

```diff
-That includes rate limiting (including on the token endpoint — see ADR-0010's Consequences),
-a circuit breaker around the bank client (ADR-0001), audit persistence, custom metrics, a
+That includes rate limiting (including on the token endpoint — see ADR-0010's Consequences),
+a circuit breaker around the bank client (ADR-0001), custom metrics, a
```

```markdown
### Update — audit persistence implemented (Stage 11)

The audit-persistence half of the out-of-scope list above has now been implemented. One
document per request is written to the `audit_records` collection in the `payment_gateway`
database by an audit middleware (ADR-0004, audit half; see ADR-0004's "Update — audit trail
half now implemented (Stage 11)" for the full contract). The full PAN and CVV are never
persisted. What remains out of scope is the asynchronous audit pipeline, field-level
encryption, and correlation IDs/trace context — see ADR-0004 for the explicit non-goals.
```

---

### P-002 · Important · Bank simulator's `authorization_code` is not captured anywhere in the audit (could address later)

**Location**: `src/PaymentGateway.Api/Services/AuditMiddleware.cs:32-66`, `src/PaymentGateway.Api/Services/MongoAuditStore.cs:21-40`
(cf. user's Stage 11 request: *"mongodb storage for the request/response, with other datas and the result from the banking simulator"*, and ADR-0004 "Decision" block: *"one document per processed request, capturing correlation id, timestamp, merchant id, endpoint, masked request/response summary, outcome, latency breakdown (total, bank-call), and which acquirer handled the call"*.)
**Description**: The current `AuditRecord` captures the *outcome label* (`Authorized` / `Declined`) but not the bank's actual response payload. The bank returns `{ authorized: true, authorization_code: "auth-code" }` (or `{ authorized: false }` with no code); only `authorized` is preserved as a label, `authorization_code` is discarded. `design.md:113-114` explicitly notes "The bank's `authorization_code` is not part of the merchant-facing response schema (per the spec's tables) and is not stored" — that's the merchant-facing surface. ADR-0004's "Decision" block calls for a "masked request/response summary" — implying the bank's response should be in the audit, not just its outcome.
**Why it matters**: The user's Stage 11 request explicitly named "the result from the banking simulator" as one of the audit-capture targets. The current shape captures the *label* of the result, not the result itself. A dispute-resolution scenario ("merchant A claims bank issued auth-code X for payment Y") needs the `authorization_code` in the audit trail to verify against the bank's records. Without it, the audit trail can prove "we sent a payment that was Authorized" but not "what the bank's authorization code for that payment was".
**Suggested fix**: Add a `BankResponseSummary` (or `BankAuthorizationCode`) field on `AuditRecord`, populated by the controller via `HttpContext.Items["Audit.BankAuthCode"]` (or extended `HttpContext.Items["Audit.BankResponse"] = ...`). The controller already has the `BankPaymentResponse` after the call; setting one extra `HttpContext.Items` is a 1-line addition. The middleware reads it the same way it reads `OutcomeItemKey`. The Mongo store writes it as a string field. PCI-DSS scope: `authorization_code` is not card data, so it's safe to persist in plaintext.

---

### P-003 · Suggestion · Total latency only; bank-call latency breakdown is missing (could address later)

**Location**: `src/PaymentGateway.Api/Services/AuditMiddleware.cs:36-38, 52`
(cf. ADR-0004 "Decision" block: *"outcome, latency breakdown (total, bank-call), and which acquirer handled the call"*)
**Description**: The middleware records `DurationMs = stopwatch.ElapsedMilliseconds` — the entire request duration, including model binding, validation, controller dispatch, repository write, response serialization. The bank call is one slice of that. ADR-0004's "Decision" block enumerates "latency breakdown (total, bank-call)". The current shape has only `DurationMs` (total).
**Why it matters**: An SRE looking at slow payments needs to distinguish "the bank is slow" from "our validator is slow". Total latency alone forces an apples-to-oranges comparison with the bank's published SLO. The bank call is already timed by `IAcquiringBankClient` (a typed `HttpClient` with a `Stopwatch`-friendly timing path can be added); the `Stopwatch` could live in `PaymentsService.ProcessPaymentAsync` and surface via `HttpContext.Items["Audit.BankDurationMs"]`.
**Suggested fix**: Capture bank-call latency in `PaymentsService` (where the call is made) and surface via `HttpContext.Items`. Add `BankDurationMs` to `AuditRecord`. The middleware writes both `DurationMs` and `BankDurationMs`. Document the two-timing-source split.

---

### P-004 · Suggestion · Middleware awaits the Mongo write synchronously on the response hot path (ADR-0004 documents this as known limitation)

**Location**: `src/PaymentGateway.Api/Services/AuditMiddleware.cs:60`
(cf. ADR-0004 "Update" block: *"Today the middleware awaits the Mongo write before the response returns; a real production system would batch-write or push to a queue."*)
**Description**: `await _store.WriteAsync(record, context.RequestAborted)` is awaited *before* the middleware returns. A slow/blocked Mongo (network blip, container restart, replica election) adds its latency to every response. The ADR's "Update" block already flags this as known-but-acceptable for now.
**Why it matters**: Documented as out-of-scope but worth a one-line flag in the review so the next stage that picks up ADR-0005 (async pipeline) has a clear pointer to where the change goes. The catch block at lines 58-65 is also where the async-write path will need to enqueue rather than await.
**Suggested fix (could address later)**: No action required for Stage 11 — ADR-0004 documents the limitation. Carry forward as a pointer for ADR-0005.

---

### P-005 · Pass · Masked request summary correctly captures last-four, omits CVV, retains currency/amount/expiry (PCI-DSS compliant)

**Location**: `src/PaymentGateway.Api/Services/AuditMiddleware.cs:98-106`
(cf. `docs/design.md:107-110`, ADR-0004 "Decision" + "Important clarification that affects scope")
**Description**: `BuildMaskedSummaryAsync` builds the `RequestSummary` as:

```csharp
{
    { "cardNumberLastFour", ExtractLastFour(body.CardNumber) },  // [^4..]
    { "expiryMonth", body.ExpiryMonth },
    { "expiryYear", body.ExpiryYear },
    { "currency", body.Currency },
    { "amount", body.Amount }
    // CVV intentionally omitted — never persisted (PCI-DSS, ADR-0004).
}
```

Every property matches `design.md:107-110` ("only the last four digits of the card number reach the domain model (`Payment`) or any response body") and ADR-0004's "Important clarification" block ("the CVV is never stored, encrypted or not"). `ExtractLastFour` returns `string.Empty` for null/short strings (line 111-112), not `null` — which matches the `PaymentResponse.CardNumberLastFour : string` shape.
**Status**: Pass. The integration test asserts exactly this shape (`AuditPersistenceIntegrationTests.cs:93-98`).

---

### P-006 · Pass · Outcome labels correctly distinguish Authorized/Declined (both 201s) via the controller's `HttpContext.Items` override

**Location**: `src/PaymentGateway.Api/Controllers/PaymentsController.cs:65`, `src/PaymentGateway.Api/Services/AuditMiddleware.cs:115-138`, `src/PaymentGateway.Api/Services/AuditRecord.cs:24-30`
**Description**: The controller sets `HttpContext.Items[AuditMiddleware.OutcomeItemKey] = payment.Status.ToString()` before returning `201`. The middleware reads that item in `ResolveOutcome` (line 120-123) before falling back to the status-code map (line 125-137). For a `201`, the default would be `"Adjudicated"`; with the controller's override it's `"Authorized"` or `"Declined"`. This is exactly what ADR-0004 "Update" describes: "Outcome labels distinguish `Authorized`/`Declined` (both are `201`s)". The middleware test `Declined_POST_writes_outcome_Declined` (`AuditMiddlewareTests.cs:143`) and `Authorized_POST_…` (`AuditMiddlewareTests.cs:94`) both pass.
**Status**: Pass. The override mechanism is clean, the test coverage is complete for both branches.

---

### P-007 · Pass · Middleware correctly captures `401`s emitted by `UseAuthorization` (pre-MVC position)

**Location**: `src/PaymentGateway.Api/Program.cs:54-58`
(cf. ADR-0004 "Update" block: *"The middleware runs before MVC's authorization layer so it captures `401`s as well as `201`/`400`/`503`"*)
**Description**: `app.UseMiddleware<AuditMiddleware>()` is registered *before* `app.UseAuthentication()` and `app.UseAuthorization()`. A request with no/invalid/expired bearer token hits `UseAuthorization`, gets rejected with `401`, and the response bubbles back through `AuditMiddleware` (which sits before `UseAuthorization` in the call stack). The middleware reads the `401` from `context.Response.StatusCode`, reads `merchantId = ""` from `context.User.FindFirstValue("sub") ?? string.Empty` (no `sub` claim on an unauthenticated principal), and writes an `AuditRecord` with `MerchantId = ""`, `StatusCode = 401`, `Outcome = "Unauthorized"`. The `Unauthenticated_request_writes_one_record_with_empty_merchant_id_and_outcome_Unauthorized` test (`AuditMiddlewareTests.cs:147-162`) verifies this end-to-end against the real middleware.
**Status**: Pass. The middleware-vs-resource-filter rationale in the design.md (`ADR-0004 "Update"`) is implemented correctly.

---

### P-008 · Pass · The masked `RequestSummary` shape is correct: last-four (string), no CVV, currency/amount/expiry retained

**Location**: `src/PaymentGateway.Api/Services/AuditMiddleware.cs:109-113` (`ExtractLastFour`), `src/PaymentGateway.Api/Services/AuditMiddleware.cs:98-106` (`RequestSummary` dictionary), `test/PaymentGateway.Api.Tests/Integration/AuditPersistenceIntegrationTests.cs:92-98`
**Description**: `ExtractLastFour` uses the C# range operator `[^4..]` to take the last four characters of `CardNumber`. The integration test asserts the exact value (`summary["cardNumberLastFour"].AsString.Should().Be("8871")` for card `2222405343248871`) and the absence of both `cvv` (`summary.Contains("cvv").Should().BeFalse()`) and full `cardNumber` (`summary.Contains("cardNumber").Should().BeFalse()`). The `Authorized_POST_…` middleware test asserts the same shape against the in-memory store (`AuditMiddlewareTests.cs:96-101`).
**Status**: Pass. PCI-DSS / ADR-0004 "Important clarification" requirements are satisfied and verified end-to-end against real MongoDB.

---

### P-009 · Pass · Middleware does not write audit records for non-`/api/payments` POST bodies (defensive shape)

**Location**: `src/PaymentGateway.Api/Services/AuditMiddleware.cs:72-74`
**Description**: `BuildMaskedSummaryAsync` early-returns `null` for anything that's not `POST /api/payments`:

```csharp
if (!HttpMethods.IsPost(request.Method)) return null;
if (!string.Equals(request.Path, "/api/payments", StringComparison.OrdinalIgnoreCase)) return null;
```

The `string.Equals(..., OrdinalIgnoreCase)` is defensible (IIS defaults to case-insensitive paths; Linux is case-sensitive but the path-constant matches). A future `POST /api/refunds` would not leak its body shape into the audit until `BuildMaskedSummaryAsync` is taught its schema.
**Status**: Pass. Defensive against accidental body capture from new endpoints; matches `IdempotencyResourceFilter.IsPostToPayments`'s controller-name-based check (also narrowly scoped).

---

### P-010 · Suggestion · README's outcome-list omits `Forbidden` (could address later)

**Location**: `README.md:188-195`
(cf. `src/PaymentGateway.Api/Services/AuditMiddleware.cs:130` mapping `403 => "Forbidden"`)
**Description**: The README's "Inspecting the audit trail" section enumerates the outcome labels: `Authorized`/`Declined`/`ValidationRejected`/`Unauthorized`/`NotFound`/`Conflict`/`HashMismatch`/`BankUnavailable`/`InternalError` — nine labels. `AuditMiddleware.ResolveOutcome` produces a tenth, `Forbidden` (for `403`), that's missing from both the README and the `AuditRecord` XML doc (S-014). With the current controller surface (no endpoint emits `403`), the label is unreachable today, so the omission is technically correct; with the future endpoint surface, it becomes a documentation gap.
**Why it matters**: Same finding as S-014 from a different angle. Either add `Forbidden` to both lists or remove it from `ResolveOutcome`.
**Suggested fix**: Add `Forbidden` to both lists, or remove `403 => "Forbidden"` from `ResolveOutcome` (let it fall to `_ => "Unknown"`).

---

## Findings summary

| ID | Severity | Category | File |
|---|---|---|---|
| S-001 | Important | Standards — `AuditRecord` is a `class`, not a `record` | `src/PaymentGateway.Api/Services/AuditRecord.cs:9` |
| S-002 | Important | Standards — stale "after UseAuthorization" doc | `src/PaymentGateway.Api/Configuration/AuditServiceCollectionExtensions.cs:8-12` |
| S-003 | Important | Standards — stale "resource filter" doc | `src/PaymentGateway.Api/Services/AuditRecord.cs:46-47` |
| S-004 | Important | Standards — JWT-mint duplicated instead of `TestJwt.Mint` | `test/PaymentGateway.Api.Tests/AuditMiddlewareTests.cs:35-54` |
| S-005 | Important | Standards — dead factory/client in Declined test | `test/PaymentGateway.Api.Tests/AuditMiddlewareTests.cs:106-110` |
| S-006 | Important | Standards — no `ILogger` for swallowed audit failures | `src/PaymentGateway.Api/Services/AuditMiddleware.cs:23-30, 58-65` |
| S-007 | Important | Standards — `AddAudit` ignores `IConfiguration` | `src/PaymentGateway.Api/Configuration/AuditServiceCollectionExtensions.cs:15-19` |
| S-008 | Nit | Standards — dead `Timestamp = DateTime.UtcNow` init default | `src/PaymentGateway.Api/Services/AuditRecord.cs:13` |
| S-009 | Nit | Standards — `request.HttpContext.RequestAborted` redundancy | `src/PaymentGateway.Api/Services/AuditMiddleware.cs:60, 82` |
| S-010 | Nit | Standards — `DateTime.UtcNow` direct, not `TimeProvider` | `src/PaymentGateway.Api/Services/AuditMiddleware.cs:46` |
| S-011 | Nit | Standards — deserialization exception swallowed silently | `src/PaymentGateway.Api/Services/AuditMiddleware.cs:84-89` |
| S-012 | Nit | Standards — empty `WithWebHostBuilder` lambda | `test/PaymentGateway.Api.Tests/Integration/AuditPersistenceIntegrationTests.cs:41` |
| S-013 | Nit | Standards — `BeOneOf` where `Be` is correct | `test/PaymentGateway.Api.Tests/Integration/AuditPersistenceIntegrationTests.cs:89` |
| S-014 | Nit | Standards — outcome-label doc omits `Forbidden` | `src/PaymentGateway.Api/Services/AuditRecord.cs:24-30` |
| S-015 | Nit | Standards — test-name leading-case inconsistency | `test/PaymentGateway.Api.Tests/AuditMiddlewareTests.cs:147` |
| S-016 | Nit | Standards — manual `BsonDocument` vs typed `[BsonElement]` DTO | `src/PaymentGateway.Api/Services/MongoAuditStore.cs:21-40` |
| S-017 | Nit | Standards — no Mongo index on `audit_records` | `README.md:165, 171`, `MongoAuditStore.cs:18` |
| P-001 | Important | Spec — `design.md` "out of scope" line still claims audit persistence is out of scope | `docs/design.md:195` |
| P-002 | Important | Spec — bank's `authorization_code` not captured in audit | `src/PaymentGateway.Api/Services/AuditMiddleware.cs:32-66` |
| P-003 | Suggestion | Spec — bank-call latency breakdown missing | `src/PaymentGateway.Api/Services/AuditMiddleware.cs:36-38, 52` |
| P-004 | Suggestion | Spec — sync Mongo write on response hot path (ADR-0004 documents) | `src/PaymentGateway.Api/Services/AuditMiddleware.cs:60` |
| P-005 | Pass | Spec — masked request summary correct (last-four, no CVV) | `AuditMiddleware.cs:98-106`, integration test |
| P-006 | Pass | Spec — outcome labels distinguish Authorized/Declined for both 201s | `PaymentsController.cs:65`, `AuditMiddleware.cs:115-138` |
| P-007 | Pass | Spec — middleware captures 401s (pre-MVC position) | `Program.cs:54-58`, `AuditMiddlewareTests.cs:147-162` |
| P-008 | Pass | Spec — masked request-summary shape end-to-end correct | `AuditMiddleware.cs:109-113`, integration test |
| P-009 | Pass | Spec — middleware only captures body for POST `/api/payments` | `AuditMiddleware.cs:72-74` |
| P-010 | Suggestion | Spec — README outcome-list omits `Forbidden` | `README.md:188-195`, `AuditMiddleware.cs:130` |

---

**Spec verdict**: Stage 11 covers the user's request "mongodb storage for the request/response, with other datas and the result from the banking simulator" *partially* — request and outcome label are captured (P-005, P-006), but the bank's actual `authorization_code` (the literal "result from the banking simulator") is discarded (P-002). The masked shape is PCI-DSS correct (P-005, P-008) and matches ADR-0004 + `design.md`. The pipeline position is correct (P-007) and the README's two `mongosh` examples are accurate against the persisted shape. `design.md`'s "Explicitly out of scope" line is the only docs-drift that needs follow-up (P-001). All six spec findings are actionable; the three Pass findings are verbatim confirmation that the existing spec sections are honored.

**Standards verdict**: Two real doc-staleness issues (S-002, S-003) need one-line fixes; one duplicated JWT-mint routine (S-004, third copy of the same code) needs the `TestJwt.Mint` refactor that Stage 10 S-001 already flagged; one test has dead factory/client setup (S-005) that the cleanup pass missed; two defensible production-quality misses (S-006 missing logger, S-007 missing fail-fast) are worth doing before the audit trail gets its first production-incident postmortem. Everything else is consistency/style.

**Recommendation**:

- **Must fix before commit**: S-002 (1-line doc fix), S-003 (1-line doc fix), S-004 (refactor `AuditMiddlewareTests.Authorize` to call `TestJwt.Mint`; also retire the Stage 10 S-001 finding), S-005 (delete dead factory/client setup), S-013 (`BeOneOf` → `Be`), P-001 (update `design.md` "out of scope" list).
- **Could address later (clean follow-ups)**: S-001 (`class` → `record class`), S-006 (`ILogger<AuditMiddleware>`), S-007 (`AddAudit` fail-fast), S-016 (BSON typed DTO vs hand-built — pick a side, document it), P-002 (capture bank's `authorization_code` via `HttpContext.Items`), P-003 (bank-call latency breakdown).
- **Pure nits** (sweep in a later cleanup or with `dotnet format`): S-008 through S-017, P-010.

The must-fix-before-commit set is five doc/test-cleanup edits totaling well under 20 lines of code change. The structural design (middleware vs resource filter, masking, outcome label override, test substitution pattern) is sound and consistent with the codebase's established conventions.