# Loop Experiment Salvage Ledger

Status: exhaustive research-branch disposition ledger for the clean first-wave rebuild.

This document prevents experimental behavior from being forgotten while also preventing it from becoming accidental MVP scope. It is not product authority or implementation authorization. `docs/OPINIONATED_PROJECT_AXIOMS.md`, the user's latest direction, and `docs/LOOP_MVP_REBUILD_CONTRACT.md` control the rebuild.

## 1. Reference Points

- Clean rebuild base: `main` at `b87dbd3` (`finalize loop/harness cutover implementation`).
- Experimental branch: `loop-feature-and-architecture-cutover` at `943ae01`.
- Independent verification commit: `22b2905`, selectively carried to the clean branch as `80c6e0b`.
- Experimental backend/runtime snapshot: `18df6e3`.
- Experimental Web authoring snapshot: `2f78be1`.
- Experimental status/planning/debt snapshot: `943ae01`.
- Earlier broad feature commits: `bd2eb21` and `5c75c65`; neither is an architecture-only cutover commit.

The cutover that was independently suitable for mainline is already represented by `b87dbd3`, which is the current `main`/`origin/main` base. The experimental loop commits are reference material, not merge candidates.

## 2. Why The Experiment Is Not The Rebuild Base

The final experiment combines:

- A general graph grammar and server-owned node catalog.
- Multiple trigger types and broad future trigger vocabulary.
- Four inference families plus scanner/context/action/human/wait/compaction/delegation behavior.
- Typed values, transforms, mappings, JSON schemas, branching, fan-out, convergence, and arbitrary bounded backedges.
- Context lifetime, trust, sensitivity, budgeting, compaction, and writeback policy.
- Immutable definition revisions, pointer management, authoring/admission/execution leases, reconciliation, and migration aliases.
- Restart-safe gates, waits, nested loop continuation, cancellation arbitration, repeat policy, and replay planning.
- A general visual graph editor and schema-driven inspector.

That breadth concentrated too much policy and too many responsibilities in a few files relative to `main`:

- `CustomLoopRunner.cs`: approximately 4,900 added lines.
- `LoopGraphDefinition.cs`: approximately 840 lines in the experiment.
- `LoopDefinitionStore.cs`: approximately 940 lines in the experiment.
- `WorkspaceLoopService.cs`: approximately 1,000 lines in the experiment.
- `loopGraphModel.js`: approximately 1,470 lines in the experiment.

These are useful architectural warning signals. The clean rebuild should mine behavior, failure cases, and tests, not recreate the same generalized systems under new names.

## 3. Disposition Vocabulary

| Disposition | Meaning |
| --- | --- |
| Reuse | The live `main` behavior or boundary already fits the approved MVP. |
| Port narrowly | Reimplement one proven behavior against the clean contract; do not cherry-pick its surrounding abstraction. |
| Rewrite | The experimental behavior is useful but its implementation shape is unsuitable. |
| Preserve seam | Record identity/evidence/authority now so the future feature can attach without schema deceit; do not implement the feature. |
| Defer | Valid later product capability, explicitly excluded from wave one. |
| Drop | Experimental compatibility, public vocabulary, or implementation machinery with no approved consumer. |

## 4. Main-Branch Foundations To Reuse

