# .NET Code Review — Stage 10 (integration test suite against Mountebank + Mongo)

**Reviewer**: .NET Code Reviewer Agent
**Scope**: Stage 10 diff vs `main`. New: `test/PaymentGateway.Api.Tests/Integration/IntegrationFixture.cs`,
`test/PaymentGateway.Api.Tests/Integration/BankSimulatorIntegrationTests.cs`,
`test/PaymentGateway.Api.Tests/Integration/AuthFlowIntegrationTests.cs`. Modified:
`src/PaymentGateway.Api/Program.cs` (appended `public partial class Program;`),
`test/PaymentGateway.Api.Tests/PaymentGateway.Api.Tests.csproj` (`Xunit.SkippableFact 1.4.13`),
`README.md` ("Running the test suite" section).
**Date**: 2026-09-27
**.NET version**: `net10.0`
**Build/test result observed**: clean build, 0 warnings. `dotnet test` →
`Passed!  - Failed: 0, Passed: 149, Skipped: 5, Total: 154`. `dotnet test --filter "Category=Integration"` →
`Skipped! - Failed: 0, Passed: 0, Skipped: 5, Total: 5`. Both verified in sandbox (Docker unavailable, so
all five `[SkippableFact]`s correctly skip with the expected message).

---

## Standards

### S-001 · Important · JWT-mint logic is duplicated instead of reusing the existing `TestJwt.Mint` helper

