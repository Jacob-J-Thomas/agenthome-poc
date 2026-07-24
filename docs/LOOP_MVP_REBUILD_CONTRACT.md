# First-Wave Loop MVP Requirements

Status: approved implementation contract, reconciled with the accepted standalone MVP surface and subsequent context/Exit feedback. Source, tests, and Web projections must implement this contract rather than older experimental behavior.

This document reduces the research implementation on `loop-feature-and-architecture-cutover` to one governed vertical slice. It does not narrow EmbodySense's long-term product direction. `docs/OPINIONATED_PROJECT_AXIOMS.md` remains authoritative.

## 1. MVP Outcome

The first-wave MVP proves that a user can build and operate a real custom loop without first building a general workflow engine.

From the Web UI, the user can:

1. Create and edit multiple custom loop definitions.
2. Build an ordered visual sequence of model-inference steps.
3. Assign an explicit, loop-scoped set of governed read-only workspace tools, with no tools assigned by default.
4. Save only definitions that the server proves are runnable.
5. Manually invoke a saved loop from an invoking prompt, a saved preset prompt, or no prompt, with invoking conversation history admitted only when explicitly configured.
6. Complete after one iteration by default, or let Exit request another iteration through a bounded model decision. The configured maximum is a ceiling and never forces repetition.
7. Request pause or cancellation and explicitly resume a safely paused run, including after a process restart.
8. Inspect the admitted definition, role, context, authority, node progress, outputs, approvals, tool activity, failures, and final result during and after execution.
9. Correlate the run trace with the append-only governance audit without placing raw prompts, outputs, or secrets in the audit log.

The executable grammar is intentionally fixed:

```text
Manual trigger -> Inference step 1 -> ... -> Inference step N -> Exit
                      ^                                      |
                      +------ bounded Exit repeat -----------+
```

The MVP is complete only when authoring, governed invocation, minimal interruption and recovery, observability, security, and auditability work as one vertical slice. A prompt editor that merely sends several model calls is not this MVP.

## 2. Scope Principles

The following requirements are non-negotiable consequences of the axioms and the live repository baseline.

| ID | Requirement |
| --- | --- |
| MVP-001 | A custom loop is a durable, inspectable, modifiable, role-bound, authority-scoped product artifact. |
| MVP-002 | A valid runnable loop is interruptible, resumable from proved safe boundaries, and cancellable. If those behaviors are absent, the result must be labeled a prototype rather than a governed-loop MVP. |
| MVP-003 | The Web UI is the first authoring and invocation projection. It must not own a separate executor, persistence model, or authority model. |
| MVP-004 | A manual "test" invocation is a real run. Assigned tools may read sensitive workspace content; normal capability, permission, approval, audit, and evidence rules always apply. Mutating commands are not assignable in wave one. |
| MVP-005 | Saved definitions are valid and immediately runnable. Invalid edits may exist in browser memory, but there are no persisted invalid drafts and no routine enable/disable workflow. |
| MVP-006 | Provider threads are transport, not product context, memory, authority, or evidence. EmbodySense-owned artifacts define what the run knows and may do. |
| MVP-007 | The system default conversation loop remains separate, read-only, and behaviorally unchanged by the custom-loop MVP. |
| MVP-008 | Deferral in this document means "not in wave one," not "removed from the long-term axioms or product direction." |
| MVP-009 | Creating a loop or inference step does not create another durable agent identity. Every wave-one loop uses the one current directory role; its inference steps are stages of that role-bound loop. |

## 3. Narrow Definition Contract

### 3.1 Persisted fields

The canonical custom-loop artifact persists an ordered definition, not a client-authored general graph.

| Field | Owner | Meaning |
| --- | --- | --- |
| `schemaVersion` | Server | Exact supported artifact schema. |
| `id` | Server | Immutable, server-generated, filename-safe loop identity. Never reused. |
| `definitionVersion` | Server | Monotonically increasing optimistic-concurrency version. |
| `contentHash` | Server | SHA-256 integrity/correlation hash of canonical UTF-8 definition bytes, excluding this field. It is not authentication. |
| `createdAtUtc` / `updatedAtUtc` | Server | UTC definition timestamps. |
| `displayName` | User | Operator-facing name; never prompt content. |
| `description` | User | Operator documentation; never prompt content. |
| `roleId` | Server | Immutable current directory-role identity resolved by the runtime, not selected by a client. |
| `triggerPolicy` | User within limits | Prompt source (`invocation`, `preset`, or `none`), bounded preset text, and whether logical invoking-conversation history is admitted. |
| `contextDefaults` | Server | Versioned, typed Inference and Exit node-kind defaults seeded by the server and shown by the UI. Users interact with context at node level in wave one. |
| `inferenceSteps[]` | User within limits | Ordered inference-step definitions, including each step's context-in/context-out policy. |
| `toolAssignments[]` | User from server catalog | Optional implemented workspace command assignments. Empty is the secure default. |
| `exitPolicy.maxAdditionalIterations` | User within limits | Zero disables continuation inference. A positive value is the maximum number of accepted Repeat decisions, never a target count. |
| `exitPolicy.decisionInstruction` | User within limits | Required when continuation is enabled; tells the Exit model when to return the governed `Complete` or `Repeat` decision. |
| `exitPolicy.contextPolicy` | User within limits | Context-in used by the Exit decision and context-out applied to the final iteration result. |
| `lastMutationOperationId` | Server | Correlates the current artifact with its pre-mutation and outcome audit records. |

Each inference step persists only:

| Field | Owner | Meaning |
| --- | --- | --- |
| `id` | Server | Stable, immutable, filename-safe step identity. |
| `name` | User | Operator-facing node label; not prompt content. |
| `instruction` | User | Required prompt-visible instruction for that step. |
| `contextPolicy` | User | Inherited defaults or an explicit context-in/context-out override for this reasoning boundary. |

The context policy is closed and contains only `mode` (`inherit` or `custom`), five context-in selectors (`includeRoleContext`, `includeTriggerPrompt`, `includeInvokingConversation`, `includeEarlierRetainedOutputs`, `includePreviousIterationResult`), and two context-out selectors (`retainForLoopReasoning`, `publishToInvokingConversation`). Context-out independently controls product context; it never controls evidence retention. There is deliberately no `additionalFixedContext`, free-form context appendix, or equivalent field; step instructions and trigger presets already own authored prompt text.

### 3.2 Fixed and derived fields

The following are fixed runtime policy or derived UI projection. They are not general user-authored graph data:

- Exactly one manual Web/API trigger.
- Exactly one Exit.
- One edge between every adjacent ordered inference step.
- One edge from Trigger to the first step and from the last step to Exit.
- A derived conditional connector from Exit to the first step when `maxAdditionalIterations` is greater than zero.
- The configured workspace provider/model inherited at run admission.
- Server-owned context defaults plus the closed node-level overrides in Section 8.
- `ReviewAtAuthorityBoundaries` review behavior.
- `RecordFailureAndSurfaceToUser` failure behavior.
- Read-only workspace startup memory/context loading.
- Structural capabilities required for context, inference, approvals, and audit.

Trigger and Exit receive deterministic projection IDs. Their identities, edges, ports, and coordinates are derived; only the closed trigger and Exit policies described above are authorable. No capability ID, provider thread ID, review policy, failure policy, or memory policy is accepted from the authoring client as authoritative data.

`contentHash` is computed over one documented canonical JSON representation with stable property ordering and normalized strings. It includes the full persisted definition, version, timestamps, and `lastMutationOperationId`, but excludes `contentHash` itself. A separate future semantic hash is not invented in wave one.

### 3.3 Approved canvas reduction

The experimental builder persisted positioned nodes and explicit user-authored flow edges. This proposal deliberately does not. The canvas is a visual, directly editable projection of one ordered routine: users add/remove/edit/reorder steps, while Trigger, Exit, forward edges, repeat edge, and layout are derived.

This is the largest product reduction in the contract. It is recommended because it removes arbitrary topology, edge identity, port semantics, coordinate persistence, disconnected-graph states, and graph reachability validation while retaining a visual loop-building workflow. If user-positioned nodes or hand-authored connections are considered essential to the first-wave meaning of "build a custom loop," this decision must be corrected before implementation; the clean implementation must not drift into a graph engine halfway through a batch.

### 3.4 What makes loops custom in wave one

Users customize:

- Loop identity text.
- The number, names, instructions, and order of inference steps.
- The explicit loop-level workspace command assignments.
- Trigger prompt source, optional preset, and optional invoking-conversation admission.
- Per-inference and Exit context-in/context-out policies.
- Exit-owned conditional continuation with a bounded maximum of additional iterations.
- Manual invocation input when the trigger selects it.

This is enough to build distinct governed routines while keeping topology, scheduling, authority, and failure semantics bounded while making product context flow explicit at every model boundary.

## 4. Hard Product Limits

Wave-one limits are server-owned constants. The Web UI must display them and use the server's values rather than duplicating hidden constants.

| ID | Resource | Proposed wave-one limit | Limit behavior |
| --- | --- | --- | --- |
| LIM-001 | Custom definitions per workspace | 50 | Further creation is rejected clearly. |
| LIM-002 | Inference steps per definition | 1 through 5 | Save and Invoke reject out-of-range definitions. |
| LIM-003 | Additional iterations | 0 through 10 | A positive value permits that many governed Repeat decisions; it never schedules repeats by itself. |
| LIM-004 | Total model attempts per run | 65 | At most five inference steps across eleven iterations plus ten Exit-decision calls while another iteration remains possible. No retries are added. |
| LIM-005 | Display or step name | 120 characters | Reject over-limit input. |
| LIM-006 | Description | 2,000 characters | Reject over-limit input. |
| LIM-007 | Step instruction | 12,000 characters | Reject over-limit input. |
| LIM-008 | Invocation or preset trigger prompt | 24,000 characters | Reject before Save/admission as applicable; preset and invocation are never combined. |
| LIM-009 | Canonical model output per attempt | 8,000 characters | Truncate once, record original length and truncation, and use that same canonical value for downstream context and evidence. This custom-loop bound preserves the absolute UTF-8 worst-case run shape beneath the finite trace budget without reducing the shared 64,000-character governed-tool formatter bound. |
| LIM-010 | Workspace startup context source | Existing 12,000-character per-source limit | Preserve source-specific truncation markers and provenance. |
| LIM-011 | Governed tool output | Existing 64,000-character formatted limit | Preserve existing explicit truncation behavior. |
| LIM-012 | Accumulated run execution deadline | 30 minutes | Persist accumulated Running/PauseRequested/CancelRequested time across transitions and restart. Paused time and process downtime do not count; approval wait does count. |
| LIM-013 | Persisted run trace | 16 MiB UTF-8 | Store immutable content blocks once and reference them from attempts. Reserve worst-case evidence capacity before every provider/tool dispatch. Never silently discard required evidence. |
| LIM-014 | Recent-run page size | 50 | Require bounded pagination for older records. |
| LIM-015 | Logical provider-request content | 256,000 characters per attempt | Fail visibly before dispatch if the deterministic assembled request exceeds the cap; no hidden semantic compaction. |
| LIM-016 | Governed tool requests | 5 per inference attempt and 30 per run | A forged or excess request is denied, traced, and audited without execution. |
| LIM-017 | Approval wait | 5 minutes per request | Timeout is a governed rejection with zero tool execution and counts toward accumulated execution time. |
| LIM-018 | Mandatory integrity-write window | 30 seconds | After an outcome may exist, required local trace/audit/checkpoint writes use a server-owned bounded token independent of caller cancellation. |
| LIM-019 | Retained run evidence per workspace | 250 run traces or 1 GiB, whichever is reached first | Never auto-prune. Admission reserves one maximum trace and fails clearly when quota is unavailable. |
| LIM-020 | Exit decision instruction | 12,000 characters | Required and bounded when continuation is enabled. A bounded saved default may remain while disabled for honest toggle-on editing, but it is not sent to a model until continuation is enabled. |
| LIM-021 | Invoking-conversation snapshot | 24,000 characters and at most 384 selected entries | Capture the newest logical messages that fit. Record selected-message truncation, represent all older omissions with one bounded aggregate manifest entry containing omitted count and aggregate original character count, and exclude the current invoking prompt so it enters model context exactly once. |

All length checks use normalized server-side values. Validation errors identify the field, actual value/count, and allowed limit without echoing secrets into logs. Tests must prove that every definition valid at the maximum component limits can complete within the aggregate trace budget; if the serialization overhead does not fit, reduce the component limits rather than admitting a run that cannot preserve required evidence.

## 5. Authoritative Validation And Save

| ID | Requirement |
| --- | --- |
| DEF-001 | Create returns a valid prompt-only seed definition with one inference step, an invocation-prompt trigger, continuation disabled, inherited context policies, zero workspace tools, and the current server-resolved directory role. |
| DEF-002 | The server validates the complete canonical definition before every Save and again before every Invoke. Frontend validation is advisory only. |
| DEF-003 | Validation rejects unknown schema versions, fields, enum values, tool assignments, and compatibility aliases. Unsupported data must never be ignored or silently defaulted. |
| DEF-004 | Validation rejects missing/duplicate/unsafe IDs, embedded-ID/filename mismatches, blank names or instructions, duplicate step IDs, out-of-range limits, and invalid Unicode/control characters where unsafe for artifacts or UI. |
| DEF-005 | Validation rejects a role ID that does not match the definition's server-owned workspace role binding. A client or edited JSON file cannot select another role. |
| DEF-006 | Validation rejects tool assignments other than the current server-owned read-only `list`, `read`, and `search` commands or outside the current directory role's server-resolved ceiling. Wave one creates no general role store; Startup supplies the one active directory role and its implemented ceiling. |
| DEF-007 | The client supplies `expectedDefinitionVersion` for Update and Delete. A stale version receives a conflict response containing the current safe metadata; the newer definition is not overwritten. |
| DEF-008 | A successful Save atomically replaces the current artifact, advances the version, recomputes the canonical hash, records UTC timestamps, and returns the server canonical form. |
| DEF-009 | A failed write removes any temporary artifact and leaves the prior definition intact. |
| DEF-010 | Save is rejected while the loop has a nonterminal run. The UI tells the user to finish or cancel the run before editing. |
| DEF-011 | Definition Create, Update, Delete, validation rejection, and version conflict are audit events. Authority/tool-assignment changes are explicitly identifiable. |
| DEF-012 | Corrupt or unsupported on-disk artifacts fail closed and remain visible as a problem. Wave one does not silently repair, migrate, or overwrite them. |
| DEF-013 | New unsaved steps use browser-local keys. On Save, the server assigns missing canonical step IDs; existing canonical step IDs are immutable. The returned canonical definition replaces temporary client keys. |
| DEF-014 | Create requires a bounded client operation ID. The same authorized operation ID plus the same canonical request returns the original definition; reuse with different content is a conflict. The operation ID grants no authority. |
| DEF-015 | Before Create/Update/Delete, the mutation-intent audit append must succeed. The atomic mutation then records `lastMutationOperationId`; only then is the outcome audit appended. Intent failure blocks mutation. Outcome-audit failure returns `committed-with-audit-warning`, never ordinary success or a false rollback claim. |
| DEF-016 | A definition artifact or deletion tombstone with an intent operation but no matching outcome remains detectably degraded on later reads. Delete retains a small tombstone with loop/version/hash/operation/timestamp so identity, non-reuse, and audit integrity survive removal of the current definition. |

A separate Validate endpoint is optional. If implemented for responsive Web feedback, it must call the same server validator as Save and cannot create a second contract.

## 6. Web Authoring And Test Surface

The canvas remains the primary mental model and editing surface, but it is a projection of the ordered definition rather than a general graph editor.

