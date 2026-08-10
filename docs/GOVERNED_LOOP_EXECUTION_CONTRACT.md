# Governed Loop Execution Contract

## Status and scope

This document defines the schema-version-1 execution vocabulary introduced by issue #326. The dependency-free contracts live in `EmbodySense.Core.Common.Loops.Execution`; the read-only compatibility mapper lives in `EmbodySense.Core.Application.Loops.Compatibility`.

This implementation establishes a canonical contract and an honest migration map. It does **not** make the contract the persisted or runtime source of truth. The existing default-conversation and custom-loop protocols remain authoritative for their current paths until the #182 convergence train explicitly adapts and retires them. No dispatcher, queue, recovery service, policy, store, or surface may treat a compatibility projection as admission or mutation authority.

The contract advances Axioms 2, 4, 5, 7, 11, 12, 13, and 15 by separating durable run posture, graph progress, external effects, projections, and implementation failpoints without weakening role-bound authority or hiding ambiguity.

Explicit non-goals are:

- full default/custom runtime convergence;
- a new execution store, compatibility store, serializer, migration, alias, or fallback reader;
- graph dispatch, durable frontier mutation, retry, repair, reconciliation, escalation, or actuator implementation;
- changing the current default-conversation or custom-loop runtime behavior;
- treating executor-specific crash failpoints as persisted product state;
- inventing one universal enum for every executor-internal event.

## Canonical separation

One `GovernedLoopExecutionEvidenceSet` composes four distinct evidence families under one exact `GovernedLoopExecutionBinding`:

- `RunId`: the stable admitted run identity;
- `Revision`: the exact `GovernedLoopRevisionReference`, including graph, immutable revision, and executable hash;
- `ExecutionGeneration`: the positive generation that prevents a stale frontier or effect from being replayed into another execution generation;
- schema version 1.

Every canonical lifecycle, frontier, effect, and projection posture is bound to that exact tuple. A complete evidence set rejects mixed run, revision, executable-hash, or generation identities. Binding equality is a prerequisite for composition, not evidence that the supplied state is otherwise legal.

The unbound construction payloads are deliberately named:

- `GovernedLoopRunLifecyclePayload`;
- `GovernedLoopFrontierPayload`;
- `GovernedLoopEffectPayload`;
- `GovernedLoopProjectionPayload`.

They exist so validation and read-only compatibility code can describe a candidate without first claiming canonical identity. They are not persistence, dispatch, recovery, queue, policy, or mutation contracts. Canonical consumers use the bound `GovernedLoopRunLifecycle`, `GovernedLoopFrontierPosture`, `GovernedLoopEffectPosture`, and `GovernedLoopProjectionPosture` values assembled into `GovernedLoopExecutionEvidenceSet`.

### Run lifecycle

Run lifecycle answers only whether one admitted run is `Admitted`, `Running`, `Waiting`, `PauseRequested`, `Paused`, `CancelRequested`, `Completed`, `Failed`, `Cancelled`, or `NeedsReview`. It carries an optimistic lifecycle version plus creation, update, and terminal timestamps. Creation cannot follow update; a terminal status requires a terminal timestamp equal to the terminal version's update time; a nonterminal status cannot carry one.

`NeedsReview` is a terminal ambiguity posture. It must retain unresolved, ambiguous, reconciliation-required, or reconciled effect evidence, or conflicting, reconciliation-required, or reconciled projection evidence that preserves the prior ambiguity and its disposition. Its frontier may remain `ReviewBlocked` or may already be an immutable `Completed`, `Failed`, or `Cancelled` snapshot when later evidence discovers the ambiguity. It is not the ordinary Human Review gate. An ordinary gate remains nonterminal `Waiting` lifecycle plus `ReviewBlocked` frontier evidence.

Run lifecycle never identifies a current graph node, an effect write window, a provider outcome, or a surface publication.

### Execution frontier

The frontier answers whether graph execution is `Active`, `Waiting`, `ReviewBlocked`, `Completed`, `Failed`, or `Cancelled`. Its node evidence uses `Ready`, `Running`, `Completed`, `Skipped`, `Waiting`, `Failed`, and `ReviewBlocked` states.

Each node entry is tied to a stable node identity and a canonical sorted unique set of incoming edge identities. `Ready` and `Skipped` carry no attempt number; other node states require a positive attempt. Completed and failed nodes require an outcome-evidence identity, skipped nodes may cite one, and other states cannot claim one.

