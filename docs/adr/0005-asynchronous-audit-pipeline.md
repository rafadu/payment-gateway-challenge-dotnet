# ADR-0005: Asynchronous audit pipeline via message broker, decoupled from the request path

## Status
Accepted

## Context
Writing an audit record synchronously, on the same request/response path as payment processing,
would add a new failure dependency (a database write) and latency to the critical path — directly
against the performance and reliability priorities set for this system. An audit write must never
be able to slow down, or fail, a payment the bank has already adjudicated.

## Decision
- The API publishes a lightweight audit event (correlation id, merchant id, masked request/
  response summary, outcome, timings, which acquirer handled the call) to a RabbitMQ queue as
  part of handling each request. Publishing is fire-and-forget from the request's perspective —
  the HTTP response is returned without waiting for the audit record to reach the database.
- A background consumer, implemented as an **in-process `BackgroundService`** within the same API
  process — not a separately deployed service — subscribes to the queue, applies the field-level
  encryption described in ADR-0004, and writes the resulting document to the audit collection.
- The queue is declared durable with persistent messages, so a broker or consumer restart doesn't
  lose in-flight audit events. A dead-letter queue captures messages the consumer repeatedly fails
  to process (e.g. a malformed payload), so a single poison message can't stall the queue.

## Consequences
- Keeping the consumer in-process, rather than a separate worker container, is a deliberate
  simplicity trade-off: one deployable, one container, no inter-service deployment coordination.
  The cost is that audit processing competes for the same process's resources (thread pool, GC)
  as request handling — under a large audit backlog this could indirectly affect API
  responsiveness, which a fully isolated worker would avoid. Accepted in favor of simplicity;
  revisit if audit volume or processing cost grows enough to matter.
- If the broker is unreachable when the API tries to publish, the payment response must still
  succeed — audit is secondary to a payment the bank has already adjudicated. A publish failure
  is logged and alerted on (ADR-0007) rather than surfaced to the caller. This means a broker
  outage is an availability risk for the *audit function specifically*, not for payment
  processing — an accepted, monitored gap rather than one solved with, e.g., a local durable
  outbox. The outbox pattern would be the natural next step if audit completeness during a broker
  outage became a hard requirement.
- Message ordering is not relied upon: each audit event is self-contained, keyed by its own
  correlation id, so out-of-order or at-least-once (duplicate) delivery is tolerated. The audit
  write path is idempotent on correlation id so duplicate deliveries are harmless.
