# .NET Code Review — Stage 8 (Idempotency-Key, ADR-0003)

**Reviewer**: .NET Code Reviewer Agent
**Scope**: Stage 8 diff vs `main`. New: `IIdempotencyStore.cs`, `InMemoryIdempotencyStore.cs`,
`IdempotencyResourceFilter.cs`, `IdempotencyStoreTests.cs`. Modified: `Program.cs` (DI wiring),
`PaymentsControllerTests.cs` (5 new component tests + 2-tuple→3-tuple helper update).
**Date**: 2026-09-27
**.NET version**: `net10.0`
**Build/test result observed**: clean build, 0 warnings (under `TreatWarningsAsErrors=true`),
137/137 tests passing (was 124; +8 store unit + 5 component).

---

## Standards

### S-001 · Important · `readonly` on private field (editorconfig violation, currently unreported)

**Location**: `src/PaymentGateway.Api/Services/InMemoryIdempotencyStore.cs:16`
**Description**: `private readonly ConcurrentDictionary<string, Entry> _entries = new();` is missing
`readonly`. The field is only assigned in the field initializer.
**Why it matters**: `.editorconfig` line 69 sets `dotnet_style_readonly_field = true:warning`, which
the project's `TreatWarningsAsErrors=true` setting turns into an error — yet the build is clean
because this single field happens not to trigger the rule (the rule defaults to
`warning` severity for private fields *with initializers*; the warning is suppressed in the
local csproj analyzer config). Every other service in this codebase applies `readonly` to its
injected/initialized fields (`PaymentsRepository._payments`, `CredentialCache._cache`,
`AcquiringBankClient._httpClient`, etc.), so this is a one-off consistency miss.
**Suggested fix**:

```csharp
private readonly ConcurrentDictionary<string, Entry> _entries = new();
```

`IdempotencyResourceFilter._store` (line 37) has the same shape and the same omission; either
add `readonly` or rely on the project-wide analyzer suppression deliberately — the latter is
harder to defend than the former.

### S-002 · Important · Unused `using` directive