| ID | Requirement |
| --- | --- |
| UI-001 | The Loops surface lists multiple custom definitions and keeps the system default loop separate and read-only. |
| UI-002 | Create shows `Manual trigger -> Inference -> Exit` immediately. Trigger and Exit are visibly system-owned. |
| UI-003 | Users can add, remove, rename, edit, and reorder inference steps up to the server-advertised limit. |
| UI-004 | The canvas auto-lays out the derived sequence and connectors. Reordering may use direct drag-to-reorder and accessible move-earlier/move-later controls. Freeform coordinates are not persisted. |
| UI-005 | Selecting a step exposes its name, prompt-visible instruction, and resolved context policy. Selecting Exit exposes continuation enablement, decision instruction, maximum additional iterations, and its resolved context policy. The conditional connector appears only when continuation is enabled. |
| UI-006 | Loop description is clearly labeled operator documentation and never represented as model instruction. |
| UI-007 | The tool-assignment panel starts empty and shows only server-advertised `list`, `read`, and `search` commands. `append`, `write`, and `delete` are explicitly labeled unavailable in wave one rather than appearing as planned/runnable options. |
| UI-008 | The surface shows the effective role, inherited provider/model, context defaults and node overrides, fixed review/failure behavior, assigned tools, maximum model/tool-call counts, and whether workspace or conversation content may be exposed to the configured provider. It exposes no redundant free-form fixed-context field. |
| UI-009 | Dirty state, unsaved navigation, reload, stale-save conflicts, and server validation errors are explicit. Reload never silently discards edits. |
| UI-010 | Save, Delete, and Invoke eligibility comes from the server canonical state, not a frontend-only guess. |
| UI-011 | The invocation control says that a test run is real and governed. Assigned read tools may expose workspace content to the configured model, and an Exit-requested iteration may request those tools again. Permission and approval rules still apply. |
| UI-012 | Invoke shows the maximum model and tool-request counts, trigger prompt source, invoking-conversation admission, context publication destinations, and admitted read-tool assignment before the user starts the run. Mutating-tool acknowledgement is unnecessary because mutating commands are not assignable in wave one. |
| UI-013 | The editor and run inspector have keyboard-operable controls, programmatic labels, visible focus, and screen-reader status announcements for validation, approval, and lifecycle changes. |
| UI-014 | Untrusted names, instructions, input, outputs, paths, tool results, and errors render as text, never executable HTML. |

There is no generic node palette, port inspector, edge editor, edge condition, zoom/fit requirement, minimap, undo/redo system, disconnected drafting mode, or freeform graph layout in wave one.

## 7. Invocation Admission And Execution Semantics

### 7.1 Admission

Web Invoke and Resume are authenticated hub methods so the server binds approval ownership to the actual `Context.ConnectionId`; the client cannot forge an owner by sending a connection ID. A non-browser API caller must use the same authenticated hub protocol. CRUD, reads, Pause, and Cancel may remain protected REST operations.

Invoke accepts only a bounded client operation ID, loop ID, expected definition version/hash, and an invocation prompt when the admitted trigger requires one. Preset and no-prompt triggers reject an invocation prompt rather than silently mixing sources. Invoke does not accept a role, provider, model, capability set, effective tool list, permission result, context payload, graph, approval-owner ID, or run status from the client.

Before any provider request or side effect, the runtime must:

1. Authenticate and authorize the local Web/API caller.
2. Load the current artifact by safe server-composed path.
3. Strictly validate the artifact and its current directory-role binding.
4. Verify the client's expected definition version/hash.
5. Resolve the client operation ID: the same authorized ID and same canonical request returns the original run; reuse with different content conflicts.
6. Reject a second nonterminal run of the same loop.
7. Atomically acquire the custom-workspace execution gate or persist/return `workspace_execution_busy` before creating/admitting a run. The same Invoke operation ID replays that rejection; a later retry requires a new operation ID.
8. Resolve the configured provider/model and the server-owned effective command/capability maximum.
9. Capture the bounded context snapshot and source manifest described in Section 8.
10. Create a unique run ID and canonical admitted-definition snapshot.
11. Reserve the maximum run-trace capacity under both per-run and workspace quotas.
12. Durably persist the operation record and run in `Admitted` state.
13. Append the admission/authority audit event.
14. Transition durably to `Running` and only then dispatch the first inference attempt.

If any required admission persistence or audit action fails, no inference or tool execution occurs.

### 7.2 Ordered execution

| ID | Requirement |
| --- | --- |
| RUN-001 | Execute the first iteration exactly once. Start another only after a successful Exit model call returns the exact governed `Repeat` decision and the accepted-repeat count remains below `maxAdditionalIterations`. |
| RUN-002 | Within each iteration, execute inference steps once each in persisted order. |
| RUN-003 | Persist a node-attempt Started event before provider dispatch. A provider request must not begin if that event cannot be durably recorded. |
| RUN-004 | Each attempt uses a fresh provider transport context and a complete EmbodySense-owned logical request. Hidden provider history must not influence the result. |
| RUN-005 | Every inference step inherits the same admitted loop tool assignment. Node-level widening or narrowing is not part of wave one. Exit decision calls are deliberately tool-less: they receive no dynamic tool schema and cannot request workspace actions. |
| RUN-006 | Persist the canonical model output, provider/model correlation, resolved context-in/context-out policy, Completed event, and next-boundary checkpoint before starting another step, Exit decision, or iteration. |
| RUN-007 | The last inference canonical output is the server-bound iteration result regardless of model-context retention. Inference `retainForLoop` controls later same-iteration model visibility. Exit returns the final iteration result; on `Repeat`, Exit `retainForLoop` controls whether it becomes the selectable previous-iteration result, and on terminal completion it records whether the result remains in product context. Exit `publishToConversation` controls publication. Evidence retention is unconditional and separate. |
| RUN-008 | A later node or iteration receives only context explicitly selected by its context-in policy and previously retained by the producing node's context-out policy. The previous iteration result is a distinct selectable source. |
| RUN-009 | A failed attempt stops all later steps and iterations. There are no automatic retries, repair calls, fallback models, compensation, or failure branches. |
| RUN-010 | The runner independently enforces step, iteration, total-attempt, deadline, cancellation, and evidence-size limits even though Save validation should already make the definition safe. |
| RUN-011 | An unsupported or tampered state fails visibly before dispatch. The runtime never skips an unknown node, command, or event. |
| RUN-012 | Before provider dispatch, both the attempt-Started trace and its matching Started audit must be durable. After the provider returns, persist `OutcomeObserved`, append the outcome audit, then persist `CheckpointCommitted`. Only `CheckpointCommitted` is a resumable boundary. |
| RUN-013 | Immutable definition, context, instruction, input, and output content blocks are stored once per run and referenced by hash/ID from attempt records. This deduplication must preserve exact logical request reconstruction without duplicating whole prompts in every event. |
| RUN-014 | Tool calls inside an inference attempt are governed observations. Authority denial, permission denial, approval rejection, validation failure, or ordinary tool failure causes zero prohibited side effect and is returned to the model; it does not by itself terminate the inference. A failed provider attempt, integrity failure, or uncertain post-actuation outcome terminates the run. |
| RUN-015 | If continuation is disabled, Exit completes deterministically without an LLM call. If enabled, an Exit call may return only `Complete` or `Repeat`. Missing, invalid, failed, cancelled, or uncertain Exit decisions never start another iteration and become `NeedsReview` unless cancellation can be proven safely. |
| RUN-016 | Once the accepted-repeat count reaches `maxAdditionalIterations`, Exit completes deterministically after the final allowed iteration without another model call. A decision that cannot affect traversal is not requested; the ceiling is enforcement, not a synthetic `Repeat` decision. |
| RUN-017 | Conversation publication is an idempotent, checkpointed context-out effect. Output evidence must be durable before append and the publication outcome/correlation must be durable before committing the checkpoint. A definite append failure is recorded and never reported as success; an uncertain append outcome becomes `NeedsReview`. Resume never appends the same node or terminal result twice. |
| RUN-018 | Exit parsing trims surrounding whitespace and accepts only one case-insensitive ASCII token, `Complete` or `Repeat`. Markdown fences, prose, punctuation, JSON, multiple tokens, or an empty response are invalid. The raw bounded response remains evidence. |

## 8. Context, Memory, And Prompt Trust

### 8.1 Captured sources and resolved node policies

At admission, the runtime captures one bounded, immutable directory-role/context snapshot for the whole run. Resume uses that snapshot even if workspace context files later change. The UI displays the capture time and staleness implication. Product context is pinned; executable security enforcement is not. Current non-overridable harness security instructions and current server enforcement always apply and may narrow an admitted run, with the exact per-attempt governance version/content recorded in the trace.

Every model-facing Inference or enabled Exit node resolves its admitted context policy from directory-role defaults -> the definition's typed loop defaults -> node override. The server seeds the approved MVP defaults: include directory role/startup context, admitted trigger prompt, earlier retained outputs, and the previous retained iteration result; exclude invoking-conversation history unless explicitly admitted at Trigger and selected by the node; retain Inference output for later loop reasoning without publishing it; and publish the final Exit result to the invoking conversation without retaining it as future loop context. The effective typed defaults are inspectable; there is no free-form default-context text.

