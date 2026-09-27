# Code Review — Stage 7 (JWT auth gating on PaymentsController)

**Reviewer**: .NET Code Reviewer Agent
**Scope**: Stage 7 diff against `main` (`PaymentsController`, `MerchantId` on `Payment`,
`IPaymentsService.ProcessPaymentAsync` signature, `AddJwtAuthentication` extension + config,
`Program.cs` wiring, both csproj JWT packages, `PaymentsControllerTests` rewrite + new JWT helper,
`PaymentsServiceTests` thread-through + new merchant-id test, new
`JwtAuthenticationServiceCollectionExtensionsTests`).
**Date**: 2026-09-27
**.NET version**: `net10.0`
**Build/test result observed**: clean build, 0 warnings (also under
`TreatWarningsAsErrors=true`), 121/121 tests passing (was 108).

---

## Standards

### S-001 · Important · Standards / formatting

**Location**: `src/PaymentGateway.Api/Services/IPaymentsService.cs:13-25`
**Description**: The interface body's XML doc and method declaration lost their indentation.
In `git show main:src/PaymentGateway.Api/Services/IPaymentsService.cs`, every line inside
`public interface IPaymentsService { … }` is indented 4 spaces; in the rewrite the same lines
sit flush at column 1.
**Why it matters**: `csharp_indent_block_contents = true` in `.editorconfig` and the
established style in every other interface (e.g. `IAcquiringBankClient`, `IPaymentsRepository`,
`ITokenIssuanceService`) use 4-space indent for interface members. The build is clean because
no formatter or indentation analyzer is wired up, so a manual reviewer is the only line of
defence — exactly the case this stage is producing otherwise spotless output for, so the
regression sticks out.
**Suggested fix**: Re-indent the inner `///` block and the `Task<Payment> ProcessPaymentAsync(…)`
declaration by 4 spaces (matches `PaymentsService.cs`, `IPaymentsService.cs@main`):

```csharp
public interface IPaymentsService
{
    /// <summary>
    /// Processes <paramref name="request"/> (assumed already validated) against the
    /// acquiring bank on behalf of <paramref name="merchantId"/>, and persists the
    /// resulting <see cref="Payment"/> tagged with that merchant id (ADR-0010).
    /// </summary>
    Task<Payment> ProcessPaymentAsync(
        PostPaymentRequest request, string merchantId, CancellationToken cancellationToken = default);
}
```

### S-002 · Nit · Standards / package version

**Location**:
- `src/PaymentGateway.Api/PaymentGateway.Api.csproj:13`
- `test/PaymentGateway.Api.Tests/PaymentGateway.Api.Tests.csproj:12`

**Description**: Both projects pin `Microsoft.AspNetCore.Authentication.JwtBearer` at `8.0.10`,
while the projects target `net10.0` (and the test project already takes
`Microsoft.AspNetCore.Mvc.Testing 10.0.0`). `dotnet list package --outdated` reports
`10.0.12` as the current match for the runtime version.
**Why it matters**: The 8.x line keeps a parallel `System.IdentityModel.Tokens.Jwt 8.3.0`
graph while ASP.NET Core 10.0 is shipping on a newer ABI. It works in practice (build is clean,
tests pass), but the mismatch is a small foot-gun for future security patches — the type
forwarding works, but patches (e.g. CVE fixes) ride on the higher version, not the pinned one.
**Suggested fix (could address later)**: Bump both `<PackageReference>`s to the matching
`10.0.x` line so the runtime and the IdentityModel graph move together. No code changes needed;
the public APIs used (`MapInboundClaims`, `TokenValidationParameters`,
`SecurityAlgorithms.HmacSha256`, `SymmetricSecurityKey`) are unchanged.

### S-003 · Nit · Standards / test helper