| Foundation | Disposition | Rebuild rule |
| --- | --- | --- |
| Core dependency boundaries and architecture guards | Reuse | Common owns dependency-free vocabulary; Application owns ports/orchestration; Clients and Persistence implement ports; Startup composes; Web/CLI consume Startup. |
| `DefaultConversationLoopRunner` and `AgentRuntime.RunTurnAsync` | Reuse unchanged | Ordinary chat remains the existing governed default loop. Do not route it through an unfinished generic scheduler. |
| Codex app-server inference adapter | Reuse | Provider transport remains replaceable and does not own product context. Custom attempts need run-scoped correlations and self-contained logical input. |
| `ToolBroker` | Reuse and close correlation gaps | Keep assignment/authority audit -> path/policy audit -> approval audit -> execution-intent audit -> actuator -> outcome audit. Bind it to the admitted custom loop, never the default loop. |
| `ToolPermissionService` and permission store | Reuse | Preserve canonical path, workspace containment, reparse rejection, operation mapping, most-specific rule, deny, and approval fallback behavior. |
| `LocalWorkspaceClient` | Reuse only behind broker | It is an actuator, not a governance boundary. No graph/Web direct calls. |
| Workspace startup context provider | Reuse | Capture one bounded directory-role snapshot with provenance for the run. |
| Local append-only audit log | Reuse and extend schema | Add definition/run lifecycle correlations without placing raw prompt/output/context content in audit metadata. |
| Local-session Web authentication and headers | Reuse | Every loop route remains localhost/same-origin/session-token protected. |
| Safe loop artifact paths and temporary-file replacement | Reuse | Add strict custom schema, expected-version writes, run snapshots, and lifecycle conditional transitions. |
| Current loop/run identity vocabulary | Reuse narrowly | Extend only fields consumed by the ordered MVP; do not import experimental planned-node vocabulary. |
| Runtime diagnostic source/authority visibility | Reuse principles | Custom-run detail must be at least as honest about context, omissions, capabilities, and failure. |
| Verification and >90% production coverage gates | Reuse | Behavior-first public tests, architecture guards, frontend tests, and real-browser acceptance remain mandatory. |

## 5. Experimental Node And Trigger Matrix

The research branch exposes fourteen executable authoring concepts plus additional compatibility/planned constants. Every one is classified here.

| Experimental capability | Experiment state | Disposition | Wave-one treatment |
| --- | --- | --- | --- |
| Manual Web/API trigger | Executable | Port narrowly | Exactly one derived trigger per definition; authenticated manual text invocation only. |
| Command `/loop-id` trigger | Executable | Defer; preserve seam | Record trigger/surface/requestor identity now. Do not ship chat command routing. |
| Cron trigger | Documented/planned | Defer | No scheduler, recurrence calendar, timezone, misfire, unattended authority, or job ownership. |
| Model tool-call trigger | Documented/planned | Defer | No model-facing loop invocation. |
| Wake/webhook/file/event trigger | Documented/partly modeled | Defer | No listener, authentication, dedupe, queue, dead-letter, or wake delivery system. |
| Raw inference | Executable | Port narrowly | One through five ordered text-in/text-out steps with required instruction, loop-level model/tools, per-node typed context-in/context-out, and visible output. |
| Decision inference | Executable | Defer | No structured choices, branches, or output routing. |
| Separate Judge inference | Compatibility vocabulary | Drop as distinct node | A future judging behavior can use a Decision/template rather than a duplicate family. |
| Daemon inference | Executable | Defer | No audit-only response or special background/writeback semantics. |
| Repair inference | Partly executable | Defer | First failure terminates; no repair/retry/escalation node. |
| Context injection | Executable | Defer node; reuse need | Wave one uses typed Trigger admission and Inference/Exit context policies, not a free-form context node or Additional fixed context field. |
| Output scanner | Executable | Defer | No regex/JSON/status routing, scanner rules, or implicit branching. |
| Workspace action/script | Executable through ToolBroker | Defer node; reuse governed tools | Inference may request explicitly assigned workspace commands through `embodysense.command`; there is no direct action/script node. |
| Human feedback/gate | Restart-safe continuation | Defer node | User lifecycle pause/resume is MVP; model-authored human-input checkpoints and response schemas are not. |
| Wait/delay | Executable | Defer | No timer, durable wake delivery, synchronous wait, or background continuation. |
| Force compaction | Deterministic replacement | Defer concept; drop executor | No compaction node or generalized context budgeting engine. |
| Memory operation | Compatibility/planned | Defer | Existing startup memory may be read; no automatic memory mutation node. |
| Delegated loop | Executable with parent/child continuation | Defer | No nested runs, recursive authority, child lifecycle, or parent continuation. |
| Exit | Executable | Port narrowly as derived node | Exactly one derived terminal; final step output becomes result. |
| Exit-owned repeat | Executable with broad policy | Port narrowly | A governed Exit model may request the fixed first-step target, bounded by zero through ten additional iterations; the ceiling never forces a repeat. No backoff/failure threshold/arbitrary target. |
| Ordinary loopback/recovery edge | Executable with visit bounds | Defer | No arbitrary cycles/backedges. |
| Before/after/failure hooks | Compatibility/planned | Defer attachment; drop node aliases | No hook persistence or execution and no legacy hook graph nodes. |
| Fork/Join | Explicitly removed during experiment | Drop | If branching returns, use ordinary connectors and runtime-managed convergence; do not revive special nodes. |