The runtime intersects each node selection with material actually admitted or retained. Selecting a source never manufactures it, widens authority, or bypasses bounds. `includeRoleContext = false` may omit directory-role and startup product context, but it never removes non-overridable harness governance, current enforcement, or the minimum trusted run/authority metadata required to execute and audit safely. Each provider request is assembled from EmbodySense-owned state in this order:

1. Current fixed harness governance/developer instructions captured for that attempt; current policy may narrow but never widen admitted authority.
2. When selected, explicit trusted workspace sources loaded through the existing context provider: contextual role instructions from the nearest `AGENTS.md` and `.agent/ROLE.md`, followed by durable agent identity from `.agent/SOUL.md` and `.agent/PERSONALITY.md`.
3. When selected, bounded contextual state from `.agent/CONTEXT.md`, `.agent/MEMORY.md`, and `.agent/models.json`, with source labels and truncation.
4. Trusted run metadata describing loop, run, role, iteration, step, and effective tools. Metadata informs the model but cannot alter server enforcement.
5. The current step's authored instruction.
6. The admitted trigger prompt (invocation or preset) when selected, as untrusted contextual data.
7. The logical invoking-conversation history when Trigger admitted it and this node selects it, as untrusted contextual data. Provider-thread history is never substituted for this product context.
8. Ordered canonical outputs from earlier nodes that were retained for loop reasoning and are selected by this node, as untrusted contextual data.
9. For an Exit-requested later iteration, the retained previous-iteration result when selected, as untrusted contextual data.

No unrelated conversation transcript is attached. A manual trigger may explicitly admit the server-bound invoking user session's bounded logical conversation history; command-style, preset, and future scheduled runs may exclude it. The current invoking prompt is excluded from that history snapshot and admitted once through the trigger-prompt source. Trigger admission does not append the prompt again or write durable memory.

### 8.2 Trust requirements

| ID | Requirement |
| --- | --- |
| CTX-001 | Fixed harness governance outranks role and loop instructions and cannot be overridden by definition or invocation data. |
| CTX-002 | Only explicitly designated role instruction and durable agent-identity files, plus the authored step instruction, enter an instruction channel. Description, names, layout, input, memory, arbitrary workspace files, prior model output, tool output, and external content do not silently become trusted instructions. |
| CTX-003 | Contextual data may inform reasoning but cannot add a tool, command, role, permission, provider, model, review policy, or actuator. Enforcement uses the admitted server snapshot regardless of model claims. |
| CTX-004 | The context manifest records source type, source identity/path, provenance class, content hash, captured content used by the model, order, character count, truncation, omission, and capture timestamp. |
| CTX-005 | `.agent/MEMORY.md` remains the primary durable local memory registry and is loaded through the existing bounded startup path when present. Wave one performs no automatic memory writeback. |
| CTX-006 | Wave one cannot mutate a memory file because mutating commands are not assignable. A future memory mutation must use an explicitly assigned governed command and normal optimistic-concurrency, permission, approval, trace, and audit evaluation; inference alone never grants memory-write authority. |
| CTX-007 | No context crosses runs or conversations implicitly. Admission pins the server-owned invoking conversation ID plus captured history version when one exists. Trigger may import its bounded snapshot, and context-out may explicitly publish a canonical output only to that same conversation. Import and publication are independent. If no destination exists, publication is a recorded omission; a definitely stale/missing destination is a recorded failure; an uncertain append is `NeedsReview`. Evidence remains in every case. Provider-thread state and durable memory remain separate. |
| CTX-008 | Provider/model identity is captured per attempt. No provider-specific hidden state is accepted as evidence or continuation state. |
| CTX-009 | The configured provider/model is pinned as part of admission. If it is no longer available on Resume, Resume fails visibly and the run remains Paused; no fallback or silent model switch occurs. |
| CTX-010 | There is no free-form "additional fixed context" field. Authored prompt text belongs to a trigger preset, an inference instruction, or an Exit decision instruction; all other model input comes from typed, inspectable sources. |
| CTX-011 | Context-out controls product context flow only. Every bounded output, including evidence-only output, remains in the local run trace; conversation publication and durable memory writeback are separate decisions. |

## 9. Governance And Tool Authority

### 9.1 Authority model

The client authors command assignments, not raw capability IDs.

- Structural capabilities for context loading, provider inference, approval routing, and audit are derived by the server.
- Optional workspace command assignments come from a server-owned implemented catalog. Wave one exposes only governed `list`, `read`, and `search`; mutating commands are deferred until they have explicit optimistic target preconditions and before/after evidence.
- A new loop has no workspace command assignments.
- A prompt-only loop is valid.
- All inference steps inherit the complete admitted loop assignment.
- The server maps admitted command assignments to runtime capability IDs. Client-supplied capability or effective-authority data is ignored and rejected where present.
- The directory role supplies a server-owned authority ceiling. In wave one the Startup-owned current role plus implemented read-command catalog is the ceiling; no general role store is added. A custom loop may narrow that ceiling but never expand it.
- The admitted command assignment is an immutable maximum, not a promise that authority cannot later be revoked. Resume and every tool request re-evaluate the current role ceiling, implemented catalog, and current permission policy. Narrowing applies immediately; later widening never expands the admitted run.

### 9.2 Required actuation path

Every model-requested workspace operation follows this order:

```text
admitted assignment/current-ceiling check + trace/audit
  -> canonical path and permission-policy check + trace/audit
  -> connection-scoped approval request/decision + trace/audit when required
  -> execution-intent trace/audit
  -> workspace read actuator
  -> outcome trace/audit and visible observation
```

| ID | Requirement |
| --- | --- |
| GOV-001 | `embodysense.command` remains the only model-facing workspace actuator. It is not exposed when the admitted assignment is empty. |
| GOV-002 | A forged request for an unassigned command is rejected before permission evaluation or side effect and is audited. |
| GOV-003 | No loop node, controller, Web service, or custom runner may call `LocalWorkspaceClient` or another raw actuator directly. |
| GOV-004 | Canonicalize every target server-side, reject workspace escape and same-prefix siblings, reject any existing reparse-point segment, and distinguish create/modify/append/delete/read/list policy operations. |
| GOV-005 | Missing, invalid, unsupported, or unmatched permission policy requires human approval; it never defaults to allow. The most-specific rule wins and explicit deny wins equal-specificity conflicts. |
| GOV-006 | Approval is a blocking pre-actuation gate scoped to the authenticated hub connection that invoked or explicitly resumed the run. Rejection, timeout, disconnect, or cancellation causes zero tool execution. A client-supplied connection ID is never trusted. |
| GOV-007 | Existing bounded reads/searches, binary detection, search-file bounds, reparse skipping, output truncation, and cancellation behavior remain intact. |
| GOV-008 | Native Codex app-server shell, file-change, patch, permission, MCP elicitation, browser/web, subagent, and user-input requests remain unavailable or declined and audited. They cannot be used as alternate actuator paths. |
| GOV-009 | Run ID, loop ID, role ID, definition hash, iteration, step ID, attempt number, and request correlation flow through inference, ToolBroker, permission, approval, execution, and audit events. |
| GOV-010 | A pre-actuation audit failure prevents the side effect. A post-actuation audit failure produces a visible uncertain/degraded integrity outcome and is never automatically retried. |
| GOV-011 | Trace/audit records contain both the admitted command maximum and the current role/catalog/permission-policy hashes actually evaluated. A policy or role-ceiling change may revoke a pending/read request but cannot grant a command absent from admission. |
| GOV-012 | If the approval-owning connection disconnects, the pending approval is rejected as `owner_disconnected`; reconnect does not revive it. If no approval owner exists for a later request, that request is denied. The governed denial is returned to the model and the run may continue. |
| GOV-013 | Approval timeout or explicit rejection is an ordinary governed tool observation. Process restart with an open provider attempt still follows the NeedsReview recovery rule because the complete inference outcome is unknown even when no read actuator ran. |

## 10. Web And Artifact Security