Cancellation is an aggregate frontier fact. Schema 1 intentionally defines no synthetic `Cancelled` node status, so a `Cancelled` frontier retains every node's exact last committed posture, incoming edges, attempt, and outcome reference. Cancellation cannot rewrite an active or waiting node as skipped or failed, and it cannot fabricate a node outcome.

The frontier is graph progress only. It cannot substitute for an effect attempt, prove an external outcome, or erase an open effect when a node or run becomes terminal.

### Effect evidence

Effect origins are closed to provider, actuator, publication, memory mutation, notification, and system-job families. Every effect has a stable operation identity and generation. Phase, outcome, and evidence completeness remain separate dimensions:

| Durable fact | Meaning | Redispatch posture |
| --- | --- | --- |
| `IntentPrepared` | Canonical intent exists and no attempt has crossed its irreversible dispatch boundary. | Eligible for its first governed dispatch only. |
| `DispatchNotStarted` | A prior dispatch opportunity definitely did not cross the irreversible boundary. | May be eligible for safe redispatch only under later #117 policy. |
| `DispatchBoundaryReached` + `OutcomeUnknown` | External dispatch may have happened and no conclusive outcome is retained. | Never automatically redispatch. |
| `OutcomeObserved` + `Succeeded` or `Failed` | A conclusive external outcome is retained. | Never repeat the completed attempt. |
| `OutcomeObserved` + incomplete evidence | The result is known but required audit/evidence completion failed. | Preserve the result; do not redispatch to repair bookkeeping. |
| `Committed` | Required result/publication projection is durably committed. | Terminal and non-repeatable. |
| `ReconciliationRequired` | Evidence is ambiguous or conflicting. | Explicit reconciliation or operator disposition is required. |
| `Reconciled` | An explicit disposition is retained. | The disposition does not fabricate an external outcome. |

The public state matrix exposes structural dispatch eligibility for `IntentPrepared` and `DispatchNotStarted`, and specifically forbids redispatch for `Committed`, `ReconciliationRequired`, and `Reconciled` effects. `IntentPrepared` permits only the initial governed dispatch; treating `DispatchNotStarted` as safe to redispatch still requires later policy. #116 owns the future canonical effect-attempt protocol and operator disposition. #117 owns retry eligibility, ambiguity policy, repair, reconciliation, compensation, escalation, and failure routing after the facts are known.

### Projection evidence

Projection classes are local runtime, durable read model, and surface projection. Projection status is `Pending`, `Committed`, `Conflict`, `ReconciliationRequired`, or `Reconciled`. A reconciliation-required projection can advance only to `Reconciled`, which requires a bounded reconciliation or operator-disposition evidence identity. Its optional committed version distinguishes a repaired projection from a disposition that intentionally waived repair. `Reconciled` is terminal and idempotent; it cannot silently return to ordinary pending or committed synchronization.

Projection evidence never proves an external effect outcome. A run-record update, runtime cache, or surface refresh may lag or conflict without changing the already observed provider or actuator result. Conversely, a committed or reconciled surface projection cannot make an outcome-unknown effect successful. Normal `Committed` projection evidence never carries reconciliation evidence.

Publication that changes harness-owned or external durable content is an effect. Updating a run read model or a surface view from already canonical evidence is a projection. A composed operation retains both facts when it performs both responsibilities.

### Failpoints

Failpoints name implementation crash windows. They must correspond either to a committed durable fact or to an intentionally open write window, but they are not canonical lifecycle, frontier, effect, or projection values. `DefaultConversationTurnBoundary`, persistence archive phases, and similar executor-specific seams remain test infrastructure until their owning implementation is retired.

The current default-conversation failpoints map exhaustively as follows. “Open window” describes the durable facts on either side of process loss; it does not introduce a canonical state or authorize recovery by itself.