## 6. Authoring And UI Matrix

| Capability | Disposition | Wave-one decision |
| --- | --- | --- |
| Multiple custom definitions | Port narrowly | List, create, select, update, and delete multiple narrow definitions. |
| Valid seed loop | Port narrowly | Server creates one invocation-triggered prompt-only inference step, no tools, inherited node context, and continuation disabled. |
| Name and description | Port narrowly | Operator text; Description never silently becomes prompt instruction. |
| Experimental Notes field | Drop | Description is the one operator-documentation field in wave one. Do not persist or expose a second Notes field. |
| Edit step instruction | Port narrowly | Explicitly prompt-visible and source-labeled. |
| Add/remove/reorder inference | Port narrowly | Ordered array, one through five steps. |
| First-class canvas | Rewrite | Keep the graph visible as the primary builder, but derive Trigger/Exit/connectors from order. |
| Freeform coordinates | Defer/drop persistence | Auto-layout; no execution semantics or artifacts depend on coordinates. |
| Disconnected browser drafting | Drop from product contract | The ordered editor may have transient incomplete form fields, but Save never persists an invalid graph/draft. |
| Arbitrary connector creation/rewiring | Defer | Reorder steps instead of authoring edges. |
| Generic port and edge inspector | Drop | No wave-one consumer. |
| Generic server node catalog/schema engine | Drop | Expose only implemented workspace command descriptors and server-owned limits needed by this UI. |
| Structured editors for Decision/Human/Scanner/etc. | Defer | No public DTOs, controls, enums, or placeholder cards. |
| Role/authority preview | Port and strengthen | Read-only server-resolved role, provider/model, effective commands, fixed policies, max calls, and side-effect warning. |
| Dynamic role/model/context/policy selectors | Defer | No node or invoke-time override. |
| Loop-level tool selector | Rewrite | Assign concrete implemented `list/read/search` commands, not raw capability strings. Empty by default; mutating commands are deferred. |
| Dirty-state warning/reload | Port lesson | Protect unsaved work and make reload explicit. |
| Server validation | Port principle | One authoritative validator decides Save/Invoke; structured field errors. |
| Persisted drafts and enable/disable | Drop | Saved means valid and runnable. |
| Default loop projection | Preserve baseline | Separate/read-only; never editable as a custom loop. |
| Recent runs and timeline | Port narrowly | Required for testability and observability. |
| Replay planning/comparison UI | Defer | Capture replay-quality trace now; no replay controls. |
| Accessibility basics | Port requirement | Keyboard ordering/editing, labels, focus, and announced status. |
| Undo/redo, minimap, zoom/fit, advanced edge UX | Defer | Not acceptance requirements. |

## 7. API Surface Matrix

| Experimental/API idea | Disposition | Wave-one endpoint behavior |
| --- | --- | --- |
| List/read definitions | Port narrowly | Authenticated safe summaries and canonical detail. |
| Create/update/delete | Port narrowly | Server IDs/role, expected version, strict validation, mutation audit, historical trace retention. |
| Validate | Optional narrow port | Only if the UI needs pre-Save feedback; use exactly the Save validator. |
| Invoke | Rewrite | Authenticated hub method with client operation ID, expected version/hash, invocation prompt only when Trigger requires it, server-bound conversation identity, and server-captured approval owner; no client authority/context/graph/connection ID. |
| Recent runs/run detail | Port narrowly | Bounded summaries and sensitive authorized trace view. |
| Trace retention/deletion | Add narrow control | Bounded workspace quota, no automatic pruning, idempotent intent-audit -> atomic tombstone -> outcome-audit deletion protocol. |
| Pause/cancel/resume | Rewrite narrowly | Exact durable states and expected-state transitions. |
| Replay/plan replay | Defer | No endpoints. |
| Reconciliation/pointer repair | Defer/drop | Corruption is surfaced, not auto-repaired. |
| Dynamic catalog/policy | Drop | No general catalog or policy endpoints. |
| Nested continuation/human/wait response | Defer | No continuation endpoint families. |
| Compatibility aliases/migrations | Drop | Experimental artifacts were never a clean-branch public contract. |