| ID | Requirement |
| --- | --- |
| SEC-001 | Every loop list/read/create/update/delete/run-detail/trace-delete/pause/cancel route and every Invoke/Resume hub method uses the existing local-session authorization policy. No loop mutation or invocation surface is anonymous. |
| SEC-002 | Preserve loopback-only host binding, approved host validation, same-port Origin validation, session-token authentication, and authenticated real-time connections. |
| SEC-003 | Preserve the Content Security Policy, `X-Content-Type-Options: nosniff`, and no-referrer behavior. |
| SEC-004 | The anonymous session-token bootstrap is not described as hardened remote authentication. This MVP neither weakens nor expands it to remote clients. |
| SEC-005 | The Web session token is never included in prompts, definitions, run traces, audit metadata, actor IDs, request summaries, or persisted browser state beyond the existing session mechanism. |
| SEC-006 | Loop and run IDs are server-generated; persisted step IDs are server-canonical. All IDs and operation IDs are normalized, length-bounded, strict-format, filename-safe where applicable, and never used to compose paths without the existing containment helper. |
| SEC-007 | Every loaded artifact is untrusted input and is strictly revalidated before use. Client claims about paths, role, authority, status, versions, or hashes are never accepted without server verification. |
| SEC-008 | User-controlled text is rendered with text-safe DOM operations. Content cannot inject HTML, script, event handlers, URLs, or CSS into the authoring or trace UI. |
| SEC-009 | API and audit errors use stable classes and safe details. Stack traces, environment data, provider credentials, session secrets, raw prompts, raw responses, and raw context documents do not enter the audit log. |
| SEC-010 | Request size, definition size, execution count, duration, output, trace, and page-size limits are enforced server-side to prevent accidental or hostile resource exhaustion. |
| SEC-011 | Sensitive traces remain under the user-owned `.agent` tree, which the repository already ignores. The harness inherits operating-system filesystem access controls, serves trace content only through authorized local endpoints, and never uploads/syncs it automatically. Wave one makes no at-rest encryption claim, and the UI labels the retention risk honestly. |

## 11. Lifecycle, Checkpoints, And Crash Recovery

### 11.1 Exact run states

```text
Admitted -> Running
Admitted -> Cancelled | Failed
Running -> PauseRequested -> Paused -> Running
Running | PauseRequested | Paused -> CancelRequested -> Cancelled
Running | PauseRequested -> Completed | Failed | NeedsReview
CancelRequested -> Cancelled | NeedsReview

Recovery-only transitions after the prior execution-owning process is gone:
Admitted -> Paused | Failed | NeedsReview
Running | PauseRequested -> Paused | NeedsReview
CancelRequested -> Cancelled | NeedsReview
```

Terminal states are `Completed`, `Failed`, `Cancelled`, and `NeedsReview`. Only `Paused` may resume. `NeedsReview` is terminal and never resumes the uncertain attempt; the user inspects the evidence and deliberately starts a new run if desired.

### 11.2 Boundary semantics

| ID | Requirement |
| --- | --- |
| LIFE-001 | Persist a checkpoint after each completed inference step, Exit decision, and repeat boundary. It contains admitted identity/hash, trigger/conversation binding, context-snapshot reference, resolved context-in/out policy, included and omitted source references/reasons, retained-versus-evidence-only outputs, previous-iteration result, pending Exit decision, accepted-repeat count, conversation-publication correlation, next step/Exit, and lifecycle version. |
| LIFE-002 | Pause is cooperative. `PauseRequested` is visible immediately, but the currently executing inference/tool request is not claimed to have stopped. No new inference starts after the next proved safe boundary. |
| LIFE-003 | A pause request that races with the final completed inference may end as `Completed`; the UI and audit show the ordering. |
| LIFE-004 | Cancellation propagates to provider, approval wait, tool execution, and traversal where supported. No later step starts after `CancelRequested`. Once an outcome may exist, mandatory trace/audit/checkpoint writes use the independent bounded integrity token from LIM-018 and are not cancelled by the caller token. |
| LIFE-005 | Cancellation becomes `Cancelled` only when the runtime can prove the last operation's outcome. If a provider or side effect may have completed without durable terminal evidence, the run becomes `NeedsReview`. |
| LIFE-006 | Resume is an explicit authenticated action. It uses the admitted definition, authority, and context snapshot, not current edited files or active chat context. There is no background worker or automatic continuation. |
| LIFE-007 | A Started node attempt without an integrity-complete `CheckpointCommitted` boundary after runtime restart becomes `NeedsReview` and is never automatically dispatched again, even if an output was observed. |
| LIFE-008 | Recovery is exact: `Admitted` with complete admission and no dispatch becomes Paused; `Running`/`PauseRequested` with an integrity-complete checkpoint and no open attempt becomes Paused; `CancelRequested` with no uncertain operation becomes Cancelled; Paused remains Paused; incomplete admission/audit becomes Failed or NeedsReview. Recovery records an explicit reason and never resumes automatically. |
| LIFE-009 | Durable expected-state/version transitions reject duplicate Invoke, Resume, Pause, Cancel, and terminal races deterministically. An in-memory semaphore alone is not the persistence contract. |
| LIFE-010 | Wave one supports exactly one EmbodySense server process per workspace. A conflicting process fails closed for custom-loop hosting; no cross-process inspection, read coordination, distributed lease, or failover behavior is promised. |
| LIFE-011 | At most one nonterminal run exists per loop and at most one custom loop actively executes in the workspace process. Before `Admitted -> Running` or `Paused -> Running`, atomically acquire the gate. If unavailable, Invoke returns the idempotent `workspace_execution_busy` rejection and Resume remains Paused; neither request queues, starts deadline accounting, mutates lifecycle, nor rebinds approval ownership. Custom runs use isolated run-scoped provider transport, ToolBroker, approval owner, and cancellation state rather than the default conversation loop's objects. Paused runs release the custom gate; ordinary chat behavior/availability is not globally serialized behind it. |
| LIFE-012 | Update and Delete reject while the loop is `Admitted`, `Running`, `PauseRequested`, `Paused`, or `CancelRequested`. The user may cancel, then edit. |
| LIFE-013 | Accumulated execution time is persisted whenever lifecycle state changes and at checkpoints. Approval wait counts; safely Paused time and process downtime do not. Deadline cancellation follows the same proven-versus-uncertain classification as an explicit Cancel. |

## 12. Run Trace, Audit Log, And Observability

### 12.1 Two different records

Run trace and audit log serve different purposes and must not be conflated.

**Sensitive local run trace**

The authorized, user-visible run trace stores the exact bounded product state needed to understand and reconstruct the logical provider requests:

- Canonical admitted definition and content hash.
- Full bounded invocation input.
- Exact current governance-instruction block used by each attempt, including its runtime version/hash.
- Exact bounded role/context/memory snapshot and source manifest.
- Effective command/tool schema snapshot.
- Ordered step instructions and exact canonical prior outputs used for every attempt.
- Provider/model/request correlations and visible canonical outputs.
- Checkpoints, lifecycle transitions, each bounded tool request, server-resolved target, authority/permission/approval decisions, exact canonical tool result returned to the model, failures, and final result.

The trace is explicitly sensitive local data. It is read only through authorized local endpoints or direct user-owned workspace access. It never contains the Web session token, provider-private reasoning, or harness credentials.

Trace storage is bounded by LIM-019 and never pruned automatically. The production maximum-bounded-shape runner/codec fixture includes 30 allowed governed requests plus the visible 31st over-limit denial and encodes to 15,246,408 bytes, which leaves 482,232 bytes beneath the 15 MiB reservation target and 1,530,808 bytes beneath the 16 MiB hard cap while retaining every required event and exact bounded payload. A terminal trace without an integrity warning continues accounting its reserved 8,192-byte warning slot, for 15,254,600 accounted bytes and 474,040 bytes of remaining 15 MiB headroom at that maximum shape. The Web UI shows current count/bytes and reserved capacity. An authenticated user may explicitly delete a terminal run's sensitive content using its expected trace hash and a confirmation operation ID. Deletion retains a small immutable tombstone containing run/loop identity, terminal status, definition/trace hashes, timestamps, deletion actor/surface, and audit correlation; it never rewrites the historical audit log.

**Append-only audit log**

The audit log stores structured metadata and correlations, not raw instruction, input, context, file content, tool output, or model output. Hashes, lengths, safe classifications, command names, resolved safe targets where already allowed by existing policy, decisions, timestamps, and IDs are appropriate.

### 12.2 Required trace and audit events

