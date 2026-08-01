# Custom-loop receipt-retention contract

This document defines the schema-1 contract shared by custom-loop definition mutation receipts, definition tombstones, and lifecycle-control receipts. It is an implementation contract derived from the visibility, replay, recovery, concurrency, and human-authority axioms; it is not a roadmap or a claim that filesystem cleanup and Web projection are already implemented.

## Capacity and reservation

All sizes are measured from canonical persisted UTF-8 bytes. Every schema-1 JSON property is required on input, including properties whose intentional value is `null`; omission is not equivalent to an explicit semantic null. Per-class compact-proof bytes are the canonical proof-entry bytes plus their array separators; the shared ledger envelope is enforced by the aggregate ledger ceiling. Accounted workspace bytes include raw artifacts, compact proof, and at most one active cleanup journal for each of the three artifact classes.

| Artifact class | Raw count | Raw aggregate bytes | Pending-completion reserve | Compact proof count | Compact proof bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Definition mutation receipt | 10,000 | 128 MiB | 64 receipts / 40 MiB | 100,000 expired operations | 32 MiB |
| Definition tombstone | 10,000 | 64 MiB | 64 tombstones / 1 MiB | 10,000 lineage identities | 16 MiB |
| Lifecycle-control receipt | 20,000 | 128 MiB | 128 receipts / 8 MiB | 100,000 expired operations | 32 MiB |

Normal admission stops at the raw ceiling minus the reserve. Reserved capacity is not spare capacity for new work: only the integrity-preserving completion of already-pending work may consume it. Each cleanup selects at most 64 raw artifacts and 4 MiB. A compact proof ledger is capped at 80 MiB, each active class journal at 8 MiB, and total accounted workspace usage at 424 MiB.

Compact proof is deliberately finite. If it cannot accept every required operation fingerprint and lineage record, cleanup and new admission fail closed with `ProofCountLimit`, `ProofByteLimit`, or `ProofCapacityExhausted`. The runtime must never regain capacity by forgetting an old operation identity or deleted loop identity.

## Exact replay and expiry

Exact replay is promised for 30 days from a receipt's terminal UTC timestamp. A receipt is expired when the observation time is greater than or equal to `completedAtUtc + 30 days`; equality is expired. Pending receipts have no expiry and are never cleanup candidates.

Operation lookup has three meanings:

- `Exact`: the complete receipt remains and the original result can be replayed exactly.
- `Expired`: the full receipt was compacted, but schema-1 proof retains the operation ID, artifact class, request hash, outcome hash, completion time, and exact expiry time. Definition-mutation proof also requires the exact `create`, `update`, or `delete` mutation kind and its compatible terminal store outcome; lifecycle-control proof requires both fields to be null. The caller must receive an explicit expired response and must not reuse the ID as a new operation.
- `Unknown`: neither a full receipt nor compact proof recognizes the ID. This is not interchangeable with `Expired`.

The definition-mutation outcome matrix mirrors the authoritative persisted receipt contract exactly: `Create` permits `Created`, `Conflict`, or `LimitExceeded`; `Update` permits `Updated`, `Conflict`, or `NotFound`; and `Delete` permits `Deleted`, `Conflict`, `NotFound`, or `AlreadyDeleted`. Every other kind/outcome pair is invalid, including replay-facing `AlreadyCreated`, `OperationConflict`, and `LimitExceeded` for Update or Delete.

A delete receipt's expired-operation proof belongs to the definition-mutation class and carries the `delete` kind plus its exact outcome. Only a successful `Deleted` outcome owns or creates a tombstone. Before that receipt is removed, cleanup must also write a matching definition-lineage proof containing the loop ID, immutable role binding, last version and content hash, last mutation ID, and deletion timestamp. The expired proof retains a canonical binding hash over its request hash, outcome hash, successful outcome, and that complete lineage tuple; validation rejects either side when the fingerprint and lineage do not match exactly. Failed or no-op delete outcomes such as `Conflict`, `NotFound`, and `AlreadyDeleted` retain their operation fingerprint without inventing lineage ownership. Later tombstone compaction requires and preserves the successful delete's same role-bound lineage. Each deleted lineage has one unique last-mutation owner, so one successful delete operation cannot be attributed to multiple loop identities. This permanent proof prevents loop-ID reuse after both full artifacts expire.