| `DefaultConversationTurnBoundary` member | Durable fact or intentionally open window |
| --- | --- |
| `Unknown` | No boundary. It is invalid for concrete crash injection and maps to no canonical fact. |
| `TurnAdmitted` | The schema-1 turn record and admitted run identity are committed. |
| `RunStartSaved` | The Started run projection is committed while the protocol has not yet checkpointed `RunStarted`; this is an open projection/checkpoint window. |
| `RunStartCheckpointed` | The protocol's `RunStarted` transition is committed. |
| `UserAccepted` | The exact user message and stable identity are committed as accepted input. |
| `UserPublicationPrepared` | The user-publication intent is committed and the transcript append is definitely not yet evidenced. |
| `UserTranscriptAppended` | The harness transcript append committed while its protocol outcome checkpoint remains open. |
| `UserPublished` | Exact-once user-publication outcome evidence is committed. |
| `ProviderDispatchPrepared` | Stable provider attempt/correlation identity is committed before the irreversible transport-write boundary. |
| `ProviderDispatchStarted` | The irreversible provider boundary is committed and the outcome remains unknown until later typed evidence. |
| `ProviderOutcomeObserved` | Typed provider success, failure, or audit-incomplete outcome evidence is committed. |
| `AssistantPublicationPrepared` | The assistant-publication intent is committed and the transcript append is not yet evidenced. |
| `AssistantTranscriptAppended` | The harness transcript append committed while its protocol outcome checkpoint remains open. |
| `AssistantPublished` | Exact-once assistant-publication outcome evidence is committed. |
| `TranscriptSynchronized` | The local runtime/conversation projection is committed from the canonical transcript. |
| `TerminalPrepared` | Desired terminal lifecycle evidence is committed while the terminal run projection may still lag. |
| `TerminalRunSaved` | The terminal run projection is committed while the protocol's final terminal checkpoint remains open. |
| `TerminalCommitted` | The final terminal checkpoint and run-projection synchronization fact are committed. |

## Legal composition rules

`GovernedLoopExecutionValidator` and `GovernedLoopExecutionStateMatrix` enforce the public schema-1 rules. The important cross-family rules are:

| Run posture | Frontier posture | Effect/projection requirement |
| --- | --- | --- |
| `Admitted` or `Running` | Active graph evidence | May retain pending work; no terminal projection claim. |
| `Waiting` | `Waiting` or `ReviewBlocked` | Waiting or Human Review is nonterminal and cannot fabricate an effect result. |
| `PauseRequested`, `Paused`, or `CancelRequested` | Nonterminal or cancellation-directed frontier | Existing open or conclusive effects remain visible. |
| `Completed`, `Failed`, or `Cancelled` | Matching terminal frontier | No unresolved effect, pending/conflicting projection, or reconciliation-required projection may be erased or hidden; an explicitly `Reconciled` projection is resolved. |
| `NeedsReview` | Terminal or review-blocked evidence consistent with the retained facts | At least one unresolved/ambiguous effect or conflicting/reconciliation-required/reconciled projection history remains inspectable. |

A terminal run status never closes an ambiguous effect by implication. Historical facts are append-only; a later status, enum, adapter, or human decision cannot reinterpret an old outcome. Human review may decide what the system should do next, but it cannot claim that an unknown external effect succeeded or failed.

Terminal snapshots are immutable. Once a lifecycle reaches `Completed`, `Failed`, `Cancelled`, or `NeedsReview`, no higher lifecycle version may restate or rewrite it. Once a frontier reaches `Completed`, `Failed`, or `Cancelled`, no higher frontier version may restate or rewrite its aggregate status or retained node evidence. Later audit, reconciliation, or operator evidence remains separate and append-only rather than mutating the terminal lifecycle or frontier snapshot.

Aggregate successors are append-only. `GovernedLoopExecutionValidator.ValidateTransition(current, next)` requires one exact binding, validates the successor composition, validates every changed lifecycle, frontier, effect, and projection plane, and rejects omission of any previously retained effect or projection identity. An exact unchanged plane is legal, including an immutable terminal lifecycle or frontier while later effect or projection evidence advances. New effect and projection identities may be appended only when the complete successor remains bounded, canonical, attributable, and otherwise legal.

For a nonterminal aggregate, every frontier, effect, and projection timestamp must fall between lifecycle creation and the current lifecycle update. A terminal lifecycle keeps its terminal update time immutable, so later append-only audit, reconciliation, operator-disposition, and projection evidence may carry a later timestamp while still binding to that terminal snapshot; no evidence may predate run creation.

## Read-only compatibility mapping

`GovernedLoopCompatibilityProjector` maps current default-conversation and custom-loop records for inspection and convergence analysis. Compatibility source payloads are intentionally unbound and non-authoritative.

Its closed result semantics are:

- `Complete`: every required canonical binding and evidence field is present, exact, and valid. Only a source with canonical effect intent hashes and optimistic projection versions can produce this result.
- `Partial`: a valid canonical subset can be projected, but one or more facts are unavailable. Every omission is an explicit compatibility gap; no value is guessed.
- `Unsupported`: authoritative source validation rejects the source, or a finite adapter-safety bound rejects its shape before validation; the closed gap code distinguishes those causes.

The current legacy records lack the canonical effect-intent hashes owned by #338 and the optimistic projection versions required by the canonical contract. The mapper therefore must not synthesize `GovernedLoopEffectPayload` or `GovernedLoopProjectionPayload` values from those records. A partial result may expose explicitly noncanonical, Application-only effect or projection observations plus `CanonicalEffectIntentUnavailable` and `ProjectionEvidenceUnavailable` gaps. Those observations reuse closed vocabulary for explanation only; they cannot enter a canonical evidence set, store, runtime port, recovery path, policy decision, mutation, or queue.

There is no compatibility store. Mapping is side-effect-free, performs no provider or actuator call, mutates no source record, grants no authority, and creates no migration or fallback path. Unsupported schema-1 experimental artifacts still require explicit cleanup or reinitialization.

## Current protocol disposition inventory

“Adapt, then retire” means the existing type remains authoritative for the current runtime while a read-only mapper exists; #182 later replaces the execution responsibility without introducing a second persisted truth. “Preserve as surface concern” means the data may remain useful to conversation projection after it stops representing execution lifecycle. “Retain as failpoint” means it remains implementation/test-only until its owner is retired.

| Current type | #326 disposition | Convergence owner and constraint |
| --- | --- | --- |
| `DefaultConversationTurnRecord` | Adapt read-only, then retire as execution truth. | #182 must project from the canonical runtime rather than dual-write another turn record. |
| `DefaultConversationTurnCheckpoint` | Replace with composed lifecycle, frontier, effect, and projection evidence. | No ordinal rename or one-to-one persisted alias. |
| `DefaultConversationProviderOutcome` | Replace with canonical effect phase, outcome, and evidence status. | Outcome-unknown remains non-redispatchable. |
| `DefaultConversationTurnTransition` | Adapt as historical checkpoint evidence, then retire with the turn store. | Existing append-only history is never rewritten. |
| `DefaultConversationTurnMessage` | Preserve as a legitimate conversation-publication payload. | It must no longer carry execution-lifecycle authority after convergence. |
| `DefaultConversationTurnBoundary` | Retain as the default runner's failpoint only. | Never persist or expose it as canonical lifecycle. |
| `IDefaultConversationTurnFailpoint` | Retain as an implementation test seam, then retire with the runner. | Executor-specific crash injection remains outside Common execution state. |
| `DefaultConversationTurnInterruptedException` | Retire with the default-only failpoint path. | It is not a canonical failure taxonomy. |
| `DefaultConversationTurnRecoveryClassification` | Adapt for read-only gaps, then replace with #117 recovery policy over canonical facts. | Classification cannot change an effect outcome. |
| `DefaultConversationTurnRecoveryReport` | Adapt temporarily, then retire. | Canonical evidence plus #117 policy becomes the report source. |
| `DefaultConversationTurnRecoveryResult` | Adapt temporarily, then retire. | No compatibility result may resume or dispatch work. |
| `DefaultConversationTurnRecoveryService` | Retain behind the current runtime, then retire or adapt in #182. | No dual recovery authority. |
| `DefaultConversationTurnReviewCause` | Adapt to ambiguity/reconciliation explanation. | #116 owns disposition; #117 owns later policy. |
| `DefaultConversationTurnReviewClassification` | Adapt temporarily, then retire. | It cannot become an external outcome. |
| `DefaultConversationTurnReviewDisposition` | Preserve as historical operator intent until #116 supplies canonical disposition. | Abandonment does not mean external failure. |
| `DefaultConversationTurnReviewResolution` | Adapt as explicit disposition evidence, then retire as execution truth. | Historical resolution remains attributable and immutable. |
| `DefaultConversationTurnReviewService` | Retain behind the current runtime, then replace through #116/#182. | It cannot mutate compatibility projections. |
| `IDefaultConversationTurnStore` | Retain for the current runtime, then retire. | No canonical or compatibility store is added by #326. |
| `DefaultConversationTurnStoreResult` | Retire with the store. | It is storage protocol, not run lifecycle. |
| `DefaultConversationTurnStoreStatus` | Retire with the store. | It is storage outcome, not effect outcome. |
| `VolatileDefaultConversationTurnStore` | Retire with the default-only store contract. | It cannot become a canonical fallback. |
| `DefaultConversationTurnProtocol` | Retain as current schema-1 truth, then retire in #182 after parity. | No in-place widening into a false generic protocol. |
| `DefaultConversationTurnProtocolValidator` | Retain with the old protocol, then retire. | Canonical validation stays separate during convergence. |
| `DefaultConversationTurnStore` | Retain as current persistence truth, then retire in #182. | Never dual-write canonical or compatibility artifacts. |
| `DefaultConversationTurnNativeFileSystem` | Retire with `DefaultConversationTurnStore`. | Physical safety remains required in its replacement store. |
| `DefaultConversationTurnRetirementEvidenceProof` | Retain as historical store evidence, then retire with that store. | Existing proof is not migrated or reinterpreted. |
| `DefaultConversationTurnSourceProofPublicationIntent` | Retain as historical store evidence, then retire with that store. | Existing intent cannot be converted into a guessed canonical projection. |
| `IDefaultConversationTurnStoreCoordination` | Retain as persistence test seam, then retire. | Not a product lifecycle port. |
| `DefaultConversationTurnArchivePhase` | Retain as persistence failpoint, then retire. | Never becomes canonical lifecycle. |
| `DefaultConversationTurnLeasePhase` | Retain as persistence failpoint, then retire. | Lease phases remain implementation detail. |
| `DefaultConversationTurnStoreOperation` | Retain as persistence failpoint context, then retire. | Store verbs are not effect origins. |
| `DefaultConversationTurnFileIdentity` | Retain as persistence integrity evidence, then retire with the store. | It is physical-file identity, not run identity. |