Every wave-one route uses the existing `LocalSession` authorization, host/origin/session-token checks, and safe error handling.

## 8. Definition, Persistence, And Concurrency Matrix

| Capability | Disposition | Wave-one decision |
| --- | --- | --- |
| Local `.agent/loops` JSON artifacts | Reuse | Preserve local-first inspectability and safe path conventions. |
| General persisted graph | Drop for MVP | Persist ordered steps plus derived projection. |
| Safe canonical IDs | Reuse/strengthen | Loop/run IDs are server-generated; persisted step IDs and operation IDs are strict, bounded, immutable where applicable, and path-contained. |
| Strict artifact validation | Port principle | Reject unknown schema/fields/enums/commands and embedded-ID mismatch. |
| Monotonic definition version/hash | Port narrowly | Current artifact only; documented canonical SHA-256 integrity hash excludes itself and is not authentication. |
| Expected-version Save/Delete | Port narrowly | Stale writers fail rather than overwrite. |
| Atomic current-definition replacement | Reuse | Temporary write and replace, cleanup on failure. |
| Immutable definition revision directories | Defer | Run-owned admitted snapshot is enough for wave one. |
| Run-owned admitted snapshot | Port with revocation rule | Definition/context/model and command maximum are pinned. Current role ceiling/catalog/permission policy may narrow authority on Resume/use; later widening cannot expand it. |
| Allow edit during active run | Reject for reduced MVP | Save/Delete return conflict for nonterminal runs; cancel then edit. |
| Authoring lease | Drop | The conflict rule and expected-version save replace it. |
| Invocation-admission/execution lease framework | Reject generalized framework; implement narrow ownership | A single-host file lock rejects a second custom-loop host, while a nonqueueing in-process execution gate and durable per-run expected-state transitions serialize custom execution. There is no distributed inspection/failover promise. |
| Idempotent Create/Invoke | Rewrite narrowly | Persist bounded operation IDs; same request replays original, different request conflicts. |
| One nonterminal run per loop | Add explicit guard | Duplicate Invoke conflicts unless it is an idempotent replay. |
| Concurrent provider execution | Reduce without changing chat | One active custom execution gate plus isolated run-scoped provider/broker/approval state; cross-loop Invoke/Resume returns `workspace_execution_busy` before state/owner/deadline changes and never queues; do not globally serialize ordinary chat. |
| Lifecycle CAS/expected state | Rewrite narrowly | Needed for duplicate Resume/Pause/Cancel and terminal races; not a general lease system. |
| Definition delete | Rewrite | Expected version, pre/post mutation audit, block nonterminal, retain definition tombstone and run history, never reuse ID. |
| Run trace quota | Add narrow bound | 16 MiB/run, 250 traces or 1 GiB/workspace, pre-dispatch reservations, no automatic pruning. |
| Artifact migrations/aliases | Drop | No compatibility obligation for unreleased experiment schemas. |
| Pointer reconciliation journal | Defer | Surface corruption safely. |

## 9. Runtime And Context Matrix