These fields are the only accepted schema-1 proof shape. This experimental contract has no compatibility reader or automatic migration: any artifact produced from an earlier pre-release shape must be explicitly removed or reinitialized before downstream persistence uses this contract.

## Posture classification

Every class posture reports a count and aggregate bytes for every category, including zero-valued categories:

- `Live`: terminal exact replay is still promised.
- `Pending`: no terminal outcome exists.
- `Unaudited`: the terminal outcome is not durably audit-marked.
- `Degraded`: readable evidence carries an integrity or recovery warning.
- `Compactable`: complete, audited, unambiguous, ownership-resolved evidence is outside the replay horizon.
- `RetainedLiveLineage`: an expired Create receipt remains the required raw lineage of a live definition and is not compactable.
- `RetainedLineage`: compact definition lineage or non-reuse proof.
- `ExpiredIdempotency`: compact expired-operation fingerprint proof.
- `Corrupt`: canonical validation failed.
- `OwnershipUnresolved`: exclusive cross-process ownership is not established.
- `Ambiguous`: duplicate or conflicting evidence prevents unique attribution.

Only `Compactable` is safely prunable. Classification never makes pending, unaudited, degraded, corrupt, ownership-unresolved, or ambiguous evidence eligible for deletion.

Posture also exposes the oldest and newest exact-replay expiry exactly when live receipts exist, class and workspace accounted bytes, immutable limits and reserves, and separate actionable quota-exhaustion and cleanup-block reasons. Corruption, audit unavailability, cleanup conflict, and capacity exhaustion remain distinct states.

## Cleanup journal and recovery

A caller submits a governed schema-1 cleanup command containing only the artifact class, operation ID, actor, surface, and count/byte bounds. It has no caller-controlled timestamp or replay cutoff. The class-specific adapter observes current UTC from its trusted `TimeProvider` and uses the canonical request factory to derive the persisted request time and exact replay cutoff. The resulting request binds the command scope to that trusted time observation; its canonical SHA-256 hash prevents a reused cleanup ID from changing scope.

The durable schema-1 journal advances through explicit stages:

1. `IntentPersisted`: immutable candidate IDs, hashes, sizes, compact proof, and bounded cross-process owner are durable before mutation.
2. `IntentAuditStarted`: the single bounded intent-audit attempt is durably marked before the append-only audit mutation.
3. `IntentAuditRecorded`: the governed intent is durably audited.
4. `ProofLedgerWritten`: a canonical replacement proof ledger is atomically written and hash-verified.
5. `ArtifactsRemoved`: each candidate is hash-revalidated and the selected raw artifacts are removed as one attributed batch.
6. `OutcomeAuditStarted`: the single bounded outcome-audit attempt is durably marked.
7. `Completed` or `CommittedWithAuditWarning`: completion is durable, with an explicit warning if the bounded audit attempt could not be confirmed.

`IntentAuditStarted` and `OutcomeAuditStarted` do not claim that their append-only audit write was atomic with the journal. On recovery, either stage is treated as an uncertain prior append and is not retried. `AbandonedConflict` means a candidate changed or disappeared and nothing is attributed to that batch. `Degraded` means recovery cannot advance safely; if degradation occurs after artifact removal, the journal retains the proof-ledger hash plus the full immutable candidate count and byte attribution rather than erasing committed-removal evidence. A cleanup implementation must resume from the last durable stage under a fresh bounded owner; it must not infer a later stage from missing files, repeat an uncertain audit append, or delete evidence when corruption, audit availability, ownership, or attribution is unresolved.

## Architecture boundary

`Core.Common` owns dependency-free categories, budgets, exact replay policy, and quota/block reasons. `Core.Application` owns the timestamp-free schema-1 cleanup command, trusted-time request factory, persisted request, proof, journal, posture, validation, deterministic canonical hashing/equality, and the class-specific `ICustomLoopReceiptRetentionPort` for inspection, exact/expired/unknown lookup, and bounded governed cleanup.

Concrete filesystem mutation, cleanup scheduling, Startup composition, API/UI projection, and browser behavior are intentionally outside this foundation and must implement these contracts without introducing schema migration or legacy compatibility readers.