| ID | Event family | Required events |
| --- | --- | --- |
| AUD-001 | Definition | Create intent/outcome, Update intent/outcome, Delete intent/outcome, validation rejection, stale conflict, and authority-assignment change. |
| AUD-002 | Admission | Invoke requested, idempotently replayed, admitted, or rejected; operation ID; actor/surface; approval-owning connection correlation; role; definition version/hash; and effective commands. |
| AUD-003 | Lifecycle | Run started, pause requested, paused, resumed, cancel requested, cancelled, failed, needs-review, and completed. |
| AUD-004 | Inference | Attempt started/completed/failed/cancelled/uncertain with run, iteration, step, attempt, provider/model, request correlation, duration, input/output lengths/hashes, and safe error class. |
| AUD-005 | Tool governance | Authority evaluation, permission decision and matched rule, approval request/decision, execution result, side-effect summary, and integrity warning under one correlation chain. |
| AUD-006 | Recovery | Runtime interruption classification, checkpoint selected, stale-owner detection, recovery to Paused, and uncertainty classification. |

Every run-trace event has a stable sequence number, UTC timestamp, event ID, applicable attempt-local fields, and safe outcome. Its containing durable run record supplies run, loop, and surface identity; the immutable admitted snapshot supplies definition version/hash and role. Ordering must remain readable after restart.

### 12.3 Integrity behavior

- Admission trace and audit writes must both succeed before provider dispatch.
- Attempt Started trace and matching Started audit must both succeed before that attempt dispatches.
- After an outcome may exist, the runtime uses the independent bounded integrity token to persist OutcomeObserved trace, append outcome audit, and then persist CheckpointCommitted. Only the final checkpoint is resumable.
- Failure anywhere after outcome observation and before CheckpointCommitted prevents the next attempt and becomes Failed or NeedsReview according to whether provider/read outcome integrity is certain.
- A run is not reported `Completed` until its terminal trace is durable.
- If a terminal audit append fails after the terminal trace is durable, preserve the truthful lifecycle outcome but set and visibly surface an audit-integrity warning. Do not pretend the audit is complete or roll back history.
- If a tool may have changed the environment but its outcome cannot be durably traced/audited, classify the run `NeedsReview` and never retry automatically.

### 12.4 Web observability

| ID | Requirement |
| --- | --- |
| OBS-001 | Show recent runs and a durable run-detail page that remains usable after process restart and after definition deletion. |
| OBS-002 | During execution, show run/loop/role identity, admitted version/hash, current status, iteration, step, attempt, elapsed time, deadline, effective tools, and pending approval. |
| OBS-003 | Distinguish `PauseRequested` from `Paused` and `CancelRequested` from `Cancelled`; do not infer lifecycle solely from transcript text. |
| OBS-004 | Show ordered node outputs and their retained/evidence-only/publication disposition, final result, Complete/Repeat/ceiling/invalid Exit outcomes, tool request/result and side-effect summary, permission/approval outcome, failure class, and audit-integrity warnings. |
| OBS-005 | Provide an expandable context/authority view showing each attempt's resolved policy, included/omitted sources and reasons, source order, captured content, hashes, truncation, memory presence, previous-iteration reference, provider/model, tool assignments, admitted context timestamp, and conversation publication correlation. |
| OBS-006 | Never claim to expose hidden chain-of-thought or provider-private reasoning. Visible diagnostics describe logical context, requests, decisions, and outcomes. |
| OBS-007 | Node-level status updates are required; token-by-token model streaming is not. Polling or existing authenticated real-time projection may satisfy live updates. |
| OBS-008 | Show workspace trace quota, per-run size, sensitive-data warning, and explicit terminal-trace deletion/tombstone outcome. No automatic retention action is hidden from the user. |
| OBS-009 | Show provider-reported token/usage/cost data when available and label it unavailable when not. Never fabricate cost precision; always show the deterministic maximum inference/tool-request counts before Invoke. |

## 13. Persistence, Concurrency, And Deletion

| ID | Requirement |
| --- | --- |
| PER-001 | Definitions and run artifacts remain local under established `.agent/loops` paths and use existing safe artifact-path composition. |
| PER-002 | Definition writes use temporary-file replacement and optimistic expected-version checks. |
| PER-003 | Invocation stores a canonical run-owned definition snapshot before execution. Direct artifact edits after admission cannot alter the active run. |
| PER-004 | Run records use durable lifecycle versions/compare-and-swap or an equivalent conditional transition so concurrent controllers cannot both own a Resume or terminal transition. |
| PER-005 | Create and Invoke persist bounded client operation IDs before mutation/admission: same key plus same canonical authorized request returns the original result; same key plus different content conflicts. Save/Delete use expected definition versions; lifecycle commands use expected lifecycle versions, producing deterministic replay/conflict behavior. Operation IDs never bypass authentication or authorization. |
| PER-006 | Delete requires the expected definition version, is blocked by a nonterminal run, removes the current runnable definition, retains a definition tombstone and all historical traces/admitted snapshots, and writes intent/outcome audit events. |
| PER-007 | Deleted loop IDs are never reused. Historical run detail remains addressable by safe run identity even when the current definition no longer exists. |
| PER-008 | Wave one adds no immutable definition-revision directory, pointer reconciliation journal, migration framework for experimental schemas, or automatic corrupt-artifact repair. |
| PER-009 | A persistence failure is surfaced honestly. A provider response alone never makes a run successful. |
| PER-010 | Terminal trace deletion is a separate authenticated, confirmed, expected-hash, idempotent operation. Intent audit must succeed before deletion; the store then atomically replaces trace content with a tombstone and appends outcome audit. Intent failure leaves content intact. Outcome failure leaves the deletion committed and marks the tombstone `committed-with-audit-warning`. Same operation ID/request replays the original result; reuse with different content conflicts. Audit contains metadata, never deleted content. |
| PER-011 | No new run is admitted unless the store can reserve the maximum per-run trace allowance under both per-run and workspace quotas. Reservation release/commit is deterministic across terminalization and restart. |

## 14. Failure Semantics

| ID | Requirement |
| --- | --- |
| FAIL-001 | Distinguish validation rejection, admission conflict, authority denial, permission denial, approval rejection, provider failure, tool failure, persistence failure, audit-integrity failure, cancellation, timeout, and uncertain outcome. |
| FAIL-002 | Safe user-facing detail explains the failure and the last proved checkpoint without leaking stack traces or secrets. Full local diagnostic detail remains subject to existing logging policy. |
| FAIL-003 | A failed inference attempt prevents every later step and repeat iteration. An ordinary governed tool denial/rejection/validation failure is an observation inside that inference and does not itself end the run; post-actuation integrity uncertainty does. |
| FAIL-004 | No automatic retry exists in wave one, including after provider timeout, cancellation, persistence error, audit error, or restart. |
| FAIL-005 | No fallback provider, repair inference, failure branch, compensation, or guessed success exists in wave one. |
| FAIL-006 | A failed/needs-review run retains all prior successful outputs and governance evidence so a human can decide what to do next. |

## 15. Architecture And Implementation Constraints

| ID | Requirement |
| --- | --- |
| ARCH-001 | `Core.Application` owns custom-loop validation/orchestration ports and execution. `Core.Persistence` implements artifacts; `Core.Clients` implements provider/actuator adapters; `Core.Startup` composes and exposes the surface-facing facade. |
| ARCH-002 | Web production code references only `Core.Startup` among Core projects. Controllers and JavaScript project the shared contract and contain no independent runner, permission engine, or artifact writer. |
| ARCH-003 | Custom loops use a run-scoped inference/tool composition bound to the admitted custom loop. They must not reuse the default conversation loop's permanently bound `ToolBroker`. |
| ARCH-004 | The default conversation loop continues through `DefaultConversationLoopRunner` and `AgentRuntime.RunTurnAsync`; no separate CLI model-turn path, `Core.Application.Harness`, or `AgentHarnessSession` is reintroduced. |
| ARCH-005 | The ordered custom-loop schema should not force the default loop or Common layer to adopt the experimental general graph model. Reuse existing vocabulary only where the semantics match. |
| ARCH-006 | No general scheduler, event bus, policy engine, capability registry for unimplemented systems, evidence framework, graph interpreter, lease framework, or compatibility layer is introduced for hypothetical consumers. |
| ARCH-007 | Tests use public behavior boundaries. No reflection/private-member access, friend assembly, or frontend private-test exports are added. |
| ARCH-008 | README status and `docs/AGENT_LOOP.drawio` change in the same batch as runtime behavior and never claim deferred features are live. |

