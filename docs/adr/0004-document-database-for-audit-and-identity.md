# ADR-0004: Document database for audit trail and authentication/authorization data, with field-level encryption for sensitive fields

## Status
Accepted

## Context
Two data classes don't fit the in-memory stores used for the challenge's core scope:

1. An **audit trail** of every processed request/response, for compliance, dispute resolution,
   and forensics (see the audit discussion in `docs/design.md`).
2. **Authentication/authorization data** for merchants calling the API (API key/secret material,
   roles/scopes, key rotation metadata).

Both need to survive process restarts and be shared across horizontally scaled instances — the
same centralization gap already flagged as a limitation of the challenge-scoped
`IPaymentsRepository` and `IIdempotencyStore`.

Audit records specifically are heterogeneous and expected to evolve: new fields arrive as new
acquirers (ADR-0006), new validation rules, or new bank-response metadata come online. A rigid
relational schema would mean a migration for each such change.

Operating a single database technology, rather than one engine per concern, is preferred given
this system has no dedicated platform/DBA team — every additional stateful dependency has a real
operational cost (backups, upgrades, on-call familiarity).

## Decision
Use a **document database (MongoDB)** for both audit records and merchant authN/authZ data.

- **Audit collection**: one document per processed request, capturing correlation id, timestamp,
  merchant id, endpoint, masked request/response summary, outcome, latency breakdown (total,
  bank-call), and which acquirer handled the call (ADR-0006). Schema flexibility means adding a
  field for a new acquirer's metadata, or a new rejection detail, needs no migration.
- **Merchants collection**: merchant id, hashed API key/secret, roles/scopes, key rotation
  metadata.

**Field-level encryption**: any genuine PII stored (e.g. caller IP address, merchant contact
metadata) is encrypted at the application layer before being written, using envelope encryption
(a per-field data-encryption key wrapped by a key-encryption key held in an external
KMS/secrets manager) rather than relying on at-rest disk encryption alone — so a database-level
compromise (a leaked backup, an over-privileged read) doesn't expose plaintext PII. MongoDB's
Client-Side Field Level Encryption / Queryable Encryption is the concrete mechanism this points
to, to avoid hand-rolling cryptography.

**Important clarification that affects scope**: the **CVV is never stored, encrypted or not**.
PCI-DSS explicitly prohibits retaining CVV/CV2 after authorization completes — this is a hard
compliance rule, not a risk-tolerance choice encryption could satisfy. The audit trail follows
the exact same masking rule already established for the rest of this system: only the last four
digits of the PAN are ever persisted, in any store, encrypted or not. "Encrypt PAN" is therefore
read here as "encrypt whatever card-adjacent metadata is retained (e.g. a last-four reference)",
not as license to retain more of the card than the rest of the design already allows.

## Consequences
- Accepted trade-off: authN/authZ data is comparatively small and simply related
  (merchant → API keys → roles), and would arguably be a more natural fit for a relational
  database with strong ACID guarantees. Using the same document database for both concerns is a
  deliberate simplicity trade-off (one engine to run, back up, and operate), not a claim that a
  document database is the ideal fit for auth data specifically.
- Document flexibility for the audit collection avoids migrations as the system grows, at the
  cost of weaker schema enforcement at the database layer — mitigated by defining and validating
  a single shared `AuditRecord` contract in application code rather than relying on the database
  to enforce shape.
- This store must be reachable from every API instance — unlike the in-memory stores it becomes
  a hard dependency of both authentication and audit, so its own availability now matters.
  Auth lookups sit on the hot path (every authenticated request), which argues for a caching
  layer (e.g. a short-lived cache of validated API keys) to avoid a database round-trip per
  request — noted as a follow-on, not built as part of this decision.

## Update — merchant authN/authZ half now implemented (see ADR-0010)
The "follow-on" flagged above has been built: ADR-0010 implements a MongoDB `merchants`
collection plus an in-memory read-through credential cache (Redis evolution documented
separately in ADR-0011). That closes the merchant authN/authZ half of this ADR's scope.

The **audit trail** half of this ADR — the `AuditRecord` collection, field-level encryption for
PII, correlation IDs, etc. — remains undecided/out of scope for this submission. Don't treat
ADR-0010 as having implemented audit persistence; it hasn't.