| Capability | Disposition | Wave-one decision |
| --- | --- | --- |
| Ordered inference execution | Rewrite small | Sequential interpreter over ordered steps, not generic graph scheduling. |
| Explicit invocation envelope | Port | Loop/run/version/hash/role/surface/trigger/input/authority/context identity. |
| Explicit final result | Port narrowly | The last inference canonical output is the iteration result independent of later-model retention; the final Exit policy controls terminal conversation publication. |
| Trigger prompt | Port | Invocation input, a saved preset, or no prompt is admitted exactly as configured. |
| Prior current-iteration outputs | Port | Only ordered canonical outputs retained by their producer and selected by the receiving node. |
| Previous-iteration carry | Port narrowly | The immediately previous iteration result only when retained and selected. |
| Workspace role/startup context | Reuse and snapshot | Capture once at admission; resume does not silently reload changed files. |
| Invoking conversation history | Port narrowly | Trigger may admit the logical user-session transcript and each model-facing node independently selects it. Provider-thread history is never substituted. |
| Provider-neutral logical context | Preserve seam | Exact bounded logical request reconstructable from run trace. |
| Provider thread history | Drop as product state | Fresh/self-contained attempt transport. |
| Context source manifest | Port narrowly | Exact content used, source/order/provenance/hash/truncation/omission/timestamp. |
| Full typed values/ports/bindings | Defer | Plain canonical text only. |
| JSON schemas/pointers/templates/merge policies | Defer | No structured dataflow. |
| General trust/sensitivity/lifetime/writeback engine | Defer | Typed source selection, fixed source classification, evidence/context separation, and authority separation are still mandatory. |
| Branch/fan-out/convergence | Defer | Fixed line only. |
| General cycles | Defer | Exit repeat only. |
| Token streaming | Defer | Node progress and durable output required. |
| Shared Startup/runtime boundary | Reuse | Web projects Application orchestration through Startup. |
| Default-chat scheduler convergence | Defer | Leave default runner stable. |

## 10. Lifecycle And Recovery Matrix

| Capability | Disposition | Wave-one decision |
| --- | --- | --- |
| Started-before-dispatch evidence | Port/strengthen | Started trace and Started audit must both persist before provider dispatch. |
| Per-attempt integrity protocol | Rewrite narrowly | Started trace/audit -> dispatch -> OutcomeObserved trace -> outcome audit -> CheckpointCommitted; only the checkpoint resumes. |
| Checkpoint after inference | Port narrowly | Next step/Exit, iteration, canonical outputs, admitted snapshots, lifecycle version, and integrity-complete boundary. |
| Checkpoint at repeat boundary | Port narrowly | Never replay a proved completed iteration on resume. |
| Cooperative pause | Port | Request visible immediately; take effect before next inference. |
| Resume after restart | Port narrowly | Only Paused resumes; use admitted snapshots. |
| Cancellation propagation | Port narrowly | Cancel provider/approval/tool/traversal where possible, but never cancel bounded mandatory post-outcome integrity writes. |
| Started/outcome without committed checkpoint | Preserve lesson | NeedsReview; never automatic retry. |
| Exact recovery table | Rewrite | Admitted/no-dispatch and Running/complete-checkpoint -> Paused; safe CancelRequested -> Cancelled; open/incomplete outcome -> NeedsReview; never auto-resume. |
| Background continuation | Defer | Explicit Web/API resume only. |
| Human/wait/delegated checkpoint variants | Defer | No generalized continuation union. |
| General execution lease/reconciliation | Reject generalized framework | Use the narrow single-host lock, nonqueueing custom-execution gate, and expected-state transitions; do not add distributed ownership or reconciliation. |
| Retry/repair/compensation | Defer | First failure stops. |
| Replay execution | Defer | Inspection only. |

## 11. Governance, Security, Audit, And Observability Lessons To Carry

These are MVP requirements, not cleanup work:

1. **Run-bound authority**
   - Resolve role and structural capabilities server-side.
   - Admit only concrete `list/read/search` assignments from a server-owned implemented catalog; mutation waits for optimistic preconditions and before/after evidence.
   - Bind inference tool advertisement and ToolBroker enforcement to the same run snapshot.
   - Treat admission as a maximum. Current role/catalog/permission policy may revoke authority; later widening never expands the run.
   - Never reuse the default loop's bound broker for a custom run.

2. **One governed actuator path**
   - Preserve `assignment/authority audit -> path/policy audit -> approval audit -> execution-intent audit -> actuator -> outcome audit` behind `embodysense.command`.
   - Direct workspace client/action-node/Web actuation is prohibited.
   - Native app-server shell/file/patch/permission/MCP/browser/subagent/user-input routes remain declined/audited.

3. **Prompt authority separation**
   - Fixed harness governance and explicit role/step instructions are labeled instruction sources.
   - Invocation, memory/context data, workspace content, model output, and tool output cannot grant authority.
   - Display name, description, and layout never silently enter prompts; the wave-one definition has no Notes field.