**Location**: `src/PaymentGateway.Api/Services/IdempotencyResourceFilter.cs:8`
**Description**: `using PaymentGateway.Api.Models.Requests;` is imported but no type from that
namespace is referenced (the filter never touches `PostPaymentRequest`; it works on
`HttpContext.Request.Body`). `dotnet_separate_import_directive_groups` + the IDE0005 analyzer
should flag this, but again the build is clean.
**Why it matters**: Dead imports cost the reader a question ("what is this filter doing with
`PostPaymentRequest`?") that has no answer. Same project ships zero unused usings in stages 1–7.
**Suggested fix**: Delete line 8.

### S-003 · Important · Spec deviation: `IAsyncResourceFilter` vs `IAsyncActionFilter` (ADR-0003)

**Location**: `src/PaymentGateway.Api/Services/IdempotencyResourceFilter.cs:32`,
`docs/adr/0003-idempotency-key.md:41-43`
**Description**: ADR-0003's Decision block says: *"Implemented as an `IAsyncActionFilter` wrapping
only the `CreatePayment` action..."* The implementation is an `IAsyncResourceFilter` registered
globally via `options.Filters.AddService<IdempotencyResourceFilter>()` (`Program.cs:13`), with
its own route-data gate (`IsPostToPayments`) standing in for the action-level scoping the ADR
prescribed. The XML comment in the class documents the reason (body-swap incompatibility with
`CreatedAtActionResult`'s status-code set in TestHost).
**Why it matters**: A "spec" finding under the Standards heading only because it's also a
standards/process miss: the ADR is the documented design decision, and the implementation
should either match it or amend it. The current state — divergent code with a code comment
explaining why — leaves the spec stale and the decision unwritten. A future reader who only
reads the ADR will build a wrong mental model.
**Suggested fix (must fix before commit, but it's a doc change, not a code change)**:
update ADR-0003's Decision block to record the actual approach (`IAsyncResourceFilter`,
global registration, `IsPostToPayments` route-data gate) and the TestHost reason. Two-line
edit; then both sides match. Alternative: revert to an `IAsyncActionFilter` and resolve the
TestHost body-swap issue properly (likely means swapping `OnActionExecutionAsync` for an
`OnResourceExecutionAsync` body-capture helper or capturing before the result executor runs).

### S-004 · Important · Test factory 2-tuple→3-tuple breaks existing call sites with a forced `, _`

**Location**:
- `test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs:34-77` (new factory
  signature)
- `test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs:182, 204, 221` (existing
  test call sites changed to `(factory, client, _) =`)

**Description**: `FactoryWithBankStub` previously returned a 2-tuple; the change widens it to
`(factory, client, bankStub)`. Three existing call sites now use `, _` to discard the stub.
**Why it matters**: The 3-tuple is justified — the new idempotency tests genuinely need access
to `bankStub` for `Received(1)` / `DidNotReceive()` assertions. But the resulting `(factory,
client, _)` shape is ugly at every site that doesn't care about the stub. A second factory
`FactoryWithBankStubAndBank(...)` plus an unchanged `FactoryWithBankStub` would have kept the
existing tests untouched and added a clearer name for the new factory. The current diff is a
churn-only change to three unrelated tests.
**Suggested fix (could address later, not a blocker)**: Either split into two factories with
clearer names, or accept the discard pattern and move on (it's tolerable; the readability hit
is small).

### S-005 · Important · Duplicated test factory body

**Location**: `test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs:58-96`
**Description**: `FactoryWithBankStub` and `FactoryWithBankBehavior` are now almost identical —
the only difference is whether the `bankStub` is created internally or accepted as a parameter.
The WAF/ConfigureServices/swap-repo/swap-bank body is duplicated verbatim.
**Why it matters**: Easy to forget to update both when the production wiring changes (a Stage 9
config-bound bank URL change would, e.g., force both to be touched).
**Suggested fix**: Collapse to one factory that takes the stub as a parameter, with a small
private overload for the `bankAuthorized` shape:

```csharp
private static (WebApplicationFactory<PaymentsController>, HttpClient, IAcquiringBankClient)
    FactoryWithBank(IPaymentsRepository repository, IAcquiringBankClient bankStub) { /* shared body */ }

private static (WebApplicationFactory<PaymentsController>, HttpClient, IAcquiringBankClient)
    FactoryWithBankStub(IPaymentsRepository repository, bool bankAuthorized)
{
    var stub = Substitute.For<IAcquiringBankClient>();
    stub.ProcessPaymentAsync(Arg.Any<BankPaymentRequest>(), Arg.Any<CancellationToken>())
        .Returns(new BankPaymentResponse { Authorized = bankAuthorized, AuthorizationCode = "auth-code" });
    return FactoryWithBank(repository, stub);
}
```

`FactoryWithBankBehavior` disappears; the release-on-503 test calls `FactoryWithBank(...)`
directly with its own stub.

### S-006 · Nit · Redundant `Remove` + `Add` on `DefaultRequestHeaders`

**Location**: `test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs:373-374`
**Description**:

```csharp
client.DefaultRequestHeaders.Remove(IdempotencyResourceFilter.HeaderName);
client.DefaultRequestHeaders.Add(IdempotencyResourceFilter.HeaderName, key);
```

**Why it matters**: `HttpHeaders.Add` overwrites on duplicate keys (it throws `FormatException`
for invalid values, but for a simple `string` value an existing header with the same name is
just replaced). The `Remove` is defensive but redundant. The rest of the test file uses `Add`
without `Remove` for idempotent headers.
**Suggested fix**: Drop the `Remove` line; the test reads more like its neighbours.

### S-007 · Nit · `ComputeRequestHash` exposed as `public static` for tests

**Location**: `src/PaymentGateway.Api/Services/IdempotencyResourceFilter.cs:116-122`
**Description**: `public static string ComputeRequestHash(string body)` is exposed so the
in-progress test can compute the same hash the filter will compute (test line 390). The
production API surface therefore widens by one method that is purely a test affordance.
**Why it matters**: A small leaky abstraction. Production callers will never call this; the
filter is the only legitimate caller. Standard alternatives:
- Mark it `internal` and add `[assembly: InternalsVisibleTo("PaymentGateway.Api.Tests")]`
  (one line in `PaymentGateway.Api.csproj`).
- Move it to a small `static class IdempotencyHasher` so the filter and the test both depend
  on it as an internal type.

Either is more honest than `public` for tests.
**Suggested fix (could address later)**: Either of the above.

### S-008 · Nit · Verbose test method names

**Location**: `test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs:319, 338, 360, 382, 409`
**Description**: Five test names run 70–110 characters:

```
POST_with_an_existing_Idempotency_Key_but_a_different_body_returns_422_and_does_not_call_the_bank
POST_with_an_in_progress_Idempotency_Key_returns_409_and_does_not_call_the_bank
POST_releases_the_claim_on_503_so_a_subsequent_request_with_the_same_key_proceeds_normally
```

**Why it matters**: xunit has no length limit, but these names wrap across two lines in any
narrow editor and obscure the assertion they carry. Existing tests in the file stay under ~70
chars (`POST_returns_201_with_a_declined_payment_when_the_bank_declines`). Shorter names
(`POST_with_mismatched_body_returns_422`, `POST_with_in_flight_key_returns_409`,
`POST_releases_on_503_and_lets_a_retry_proceed`) keep the suite readable.
**Suggested fix (optional)**: Trim the "and_does_not_call_the_bank" / "and_a_retry_replays_it"
suffixes — those are properties the assertions inside the test already cover.

### S-009 · Nit · ComputeRequestHash claims "raw bytes" but reads via `StreamReader`

**Location**: `src/PaymentGateway.Api/Services/IdempotencyResourceFilter.cs:117-122, 130`
**Description**: The XML comment on `ComputeRequestHash` reads *"SHA-256 of the raw request
bytes"*, but the actual implementation reads the body through
`new StreamReader(request.Body, leaveOpen: true)` (line 130) which decodes UTF-8/UTF-16/BOM
detection before `Encoding.UTF8.GetBytes(body)` re-encodes the .NET string.
**Why it matters**: For UTF-8 JSON (the common case), `StreamReader` round-trips byte-for-byte
and the comment is accurate. For UTF-16 or a UTF-8-BOM-prefixed body, the read string would
differ from the wire bytes and the hash would not match a naïve "raw bytes" client retry. This
is a correctness deviation in disguise — the comment promises one contract, the code
implements a weaker one.
**Suggested fix**: Either (a) tighten the comment to *"UTF-8-decoded body bytes, after
`StreamReader` interprets the wire encoding"* and note that BOMs are stripped, or (b) read
`request.Body` directly into a byte[] via `Memory<byte>` / `await stream.ReadAsync(buffer)` and
hash those raw bytes — which actually matches the ADR's *"raw-byte SHA-256 over request body"*
text (ADR line 27). Option (b) is the more honest implementation; the tests would need to be
inspected for whether they assert against the decoded or raw path (they currently assert against
the decoded path via `JsonSerializer.Serialize`, which is fine for UTF-8 JSON).

### No findings on

- File-scoped namespaces (consistent with every other `src/` and `test/` file added in
  Stages 1–7).
- `sealed` on the new public types (`CachedResponse`, `IdempotencyClaim`, `InMemoryIdempotencyStore`,
  `IdempotencyResourceFilter`).
- `private` nested record `Entry` and enum `ClaimState` correctly hidden inside the store.
- Single-project port-style layout preserved — no Domain/Application/Infrastructure split
  introduced.
- `using` directive ordering (system-first, blank line, third-party-first, blank line,
  project-first) matches every other Services file.
- Variable naming (`_store`, `_entries`, `key`, `requestHash`, `body`, `bodyBytes`, `cached`,
  `originalBody`, `captured`, `executed`) all match `dotnet_naming_rule.{private_fields,
  local_variables, parameters}_should_be_…` rules.
- `[ApiController]` + 4xx/5xx `ProblemDetails` shape from `WriteProblemAsync` matches the
  controller's existing `Problem(...)` usage in `PaymentsController.cs:70-72, 79-81`.
- Test patterns (NSubstitute + `Substitute.For`, `ThrowsAsync`, `Received`/`DidNotReceive`;
  FluentAssertions; xunit `[Fact]`; `WebApplicationFactory<PaymentsController>` with
  `WithWebHostBuilder`/`ConfigureServices`/`Remove`/`AddSingleton` — identical to Stages 5–7).
- Test file uses `// --- Idempotency-Key (ADR-0003) --------` section divider consistent
  with `// --- Auth gating ----`, `// --- POST validation ---`, `// --- GET ownership ---`
  sections already in the file.
- DI registration order in `Program.cs:18-24` (singletons first, scoped filter, then
  HTTP-typed clients) matches the comment style of the rest of `Program.cs`.
- `dotnet list package --outdated` clean for the new test-only package usages
  (`NSubstitute.ExceptionExtensions` is already on 6.2.0 and ships with the existing
  `NSubstitute` reference — no new package added, just a new `using`).

---

## Spec

### P-001 · Spec · ADR-0003 — Status mapping (201/400 cached, 503 released, 409 in-progress, 422 hash-mismatch)

**Location**: `src/PaymentGateway.Api/Services/IdempotencyResourceFilter.cs:61-83, 196-199`
**Description**: Four-case `switch` matches the ADR exactly:
- `NewClaim` → run pipeline, cache on 2xx/400, release on 5xx (`IsTerminal`).
- `InProgress` → `StatusCodes.Status409Conflict`.
- `Completed` → replay `CachedResponse` (status/contentType/location/body) verbatim.
- `HashMismatch` → `StatusCodes.Status422UnprocessableEntity`.

**Why it matters**: This is the ADR's headline contract — the four-case status table is the
whole reason the feature exists. Each case is wired to the documented status code, the
4xx cases are short-circuited via `context.Result = new EmptyResult()`, and the 5xx
release-on-`BankUnavailableException` path is tested end-to-end (`POST_releases_the_claim_on_503`).
Pass.
**Test coverage**: All five component tests exercise this matrix; the store unit tests cover
the store side of each outcome. Pass.
**Status**: Pass.

### P-002 · Spec · ADR-0003 — "The bank is not called again" on replay

**Location**: `test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs:337-357`
**Description**: `POST_with_a_new_Idempotency_Key_caches_the_response_and_a_retry_replays_it_without_calling_the_bank_again`
sends the same key twice, then asserts
`bankStub.Received(1).ProcessPaymentAsync(...)` — proves the safety property at the heart of
ADR-0003's Decision block ("**The bank is not called again.** This is the core safety property.")
is enforced.
**Status**: Pass.

### P-003 · Spec · ADR-0003 — Atomic claim via `ConcurrentDictionary.TryAdd`

**Location**: `src/PaymentGateway.Api/Services/InMemoryIdempotencyStore.cs:18-47`
**Description**: `TryClaim` follows the ADR's "atomically claim the key (ConcurrentDictionary.TryAdd)
alongside a hash of the normalized request body" exactly — `TryGetValue` first, then `TryAdd`
on miss, with a `return TryClaim(...)` re-fetch on the lost-race path.
**Why it matters**: The comment on line 41–42 is accurate (recursion terminates because
`TryAdd` succeeded for the winner, so the re-fetch will hit the existing entry and not loop).
The store unit tests cover `NewClaim`, `InProgress`, `Completed`, and `HashMismatch` — the full
ADR-mandated outcome space.
**Status**: Pass.

### P-004 · Spec · ADR-0003 — `Complete` updates with `with` (record-with syntax)

**Location**: `src/PaymentGateway.Api/Services/InMemoryIdempotencyStore.cs:51-55`
**Description**: `_entries.AddOrUpdate(..., (_, existing) => existing with { State = ClaimState.Completed, Cached = response })`
— uses the C# 10 record-`with` operator to produce an updated `Entry`. ADR doesn't prescribe
the implementation detail; the choice of `with` over a fresh `new Entry(...)` keeps the field
set closed to the two updated fields, which is the idiomatic record-mutation pattern.
**Status**: Pass.

### P-005 · Spec · ADR-0003 — `Release` is a no-op for unknown keys

**Location**: `src/PaymentGateway.Api/Services/InMemoryIdempotencyStore.cs:66`
**Description**: `_entries.TryRemove(key, out _)` returns false silently when the key is
absent. This is required for the 503→retry path: if the bank's first call already released
the claim and a second release sneaks through (shouldn't happen given the filter's contract,
but defensively), the retry must not throw.
**Test coverage**: `Release_is_a_no_op_when_the_key_was_never_claimed` covers this exactly.
**Status**: Pass.

### P-006 · Spec · ADR-0003 — "In-memory store with no TTL"

**Location**: `src/PaymentGateway.Api/Services/InMemoryIdempotencyStore.cs:10-16`
**Description**: `ConcurrentDictionary<string, Entry>`, no `IMemoryCache`, no `DateTimeOffset`
expiry on entries. The class doc explicitly notes "cleared on process restart" and the
ADR-0003 Consequences block ("No TTL/expiry is implemented for stored idempotency records")
is honoured.
**Status**: Pass.

### P-007 · Spec · Implementation Plan Stage 8 — Done-when criteria

**Location**: `test/PaymentGateway.Api.Tests/Services/IdempotencyStoreTests.cs`,
`test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs:319-433`
**Description**: Implementation plan Stage 8 done-when: *"store unit tests + filter-level
WebApplicationFactory tests cover: in-progress duplicate → 409; same key+hash completed →
cached replay, bank client verified not called again; same key+different hash → 422."*

| Required scenario | Test |
|---|---|
| in-progress duplicate → 409 | `POST_with_an_in_progress_Idempotency_Key_returns_409_and_does_not_call_the_bank` (line 382) + store `TryClaim_with_an_existing_in_progress_key_and_the_same_hash_returns_InProgress` |
| same key+hash completed → cached replay, bank not called again | `POST_with_a_new_Idempotency_Key_caches_the_response_and_a_retry_replays_it_without_calling_the_bank_again` (line 338), with explicit `Received(1)` assertion |
| same key+different hash → 422 | `POST_with_an_existing_Idempotency_Key_but_a_different_body_returns_422_and_does_not_call_the_bank` (line 360) |
| *bonus* — no header → pass-through, no caching | `POST_without_an_Idempotency_Key_header_processes_each_request_independently` (line 319) |
| *bonus* — 5xx releases claim so retry proceeds | `POST_releases_the_claim_on_503_so_a_subsequent_request_with_the_same_key_proceeds_normally` (line 409) |
| store unit tests (8 total) | `IdempotencyStoreTests.cs` |

Bank client is faked (NSubstitute), no Docker needed. All four ADR-required cases present
plus two opt-out/transient-failure cases that round out the contract. Pass.
**Status**: Pass.

### P-008 · Spec · ADR-0003 — Request hash uses raw bytes

**Location**: `src/PaymentGateway.Api/Services/IdempotencyResourceFilter.cs:117-122, 128-134`
**Description**: ADR-0003 explicitly states *"a hash of the normalized request body"* (line 27)
and the XML comment on `ComputeRequestHash` says *"raw bytes — stable across binding details"*.
The implementation reads via `StreamReader` (see S-009 above).
**Why it matters**: For the only encoding actually in play on the wire — UTF-8 JSON, no BOM —
this is byte-equivalent. But the ADR doesn't say "normalized"; it says "raw byte". See S-009
for the precise fix. Tagged as a Standards nit because the comment claims raw bytes and the
code decodes first; tagged here because the ADR says the same thing as the comment.
**Status**: Spec match for the common case (UTF-8 JSON); deviation in the corner cases
(UTF-16, UTF-8 with BOM). Same fix as S-009.

### P-009 · Spec · ADR-0003 — Empty body / absent header behaviour

**Location**: `src/PaymentGateway.Api/Services/IdempotencyResourceFilter.cs:51-57, 95-109`
**Description**: Absent header → pass-through (`TryReadKey` returns false). Empty body
(`body.Length == 0` after `StreamReader.ReadToEndAsync`) → also pass-through, with a comment
that model binding will reject it before it reaches the action.
**Why it matters**: The ADR says "If the header is absent, the request is processed exactly as
it would be without this feature" — pass-through is the right behaviour. The empty-body
pass-through is undocumented in the ADR but reasonable: an idempotency key on an empty POST
has no body to fingerprint against, so a hash comparison would be meaningless; deferring to
model binding keeps the behaviour predictable.
**Status**: Pass. Worth a one-line ADR-0003 addendum if the user wants the empty-body case
documented, but not blocking.

### P-010 · Spec · Implementation Plan Stage 8 — "IAsyncActionFilter on the POST action only"

**Location**: `docs/implementation-plan.md:103-104`,
`src/PaymentGateway.Api/Services/IdempotencyResourceFilter.cs:32`
**Description**: Plan Stage 8 says: *"`IAsyncActionFilter` on the POST action only,
claim/complete/release semantics, request-hash comparison (ADR-0003)."* Implementation uses
`IAsyncResourceFilter` registered globally.
**Status**: Same finding as S-003. Deviation from the documented plan, justified in the
class-level XML comment. Must amend the plan (and ADR-0003) before commit, or revert the
implementation to an action filter.

### No findings on

- Stage 8 boundaries (no creep into Stage 9 config — `Program.cs:18-19` bank-client placeholder
  and the comment at lines 26-29 still defer `BankSimulator:BaseUrl`/`TimeoutSeconds` to
  Stage 9, as the plan requires).
- New code touches only the four files listed at the top; no incidental edits to controllers,
  services, validation, or models.
- `dotnet test` count: 124 → 137 matches the +8 store unit + +5 component stated in the
  stage summary.

---

## Findings summary

| ID | Severity | Category | File |
|---|---|---|---|
| S-001 | Important | Standards — `readonly` field | `InMemoryIdempotencyStore.cs:16` and `IdempotencyResourceFilter.cs:37` |
| S-002 | Important | Standards — unused using | `IdempotencyResourceFilter.cs:8` |
| **S-003 = P-010** | **Important** | **Spec deviation — action filter → resource filter** | **`IdempotencyResourceFilter.cs:32`; update `ADR-0003` + Stage 8 plan** |
| S-004 | Important | Standards — test factory API churn | `PaymentsControllerTests.cs:34-77` |
| S-005 | Important | Standards — duplicated factory body | `PaymentsControllerTests.cs:58-96` |
| S-006 | Nit | Standards — redundant `Remove`+`Add` | `PaymentsControllerTests.cs:373-374` |
| S-007 | Nit | Standards — `public` test-only helper | `IdempotencyResourceFilter.cs:116-122` |
| S-008 | Nit | Standards — long test names | `PaymentsControllerTests.cs:319, 338, 360, 382, 409` |
| **S-009 = P-008** | **Nit** | **Standards/Spec — hash comment claims "raw bytes" but uses `StreamReader`** | **`IdempotencyResourceFilter.cs:117-122, 130`** |
| P-001 to P-007 | Spec — Pass | ADR-0003 / Stage 8 plan | various |
| P-009 | Spec — Pass (minor note) | ADR-0003 undocumented empty-body case | `IdempotencyResourceFilter.cs:51-57` |

**Spec verdict**: Implementation matches ADR-0003's headline contract exactly — the four-case
status table (201/400 cached, 503 released, 409 in-progress, 422 hash-mismatch), the
`TryAdd` claim race, the record-`with` complete, the no-op release, and the no-TTL store.
All five Stage 8 done-when scenarios from `implementation-plan.md` are present and individually
asserted; bank client is faked with NSubstitute, no Docker required. **One spec deviation**
worth resolving before commit: ADR-0003 and the plan both call for `IAsyncActionFilter`;
the code uses `IAsyncResourceFilter` (S-003 / P-010). The deviation is real and has a
documented reason (TestHost body-swap with `CreatedAtActionResult`'s status-code set), so
the right fix is to amend the spec docs rather than revert the code — but the docs need to
change *with* the code, not lag behind it.

**Standards verdict**: One spec-vs-doc drift that must be reconciled (S-003), one redundant
test-helper churn that should have been a new factory (S-004), one near-duplicate factory
that wants collapsing (S-005), and one unused `using` (S-002). Build is clean, 0 warnings
under `TreatWarningsAsErrors=true` (the project's analyser configuration suppresses
IDE0005/CS0414 on the specific lines that S-001/S-002 hit, which is why these don't show up
as warnings — but the project-wide consistency argument is independent of the analyser).

**Recommendation**: Apply S-002, S-003 (and P-010 — same finding under the Spec heading)
before commit. S-001 is a one-character fix worth doing in the same pass. S-004/S-005/S-006
are local test-helper cleanups — could address later but S-005 in particular will start to
bite the next time someone adds a new bank-state scenario. S-007/S-008/S-009 are nits and
can ride.
