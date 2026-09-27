# ADR-0002: Reject non-positive payment amounts

## Status
Accepted

## Context
The requirements table specifies that `Amount` is required and must be an integer (representing
the minor currency unit), but does not explicitly state it must be positive. Taken literally, a
request with `amount: 0` or a negative amount would pass validation and be forwarded to the
acquiring bank.

## Decision
Add a validation rule requiring `Amount > 0`. A request with a zero or negative amount is
**Rejected** (`400 Bad Request`) before the bank is ever called.

This is a deliberate extension beyond the literal text of the spec, made explicit here rather
than silently assumed.

## Consequences
- A charge of zero or negative value has no real-world business meaning for a payment gateway;
  letting one reach the bank would be a wasted call at best and a confusing/undefined bank
  response at worst (the simulator's behavior for such a value is undefined by its own contract).
- This keeps the "Rejected" path doing what it's meant to do: catching invalid input before any
  external call or persistence happens, consistent with the fail-fast rationale used for the
  other validation rules.
- No currency-specific minor-unit precision rules (e.g. zero-decimal currencies) are enforced,
  since the three supported currencies (see main design doc) all use two decimal places. This is
  noted as an assumption, not a general solution.