4. **Local Web security**
   - Protect every loop route and real-time connection with existing local-session rules.
   - Invoke/Resume through the authenticated hub so approval ownership comes from the server connection context, not a client-supplied ID.
   - Approval timeout/disconnect rejects the request with zero execution; reconnect does not revive it.
   - Keep loopback host and same-port Origin validation, CSP, `nosniff`, and no-referrer.
   - Never persist or prompt the session token.
   - Render untrusted content as text.

5. **Trace versus audit**
   - Sensitive local run trace contains exact bounded invocation/context/instructions/outputs plus each tool request, resolved target, governance decisions, and canonical result returned to the model.
   - Append-only audit contains safe metadata, hashes, decisions, timestamps, and correlations only.
   - Definition control-plane and lifecycle events are audited, not only tool calls.
   - Bound retained trace count/bytes, never auto-prune, and retain an audited tombstone after explicit terminal-trace deletion.

6. **One correlation chain**
   - Carry loop, run, role, definition hash, surface, iteration, step, attempt, provider request, tool request, permission, approval, execution, and outcome IDs through every relevant trace/audit event.

7. **Fail-closed integrity**
   - Admission and matching Started trace/audit failure prevents dispatch.
   - Use `OutcomeObserved -> outcome audit -> CheckpointCommitted`; only the final boundary resumes.
   - Caller cancellation cannot suppress mandatory bounded post-outcome integrity writes.
   - Pre-actuation audit failure prevents execution; post-actuation evidence/audit uncertainty becomes NeedsReview or a visible degraded-integrity condition and is never retried.

8. **Visible state**
   - Show admitted version, role, context sources/omissions, provider/model, tools, maximum calls, current node/iteration, pending approval, lifecycle request versus reached state, output, side effects, failure, and final result after restart.
   - Never claim chain-of-thought visibility.

9. **Bounded resource use**
   - Limit definitions, steps, iterations, attempts, per-attempt/run tool requests, input/instruction/output/context/trace sizes, retained evidence, approval wait, execution time, and result pages server-side.

10. **Human-owned concurrency**
    - Expected-version writes preserve edits.
    - Nonterminal-run conflicts are visible.
    - Durable expected-state transitions arbitrate controller races.
    - Create/Invoke operation IDs make duplicate delivery deterministic.
    - Mutating custom-loop tools remain deferred until concurrent target changes can be detected rather than overwritten.

## 12. Historical Questions Resolved For This Rebuild

| Earlier question or direction | First-wave decision |
| --- | --- |
| Is actual repeat required? | Yes. Exit-owned and fixed to the first step, but conditional: only a valid model `Repeat` decision traverses it and the configured additional-iteration count is a hard ceiling. |
| May repeat target be arbitrary? | No. |
| Should repeat connector be automatic? | Yes. It is a derived projection, not persisted edge data. |
| Is freeform placement required? | No. First-class auto-laid-out canvas with ordered editing. |
| Are disconnected nodes/persisted drafts allowed? | No. Saved artifacts are valid/runnable. |
| Is arbitrary rewiring required? | No. Reorder steps. |
| Is chat `/loop-id` required? | No; preserve surface/trigger identity seam and defer routing. |
| May invoking conversation history enter manual invocation? | Yes, only when Trigger admits it and the receiving Inference/Exit node selects it. |
| Can users edit while a run is active? | No for this reduced MVP. Cancel/finish, then edit. The run still owns an admitted snapshot. |
| Are Decision/Scanner/Context/Human/Wait/etc. merge blockers? | No. Explicitly deferred. |
| Are dedicated custom scripts and delegated loops merge blockers? | No for this deliberately reduced MVP; earlier experimental direction is superseded. Inference retains governed model-facing commands. |
| Are node-level tool/model/context overrides required? | Typed context-in/context-out overrides are required for Inference and Exit. Role, model, tools, review, failure, and durable memory/writeback remain loop/runtime owned. |
| Are raw capability IDs user-editable? | No. Users select implemented commands; the server derives capabilities. |
| Does a loop need tools? | No. Zero-tool prompt-only loops are valid and the default. |
| Are mutating `append/write/delete` tools included? | No. Wave one exposes only `list/read/search`; mutation waits for optimistic target preconditions and before/after evidence. |
| Are immutable revision directories required? | No. Expected-version current artifact plus run-owned snapshot. |
| Are authoring/admission/execution lease frameworks required? | No generalized framework. The narrow single-host lock rejects a second custom-loop host, the in-process gate never queues, and durable expected-state transitions govern requests. No distributed coordination or failover is promised. |
| Is restart-safe continuation required? | Yes, only from integrity-complete CheckpointCommitted boundaries; incomplete attempts become NeedsReview. |
| How is approval ownership bound? | Invoke/Resume are authenticated hub methods; the server connection context owns approvals. Timeout/disconnect rejects with zero execution. |
| May run traces grow or prune silently? | No. Per-run/workspace quota, capacity reservation, explicit terminal-trace deletion, and retained audit tombstone. |
| Is live replay required? | No. Replay-quality trace capture is required; replay execution/UI is deferred. |
| Is model-facing loop management required? | No. |
| Are hooks required? | No. |
| Must the visual UI be preserved? | Yes as the primary builder, but as a derived ordered projection rather than the experimental general graph editor. |

