# Toward a production payment gateway

`docs/design.md` and ADR-0001 through ADR-0003 describe what is actually implemented for this
assessment. This document captures the further decisions made when the exercise is treated as
the seed of a real, always-on payment gateway rather than a scoped take-home. **Nothing in this
document is implemented in the current codebase** — it exists to make the reasoning explicit,
and as a map for where the system would go next.

## Why go further than the assessment scope at all

The assessment is deliberately narrow: one bank, in-memory storage, no auth. Treating it as a
production system's starting point surfaces gaps a narrower reading wouldn't force a decision on
— most concretely, that the in-memory repository and idempotency store (ADR-0003) silently stop
working correctly the moment the service runs as more than one instance. That gap, more than any
other, is why several of the ADRs below exist.

## Summary of decisions

| Concern | Decision | ADR |
|---|---|---|
| Application structure | Modular monolith, ports & adapters per acquiring bank | [ADR-0006](adr/0006-modular-monolith-multi-acquirer.md) |
| Persistent storage | Single document database (MongoDB) for audit trail + merchant auth data, field-level encryption on genuine PII | [ADR-0004](adr/0004-document-database-for-audit-and-identity.md) |
| Audit processing | Async via RabbitMQ + an in-process background consumer, off the request path | [ADR-0005](adr/0005-asynchronous-audit-pipeline.md) |
| Observability | Custom metrics via `System.Diagnostics.Metrics` (OpenTelemetry-compatible) | [ADR-0007](adr/0007-custom-metrics.md) |
| Bank-call resiliency | Timeout only; circuit breaker deferred pending the metrics above | [ADR-0001](adr/0001-bank-call-resiliency-timeout-only.md) |
| Structural enforcement | ArchUnitNET architecture tests | [ADR-0008](adr/0008-architecture-and-performance-testing.md) |
| Performance validation | BenchmarkDotNet (in-process) + k6 (whole-stack) | [ADR-0008](adr/0008-architecture-and-performance-testing.md) |
| CI/CD | GitHub Actions, staged by cost/speed | [ADR-0009](adr/0009-cicd-github-actions.md) |
| Merchant retry safety | Idempotency-Key header | [ADR-0003](adr/0003-idempotency-key.md) (already implemented) |

## How these fit together

A request's path in this fuller picture:

```
Merchant
  │  Idempotency-Key, API key
  ▼
PaymentsController  ──► IPaymentsHandler ──► AcquirerRouter ──► IAcquiringBank adapter
      │                        │                                  (Mountebank today,
      │                        └──► IPaymentsRepository            more later — ADR-0006)
      │
      ├──► metrics (ADR-0007): outcome counters, bank-call latency histogram
      │
      └──► publish audit event ──► RabbitMQ ──► background consumer ──► Mongo (encrypted PII)
                                                     (ADR-0004, ADR-0005)
```

The request/response cycle itself is unchanged from `docs/design.md` — auth, metrics emission,
and audit publishing are additive around it, and none of them are allowed to add blocking latency
or a new failure mode to the payment decision itself. That constraint is the throughline across
ADR-0001 (no blind retries), ADR-0005 (audit is async and its failure doesn't fail the payment),
and ADR-0007 (metrics are fire-and-forget instrumentation, not a dependency).

## Honest gaps even after all of the above

- **Auth data on the hot path.** Every authenticated request needs a merchant/API-key lookup;
  without a cache in front of Mongo (ADR-0004), that's a database round-trip per request purely
  for authorization, which will show up in the p95/p99 numbers ADR-0007 is meant to surface.
- **Audit completeness during a broker outage.** ADR-0005 accepts that a RabbitMQ outage is an
  availability risk for audit specifically. A durable local outbox would close this gap but isn't
  built — noted as the natural next increment, not solved speculatively.
- **The deepest one, unchanged from `docs/design.md`:** a timeout on the gateway's own call to a
  real acquirer is still ambiguous — the bank may have authorized before the response was lost.
  None of the additions here (database, broker, metrics) solve that; it needs bank-side
  idempotency-key support, which this simulator doesn't offer. Everything above makes the system
  more observable and more available — it does not make that specific ambiguity go away.

## What would actually get built, in order, if this became a real roadmap

1. Metrics (ADR-0007) — cheapest, and every later decision (circuit breaker sizing, alerting
   thresholds, whether the async audit pipeline is keeping up) depends on having this data first.
2. Centralized storage for payments/idempotency (closing the horizontal-scaling gap) before
   anything else, since it's a correctness bug waiting for a second instance, not a nice-to-have.
3. Auth (ADR-0004's merchant data) — needed before this is exposed beyond a trusted network.
4. Audit pipeline (ADR-0004/0005) — compliance-driven, not performance-driven, so it can follow.
5. Modular-monolith restructuring (ADR-0006) — deferred until a second acquirer is actually on
   the roadmap; restructuring speculatively for a "when" that isn't yet scheduled would be the
   over-engineering this whole exercise has otherwise tried to avoid.
6. Architecture/performance test tooling and the full CI/CD pipeline (ADR-0008/0009) — layered in
   incrementally as each of the above lands, rather than built up front against code that doesn't
   exist yet.