## 16. Acceptance Matrix

### 16.1 Authoring and persistence

- [ ] Create seeds a valid one-step, invocation-triggered, continuation-disabled loop with inherited node context, zero tools, and the server-resolved role.
- [ ] List/select supports multiple custom definitions without making the default loop editable.
- [ ] Add, remove, rename, edit, and reorder steps within hard limits.
- [ ] Derived Trigger, Exit, forward connectors, and repeat connector render correctly without persisted graph edges.
- [ ] Save/reload preserves canonical semantic order, stable step IDs, exact instructions, trigger policy, node context policies, tool assignments, and Exit continuation policy.
- [ ] Unknown fields/schema, oversize content, bad IDs, duplicate steps, bad role, unknown tools, tampered filename/content, and corrupt artifacts fail closed.
- [ ] Stale Update and Delete never overwrite newer state.
- [ ] Definition audit records include safe mutation and authority-change metadata.
- [ ] Mutation-intent audit failure blocks Create/Update/Delete; outcome-audit failure returns a durable committed-with-warning result detectable after restart.
- [ ] Duplicate Create operation IDs replay the original result; reuse with different content conflicts.
- [ ] Deleting a definition retains and can display historical run traces.

### 16.2 Invocation and context

- [ ] Invocation input and admitted run record persist before provider dispatch.
- [ ] Duplicate Invoke operation IDs replay the original run even if it already completed; reuse with different input/version conflicts.
- [ ] One through five steps execute in exact authored order.
- [ ] Continuation disabled produces one iteration and no Exit model call.
- [ ] `Complete` produces no repeat; `Repeat` produces exactly one additional iteration while below the ceiling; a configured ceiling alone never creates another iteration.
- [ ] Invalid, failed, cancelled, missing, or uncertain Exit decisions never repeat and are surfaced as `NeedsReview` when the terminal outcome cannot be proven.
- [ ] The final inference canonical output is the explicit run result regardless of later-model-context retention, and Exit context-out controls whether it is retained in terminal product context and/or published to the invoking conversation.
- [ ] Invocation, preset, and no-prompt trigger sources are distinct and enforced. Invoking conversation history is attached only when Trigger admits it and the model-facing node selects it.
- [ ] Context-in never receives a source excluded at Trigger or discarded by an earlier context-out policy; evidence remains inspectable in every case.
- [ ] Startup context is captured once, source-labeled, bounded, and identical on resume after files change.
- [ ] The actual provider/model, exact logical input blocks, and truncation/omission metadata are inspectable.
- [ ] Provider transport history is not required to reconstruct an attempt.
- [ ] Hostile invocation/prior-output/tool/workspace text cannot add authority or change an instruction source classification.
- [ ] Maximum valid inputs, outputs, context, and tool observations fit the reserved trace budget; over-budget requests fail before dispatch rather than losing evidence.

### 16.3 Governance and security

- [ ] A no-tool loop does not advertise `embodysense.command`.
- [ ] A forged unassigned command fails before permission evaluation and side effect and is audited.
- [ ] An assigned command is still constrained by canonical paths, reparse checks, directory policy, and approval.
- [ ] Explicit deny and approval rejection produce zero side effects.
- [ ] Approval-required activity is visibly pending and only the owning/resuming browser connection can answer it.
- [ ] Invoke/Resume hub binding cannot be forged through a client-supplied connection ID.
- [ ] Approval timeout, owner disconnect, reconnect, and explicit cancellation all produce a recorded zero-execution rejection; reconnect never revives the old approval.
- [ ] Assigned allowed activity succeeds through ToolBroker and shares the run correlation chain.
- [ ] `append`, `write`, and `delete` cannot be assigned; forged requests for them fail before permission evaluation or actuation and are audited.
- [ ] Native app-server command/file/patch/permission/MCP/browser/subagent/user-input routes remain declined and audited.
- [ ] Missing/invalid local-session token, disallowed host, bad Origin, and unauthenticated real-time connection cannot read, mutate, or invoke loops.
- [ ] XSS payloads remain inert text.
- [ ] Session token, credentials, raw context, prompts, and outputs are absent from audit metadata.
- [ ] Path traversal, same-prefix escape, unsafe IDs, reparse escape, malformed JSON, oversized payloads, and unknown fields fail closed.
- [ ] Narrowing the current role ceiling or permission policy applies to a paused/admitted run immediately; later widening never expands its admitted command maximum.

### 16.4 Lifecycle and recovery

- [ ] A second nonterminal invocation of the same loop is rejected deterministically.
- [ ] Invoke/Resume for another custom loop returns `workspace_execution_busy` before admission, state mutation, deadline accounting, or approval-owner rebinding; no hidden queue is created. Invoke rejection is idempotently replayed and Resume remains Paused.
- [ ] Pause requested during a controlled blocked attempt is visible immediately and becomes Paused only at the next proved checkpoint.
- [ ] Resume after a new runtime instance starts at the next step without replaying a completed attempt and uses the admitted context/definition.
- [ ] Cancel before dispatch and at each safe boundary becomes terminal and prevents later attempts.
- [ ] Cancellation during uncertain provider/tool activity becomes NeedsReview rather than guessed cancellation/success.
- [ ] Crash after attempt Started but before terminal evidence becomes NeedsReview and never automatically re-executes.
- [ ] Crash with no open attempt and a complete checkpoint recovers to Paused, not Running.
- [ ] Admitted/no-dispatch, Running/complete-checkpoint, CancelRequested, and incomplete-integrity recovery cases follow the exact recovery table rather than one generic stale-run rule.
- [ ] Concurrent Resume/Pause/Cancel/terminal requests have one durable winner.
- [ ] Editing or deleting a loop with a nonterminal run is rejected clearly.
- [ ] Paused time is excluded from the run execution deadline.

### 16.5 Evidence, failure, and integrity

- [ ] Timeline sequence IDs and UTC timestamps remain ordered and readable after restart.
- [ ] Definition/version/hash, role, context snapshot, effective tools, node outputs, provider correlation, permission/approval decisions, tool side-effect summaries, failures, and result remain inspectable.
- [ ] Run trace contains exact bounded contextual content; audit contains correlations and safe metadata only.
- [ ] For every read tool call, the trace retains the exact bounded request, server-resolved target, governance decisions, and canonical result supplied to the model; audit retains metadata only.
- [ ] Attempt dispatch requires both Started trace and Started audit; only a post-outcome `CheckpointCommitted` boundary is resumable.
- [ ] Caller cancellation cannot cancel mandatory post-outcome integrity writes; a bounded integrity-write failure becomes visible Failed/NeedsReview.
- [ ] Persistence failure before admission/attempt prevents dispatch.
- [ ] Persistence failure after an output prevents the next attempt and is surfaced honestly.
- [ ] Audit failure before an actuator prevents the side effect.
- [ ] Post-side-effect audit failure creates a visible integrity warning/NeedsReview path and no retry.
- [ ] A failed attempt stops all later steps and iterations and retains earlier evidence.
- [ ] The UI distinguishes request states from reached states and never fabricates private reasoning.
- [ ] Trace quota is visible, admission reserves capacity, and no automatic pruning occurs. Trace-delete intent-audit failure preserves content; outcome-audit failure leaves an idempotently replayable committed-with-warning tombstone.

### 16.6 Repository regression gates

