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

## Update — implemented

This ADR is now built (previously "documented, not built"). Implementation notes:

- `PaymentMetrics` (a singleton over an `IMeterFactory`-created `Meter` named
  `PaymentGateway.Payments`) owns the four instruments. Call sites:
  `payments.processed.count` from `PaymentsService` (Authorized/Declined) and `PaymentsController`
  (Rejected); `payments.bank.call.duration` from `AcquiringBankClient` (timed around the HTTP call,
  with `outcome` = success/timeout/error — a genuine caller cancellation is deliberately not
  recorded); `payments.idempotency.replay.count` from `IdempotencyResourceFilter` on a cached
  replay; `payments.rejected.reason.count` from `PaymentsController`, one increment per failed
  FluentValidation rule.
- Exposition is OpenTelemetry (`OpenTelemetry.Extensions.Hosting` +
  `OpenTelemetry.Exporter.Prometheus.AspNetCore`) with a Prometheus scraping endpoint at
  `GET /metrics`, wired in `AddObservability`. Instrumentation code is untouched by this choice —
  swapping to OTLP/App Insights/Datadog changes only that extension method.
- Two clarifications beyond the Decision text: the `acquirer` tag is the constant `"simulator"`
  (single acquirer; ADR-0006's routing is out of scope), and the `currency` tag on a *rejected*
  payment is normalised to a real 3-letter code or `"unknown"`, since a rejected request's currency
  is unvalidated input and tagging it raw would let a probe inflate tag cardinality.
- Non-goals still deferred: the `/metrics` endpoint is unauthenticated (restrict at the
  ingress/network layer in production); no distributed exemplars/tracing correlation; no
  RED/USE dashboards or alert rules shipped in this repo.
