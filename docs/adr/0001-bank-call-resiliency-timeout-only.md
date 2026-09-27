# ADR-0001: Bank call resiliency — bounded timeout only, circuit breaker deferred

## Status
Accepted

## Context
The gateway calls the acquiring bank simulator synchronously as part of processing a payment.
The simulator can:
- return `200 OK` with an authorized/declined outcome,
- return `503 Service Unavailable` (deterministically, for card numbers ending in `0`),
- or, in a real bank, simply hang or drop the connection.

Two failure modes need protection:
1. A single slow/hung call shouldn't hold a request thread indefinitely.
2. Sustained bank unavailability shouldn't degrade the gateway's own performance for every
   incoming request while it is happening.

Retrying the bank call automatically was already ruled out (see the "no auto-retry" decision in
the main design doc): the bank simulator has no idempotency-key support, so a blind retry of a
non-idempotent authorization call risks double-authorization if the original call actually
succeeded but the response was lost. That risk is strictly worse than surfacing a single `503`
to the merchant.

A circuit breaker (e.g. via Polly) would address failure mode 2 by failing fast once the bank is
clearly down, instead of letting every request pay the full timeout cost. It's a well-known
pattern for this exact problem.

## Decision
Implement only a bounded client-side timeout on the bank HTTP call (via the typed `HttpClient`
configured for `IAcquiringBankClient`). Do **not** implement a circuit breaker in this iteration.

Reasoning:
- A circuit breaker's usefulness depends entirely on correctly tuned thresholds (failure-rate
  window, break duration, half-open probe interval). Those numbers can't be picked sensibly
  without real traffic/latency/failure-rate data — guessing them risks tripping too eagerly
  (rejecting healthy requests during a blip) or too late (no protective effect at all).
- The natural prerequisite for a well-tuned circuit breaker is **observability**: structured
  metrics on bank-call latency, timeout rate, and `503` rate. That instrumentation doesn't exist
  yet and is valuable independent of a circuit breaker (it's also what an on-call engineer would
  reach for first to diagnose a bank outage).
- For this challenge's scope and traffic profile, a bounded timeout already prevents the worst
  outcome (a request hanging forever), which is the main safety property needed.

## Consequences
- Under a sustained bank outage, the gateway will still attempt (and time out on) each incoming
  request individually, rather than fast-failing after N consecutive failures. This costs more
  thread/connection time under load than a circuit breaker would.
- Every bank-unavailable outcome surfaces as `503 Service Unavailable` to the merchant, with no
  payment persisted (see the persistence-scope decision in the main design doc).
- Follow-up work, in order, if this were to go further: (1) add structured metrics/logging for
  bank-call outcomes and latency, (2) use that data to size a circuit breaker (e.g. Polly's
  `CircuitBreakerAsync`), (3) add the breaker in front of the existing timeout policy.