**Location**: `test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs:225-226`
**Description**: The `internal static class TestJwt` helper uses
`System.Security.Claims.ClaimsIdentity` and `System.Security.Claims.Claim` with full type
qualifiers instead of a `using System.Security.Claims;` directive.
**Why it matters**: The rest of the test file uses the convention "add a `using`, drop the
qualifier" (see lines 1–18). Two extra characters of qualified-name noise in a helper class is
a minor readability miss against an otherwise consistent file.
**Suggested fix**: Add `using System.Security.Claims;` to the file and let the call sites
shrink to `new ClaimsIdentity(new[] { new Claim("sub", subject) })`.

### S-004 · Nit · Standards / unnecessary package reference

**Location**: `test/PaymentGateway.Api.Tests/PaymentGateway.Api.Tests.csproj:12`
**Description**: `Microsoft.AspNetCore.Authentication.JwtBearer` is added to the test
project, but the only test usage (`PaymentsControllerTests.TestJwt.Mint`) calls
`JsonWebTokenHandler`, `SymmetricSecurityKey`, `SigningCredentials`, `SecurityAlgorithms` —
all of which live in `Microsoft.IdentityModel.JsonWebTokens` and `Microsoft.IdentityModel.Tokens`.
Those come in transitively through `JwtBearer`, but for a project whose only goal is "mint a
test token", the explicit reference should be the Microsoft.IdentityModel packages.
**Why it matters**: Pulling in `JwtBearer` for a test fixture implies production-grade
behaviour the fixture doesn't need; `Microsoft.IdentityModel.JsonWebTokens` is the lighter,
honest choice.
**Suggested fix (optional)**: Replace the `JwtBearer` reference with
`Microsoft.IdentityModel.JsonWebTokens` (and rely on the transitive
`Microsoft.IdentityModel.Tokens`). Leaves the test project free of ASP.NET Core auth surface
it doesn't exercise.

---

No findings on:

- C# idioms / variable naming (`_paymentsService`, `_paymentsRepository`, `_validator`,
  `_tokenIssuance`, `_cache`, `_service`, `_repository`, `_bank` — all match
  `dotnet_naming_rule.private_fields_should_be__camelcase`).
- `readonly` on injected fields (`dotnet_style_readonly_field = true:warning`).
- `using` directive grouping (system-first, separated, outside namespace — matches the existing
  files).
- Single-project structure (no Domain/Application/Infrastructure split introduced — consistent
  with `docs/handoff.md` and `docs/implementation-plan.md`).
- Test pattern (NSubstitute + FluentAssertions + xunit `[Fact]`/`[Theory]` + `MemberData`,
  identical to the Stage 4–6 suite; `WebApplicationFactory<…>` pattern consistent with
  `AuthControllerTests`).
- Action-method naming (`ProcessPayment`/`GetPayment` without `Async` suffix for controllers —
  the handoff explicitly flagged the old `GetPaymentAsync` rename as a missed nit, fixed here).

---

## Spec

### P-001 · Spec · ADR-0010 — JWT Bearer validation parameters (closed)

**Location**: `src/PaymentGateway.Api/Configuration/JwtAuthenticationServiceCollectionExtensions.cs:32-46`
**Description**: Pins `ValidAlgorithms = ["HS256"]` via `SecurityAlgorithms.HmacSha256`, sets
`MapInboundClaims = false`, `ValidateIssuer = false`, `ValidateAudience = false`,
`ValidateLifetime = true`, `ValidateIssuerSigningKey = true`. This is the exact set called out
in `docs/adr/0010` and `docs/handoff.md` "Open questions" #2.
**Why it matters**: Closing all three handoff-mandated knobs (algorithm pinning, claim mapping,
issuer/audience off) is the difference between "JWT-shaped authentication" and "authentication
that's not vulnerable to `alg=none` / algorithm-confusion / stale `iss`/`aud` mismatches".
The implementation matches the spec bit-for-bit.
**Status**: Pass. No fix needed.

### P-002 · Spec · ADR-0010 — `[Authorize]` on both actions