Related default-only types that do not begin with `DefaultConversationTurn` follow the same plan:

| Current type | Disposition |
| --- | --- |
| `DefaultConversationLoopRunner` and `IDefaultConversationLoopRunner` | Retain as current runtime truth; #182 adapts or retires after canonical parity. |
| `DefaultConversationLoopTurnRequest`, `DefaultConversationLoopTurnResult`, and `DefaultConversationLoopTurnStatus` | Preserve as surface-facing request/result shapes only if they cease to own lifecycle meaning; otherwise retire with the runner. |
| `DefaultConversationLoopGraphContract` | Replace with canonical graph admission/dispatcher contracts in the #311/#312 sequence. |
| `DefaultConversationRequestReconciliationReader` and snapshot | Retain as current Startup recovery projection, then source it from canonical runtime evidence in #182. |
| `DefaultConversationReviewSnapshot` | Preserve as a safe surface projection only; it never becomes execution or effect truth. |
| `DefaultConversationCapabilityAuthorityRevalidator` | Adapt to the canonical dispatcher/effect path without widening authority. |

## Custom-loop mapping

The current custom runtime remains an ordered, separately persisted execution truth. Its evidence families map explicitly as follows; “adapt” always means a read-only projection until the named owner replaces the legacy responsibility without dual writes.

