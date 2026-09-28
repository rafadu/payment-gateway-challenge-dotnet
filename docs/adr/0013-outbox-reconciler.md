# ADR-0013: Outbox / pending-write reconciler for `POST /api/payments`

## Status

Accepted

## Context

`POST /api/payments` has three failure modes that leave the gateway and the merchant's record
of the world out of sync:

1. **Bank returns `200 {authorized: true}`, then `_payments.AddAsync` throws** (Mongo down /
   timeout) — the merchant is charged at the bank, the gateway has no record, the merchant
   gets a `5xx`.
2. **Client disconnects after the bank call has started but before the gateway has persisted** —
   bank call still completes server-side; gateway drops the response.
3. **Gateway process restart between the bank response and the persist** — same outcome as (2).

The merchant→gateway `Idempotency-Key` (ADR-0003) does **not** close this. Its release-on-5xx
semantics mean a retry reaches the bank a second time — without bank-side idempotency
(ADR-0012) that's a double-charge. With ADR-0012, the second bank call returns the cached
answer; the gateway's `_payments.AddAsync` then succeeds and the merchant sees the cached `201`.
**But** for failure mode (1), the gateway returned `503` *after* the bank charged and *before*
the gateway persisted — the merchant's `Idempotency-Key` claim was released, so on retry the
filter sees no claim and the gateway calls the bank again. ADR-0012 dedupes on the bank side, so
the merchant gets the same answer; the *Payment*, however, is now created from the retry path,
not the original failed path. Functionally the same outcome — but the gateway's only durable
proof that the bank authorized is the bank's cache, not its own ledger. That's an external
dependency the gateway shouldn't need.

The deeper question — "what if the bank itself crashed between receive and respond?" — is
out of scope here. Both ADR-0003 and ADR-0012 assume the bank processed the request at least
once; the un-covered case is rare, unrepresentable in Mountebank, and listed as a known
limitation.

## Decision

Implement an **outbox** for `POST /api/payments`: a write-ahead record in a Mongo collection
(`bank_intents`) that the handler writes before the bank is called and updates as the bank
responds. A hosted reconciler periodically sweeps for intents whose `UpdatedAt` is older than a
threshold and materializes any missing `Payment` rows.

### Handler four-write sequence (`ProcessPaymentHandler`)

```
1. _intents.AddAsync(BankIntent.StartPending(paymentId, merchantId, snapshot))       (durable)
2. await _bankClient.ProcessPaymentAsync(bankReq, idempotencyKey)
3. _intents.RecordOutcomeAsync(paymentId, bankResponse)                              (durable)
4. _repository.AddAsync(Payment.FromBankOutcome(..., paymentId))                    (durable)
5. _intents.MarkReconciledAsync(paymentId)                                          (durable)
```

The id is generated upfront and shared between `BankIntent.Id` and `Payment.Id`. If a sweeper
materializes the Payment later, the id matches the cached `Idempotency-Key` response's id (per
ADR-0003) — no id-mismatch bugs from the recovery path producing a "different" Payment.

The intent's request snapshot (`BankIntentRequest`) holds **only the last four digits** of the
card number, plus expiry, currency, and amount. The PAN and CVV never enter the outbox —
the ADR-0008 architecture-test PCI safety net enforces this.

### Reconciler (`BankIntentReconciler` + `IntentReconciliationLogic`)

Hosted `BackgroundService`. Every `BankIntentReconcilerOptions.PollIntervalSeconds`
(default `5`), it calls `IntentReconciliationLogic.ReconcileStaleAsync(staleAfter)`. For each
stale intent:

- **Payment already exists** → mark intent `Reconciled` (idempotent catch-up for write #4
  having failed after the Payment was durable).
- **Authorized or Declined** → reconstruct `Payment` from the intent's stored snapshot +
  response, persist, mark `Reconciled` (the primary gap-closing scenario: write #3 failed).
- **Pending** (R-7) → increment `Attempts` and leave status alone. The bank call cannot safely
  be retried without idempotency-key support (ADR-0012) — replaying an unknown bank state
  risks double-charging. The `Attempts` counter increments on each sweep so ops dashboards can
  spot a stuck Pending.

Per-intent errors are logged and the loop continues — a single bad intent must not block the
rest of the pass.

### Mongo collection

- Collection name: `bank_intents`.
- Compound index `(Status, UpdatedAt)` — created by the repository's constructor (best-effort
  with a logged warning if it fails; without it, `FindStaleAsync` is a full collection scan).
- Document id is stored as a `string` (matches `payments` collection convention; sidesteps the
  `GuidRepresentation.Unspecified` serialization issue without a global serializer).
- Domain conversion via `BankIntentDocument.FromDomain` / `ToDomain` — the domain model is
  what flows through the rest of the codebase; the document type is internal to persistence.

### Composition

`Configuration/BankIntentsServiceCollectionExtensions.AddBankIntents(configuration)` registers the
options binding, the repository (Mongo-backed; in-memory when `BankIntents:UseInMemory=true`),
the logic, and the hosted service. Per-pass scope inside the hosted service so any future
scoped dependency on the reconciler resolves fresh — today every dependency is singleton, but
the per-pass scope makes that evolution safe without a code change here.

## Consequences

- **Closes the silent-crash gap.** Failure modes 1–3 above now end with a `Payment` in the
  gateway's ledger. The merchant's `Idempotency-Key` retry (if any) returns the cached
  `201` for free; a fresh retry that hits the bank again gets the cached answer (ADR-0012) and
  the gateway still produces the same `Payment` row because of the shared id.
- **Composes with ADR-0003 and ADR-0012.** Three layers of defense for three failure modes:
  - ADR-0003 (merchant→gateway): merchant retries hit the gateway's replay cache, not the bank.
  - ADR-0012 (gateway→bank): bank retries (after the gateway returned 5xx) hit the bank's cache.
  - ADR-0013 (gateway outbox): silent gateway crashes are recovered by the sweeper.
- **Remaining un-covered case: bank crashes between receive and respond.** ADR-0003 and
  ADR-0013 both assume the bank processed the request at least once. Listed as a known
  limitation. Closing this would require storing the merchant's `Idempotency-Key` on the
  `BankIntent` and the sweeper retrying the bank call with the same key — deferred as a
  follow-up.
- **R-7 — stale Pending is left for ops, not auto-retried.** Without bank-side idempotency,
  retrying a Pending intent whose bank state is unknown risks double-charging. The `Attempts`
  counter is the visible signal; ops investigates via the bank's transaction report.
- **Eventual consistency on bookkeeping (write #4).** Crash between write #3 and write #4
  leaves a `Payment` plus an `Authorized`/`Declined` intent. The reconciler picks it up, sees
  the Payment already exists, marks `Reconciled`, done. Eventually consistent — no data loss.
- **In-memory state of the simulator isn't extended** — the simulator already returns the
  bank's cache hit on a retry. The outbox is purely gateway-side.
- **Adds 4 new types** (`BankIntent`, `BankIntentRequest`, `BankIntentStatus`,
  `IBankIntentsRepository` + Mongo impl, `BankIntentDocument`, `IntentReconciliationLogic`,
  `BankIntentReconciler`, `BankIntentReconcilerOptions`) and ~250 lines of tests (unit for the
  domain and logic, integration for the Mongo round-trip). One Mongo collection, one compound
  index. No new dependencies; reuses existing `MongoDB.Driver`, `Microsoft.Extensions.Hosting`,
  and `Microsoft.Extensions.Options`.