**Location**: `src/PaymentGateway.Api/Controllers/PaymentsController.cs:23`
**Description**: `[Authorize]` is at the controller class, applying to both `ProcessPayment`
and `GetPayment`. `AuthController` remains unauthenticated by design.
**Why it matters**: Matches ADR-0010's text "`PaymentsController` requires
`Authorization: Bearer <jwt>`" applied to "POST/GET payments" — placing the attribute at the
class level covers both actions and is the single-source-of-truth pattern from the spec.
**Status**: Pass.

### P-003 · Spec · ADR-0010 / design.md — "404 not 403" for cross-merchant

**Location**: `src/PaymentGateway.Api/Controllers/PaymentsController.cs:92-95`
**Description**: When `payment is null || payment.MerchantId != CallerMerchantId()`, the
controller collapses both cases into a single `NotFound()`.
**Why it matters**: This is the exact pattern in `docs/design.md` Merchant-authentication
section and ADR-0010 Decision block ("returns `404`, not `403`, when the caller isn't the
owner — consistent with not revealing whether a payment exists to a merchant that doesn't own
it"). Collapsing null + wrong-owner into the same response is the correct way to honour the
"never reveal existence" property — leak prevention requires they be indistinguishable in the
response.
**Test coverage**: `GET_returns_404_when_the_payment_does_not_exist` and
`GET_returns_404_when_the_caller_does_not_own_the_payment` — both explicit. Pass.
**Status**: Pass.

### P-004 · Spec · design.md / ADR-0001 — Bank failure → 503 vs 400 → 500

**Location**: `src/PaymentGateway.Api/Controllers/PaymentsController.cs:66-82`
**Description**: `BankUnavailableException` → `503` with `Problem(statusCode: 503, title: …)`;
`InvalidBankRequestException` → `500` with `Problem(statusCode: 500, title: …)`.
**Why it matters**: This is the exact pair ADR-0001 and `docs/design.md` "Bank integration"
section prescribe. A bank `400` that became a `503` would invite a merchant retry that can
never succeed (the gateway built a malformed request) and would corrupt bank-availability
metrics. The two-catch pattern with `Problem(...)` is the documented way to surface them.
**Status**: Pass.

### P-005 · Spec · design.md — Authorization header / documented auth steps

**Location**: `src/PaymentGateway.Api/Controllers/PaymentsController.cs:40-83`,
`src/PaymentGateway.Api/Program.cs:33-46`
**Description**: Stage 7 wires only the auth-relevant pieces for now (bank-client defaults are
stage 9). The implementation-plan footnote explicitly defers the `BankSimulator:BaseUrl` /
`BankSimulator:TimeoutSeconds` config finalisation to stage 9, and the
`SupportedCurrencies` allow-list to stage 9 as well. The Stage 9 README "How to call the API"
section is also out of scope for this stage per the plan.
**Why it matters**: Ensures stage 7 stays reviewable. No findings — the plan is being followed
literally. The hardcoded `["GBP","USD","EUR"]` currency allow-list and the
`localhost:8080`/5-second bank client are placeholders explicitly labelled in comments to
make the stage-9 hand-off obvious.
**Status**: Pass.

### P-006 · Spec · Implementation plan Stage 7 — Done-when criteria

**Location**: `test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs`
**Description**: All the Stage 7 done-when cases are present and individually asserted:

| Scenario | Test |
|---|---|
| missing token → 401 | `GET_returns_401_when_no_token_is_provided`, `POST_returns_401_when_no_token_is_provided` |
| malformed token → 401 | `GET_returns_401_when_the_token_is_malformed` |
| wrong-key token → 401 | `GET_returns_401_when_the_token_is_signed_with_the_wrong_key` |
| expired token → 401 | `GET_returns_401_when_the_token_has_expired` |
| POST validation failure → 400 | `POST_returns_400_when_the_request_fails_validation` |
| GET owner → 200 | `GET_returns_200_with_the_payment_when_the_caller_owns_it` |
| GET missing → 404 | `GET_returns_404_when_the_payment_does_not_exist` |
| GET cross-merchant → 404 | `GET_returns_404_when_the_caller_does_not_own_the_payment` |

Tokens are minted by `TestJwt.Mint(...)`, signed directly with the test config's
`Jwt:SigningKey` — read back via
`factory.Services.GetRequiredService<IConfiguration>().GetValue<string>("Jwt:SigningKey")`. This
is exactly the `docs/handoff.md` "WAF `ConfigureAppConfiguration` doesn't reach
`Add*` extensions" gotcha and the stage-7 explicit instruction. POST happy-path is correctly
**not** re-tested here, per the plan (covered in Stage 4; re-verified end-to-end in Stage 10).

**Status**: Pass.

### P-007 · Spec · design.md — "Full PAN/CVV never persisted"

**Location**: `src/PaymentGateway.Api/Services/PaymentsService.cs:40-50`
**Description**: Even with `MerchantId` added to the persisted shape, only
`CardNumberLastFour` reaches the `Payment` model — the full PAN (`request.CardNumber!`) and
CVV (`request.Cvv!`) are still consumed only by the bank-request build and never stored.
**Why it matters**: Stage 7 widens the persisted shape, so this guarantee is worth
re-checking at this commit. It still holds.
**Status**: Pass.

### P-008 · Spec · ADR-0010 — `MerchantId` comes from the caller's JWT, not the request body

**Location**: `src/PaymentGateway.Api/Controllers/PaymentsController.cs:59,63`
**Description**: `merchantId = CallerMerchantId()` is read from `User.FindFirstValue("sub")` and
passed as a parameter to `IPaymentsService.ProcessPaymentAsync`. `PostPaymentRequest` has no
`MerchantId` field — there is no over-posting / mass-assignment vector for owning a payment
under someone else's id.
**Why it matters**: ADR-0010 specifies that merchant identity is the JWT's `sub`, never the
body. The new `MerchantId` parameter is the only place a payment gets tagged with a merchant,
and it's controller-supplied from `User` — the same code path the test mints a sub for.
**Test coverage**: The new `Persists_the_caller_merchant_id_on_the_payment` test in
`PaymentsServiceTests`, plus the implicit "owner-vs-cross-merchant" assertions in
`PaymentsControllerTests`, prove both halves (service stores it; controller uses it for
ownership). Pass.
**Status**: Pass.

---

## Findings summary

| ID | Severity | Category | File |
|---|---|---|---|
| S-001 | Important | Standards — formatting | `src/PaymentGateway.Api/Services/IPaymentsService.cs:13-25` |
| S-002 | Nit | Standards — package version | both csprojs (line 13 / 12) |
| S-003 | Nit | Standards — using directive | `test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs:225-226` |
| S-004 | Nit | Standards — package surface | `test/PaymentGateway.Api.Tests/PaymentGateway.Api.Tests.csproj:12` |
| P-001–P-008 | Spec — Pass | ADR-0010 / ADR-0001 / design.md / Stage 7 plan | various |

**Spec verdict**: No spec deviations. Every Stage 7 acceptance criterion (401 on
missing/malformed/wrong-key/expired; POST 400 on validation failure; GET 200/404 including
cross-merchant 404; `[Authorize]` on both actions; `ValidAlgorithms=["HS256"]`,
`MapInboundClaims=false`, no `iss`/`aud`/`ValidateLifetime=true`; `404` not `403` for
cross-merchant; bank errors mapping 503/500; full PAN/CVV never persisted) is implemented
and individually asserted. The handoff's "Open question" #2 (mid-implementation note that the
claim mapping must be `false`) is honoured, and the WAF-config gotcha is correctly worked
around in the test by reading the real signing key back via `factory.Services`.

**Standards verdict**: One meaningful but localised regression — the indentation inside
`IPaymentsService.cs` was lost during the rewrite (S-001). Everything else is tight: no
unused code, no aspirational-feature creep, single-project layout preserved, naming and test
patterns match Stages 1–6, `[Authorize]` correctly placed at class level, build clean with
0 warnings under `TreatWarningsAsErrors=true`.

**Recommendation**: Apply S-001 before commit; the rest can ride to a later pass. Stage 7 is
otherwise ready to ship.
