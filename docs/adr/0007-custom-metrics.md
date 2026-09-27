# ADR-0007: Custom business and bank-latency metrics

## Status
Accepted

## Context
ADR-0001 already identified that a well-tuned circuit breaker needs real bank-call latency and
failure-rate data as a prerequisite. Separately, payment outcome rates (authorized/declined/
rejected) are both an operational signal and a business/fraud signal — a sudden shift is worth
alerting on regardless of whether anything is technically "down."

## Decision
Instrument the gateway using `System.Diagnostics.Metrics` (`Meter`/`Counter`/`Histogram`) — the
OpenTelemetry-compatible standard .NET API — rather than a vendor-specific SDK, so exporting to
Prometheus, Grafana, Application Insights, or Datadog is a matter of configuring an OpenTelemetry
exporter, not changing instrumentation code.

Concrete metrics:
- `payments.processed.count` — counter, tagged by `status` (Authorized/Declined/Rejected) and
  `currency`.
- `payments.bank.call.duration` — histogram (ms), tagged by `acquirer` (ADR-0006) and `outcome`
  (success/timeout/error) — the p95/p99 data ADR-0001 requires before a circuit breaker's
  thresholds could be sized sensibly.
- `payments.idempotency.replay.count` — counter — how often a cached idempotent response is
  served instead of fresh processing (ADR-0003), a signal of how often merchants actually retry.
- `payments.rejected.reason.count` — counter, tagged by the validation rule that failed —
  distinguishes a merchant integration bug (the same field always failing) from organic invalid
  input or card-testing/probing traffic.

Exposition: an OpenTelemetry Collector (or a direct Prometheus text-format `/metrics` endpoint via
`OpenTelemetry.Exporter.Prometheus.AspNetCore`) scraped by Prometheus, visualized in Grafana.

## Consequences
- Using the standard `System.Diagnostics.Metrics` API keeps the instrumentation vendor-neutral;
  swapping the observability backend later doesn't require touching instrumented code paths.
- These metrics are the direct input to sizing ADR-0001's deferred circuit breaker and to
  alerting thresholds (elevated `503` rate from the bank, elevated `Rejected` rate).
- Metrics are aggregate/statistical and contain no card data or PII by construction (tags are
  status/currency/acquirer/reason, never request content) — no additional masking concern beyond
  what's already established for logs.
