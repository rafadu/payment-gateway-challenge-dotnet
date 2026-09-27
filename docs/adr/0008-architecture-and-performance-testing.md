# ADR-0008: Architecture tests (ArchUnitNET) and performance tests (BenchmarkDotNet + k6)

## Status
Accepted

## Context
Several structural rules have been agreed across this design (layering, module boundaries, no
raw card data outside the request DTO) that are only as durable as the team's memory of them
unless something enforces them automatically. Separately, the resiliency design (timeouts,
eventual circuit breaker) and the idempotency design both make latency/throughput claims that
should be verified under load rather than assumed.

## Decision

**Architecture tests — ArchUnitNET**, run as an ordinary test project in the standard `dotnet test`
pass, so a violating PR fails CI like any other test failure:
- Dependency direction between modules (`Domain`/`Application` never depend on `Infrastructure`
  or `Api`; acquirer modules never depend on each other — ADR-0006).
- Controllers may not reference `HttpClient`/`IAcquiringBankClient` directly — only
  `IPaymentsService`.
- No public property named like a raw card field (`CardNumber`, `Cvv`) exists outside the single
  designated request DTO — a payments-specific safety net that turns "we agreed never to persist
  the PAN" into something CI checks on every build.

**Performance tests — two tools for two different jobs:**
- **BenchmarkDotNet** (MIT-licensed, no usage restrictions) for in-process, .NET-native
  micro-benchmarks of specific hot paths — idempotency-store contention under many concurrent
  identical requests, and the actual overhead added by the bank-call timeout wrapper — run inside
  the normal CI/test pipeline without extra infrastructure. NBomber was considered for this role
  but ruled out: its license requires a paid commercial license for company/team use beyond
  personal projects, which isn't appropriate to depend on here.
- **k6** for black-box, protocol-level load and soak testing against a fully deployed stack
  (API + Mongo + RabbitMQ + bank simulator via docker-compose), the tool of choice for realistic
  throughput/latency/soak testing of the whole system, including the async audit pipeline under
  sustained load. This already covers concurrent-HTTP-load scenarios end-to-end, so no second,
  overlapping .NET-native HTTP load framework is introduced alongside it.

## Consequences
- Architecture tests convert design conversations into invariants that fail a build, not just a
  code-review opinion that erodes as the codebase and team grow.
- Splitting performance testing by tool keeps fast, in-process checks in the standard CI loop
  (every PR) while the heavier, whole-stack k6 runs are reserved for a separate, less frequent
  job (nightly/scheduled — see ADR-0009), since they're materially slower and infrastructure-
  dependent.
- Both chosen tools are fully open source with no usage-tier restrictions, avoiding a licensing
  dependency for something as foundational as the test pipeline.
- Soak testing under k6 is specifically how the unbounded-growth risk in the in-memory idempotency
  store (ADR-0003) and repository would actually be observed, rather than reasoned about
  abstractly — reinforcing why those stores need a TTL/centralized backing before real load.