**Location**:
`test/PaymentGateway.Api.Tests/Integration/BankSimulatorIntegrationTests.cs:86-105`
(cf. `test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs:436-452`)
**Description**: `BankSimulatorIntegrationTests.NewClientAsync` re-implements the JWT-mint ceremony
inline — `new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor { Subject = new
System.Security.Claims.ClaimsIdentity(...), NotBefore = ..., Expires = ...,
SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
SecurityAlgorithms.HmacSha256) })`. The exact same code already lives in
`PaymentsControllerTests.TestJwt.Mint()` (verified by signature: `Subject = ClaimsIdentity(... "sub"
...)`, `NotBefore = now`, `Expires = now.Add(lifetime ?? TimeSpan.FromMinutes(15))`, HS256).
`TestJwt` is `internal static class TestJwt`, so it's reachable from any other test in the same
assembly — which both Integration tests are. The integration test is using `new
System.Security.Claims.ClaimsIdentity(...)` and `new System.Security.Claims.Claim(...)` fully-qualified
inline to dodge the missing `using` rather than reusing or extending the existing helper.
**Why it matters**: Two copies of a signing-key mint routine that hard-codes the same algorithm /
parameter shape. Any future change (rotate to ES256, add `iss`/`aud`, swap to `Microsoft.IdentityModel.JsonWebTokens`
8.x defaults) needs to be made in two places. This is the same project that handoff.md flagged as
"sensitive to security-key drift" (`Stage 7` reviewer's open question on `ValidAlgorithms`).
**Suggested fix (could address later)**: Either reuse `TestJwt.Mint("merchant-it", signingKey)` via a
`using PaymentGateway.Api.Tests.Controllers;` at the top of the integration test, or promote
`TestJwt` to a `PaymentGateway.Api.Tests/TestInfrastructure/` helper shared by both suites. The
inline mint should disappear.

---

### S-002 · Important · `NewClientAsync` is `async Task<HttpClient>` with no `await` in its body

**Location**: `test/PaymentGateway.Api.Tests/Integration/BankSimulatorIntegrationTests.cs:86`
**Description**: `private static async Task<HttpClient> NewClientAsync()` — body has zero
`await`s. The return value is a fully constructed `HttpClient`; nothing is asynchronous at all. The
`async` modifier is misleading (CS1998 "async method lacks 'await' operators" is the canonical
flag, currently suppressed by the project's analyzer setup, but the warning is real). Compare with
the sibling pattern in `AuthControllerTests.FactoryWith` (`AuthControllerTests.cs:36-42`):
synchronous, returns a factory directly.
**Why it matters**: A test helper that looks async but does no I/O invites readers to assume it does
network I/O and might need cancellation. The synchronous intent is visible only after reading the
entire body. Future maintainers may "fix" it by adding unnecessary `await`s.
**Suggested fix**:

```csharp
private static HttpClient NewClient()
{
    var factory = new WebApplicationFactory<Program>();
    var client = factory.CreateClient();
    // ...assignment to client.DefaultRequestHeaders.Authorization...
    return client;
}
```

```csharp
var client = NewClient();
```

(used in the three `[SkippableFact]`s becomes a synchronous call — there's no `await` on
`NewClient()` to remove because there isn't one now, just the misleading `Task<...>` wrapper).

---

### S-003 · Important · Fixture comment says Mountebank returns 404 from `/`, but it actually returns 400 (the configured `defaultResponse`)

**Location**:
`test/PaymentGateway.Api.Tests/Integration/IntegrationFixture.cs:33-34`
(cf. `imposters/bank_simulator.ejs:6-15`)
**Description**: The fixture comment claims:

> Bank simulator responds to HTTP even when its imposters aren't loaded yet (Mountebank
> returns 404 from /, which is fine — we just need to know it answers).

The actual Mountebank behavior here is set by `defaultResponse` (`imposters/bank_simulator.ejs:6-15`),
which returns `statusCode: 400` for any non-matching path:

```json
{
  "statusCode": 400,
  "headers": { "Content-Type": "application/json", "Connection": "keep-alive" },
  "body": { "errorMessage": "The request supplied is not supported by the simulator" }
}
```

So the HTTP probe to `http://localhost:8080/` succeeds with a `400`, not a `404`. The behavior the
comment is capturing ("we just need it to answer") is correct, but the documented status code is
wrong.
**Why it matters**: Code comments that assert wrong wire behavior become load-bearing when someone
tries to tighten the probe ("why aren't we asserting `404`?"). Fixing the wrong status to the right
status is a one-word edit, but the wrong status also suggests the author might have been testing
against a stock Mountebank rather than the configured one.
**Suggested fix (must fix before commit)**: Replace "404" with "400 (the configured `defaultResponse`;
see `imposters/bank_simulator.ejs:6-15`)".

---

### S-004 · Important · `IntegrationFixture.ProbeAsync` doesn't actually verify the seeded demo merchant exists

**Location**: `test/PaymentGateway.Api.Tests/Integration/IntegrationFixture.cs:46-55`
**Description**: The Mongo leg of the probe runs `RunCommandAsync<BsonDocument>({ ping: 1 })` against
the `payment_gateway` database. `ping` is a MongoDB no-op server-status command — it succeeds as
long as the server responds to any command on that db, *whether or not the `merchants` collection
has been seeded*. `mongo-init/seed-merchants.js` runs once at first container start
(`/docker-entrypoint-initdb.d`) and not again unless `/data/db` is wiped. Scenarios that produce a
positive probe + a failing test:

- A developer has the `payment_gateway_mongo` container running with a stale `/data/db` (no
  `merchants` collection) but the live server is up → probe says "Mongo is available", then
  `AuthFlowIntegrationTests` fails with `BsonSerializationException` or a null-credential cache
  read.
- A fresh `docker-compose up -d mongo` reused against a persistent volume that pre-dates the demo
  merchant.

The BankSimulatorIntegrationTests don't need the seed (they mint a JWT directly), but the
AuthFlowIntegrationTests `Full_token_to_payment_to_get_round_trip_*` test absolutely does — it's
the only test in the diff that calls the real `/api/auth/token`. A late-seed failure here produces
a confusing red bar in a "passed locally last week" state.
**Why it matters**: The whole purpose of the `ServicesAvailable` flag is "don't lie to the user".
Right now it lies by omission: it says Mongo + Mountebank are reachable, but not that the seed
landed. The README itself calls this out ("Wait until the seed has run before the gateway hits the
credential store") — the README is correct; the fixture is just less defensive than the README.
**Suggested fix (could address later)**: Replace the `ping` command with a meaningful probe —
either a `db.merchants.findOne({ clientId: "demo-merchant" })` against the live driver, or a Mongo
command that asserts the count:

```csharp
var database = client.GetDatabase("payment_gateway");
var merchants = database.GetCollection<MongoDB.Bson.BsonDocument>("merchants");
var demoMerchant = await merchants
    .Find(MongoDB.Driver.Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("clientId", "demo-merchant"))
    .FirstOrDefaultAsync();
return demoMerchant is not null;
```

That converts "Mongo up, but unseeded" into a skip (which the user can act on) rather than a red
test.

---

### S-005 · Important · `Mountebank / Mongo connection strings` are hardcoded to `localhost` in the fixture, drifting from `appsettings.json` as the only source of truth

**Location**: `test/PaymentGateway.Api.Tests/Integration/IntegrationFixture.cs:38, 48`
(cf. `src/PaymentGateway.Api/appsettings.json:11-15`)
**Description**: The probe hardcodes:

```csharp
await http.GetAsync("http://localhost:8080/");
var client = new MongoClient("mongodb://localhost:27017");
```

Both strings duplicate values the app already reads from `appsettings.json`
(`BankSimulator:BaseUrl = "http://localhost:8080"`, `Mongo:ConnectionString = "mongodb://localhost:27017"`).
Today they happen to agree. If a contributor ever overrides either value (a CI matrix pointing
integration tests at a different host, a docker-compose profile that maps to a non-default port,
or even a developer's habit of forwarding 8080 to 8081), the probe silently goes "available" but
points at the wrong hosts, and the actual tests fail with confusing errors.
**Why it matters**: Three places to keep in sync (`appsettings.json`, the probe, the README's
"Prerequisites"). The fixture is the only one without an obvious "where does this string come
from?" pointer.
**Suggested fix (could address later)**: Read both values from the same `appsettings.json` the app
uses — the simplest version is to construct a `ConfigurationBuilder().AddJsonFile("appsettings.json")`
inside the fixture and reuse those keys. That centralizes the URLs to a single source.

```csharp
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();
var bankUrl = config.GetValue<string>("BankSimulator:BaseUrl")!;
var mongoConn = config.GetValue<string>("Mongo:ConnectionString")!;
```

(Requires a `ProjectReference` from the test project to a shared location for the appsettings —
or copy the values into `test/PaymentGateway.Api.Tests/appsettings.test.json`. The first option is
cleaner, the second is minimally invasive.)

---

### S-006 · Nit · `Xunit.SkippableFact 1.4.13` is 6 years stale; current stable is 1.5.85

**Location**: `test/PaymentGateway.Api.Tests/PaymentGateway.Api.Tests.csproj:15`
**Description**: The pinned version `1.4.13` is from 2020-07-09 (per NuGet). The current is `1.5.85`
(2026-08-24), with `1.5.23` (Nov 2024, 7.2M downloads) and `1.5.61` (Dec 2025, 3.7M downloads)
in between. `1.4.13` works fine on xunit 2.9.2 (its only constraint is `xunit.extensibility.execution
>= 2.4.0`), so this isn't a functional issue — the build is clean and the 5 tests skip correctly.
It's a currency / hygiene finding.
**Why it matters**: The repo has a `failed approaches` note in handoff.md about transitive
security advisories (`Snappier 1.1.6` was still vulnerable; the working pins are `1.3.1`). Stale
direct dependencies are a quieter version of the same risk — `1.4.13` was released the year
before Microsoft's "fast inner loop" guidance and predates at least three xunit major updates.
**Suggested fix (optional)**: Bump to `1.5.85` and confirm `dotnet test` still reports `5 skipped`
on the no-docker run. No code changes; the public API (`[SkippableFact]`, `Skip.IfNot`) has been
stable since `1.4.x`.

---

### S-007 · Nit · `using MongoDB.Bson;` would let `IntegrationFixture.cs:50` drop the fully-qualified `MongoDB.Bson.BsonDocument` repetition

**Location**: `test/PaymentGateway.Api.Tests/Integration/IntegrationFixture.cs:50`
**Description**: The line

```csharp
await client.GetDatabase("payment_gateway")
    .RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
```

repeats the fully-qualified type name (`MongoDB.Bson.BsonDocument`) twice on one line. The sibling
production code (`src/PaymentGateway.Api/Services/MongoCredentialStore.cs:1`) uses a clean
`using MongoDB.Bson;` directive.
**Suggested fix**:

```csharp
using MongoDB.Bson;
// ...
await client.GetDatabase("payment_gateway")
    .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
```

---

### S-008 · Nit · `using` directive grouping is one group short of the existing pattern in `BankSimulatorIntegrationTests`

**Location**: `test/PaymentGateway.Api.Tests/Integration/BankSimulatorIntegrationTests.cs:1-16`
(cf. `test/PaymentGateway.Api.Tests/Controllers/PaymentsControllerTests.cs:1-24`)
**Description**: `PaymentsControllerTests.cs` (and most other files in this codebase) keep
`FluentAssertions`, `Microsoft.*`, and `NSubstitute.*` in separate groups (one blank line each).
`BankSimulatorIntegrationTests.cs` puts `FluentAssertions` and `Microsoft.*` in the same group
(no blank line between them):

```csharp
using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
// ...
```

That's a divergence from the repo's de facto "third-party packages each on their own blank-line
group" convention. `AuthFlowIntegrationTests.cs` has the same merge. `dotnet_separate_import_directive_groups`
imposes the rule but not the granularity within the third-party block, so this is style drift, not
an analyzer violation.
**Suggested fix (optional)**: Add a blank line between `FluentAssertions` and the `Microsoft.*`
block in both integration files, matching `PaymentsControllerTests`.

---

### S-009 · Nit · `Xunit.SkippableFact` line in the csproj is mis-sorted relative to case-sensitive alphabetical order

**Location**: `test/PaymentGateway.Api.Tests/PaymentGateway.Api.Tests.csproj:15`
**Description**: With `dotnet_sort_system_directives_first` (and NuGet `PackageReference` rule
defaults), the conventional sort is case-sensitive ascending by package id. The current file
already has `coverlet.collector` at the bottom out of order; the diff inserts `Xunit.SkippableFact`
between `Microsoft.NET.Test.Sdk` and `NSubstitute`. In case-sensitive order, `X` (88) > `N` (78),
so the line should be below `NSubstitute`:

```xml
<PackageReference Include="FluentAssertions" Version="7.2.0" />
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.0.10" />
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
<PackageReference Include="NSubstitute" Version="6.2.0" />
<PackageReference Include="Xunit.SkippableFact" Version="1.4.13" />     <-- move here
<PackageReference Include="xunit" Version="2.9.2" />
<PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
```

The pre-existing `coverlet.collector` placement is also wrong (should be between `NSubstitute` and
`xunit`), but that's a pre-diff nit not introduced here.
**Suggested fix (optional)**: Move the line. `dotnet format` (if configured) would do this
automatically.

---

### S-010 · Nit · `new WebApplicationFactory<Program>()` is built per test method instead of per collection fixture

**Location**:
`test/PaymentGateway.Api.Tests/Integration/BankSimulatorIntegrationTests.cs:88` and
`test/PaymentGateway.Api.Tests/Integration/AuthFlowIntegrationTests.cs:37, 77`
**Description**: Each of the 5 `[SkippableFact]`s spins up a fresh `WebApplicationFactory<Program>`.
`TestServer` boot is non-trivial (it constructs a Kestrel in-process server, builds the entire
ASP.NET pipeline, compiles JWT Bearer middleware, opens a `MongoClient` connection pool, etc.). The
collection fixture already exists and is shared — but it only hosts the probe, not the factories.
Today this is fine because the tests are skipped in the default sandbox (Docker absent) and only
run with explicit opt-in.
**Why it matters**: With containers up, the suite pays 5× startup cost for what could be 1×. Each
`WebApplicationFactory<Program>()` also binds a fresh in-memory `IPaymentsRepository`, so
the integration tests can't see cross-test payment persistence (good for isolation, but it's a
consequence of per-test factory creation rather than a deliberate decision).
**Suggested fix (could address later)**: Promote the factory into the collection fixture:

```csharp
public sealed class IntegrationFixture : IAsyncLifetime, IAsyncDisposable
{
    public bool ServicesAvailable { get; private set; }
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        ServicesAvailable = await ProbeAsync();
        if (ServicesAvailable)
            Factory = new WebApplicationFactory<Program>();
    }
    // ...
}
```

…and have each test call `_fixture.Factory.CreateClient()` (with explicit token-setting).
This adds a shared `IPaymentsRepository` instance across the bank simulator tests — which would be
acceptable here because none of them `GET /api/payments/{id}`, so cross-test pollution can't
manifest. Document that choice in the fixture's XML doc.

---

### No findings on

- **`public partial class Program;`**: standard ASP.NET Core top-level-statement + WAF idiom; the
  inline comment in `Program.cs:58-60` accurately explains the rationale ("top-level statements
  compile to an internal Program class by default; the partial declaration below promotes it to
  public").
- **`[Collection("Integration")]` + `[CollectionDefinition("Integration")]` + `IAsyncLifetime` + `ICollectionFixture<IntegrationFixture>`**:
  textbook xunit collection-fixture pattern for shared, async-initialized state across multiple
  test classes. Matches what xunit docs recommend and what other .NET projects in the wild use.
- **`[SkippableFact]` + `Skip.IfNot(...)` with a string reason**: the reason is grep-friendly and
  surfaces in `dotnet test` output verbatim — confirmed in the run above ("docker-compose up
  (bank_simulator + mongo) is not running."). This matches the codebase's "documents itself in
  failure output" practice (compare `Theory` parameter display in `PostPaymentRequestValidatorTests`).
- **`[Trait("Category", "Integration")]` on both test classes**: lets the existing filter
  `--filter "Category=Integration"` work uniformly; matches the trait convention used elsewhere in
  the codebase.
- **Test-method naming** (`Card_ending_in_odd_digit_is_authorized`): the
  PascalCase-with-underscores form matches every other test method in this repo
  (`POST_returns_201_with_the_payment_when_the_request_is_authorized_and_valid`,
  `GET_returns_401_when_the_token_has_expired`).
- **Test-fact structure**: `[Fact]` for the always-runs would not work; `[SkippableFact]` is the
  correct attribute for an opt-in integration test that should still show up in `--list-tests`.
- **`global using Xunit;` in `Usings.cs:1`** already pulls in the xunit base, so neither new file
  needs its own `using Xunit;` — confirmed (`grep -n 'using Xunit;' Integration/*.cs` →
  no hits). The new `using System.Net.Http.Headers;`, `using Microsoft.IdentityModel.*;`, etc.
  are scoped correctly.
- **FluentAssertions usage**: `.Should().Be(HttpStatusCode.Created)` / `.Should().Be(...)` /
  `.Should().NotBeNull()` shape matches every other test in `Controllers/` and `Services/`.
- **No `IDisposable` / `IAsyncDisposable` needed on `IntegrationFixture`**: `IAsyncLifetime` is the
  standard xunit async-fixture shape; `DisposeAsync` returning `Task.CompletedTask` is xunit-idiomatic
  and correct (no managed/disposable state to release after the probe).
- **`async` `ProbeAsync` correctness**: all four `await`s are inside try blocks; the `await
  http.GetAsync` lives in its own try (so an unexpected Mountebank response doesn't trip the Mongo
  branch); the ping branch is its own try (so a transient Mongo error doesn't trip the Mountebank
  branch). Short-circuit ordering: `CanConnectTcpAsync` first (cheap), then HTTP, then ping —
  the same order other health-check probes in the .NET community use.
- **Connection-string safety**: `using var client = new TcpClient { ReceiveTimeout = 2000,
  SendTimeout = 2000 }` and `using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) }`
  both dispose properly. No dangling resources on probe failure.
- **`MongoCredentialStore` integration**: `MongoClient` connects lazily
  (`MongoServiceCollectionExtensions.cs:27` — "connects lazily, so the app still starts with Mongo
  down"), so `WebApplicationFactory<Program>().CreateClient()` does not block even if the Mongo
  container is dead. The probe guards against the real failure mode (first request through the
  pipeline).
- **`AcquireBankClient` integration**: a 503 from Mountebank → `!response.IsSuccessStatusCode` →
  `BankUnavailableException` (`AcquiringBankClient.cs:37-41`) → controller maps to `503` (`PaymentsController.cs:67-72`).
  The `BankSimulatorIntegrationTests.Card_ending_in_zero_returns_503_bank_unavailable` assertion
  line 71 is correct: it's the gateway's *external* `503`, not the bank's `503`, and that's the
  intended surface.
- **Build result**: clean, 0 warnings, 0 errors. `TreatWarningsAsErrors` is *not* set in either
  csproj or a `Directory.Build.props` (no such file exists), so analyzer warnings don't fail the
  build. The build was clean before this diff too (Stage 9 review). The S-002 `async without await`
  is a CS1998 in spirit but the warning is at the default severity and doesn't fire here.
- **`IDisposable` pattern on `WebApplicationFactory<Program>`**: xunit does dispose `IClassFixture<T>`
  / collection-fixture scoped objects on its own; calling `CreateClient()` on a non-collected
  factory leaves the in-process server alive until the AppDomain unloads. Five per-test factories
  = five in-process servers alive simultaneously during the integration run. Acceptable today
  (the suite runs against two real containers; the in-process servers don't add network pressure),
  but see S-010 if/when startup cost becomes a problem.
- **`Program.cs:58-60` comment accuracy**: the comment is correct about the why (WebApplicationFactory
  needs a publicly-visible entry point); the wording "promotes it to public" is a slight
  simplification (the partial declaration *creates* a public class that WAF can reference; it
  doesn't change the accessibility of the compiler-generated one). Not worth a finding.

---

## Spec

### P-001 · Pass · `design.md` "Testing strategy" — Integration tests cover the bank wire contract AND the auth flow against real Mongo

**Location**: `test/PaymentGateway.Api.Tests/Integration/BankSimulatorIntegrationTests.cs:30-72`,
`test/PaymentGateway.Api.Tests/Integration/AuthFlowIntegrationTests.cs:23-86`
**Description**: `docs/design.md:182-187` requires:

> Integration tests: run against the real bank simulator **and MongoDB** containers
> (`docker-compose up`), covering the Authorized / Declined / bank-unavailable scenarios
> end-to-end against the actual Mountebank wire contract, plus the full
> `/api/auth/token` → bearer-token → `/api/payments` flow against real Mongo-backed credentials,
> rather than a mocked `HttpClient`/database.

Two test classes, covering exactly the two halves:

| Scenario per `design.md` | Where covered |
|---|---|
| Authorized against the Mountebank wire contract | `BankSimulatorIntegrationTests.Card_ending_in_odd_digit_is_authorized` (card ends in `1` → `endsWith` stub returns `200 { authorized: true }` → `201 Created` body `Status = Authorized`) |
| Declined against the Mountebank wire contract | `BankSimulatorIntegrationTests.Card_ending_in_even_digit_is_declined` (card ends in `2`) |
| Bank-unavailable against the Mountebank wire contract | `BankSimulatorIntegrationTests.Card_ending_in_zero_returns_503_bank_unavailable` (card ends in `0` → Mountebank `503` → `BankUnavailableException` → gateway `503`) |
| Full `/api/auth/token` → bearer → `POST /api/payments` flow against real Mongo | `AuthFlowIntegrationTests.Full_token_to_payment_to_get_round_trip_against_real_Mongo_and_Mountebank` (real `POST /api/auth/token` with `demo-merchant`/`demo-secret`, then real `POST /api/payments` with the minted token, then real `GET /api/payments/{id}`) |

Each row is one `[SkippableFact]` (5 total). The behavior the scenarios describe matches the
production code paths exactly (`AcquiringBankClient.cs:31-41`, `PaymentsController.cs:64, 87-98`,
`AuthController.cs:37-50`).
**Status**: Pass.

---

### P-002 · Pass · `implementation-plan.md` Stage 10 — "Tagged integration tests (trait-filtered out of the default `dotnet test` run)"

**Location**:
`test/PaymentGateway.Api.Tests/Integration/BankSimulatorIntegrationTests.cs:29`,
`test/PaymentGateway.Api.Tests/Integration/AuthFlowIntegrationTests.cs:22`,
`README.md:160-169`
**Description**: Plan: *"Tagged integration tests (trait-filtered out of the default `dotnet test`
run), requiring `docker-compose up`: Authorized/Declined/bank-unavailable against the real
Mountebank wire contract, and the full `/api/auth/token` → bearer token → `/api/payments` flow
against real Mongo-backed credentials..."*

Verified in the sandbox:

- `dotnet test` (default, no filter): `Passed!  - Failed: 0, Passed: 149, Skipped: 5, Total: 154`.
  The 5 `[SkippableFact]`s *appear* in the run but skip cleanly because the fixture probe
  correctly detects the absent Docker containers.
- `dotnet test --filter "Category=Integration"`: `Skipped! - Failed: 0, Passed: 0, Skipped: 5,
  Total: 5`. Same five tests, the rest of the suite is correctly excluded from the run.
- No non-integration test is tagged `[Trait("Category", "Integration")]` — verified by
  `grep -rln "Category.*Integration" test/ --include="*.cs"` returning only the two new files.

One thing worth noting: the trait alone isn't what excludes the integration tests from the default
run — xunit doesn't auto-skip by trait. The `[SkippableFact]` + `Skip.IfNot(_fixture.ServicesAvailable, ...)`
is what excludes them in practice (when Docker isn't running). With `docker-compose up`, all five
would run on a plain `dotnet test` *and* an `--filter "Category=Integration"` (the trait filter
would be a no-op since all tests in scope are tagged). This matches the spec language: the tests
"are tagged so they can be distinguished" — and "trait-filtered out of the default `dotnet test`
run" is achieved by the combination of tagging + skip-on-no-docker, not by tagging alone.
**Status**: Pass. The README's claim "Default — runs every test except the integration suite" is
slightly too strong (the integration *suite runs* in the default; it just skips when Docker isn't
up), but the operational outcome — "safe in any environment" — is exactly what the plan asks for.
If the reviewer wants to tighten this, see P-005.

---

### P-003 · Pass · `implementation-plan.md` Stage 10 — "Done when: integration suite green with both containers up"

**Location**: `test/PaymentGateway.Api.Tests/Integration/IntegrationFixture.cs:28-58`
**Description**: Plan: *"Done when: integration suite green with both containers up."*

Not verifiable in this sandbox (Docker absent). But the suite does what it can:

- The fixture probes both the Mountebank container (`localhost:8080`) and the Mongo container
  (`localhost:27017`) *before* any test runs.
- Each test individually calls `Skip.IfNot(_fixture.ServicesAvailable, ...)` so a *partial*
  outage (one container up, the other down) also skips — not silently passes.
- The fixture distinguishes between "TCP port open" and "speaks the expected protocol": both
  ports get a deeper probe (HTTP GET against `localhost:8080/`, Mongo `ping` against
  `payment_gateway`).
- I confirmed the suite is *runnable* (no Docker implies `Skipped: 5`), which is the
  environmentally-available interpretation of "done when". With Docker, the suite exercises
  the behavior end-to-end against the real wire + real Mongo, which is what the spec asks
  for.

The one gap (S-004) is that the Mongo probe doesn't verify the `merchants` collection is actually
seeded. That's a "what counts as 'container up'?" tightening, not a spec gap.
**Status**: Pass with the S-004 caveat that the seed-readiness check could be tighter.

---

### P-004 · Pass · `implementation-plan.md` Stage 10 scope — only what's listed, no scope creep

**Location**: diff scope overall
**Description**: Plan's Stage 10 scope is *"Tagged integration tests... requiring `docker-compose up`:
Authorized/Declined/bank-unavailable against the real Mountebank wire contract, and the full
`/api/auth/token` → bearer token → `/api/payments` flow against real Mongo-backed credentials."*

The diff doesn't touch any other production code:

- `Program.cs`: one-line addition (`public partial class Program;`) plus a 3-line explanatory
  comment. No DI change, no service registration change.
- `appsettings.json` unchanged.
- `mongo-init/seed-merchants.js` unchanged.
- `imposters/bank_simulator.ejs` unchanged.
- No new `Configuration/` extensions, no new ADRs, no new packages beyond `Xunit.SkippableFact`
  (which is the test-only skip mechanism the spec implies).
- The error-mapping decisions from design.md (Authorized/Declined → 201, validation → 400, bank
  unavailable → 503, cross-merchant GET → 404, uniform 401 on auth) are not changed by these tests.

This is the cleanest possible Stage 10 surface.
**Status**: Pass.

---

### P-005 · Suggestion · README "Running the test suite" — one operational ambiguity is worth tightening

**Location**: `README.md:158-181`
**Description**: The README is structurally correct and matches `implementation-plan.md` Stage 10:
two `dotnet test` commands, the trait filter, the conditional behavior on Docker availability.
Three concrete tightening opportunities:

(a) `README.md:163` — *"Default — runs every test except the integration suite. Safe in any
environment."* This is *outcome*-correct (when Docker is absent, all 5 integration tests skip;
when Docker is up, they pass) but technically wrong about xunit: `dotnet test` doesn't ignore the
trait — it *runs* the integration tests and they *skip* because of `Skip.IfNot(...)`. A reader
who looks at `--list-tests` after a default `dotnet test` will see them all listed. Tightening:

> Default — discovers every test and runs the ones that don't need Docker. The integration suite
> is still discovered but skipped when `docker-compose up` isn't running, so the run is safe in
> any environment.

(b) `README.md:180` — *"If `docker-compose up` isn't running, `dotnet test --filter
"Category=Integration"` reports `5 skipped / 0 failed / 0 passed` with a clear message — never a
misleading 'test passed' on a suite that didn't actually run."* This is verified
(`Skipped!  - Failed: 0, Passed: 0, Skipped: 5, Total: 5`) but the opposite case isn't stated.
Adding one sentence:

> With `docker-compose up` running, the same command reports `5 passed / 0 failed / 0 skipped`.

(c) The "5 skipped" claim matches the 3 BankSimulatorIntegrationTests + 2 AuthFlowIntegrationTests
breakdown verified in the run output above.

**Why it matters**: A reviewer following the README for the *first* time after `docker-compose up`
will expect the default `dotnet test` to behave the same way as the filtered one w.r.t. skipped
count. It won't — without a filter, the 149-passed count dominates and the 5 skipped are a
one-line footnote. Spelling out both halves (with-Docker: all 5 pass; without-Docker: all 5 skip)
removes the ambiguity.
**Suggested fix (could address later)**: Apply (a) and (b). (c) is optional but worth adding the
one-line parenthetical.

---

### P-006 · Pass · ADR-0010 — demo-merchant credentials are placed correctly in the integration tests

**Location**: `test/PaymentGateway.Api.Tests/Integration/AuthFlowIntegrationTests.cs:25-26`
(cf. `mongo-init/seed-merchants.js` and `docs/adr/0010-merchant-jwt-authentication-mongodb-cache.md:75-80`)
**Description**: ADR-0010's Consequences call out: *"Integration tests now depend on a running
MongoDB instance alongside the bank simulator (both via `docker-compose up`)"* and *"Existing and
planned component tests for `POST`/`GET /api/payments` need a valid bearer token going forward —
test fixtures need a way to mint one (e.g. a seeded test merchant + a real call to
`/api/auth/token`, or a token signed directly with the test config's signing key)."*

Both approaches are used, one in each test class, exactly as ADR-0010 contemplates:

- **Real call to `/api/auth/token`** (`AuthFlowIntegrationTests`):
  `DemoClientId = "demo-merchant"`, `DemoClientSecret = "demo-secret"` — exactly the values in
  `mongo-init/seed-merchants.js:14` and the README's demo-credentials table. The full seed →
  Mongo query → `CredentialCache` → `BCrypt.Verify` → `TokenIssuanceService` path is exercised.
- **Token signed directly with the test config's `Jwt:SigningKey`** (`BankSimulatorIntegrationTests`):
  `NewClientAsync` reads `factory.Services.GetRequiredService<IConfiguration>()
  .GetValue<string>("Jwt:SigningKey")!` (the same path the Stage 7 reviewer recommended) and
  mints a JWT with `SecurityAlgorithms.HmacSha256`. The bank's wire contract is the integration
  seam here; Mongo is intentionally not in the loop (the user prompt confirms this design: "no
  Mongo round-trip here — only the bank wire is the integration seam"). The `sub = "merchant-it"`
  is fine because none of the bank tests exercise `GET /api/payments/{id}` — only POSTs whose
  ownership is asserted by the JWT middleware but not validated against any specific merchant
  document.

Both choices are consistent with the ADR's "test-authoring detail to resolve" guidance.
**Status**: Pass.

---

### P-007 · Pass · ADR-0001 / `design.md` — bank-unavailable test reflects "we don't know" semantics

**Location**:
`test/PaymentGateway.Api.Tests/Integration/BankSimulatorIntegrationTests.cs:63-72`
(`Card_ending_in_zero_returns_503_bank_unavailable`)
**Description**: `design.md:80-98` and ADR-0001 both distinguish "we don't know" (bank didn't
answer; gateway returns `503`, nothing persisted) from "definitively declined" (bank returned
`authorized: false`; gateway returns `201`, payment persisted). This test exercises the former
end-to-end: Mountebank returns a real `503` stub for card numbers ending in `0` →
`AcquireBankClient` throws `BankUnavailableException` (line 39) → `PaymentsController` maps to
`503 ServiceUnavailable` (`PaymentsController.cs:67-72`). The test's assertion at line 71
matches the gateway's external surface, exactly the contract ADR-0001 mandates.

Crucially the test does *not* also assert `repository is empty` (bank-unavailable → not persisted)
— the production code at `PaymentsService.cs` already enforces that, and a unit-test
`PaymentsServiceTests` test (in `Services/PaymentsServiceTests.cs`) covers that path with a mocked
client. Adding the same assertion to the integration test would be redundant defense, not
new coverage. The plan's "Authorized/Declined/bank-unavailable" enumeration is honored.
**Status**: Pass.

---

### P-008 · Pass · ADR-0003 — Idempotency-Key feature is not silently broken by the integration tests

**Location**: `test/PaymentGateway.Api.Tests/Integration/AuthFlowIntegrationTests.cs:42-69`
**Description**: Neither integration test sends an `Idempotency-Key` header. That's correct:
ADR-0003 says "If the header is absent, the request is processed exactly as it would be without
this feature" — passing through the `IdempotencyResourceFilter` (Stage 8) into the controller,
which is exactly the
implicit-adheres-to-ADR-0003 default. The integration tests therefore exercise the
no-key path (the one a normal client takes on its first request), and the Stage 8 component-test
coverage (`IdempotencyStoreTests`, `PaymentsControllerTests` idempotency cases) covers the
with-key paths. No drift.

The full-token→post→get test on line 68 makes the GET assertion without an `Idempotency-Key`
header on the request, so it's not caching the response from the prior POST. Both tests use
fresh `WebApplicationFactory<Program>()` instances (S-010 caveat aside), so the singleton
`InMemoryIdempotencyStore` is reset between tests anyway.
**Status**: Pass.

---

## Findings summary

| ID | Severity | Category | File |
|---|---|---|---|
| S-001 | Important | Standards — duplicated JWT-mint logic | `BankSimulatorIntegrationTests.cs:86-105` |
| S-002 | Important | Standards — `async` without `await` | `BankSimulatorIntegrationTests.cs:86` |
| S-003 | Important | Standards — wrong status code in fixture comment | `IntegrationFixture.cs:33-34` |
| S-004 | Important | Standards — probe doesn't verify seed | `IntegrationFixture.cs:46-55` |
| S-005 | Important | Standards — connection strings hardcoded | `IntegrationFixture.cs:38, 48` |
| S-006 | Nit | Standards — stale `Xunit.SkippableFact` | `PaymentGateway.Api.Tests.csproj:15` |
| S-007 | Nit | Standards — fully-qualified `BsonDocument` | `IntegrationFixture.cs:50` |
| S-008 | Nit | Standards — `using` group granularity drift | `BankSimulatorIntegrationTests.cs`, `AuthFlowIntegrationTests.cs` |
| S-009 | Nit | Standards — csproj entry mis-sorted | `PaymentGateway.Api.Tests.csproj:15` |
| S-010 | Nit | Standards — per-test factory, not per-collection | `BankSimulatorIntegrationTests.cs:88`, `AuthFlowIntegrationTests.cs:37, 77` |
| P-001 | Spec pass | design.md "Testing strategy" — bank wire + auth flow | both Integration files |
| P-002 | Spec pass | plan Stage 10 — trait-tagged + Docker-gated | both Integration files, README |
| P-003 | Spec pass | plan Stage 10 — "green with both containers up" | `IntegrationFixture.cs` |
| P-004 | Spec pass | plan Stage 10 scope — no scope creep | diff scope overall |
| P-005 | Suggestion | Spec — README "with-Docker" behavior not spelled out | `README.md:158-181` |
| P-006 | Spec pass | ADR-0010 — credential-placement matches ADR | `AuthFlowIntegrationTests.cs:25-26`, `BankSimulatorIntegrationTests.cs:86-105` |
| P-007 | Spec pass | ADR-0001 — bank-unavailable 503 path | `BankSimulatorIntegrationTests.cs:63-72` |
| P-008 | Spec pass | ADR-0003 — no-key default is preserved | `AuthFlowIntegrationTests.cs:42-69` |

---

**Spec verdict**: Stage 10 cleanly maps to all four spec sources. `design.md`'s "Testing strategy"
section is honored one-for-one (P-001): both halves — Mountebank wire contract Authorized/Declined/
503 and the full `/api/auth/token` → `/api/payments` → `/api/payments/{id}` flow against real
Mongo — are covered by exactly one `[SkippableFact]` each, with no gaps or overlap. `implementation-plan.md`
Stage 10's "trait-filtered out of the default `dotnet test` run, requiring `docker-compose up`"
requirement is implemented correctly: the `[Trait]` tag works with `--filter "Category=Integration"`
(verified in the sandbox run), and the `Skip.IfNot(_fixture.ServicesAvailable, ...)` guards the
default run (verified — 5 skipped in the no-Docker case). ADR-0010's two-pronged guidance on token
minting ("real `/api/auth/token` call *or* signing directly with `Jwt:SigningKey`") is followed —
the bank tests use the latter (deliberately, because only the bank wire is the integration seam),
the auth tests use the former. ADR-0001's "we don't know" semantics show up in the
`Card_ending_in_zero_*` test exactly as written. ADR-0003's no-header default is preserved.

**Standards verdict**: Two real holes in the test fixture (S-003 wrong status code in comment,
S-004 probe doesn't verify Mongo seed), one duplicated-but-not-shared helper (S-001), one
misleading `async` (S-002), one drift between hardcoded and `appsettings.json` connection strings
(S-005), plus five nits. The build is clean (0 warnings), `dotnet test` reports `Passed! - Failed:
0, Passed: 149, Skipped: 5, Total: 154`, and `dotnet test --filter "Category=Integration"`
reports `Skipped! - Failed: 0, Passed: 0, Skipped: 5, Total: 5`. The stage is structurally sound.

**Recommendation**:

- Apply **S-003** before commit — it's a one-word doc-comment fix that keeps the fixture's
  rationale honest about what Mountebank actually returns.
- **S-002, S-004, S-005** are real but lower-stakes; **S-002** is a 3-token change, **S-004** is a
  5-line probe rework that closes a real seed-readiness flake, **S-005** is an appsettings.json
  refactor that moves the URLs to a single source of truth.
- **S-001** (`TestJwt.Mint` reuse) and **S-010** (lift the factory into the fixture) are
  performance/maintenance wins, not correctness; both are 0-risk-to-defer.
- S-006 through S-009 are nits; address in a later cleanup pass or with `dotnet format`.

The spec-vs-doc gate is **just S-003** — wrong status code in a comment. Everything else is
quality, not coverage.
