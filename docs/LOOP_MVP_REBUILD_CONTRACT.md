# First-Wave Loop MVP Rebuild Contract

Status: proposed product and implementation contract. Source implementation should not begin until the decision register is approved or corrected by the user.

This document defines a deliberately narrow first delivery of custom loops. It does not narrow the long-term EmbodySense scope. `docs/OPINIONATED_PROJECT_AXIOMS.md` remains authoritative, including the requirements that governed loops be durable, inspectable, modifiable, interruptible, resumable, role-scoped, authority-scoped, and visibly evidenced.

## Product Outcome

A user can create, understand, edit, save, invoke, interrupt, resume, and inspect one small governed custom loop from the Web UI.

The result should prove the full product seam without building a general workflow engine:

```text
Manual trigger -> one or more inference steps -> Exit
                         ^                    |
                         |---- bounded repeat-
```

## Axiom-Derived Constraints

The wave-one design must preserve these properties even when feature breadth is small:

- A custom loop belongs to the current directory role, which is a contextual projection of the durable agent identity.
- Authority belongs to the loop. Inference steps inherit only the model-facing tools allowed by that loop.
- The user can see the loop definition, active authority, admitted version, run state, node progress, outputs, tool activity, and failure state.
- A saved loop is valid and runnable. Invalid intermediate edits exist only in browser memory.
- A run uses an admitted definition snapshot so later edits cannot change work already in progress.
- A run can pause or cancel at safe node boundaries and resume from its last durable checkpoint.
- Failures and uncertain interruption states are recorded honestly rather than coerced into success or silently retried.
- Web is the first authoring and invocation surface, but all orchestration remains behind the shared Startup/runtime boundary.
- Provider threads are transport. EmbodySense-owned context, evidence, and checkpoints are the product state.

If node-boundary interrupt and resume are removed, this delivery must be labeled a prototype rather than a valid governed-loop MVP because Axiom 2 explicitly requires interruptible and resumable loops.

## Definition Contract

Each wave-one custom loop has:

- Exactly one manual Web/API trigger.
- One or more ordered inference nodes.
- Exactly one Exit node.
- One current directory role ID. No role switching or role-store work.
- One loop-level capability set inherited by every inference node.
- One loop-level memory/context recipe defined below.
- One optional bounded Exit repeat policy.
- One monotonically increasing definition version or equivalent strong content version.
- User-facing display name and description.

Each inference node has only:

- Stable node ID.
- User-facing name.
- Required step instruction.
- Position in the ordered inference sequence.

Wave one has no node-level role, model, memory, review, failure, context, or tool override.

## Topology And Validation

The authoritative server validator accepts only:

```text
ManualTrigger -> Inference[1] -> ... -> Inference[n] -> Exit
```

When repeat is enabled, Exit may start another iteration at `Inference[1]`. No other edge may point backward.

Save rejects:

- Missing or duplicate trigger or Exit nodes.
- Zero inference nodes.
- Blank inference instructions.
- Disconnected nodes.
- Branches, joins, multiple outgoing edges, or arbitrary cycles.
- Unsupported node kinds or configuration.
- Missing required loop identity, role, capability, or version fields.
- A repeat policy without an explicit bounded iteration count.

The frontend may show immediate hints, but it must submit to server validation and render the server result. It must not maintain a second authoritative contract.

## Authoring Surface

The graph/canvas remains visible and first-class because visual loop structure is a product concept, not a modal-only configuration form.

Create seeds:

```text
Manual trigger -> Inference -> Exit
```

The user can:

- Rename and describe the loop.
- Edit an inference instruction.
- Add, remove, and reorder inference nodes.
- Configure bounded Exit repeat.
- Review the effective directory role and loop-level capabilities.
- Save, reload, invoke, pause/cancel, resume, inspect runs, and delete the loop.

The canvas auto-lays out the ordered topology. Freeform placement, disconnected drafting, arbitrary rewiring, port inspectors, and edge configuration are deferred. This keeps the graph visible without turning the first wave into a general graph editor.

There is no routine enable/disable flow. A persisted custom loop is valid and runnable. The system default loop remains separate and read-only.

## Invocation And Context