| Current custom-loop family | #326 mapping and disposition | Convergence owner and protected gap |
| --- | --- | --- |
| `CustomLoopRunRecord.Status`, lifecycle version, and timestamps | Adapt to an unbound lifecycle payload, then replace as runtime truth. | #182 consumes canonical lifecycle after parity. The adapter reports exact-revision, execution-binding, and canonical-history gaps. |
| `CustomLoopRunCheckpoint` and `CheckpointCommitted` events | Preserve as the ordered runner's cursor; do not rename or persist it as a canonical graph frontier. | #312 replaces it with revision/node/edge/generation-bound frontier evidence. Until then `DurableFrontierUnavailable` is mandatory. |
| `NodeAttemptStarted`, `NodeAttemptCompleted`, `NodeOutcomeObserved`, `NodeAttemptFailed`, `ExitDecisionStarted`, and `ExitDecisionCompleted` events | Adapt typed provider observations only; preserve the event history and never infer from `Detail` or failure prose. | #311 owns dispatcher parity and #338/#116 owns canonical intent, irreversible boundary, outcome, and audit evidence. Missing dispatch/audit facts remain explicit gaps. |
| `ToolRequestReserved`, `ToolGovernanceDecided`, `ToolOutcomeObserved`, and `ToolIntegrityFailed` events plus `CustomLoopToolTraceEvidence` | Adapt typed actuator observations only. A deny, rejected approval, or still-requested approval proves no dispatch; a legacy failed execution remains outcome-unknown when partial effects cannot be excluded; a retained success with later integrity failure remains conclusive but audit-incomplete. | #338/#116 replaces this with canonical effect attempts. `CustomLoopToolEvidencePhase` is never aliased to canonical phase, and absent intent hash/boundary evidence stays explicit. |
| `ConversationPublicationStarted` and `ConversationPublished` events | Adapt to noncanonical publication observations. Typed success remains observed; the legacy false value remains ambiguous because omission, failure, and uncertainty are conflated. | #338/#116 owns publication effect evidence and #182 owns convergence. No transcript or run projection is fabricated. |
| `Admitted`, `LifecycleChanged`, `IterationStarted`, `AdmissionAuditCompleted`, `CheckpointCommitted`, and `IntegrityWarning` events | Preserve as append-only legacy history; do not turn the heterogeneous event enum into a universal canonical enum. Only separately typed facts feed lifecycle or compatibility gaps. | #311/#312/#342 consume their owned canonical planes. Old events remain attributable until #182 retires the store. |
| `CustomLoopInvocationOperation`, `ICustomLoopInvocationOperationStore`, and invocation receipts | Preserve as admission idempotency/coordination evidence, not run lifecycle, frontier, effect outcome, or projection truth. | #114 and #182 must adapt or retire them when admission enters the shared durable plane; existing exact replay and retention evidence cannot be reinterpreted. |
| `CustomLoopControlOperation`, `ICustomLoopControlOperationStore`, control leases/receipts, and `CustomLoopAttemptCancellationHost` | Preserve as request, ownership, and cancellation-coordination evidence; a control receipt is not proof that lifecycle/frontier state changed. | #337 owns unified lifecycle-control posture and #182 owns runtime convergence. Authority and exact operation identity remain unchanged. |
| `CustomLoopRecoveryService`, `CustomLoopRecoveryResult`, and `CustomLoopRecoveryStatus` | Retain behind the current ordered runtime, expose no canonical authority through the compatibility mapper, then replace with policy over canonical facts. | #342/#117 owns failure/recovery policy after #312 supplies frontier truth; #182 retires duplicate recovery authority. |
| Run-store, trace-retention, invocation-retention, control-retention, cleanup, tombstone, and proof-ledger receipts | Preserve as operation-specific persistence/retention evidence. They are neither canonical execution events nor effect outcomes and are not migrated by #326. | Their existing owners continue enforcing idempotency and retention; later convergence must reference or explicitly retire them without silent pruning or alias readers. |

`CustomLoopToolTraceEvidence` does not contain #338's canonical effect-intent hash or a canonical optimistic projection version. A partial mapping never turns a trace observation, receipt, recovery result, or control decision into dispatch authority or a persisted canonical effect attempt.

## Ownership and implementation order

- #326 owns only the shared vocabulary, legality matrix, read-only mapping, and this migration inventory.
- #338/#116 owns canonical effect-attempt intent and outcome evidence plus operator disposition.
- #342/#117 owns failure taxonomy and explicit Fail routes; later #117 children own retry, reconciliation, compensation, escalation, circuit breaking, and fallback selection.
- #311 owns sequential canonical graph dispatch parity.
- #312 owns the durable canonical graph frontier.
- #333/#120 owns immutable revision lifecycle; execution binding consumes its exact published revision.
- #182 owns default-runtime admission, dispatcher parity, streaming/publication correlation, and retirement or explicit adaptation of the old runner/store.

The safe sequence is contract and read-only mapping first, immutable revision lifecycle next, graph dispatch/frontier and effect/failure protocols after their respective prerequisites, then #182 convergence after parity is proved. At no point may both the old protocol and a new canonical store claim mutation authority for the same run.

## Security and authority invariants

- Compatibility input is untrusted historical evidence, never a grant, approval, credential, admission token, or current policy decision.
- Convergence cannot widen role, loop, node, capability, target, memory, data, privacy, egress, recurrence, publication, or irreversible-action authority.
- An outcome-unknown provider or actuator attempt is never automatically repeated.
- A conclusive outcome is not duplicated to repair audit or projection bookkeeping.
- Human review and reconciliation retain who decided what and why, but never invent an external outcome.
- Persisted schema-1 history is append-only and is not reinterpreted through aliases, enum renames, or compatibility readers.
- Secret values do not enter bindings, compatibility gaps, canonical evidence summaries, logs, or public projections.