- [ ] Default conversation behavior and authority remain unchanged.
- [ ] Custom loops use isolated run-scoped provider/tool/approval state and do not place ordinary chat behind the custom execution gate.
- [ ] Architecture and public-test-boundary guards remain green.
- [ ] Public-boundary Common/Application/Persistence/Startup/Web tests cover the requirements above.
- [ ] Frontend tests exercise DOM-visible behavior without private test exports.
- [ ] A real-browser journey covers create, reorder, save, reload, invoke, repeat, approval/denial, pause, restart-resume, failure, inspect, and delete.
- [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1` passes.
- [ ] Every production assembly remains above the repository's 90% line-coverage floor.

## 17. Explicitly Deferred Or Excluded

The following are not wave-one implementation requirements:

- Command/chat `/loop-id`, model-facing, cron, schedule, webhook, file-watch, wake, event, or background invocation.
- Multiple triggers or trigger routing.
- Decision, judge, scanner, context-injection, workspace-action/script, human-feedback, wait, observation, daemon, repair, compaction, memory-operation, delegated-loop, subloop, and hook nodes.
- Dedicated custom scripts or delegated loops, despite earlier experimental-branch discussion that treated them as potential merge blockers.
- Mutating workspace command assignments (`append`, `write`, `delete`) until the tool contract carries optimistic target preconditions, immediate conflict detection, and before/after evidence that protects concurrent user changes.
- Branches, fan-out, convergence, joins, arbitrary backedges, failure edges, ordinary cycles, or arbitrary repeat targets.
- Typed ports, bindings, transforms, JSON Schema, JSON Pointer, structured-output routing, or generic edge conditions.
- A general node catalog/schema-driven form engine. The server may expose only the small implemented command catalog and limit values needed by this UI.
- Freeform node coordinates, arbitrary rewiring, disconnected persisted drafts, generic ports, edge inspectors, minimap, or complex canvas controls.
- Node-level role, model, tool, memory, review, failure, trust, sensitivity, lifetime, compaction, or durable writeback overrides. The closed Inference/Exit context-in and context-out policy is included in wave one.
- Invoke-time provider/model/role/tool overrides.
- Automatic memory writeback or a new memory engine.
- Token-by-token Web streaming.
- Retry, repair, compensation, backoff, fallback models, failure thresholds, or failure branches.
- Parallel inference, concurrent same-loop runs, nested runs, and distributed/multi-process execution ownership.
- Immutable current-definition revision history, authoring leases, admission lease frameworks, reconciliation journals, schema migration for experimental artifacts, and automatic repair.
- Replay execution, replay planning, comparison, evaluation, and health dashboards. Replay-quality trace capture is included now so those capabilities have honest source material later.
- Automatic background resume, scheduler-driven continuation, or long-duration wait delivery.
- Model-facing loop creation or invocation.
- Compatibility DTOs, aliases, endpoints, enum values, and UI controls for unreleased experimental schemas.

## 18. Implementation Batches And Merge Gates

The current branch was assembled in the following reviewable batches. The full vertical slice was expected to exceed 5,000 changed lines, so each batch had to remain independently reviewable without pretending that an authoring-only batch was a runnable MVP.

This section records the historical implementation sequencing used to assemble the current branch. It does not describe current endpoint availability; the listed merge gates remain binding.

### Batch 1: Canonical authoring slice

- Narrow definition model, server-owned limits, role binding, strict validation, atomic versioned CRUD, mutation auditing, and run-history-preserving Delete.
- Thin Startup facade and authenticated Web authoring API.
- Derived linear canvas with accessible add/remove/edit/reorder, Trigger admission controls, per-node context policy, tool assignment, conditional Exit ceiling, Save/reload/delete, and validation/conflict UX.
- Public behavior, security, frontend, and persistence tests.
- README and diagram updates for authoring status.

No invocation endpoint is exposed in this batch.

### Batch 2A: Prompt-only ordered runner and trace

- Closed `CustomLoopDefinition`, `CustomLoopRun`, and `CustomLoopRunEvent` models; ordered prompt-only executor; admitted definition/context/model snapshot; node context resolver; conditional Exit evaluator and ceiling enforcement; content-block deduplication; trace budgets; Started/OutcomeObserved/CheckpointCommitted protocol; failure and idempotent admission.
- Public behavior tests for ordering, Trigger admission, context-in/out, conversation publication, evidence-only output, conditional Exit, bounds, context trust, evidence integrity, and failure.

### Batch 2B: Run-scoped governed read tools

- Isolated run-scoped ToolBroker/provider composition; `list/read/search` catalog and assignment; current-policy revocation; authenticated hub approval ownership; disconnect/timeout behavior; tool-call bounds; full trace/audit correlation.
- Public behavior and adversarial tests for authority, permission, approval, path safety, native-tool decline, audit ordering, and default-chat isolation.

### Batch 2C: Pause, cancel, restart, and concurrency

- Exact lifecycle transitions; durable expected-state control; checkpoints; independent integrity-write token; cancellation; recovery table; one-process/custom-execution ownership; resume with admitted context and current security enforcement.
- Public behavior tests for every lifecycle race, restart classification, idempotent control request, deadline, and uncertainty outcome.

The runner was held unavailable from the Web until 2A, 2B, and 2C passed. These batches did not introduce a generic evidence framework, scheduler, generalized lease service, graph interpreter, node dispatch registry, or public planned-node vocabulary; the narrow canonical custom-run artifact envelope implements `RUN-013` without broadening the product model.

### Batch 3: Web invocation and merge-ready vertical slice

- Authenticated hub invocation/resume plus protected lifecycle endpoints, real-run/read-exposure warning, run list/detail/timeline, context/authority inspection, pending approval, failure, integrity warning, trace quota/deletion, and restart-safe controls.
- Full frontend, integration, adversarial-security, and real-browser acceptance matrix.
- Final README and draw.io alignment.
- Full verification and explicit merge-readiness review.

No runnable custom-loop surface is merge-ready until every batch passes together. Each batch ends at a user review checkpoint before staging or committing the next one.

## 19. Decision Register

The following approved choices intentionally replace broader or conflicting historical directions and freeze the first-wave boundary unless the user corrects them again.

1. **Persist ordered steps, not a general graph.** Trigger, Exit, edges, layout, and repeat connector are derived. The canvas remains first-class and visual.
2. **Support only Manual Trigger, Raw Inference, and Exit concepts.** Context assembly, checkpoints, governance, and audit are visible runtime envelope stages, not authorable nodes.
3. **Use a maximum of five inference steps, ten additional iterations, and sixty-five model attempts per run.** Only valid Exit `Repeat` decisions consume the additional-iteration allowance; Exit is skipped once no later iteration is possible, and there are no retries.
4. **Default to zero workspace tools and expose read-only commands only.** Users may assign implemented `list/read/search` commands at loop level; every inference step inherits the admitted set. `append/write/delete` wait for optimistic mutation preconditions and before/after evidence.
5. **Include Exit-owned bounded conditional continuation in wave one.** It always returns to the first step, but only an explicit valid model `Repeat` decision may traverse it. `maxAdditionalIterations` is deliberately a ceiling, not a target.
6. **Include durable pause, resume, cancel, checkpoint, and NeedsReview behavior.** Invoke is not exposed until these exist.
7. **Block Save and Delete while a run is nonterminal.** The user cancels before editing; active-run authoring and general authoring leases are not built.
8. **Use authenticated Web hub manual invocation only.** This securely binds the approval owner. Chat, model, schedule, listener, and background invocation are deferred.
9. **Make invoking-conversation admission explicit.** Trigger may include the logical user-session history, and each model-facing node independently selects whether to read it. Provider threads remain transport only and every request is self-contained from EmbodySense-owned state.
10. **Treat test invocation as real execution.** The UI previews authority/call counts and workspace-content exposure; ToolBroker governance remains unchanged even for read-only commands.
11. **Retain exact bounded local run traces and metadata-only append audit.** The trace is sensitive local evidence; prompts/context/outputs never enter the audit log. No automatic pruning occurs; quota and explicit audited tombstone deletion are included.
12. **Support exactly one EmbodySense server process per workspace and one nonterminal run per loop.** Durable state transitions, not an in-memory guard alone, decide lifecycle races. Custom execution state remains isolated from ordinary chat.
13. **Defer dedicated action/custom-script and delegated-loop nodes.** Earlier discussion that made them pre-merge work is superseded for this deliberately reduced MVP, while inference nodes retain governed model-facing tools.
14. **Do not migrate experimental artifacts or cherry-pick the experimental runtime/UI.** Reuse live-main foundations and selectively port behaviors/tests only after this contract is approved.
15. **Treat admitted authority as a maximum, not a frozen permission grant.** Current role ceiling, implemented catalog, and permission policy may revoke authority on Resume or tool use; later widening cannot expand the run.
16. **Use the exact Started trace/audit -> dispatch -> OutcomeObserved -> outcome audit -> CheckpointCommitted protocol.** Only an integrity-complete checkpoint may resume, and cancellation never suppresses required post-outcome integrity writes.
17. **Do not provide an Additional fixed context field.** Authored text belongs to the trigger preset, inference instruction, or Exit decision instruction; typed context selectors control all other context flow.

Approval of this register authorizes planning the implementation batches. It does not itself authorize source edits, staging, commits, or merges.
