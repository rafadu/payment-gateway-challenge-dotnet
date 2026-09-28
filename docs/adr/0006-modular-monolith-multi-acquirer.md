# ADR-0006: Modular monolith with ports & adapters for multi-acquirer support

## Status
Accepted

## Context
The assessment specifies a single bank simulator, but a payment gateway's whole purpose is to sit
in front of one or more acquiring banks/processors — for redundancy, geographic coverage, cost or
success-rate-based routing, or merchant-specific preferences. A design that hardcodes a single
bank integration into the core payment flow would require a rewrite, not an extension, the moment
a second acquirer is needed.

At the same time, the actual scale and team size here don't justify the operational cost of
splitting acquirer integrations into independent microservices (network calls between services,
service discovery, distributed tracing, independent deployments) when there is exactly one
integration today and no evidence yet that any acquirer needs to scale independently of the rest.

## Decision
Structure the gateway as a **modular monolith**: a single deployable unit, internally partitioned
into modules with enforced, one-directional dependencies.

```
PaymentGateway.Modules.Payments               (core: domain, application services, ports —
                                                IAcquiringBank, IPaymentsRepository, etc.)
PaymentGateway.Modules.Acquirers.Mountebank    (adapter implementing IAcquiringBank for this
                                                 challenge's bank simulator)
PaymentGateway.Modules.Acquirers.<FutureBank>  (a future acquirer, same shape, added without
                                                 touching the core module)
PaymentGateway.Api                             (composition root: wires modules together,
                                                 exposes HTTP)
```

Each acquirer module implements the `Payments` module's port interfaces and depends on them —
never the other way around, and never on another acquirer module directly. An `AcquirerRouter` in
the core module selects which adapter handles a given payment (by currency support, merchant
configuration, or a routing/failover policy). With one acquirer, the router is trivial — it
always returns the Mountebank adapter — but the seam exists so a second acquirer means adding a
module and a routing rule, not modifying the core payment flow.

These boundaries are enforced by architecture tests (ADR-0008), not just code review convention.

## Consequences
- More structural ceremony than a single bank integration strictly needs today — an explicit,
  deliberate trade-off made because the domain (a payment *gateway*, not a single-bank connector)
  implies multi-acquirer support is a "when," not an "if."
- Adding a second acquirer becomes additive: a new module implementing the existing port, plus a
  routing rule — no change to `ProcessPaymentHandler`, validation, persistence, or the audit pipeline.
- A modular monolith gets the boundary/ownership benefits of separation without the operational
  cost of a distributed system. It can be split into real microservices later if a specific
  module develops a genuine independent-scaling or independent-team need — this decision doesn't
  foreclose that, it just declines to pay for it up front.