## 13. Experimental Material To Port Only As Lessons Or Tests

| Experimental area | Safe material to mine | Material not to carry |
| --- | --- | --- |
| API/controller | Route naming, error-shape lessons, run-detail needs | Replay/reconciliation/catalog/dynamic-policy/nested-continuation endpoints. |
| Definition store | Atomic-write, conflict, snapshot, corrupt-artifact scenarios | Immutable revision tree, pointers, migration aliases, authoring/admission leases. |
| Graph validation | Invalid/unsupported/tampered definition cases | General reachability, branch convergence, typed ports, cycle theorem machinery. |
| Runner tests | Started-before-dispatch, stop-on-failure, restart uncertainty, cancellation race, authority correlation | Generic scheduler, node-family dispatch table, typed dataflow engine, nested execution. |
| Tool integration | Run-specific authority and ToolBroker correlation tests | Action-node direct execution or alternate tool protocols. |
| Run evidence | Timeline readability, admitted snapshot, context manifest, side-effect correlation, and the narrow canonical custom-run envelope required by `RUN-013` | The experiment's generic evidence/replay-planning abstraction. |
| Web editor | Node-card accessibility, selection, dirty state, readable connector geometry, inspector lessons | General catalog workbench, arbitrary ports/edges, freeform layout, broad node forms. |
| Browser tests | Create/edit/save/reload/invoke/inspect and malicious-content scenarios | Private helpers, screenshot artifacts, brittle implementation selectors. |

## 14. Drop From The Clean Rebuild

- Migration/compatibility code for any unreleased experimental schema.
- Legacy node aliases and fallback enum parsing.
- Persisted invalid drafts and enable/disable state machines.
- Generic public node/port/edge DTOs without a wave-one consumer.
- Raw client-authored capability arrays or effective-authority claims.
- Provider thread IDs as memory or continuation semantics.
- Duplicate frontend authority over Save or Invoke eligibility.
- Direct workspace/action execution outside ToolBroker.
- Replay planning models and endpoints.
- General lease, pointer, reconciliation, and repair frameworks.
- Generated `output/playwright/` screenshots or other QA artifacts.
- Any public endpoint, service, DTO, enum, node descriptor, or UI control that exists only for a deferred feature.

## 15. Safe Retrieval Practice

Do not cherry-pick `bd2eb21`, `5c75c65`, `18df6e3`, `2f78be1`, or `943ae01` into the rebuild.

Inspect one artifact or diff at a time:

```powershell
git show 943ae01:path/to/file
git diff b87dbd3..943ae01 -- path/to/file
```

Before porting any behavior:

1. Identify the approved contract requirement it satisfies.
2. Write or adapt a public-boundary acceptance test for that requirement.
3. Reimplement the smallest behavior against live-main architecture.
4. Verify that no deferred public vocabulary or generalized machinery came with it.
5. Update README and `docs/AGENT_LOOP.drawio` in the same behavior batch.

This ledger should be updated before scope changes, not after implementation has already expanded.