Wave one supports manual Web/API invocation only. Chat `/loop-id`, model-facing invocation, schedules, listeners, and background triggers are deferred.

Invocation captures:

- Loop ID and admitted definition version/hash.
- Run ID and Web surface identity.
- Current directory role ID.
- Loop capability IDs and effective governed tool names.
- User invocation input.
- Workspace startup context already loaded for the directory role.

Each inference receives:

1. Trusted loop/run/role/authority metadata.
2. The established workspace startup context.
3. The original invocation input.
4. Its own step instruction.
5. Ordered outputs from earlier inference nodes in the current iteration.
6. For iterations after the first, the prior iteration's final output.

Manual custom-loop invocation does not implicitly attach an unrelated chat transcript.

The configured default inference provider/model is inherited. Provider transport history is not used as loop memory. Each provider request must be reconstructable from EmbodySense-owned context and evidence.

Inference nodes inherit the loop's model-facing tool assignment. Tool requests continue through the existing `embodysense.command` -> `ToolBroker` -> permission/approval -> execution -> audit path. There is no separate action or custom-script node in wave one.

## Bounded Repeat

The proposed MVP includes one constrained form of actual looping:

- Repeat is an Exit-owned setting.
- Repeat always targets the first inference node.
- `maxAdditionalIterations` is required and has a small hard product ceiling.
- The run evidence records iteration start, iteration end, and remaining allowance.
- Each new iteration starts with no node-local outputs from the previous iteration.
- The next iteration receives the original invocation input and the previous iteration's final output.
- Any node failure ends the run. There is no failure threshold, backoff, arbitrary target, branch-local repeat, or ordinary backedge.

The exact hard ceiling is an implementation constant and should be visible in validation and UI copy. A proposed starting ceiling is 10 additional iterations.

## Persistence And Concurrency

Definitions remain local JSON artifacts under the established `.agent` loop paths.

Save uses optimistic concurrency:

- The client supplies its expected definition version.
- Save fails clearly when another writer has advanced the version.
- A successful Save atomically replaces the current definition and returns the next version.

Invocation reads and validates the current definition once, then stores an immutable run-owned snapshot or equivalent canonical serialized copy with the run. Later definition edits do not alter that run.

Editing while a run is active is allowed. The active run keeps its snapshot; the later definition version affects only future runs. This avoids an authoring lease and matches the axiom that humans, agents, and editors may act concurrently.

Wave one does not add immutable definition-history directories, authoring leases, invocation-admission leases, execution leases, reconciliation journals, or migration support for experimental schemas.

## Minimal Interrupt, Resume, And Cancellation

The runner persists a safe checkpoint after every completed inference node and at every repeat boundary. A checkpoint contains:

- Run ID and admitted definition version/hash.
- Current iteration.
- Next inference index or Exit.
- Original invocation input.
- Completed node outputs required by remaining nodes.
- Current terminal or resumable status.

Pause and cancel are cooperative:

- A pause request is observed before the next node or repeat iteration starts.
- A paused run can resume from its last completed checkpoint, including after a process restart.
- A cancellation request is observed at the same safe boundaries and becomes terminal.
- Only one resume attempt may own a run at a time; a small per-run serialization guard is sufficient for wave one.

If the process or provider is interrupted during an inference and EmbodySense cannot prove whether the inference or its tools completed, the run becomes `needs-review` or an equivalent explicit non-success state. It must not automatically repeat the uncertain node. The user can inspect evidence and choose to start a new run. This is honest recovery rather than fake exactly-once execution.

There is no background worker. Resume and cancellation are explicit user/API actions.

## Failure Contract

- A node failure fails the run.
- Later nodes and iterations do not execute.
- The failed attempt, error class, safe detail, timestamps, and prior successful outputs remain visible.
- Persistence failure must be surfaced; the UI must not report a completed run without durable terminal evidence.
- There is no retry, repair inference, compensation, failure branch, or automatic fallback in wave one.

## Run Evidence

Every invocation persists enough evidence to answer what ran, why, with what authority, and what happened:

- Loop ID, run ID, admitted definition version/hash, and admitted definition snapshot.
- Role ID, surface, capability IDs, and effective governed tool assignment.
- Start, update, pause, resume, cancellation, failure, and completion timestamps.
- Invocation input summary.
- Ordered iteration and node start/completion/failure events.
- Node instruction snapshot and visible node output.
- Provider/model/request correlation without provider-private reasoning.
- Tool request, permission, approval, execution, and audit correlation when tools run.
- Final output or failure/needs-review detail.

The Web run panel needs recent runs and one readable timeline. Replay controls and alternate-revision comparison are deferred.

## Explicitly Deferred

- Multiple triggers and automatic trigger delivery.
- Decision nodes, scanners, typed branches, fan-out, convergence, and general cycles.
- General typed ports, transforms, bindings, and JSON Schema.
- Context-injection, action/script, human-gate, wait, observation, daemon, judge, repair, compaction, memory, delegation, and hook nodes.
- Delegated loops/subloops and custom scripts as dedicated node families.
- Node-level model/tool/context/policy overrides.
- Dynamic role, model, memory, review, failure, and context policy.
- Semantic compaction, context budgeting, trust, sensitivity, and writeback engines.
- Background workers, schedulers, cron, webhooks, watchers, and wake listeners.
- Live replay, replay planning UI, evaluation, and loop health.
- Model-facing custom-loop creation or invocation.
- Hook persistence or execution.
- Compatibility or migration code for the experimental branch's unreleased schemas.

## Acceptance Journey

1. Open Loops in Web.
2. Create a loop and receive `Manual trigger -> Inference -> Exit`.
3. Edit the first instruction and add a second inference node.
4. Save successfully.
5. Reload and observe the same semantic graph and configuration.
6. Invoke with text input.
7. Observe ordered inference outputs and a completed run timeline.
8. Enable one additional repeat iteration, invoke, and observe two iterations with explicit evidence.
9. Pause a multi-node run at a safe boundary and resume it from the persisted checkpoint.
10. Trigger a controlled node failure and observe an honestly failed run with later nodes unexecuted.
11. Attempt a stale-version save and observe a clear conflict without overwriting the newer definition.
12. Delete the loop when it has no active resumable run.

## Implementation Batches

Implementation remains a separate phase after contract approval.

### Batch 1: Authoring Vertical Slice

- Minimal custom-loop definition and authoritative validator.
- Optimistic definition versioning and atomic CRUD persistence.
- Thin Startup facade and Web API.
- Minimal visible linear canvas with create, edit, save, reload, and delete.
- Public-boundary persistence/API/frontend tests.
- README and diagram updates in the same batch.

### Batch 2: Governed Invocation And Evidence

- Ordered inference runner using the existing inference and ToolBroker paths.
- Run-owned definition snapshot.
- Node and tool evidence plus recent-run/readable-timeline API and UI.
- Successful and failed invocation tests, including authority filtering.
- README and diagram updates in the same batch.

### Batch 3: Bounded Repeat And Minimal Lifecycle

- Exit-owned bounded repeat.
- Durable node-boundary checkpoints.
- Explicit pause, resume, cancellation, and needs-review handling.
- Concurrency, restart, and uncertain-interruption tests.
- UI controls and evidence updates.
- README and diagram updates in the same batch.

Each batch must pass `scripts/verify.ps1`, keep production assemblies above the repository coverage floor, and end at a review checkpoint before the next batch begins.

## Decision Register

The following are the proposed answers that require user approval before Batch 1:

1. Include bounded Exit-owned repeat in wave one, fixed to the first inference node with a maximum of 10 additional iterations.
2. Include minimal durable node-boundary pause/resume/cancel so the MVP remains aligned with Axiom 2.
3. Allow editing during a run through admitted-snapshot isolation instead of rejecting edits with an authoring lease.
4. Keep Web/API manual invocation as the only wave-one invocation surface.
5. Keep the canvas first-class but linear and auto-laid-out; defer arbitrary rewiring and freeform persisted placement.
6. Defer dedicated action/custom-script and delegated-loop nodes; inference nodes still inherit loop-assigned governed model tools.
7. Do not attach active chat history to manual custom-loop invocation.
8. Do not migrate any artifact shape that existed only on the experimental branch.

Approval of this register freezes the first-wave boundary. Later ideas remain visible in the salvage ledger and axioms but do not enter implementation without an explicit contract change.
