# Loop Experiment Salvage Ledger

Status: working reference for the clean first-wave MVP rebuild.

This document classifies material from `loop-feature-and-architecture-cutover` for selective reuse. It is not product scope, an implementation authorization, or evidence that experimental behavior is live on this branch. `docs/OPINIONATED_PROJECT_AXIOMS.md` and the user's latest direction remain authoritative.

## Reference Points

- Clean rebuild base: `main` at `b87dbd3` (`finalize loop/harness cutover implementation`).
- Experimental branch: `loop-feature-and-architecture-cutover` at `943ae01`.
- Independent verification commit: `22b2905`, selectively carried to this branch as `80c6e0b`.
- Experimental backend/runtime snapshot: `18df6e3`.
- Experimental Web authoring snapshot: `2f78be1`.
- Experimental status, planning, and debt snapshot: `943ae01`.
- Earlier feature commits `bd2eb21` and `5c75c65` are broad implementation commits, not architecture-only cutover commits.

The experimental branch is a research implementation. It is useful because it makes many future requirements concrete, not because its architecture or schemas should become the default.

## Why The Experiment Is Not The Rebuild Base

The final experiment includes immutable definition revisions, authoring and invocation admission, execution leases, typed dataflow, context lifetime and writeback policy, fan-out and convergence, durable checkpoints, nested delegation, cancellation arbitration, replay planning, server-owned catalog projection, and a general visual graph editor.

That breadth produced several concentrated implementation surfaces:

- `CustomLoopRunner.cs`: approximately 4,900 lines added relative to `main`.
- `LoopGraphDefinition.cs`: approximately 840 lines in the experimental tree.
- `LoopDefinitionStore.cs`: approximately 940 lines in the experimental tree.
- `WorkspaceLoopService.cs`: approximately 1,000 lines in the experimental tree.
- `loopGraphModel.js`: approximately 1,470 lines in the experimental tree.

These are useful failure signals. The rebuild should not reproduce the same abstractions under new names.

## Reuse From Main

| Material | Decision | Reason |
| --- | --- | --- |
| Core project boundaries and architecture guards | Reuse | Common owns dependency-free vocabulary, Application owns ports and orchestration, Clients and Persistence implement ports, Startup composes, and Web/CLI consume Startup. |
| `DefaultConversationLoopRunner` and `AgentRuntime.RunTurnAsync` | Reuse unchanged | Ordinary chat already uses the loop-first runtime cutover and must not be destabilized by custom-loop work. |
| Codex app-server inference path | Reuse | Provider transport is already isolated behind the inference adapter. Provider threads remain transport, not product context. |
| `ToolBroker`, permission evaluation, approvals, and audit | Reuse | Custom-loop inference must use the same governed model-facing tool path rather than inventing another actuator path. |
| Workspace startup context loading | Reuse | The current directory role and its local documents remain the MVP role/context boundary. |
| `LoopDefinition`, `LoopGraphDefinition`, and local JSON artifact paths | Reuse narrowly | Main already has the default-loop domain and persistence foundation. Extend only what the approved MVP consumes. |
| `LoopRunRecord` and `LoopRunStore` | Reuse narrowly | Main already persists run identity and terminal status. Add only the evidence and checkpoint fields required by the approved MVP. |
| Web and CLI through `Core.Startup` | Reuse | Surfaces must remain projections of one runtime and authority model. |

## Port Selectively

| Experimental material | What to carry forward | What not to carry forward |
| --- | --- | --- |
| Thin loop API wrapper and route naming | List, create, read, update, delete, validate, invoke, recent runs, run detail, pause/cancel/resume | Replay, reconciliation, dynamic policy, general catalog, or nested-continuation endpoints |
| Server-authoritative authoring contract | A tiny descriptor for the supported trigger, inference, and Exit concepts | The general node catalog, planned node types, conditional schema engine, and compatibility aliases |
| Valid-on-save behavior | Invalid edits may exist only in browser memory; Save rejects an invalid graph | Persisted drafts, enable/disable state transitions, and fallback save modes |
| Canvas behavior | Visible graph, basic node cards, connector geometry, selection, and accessible editing lessons | The experimental workbench as a whole, generic port inspectors, arbitrary topology, and disconnected persisted graphs |
| Run identity and evidence | Loop/run identity, admitted definition hash or version, role, surface, capabilities, node timeline, provider correlation, tool/audit correlation, outcome, and failure | The replay-planning object model and generalized typed evidence envelope |
| Definition concurrency | Expected-version update and a run-owned admitted snapshot | Immutable revision directories, authoring leases, invocation-admission leases, reconciliation journals, and migration code for unreleased schemas |
| Exit-owned repeat | One bounded repeat policy with a fixed target and explicit iteration evidence | Arbitrary backedges, generic cycle scheduling, backoff policies, branch-local repeat, and target reconciliation |
| Acceptance-test journey | Create, edit, save, reload, invoke, inspect evidence, and observe honest failure | Copying the experimental test suite or its private helpers wholesale |

## Rewrite

| Surface | Rebuild direction |
| --- | --- |
| Custom-loop runner | Implement a small ordered interpreter for the approved nodes. Keep scheduling, persistence, and projection responsibilities separate. |
| Graph validation | Validate the approved topology directly. Do not build a general workflow-graph theorem prover for wave one. |
| Definition store | Add optimistic expected-version updates and atomic writes to the existing local JSON store. Keep the run's admitted snapshot with the run. |
| Run persistence | Persist a readable run summary, ordered node attempts, bounded-repeat state, and the minimal node-boundary checkpoint. |
| Startup facade and controller | Keep CRUD, authoritative validation, synchronous invocation, recent-run reads, and minimal lifecycle commands thin. |
| Web loop editor | Build a dedicated small module around the approved topology. Do not grow more loop logic inside the existing `app.js`. |
| Tests | Write public-boundary behavior tests from the approved acceptance journey. Mine the experiment for scenarios, not implementation structure. |

## Defer From Wave One

The following remain valid long-term product ideas. Deferral here does not remove them from the axioms or future product direction.

- Multiple triggers and automatic trigger routing.
- Decision branches, scanners, fan-out, convergence, and arbitrary backedges.
- General typed ports, transforms, bindings, JSON Schema, and typed branch arguments.
- Dynamic role, model, memory, review, failure, context, and tool policies.
- Node-level model or tool overrides.
- Context lifetime, trust, sensitivity, compaction, budgeting, and writeback engines.
- Dedicated action/script, human-gate, wait, observation, daemon, judge, repair, compaction, memory, and hook nodes.
- Delegated loops and subloops.
- Background workers, scheduling, wake listeners, webhooks, and automatic wait delivery.
- General retry, repair, compensation, and failure branches.
- Live replay execution, replay planning UI, evaluation, and loop-health systems.
- Model-facing custom-loop creation or invocation.
- Hook attachment persistence and execution.

## Drop From The Rebuild

- Migration or compatibility code for schemas that existed only on the experimental branch.
- Persisted invalid drafts and routine enable/disable workflows.
- Public contracts for planned or non-executable node types.
- Duplicate frontend authority over Save or Invoke eligibility.
- Legacy aliases and permissive fallbacks added only to bridge intermediate experimental shapes.
- Generated `output/playwright/` screenshots and other local QA artifacts.
- Any public DTO, endpoint, service, or enum without a wave-one consumer.

## Historical Requirements Requiring A Fresh Decision

The experiment accumulated directions that should not silently become rebuild requirements:

- Whether bounded Exit repeat is mandatory in wave one and whether its target can be arbitrary.
- Whether freeform placement, disconnected nodes, and arbitrary rewiring are MVP requirements.
- Whether chat `/loop-id` invocation must ship alongside manual Web invocation.
- Whether editing during a run must fail or may save a later version while the active run keeps its admitted snapshot.
- Whether manual invocation receives active chat history.
- Whether decision, scanner, context-injection, script/action, human-feedback, wait, daemon, repair, compaction, memory, delegation, and custom-script nodes are merge blockers.
- The earlier direction that delegated loops and custom scripts should land before merge.
- Whether node-level model and tool overrides are required.
- Whether immutable revision history, leases, restart-safe continuation, cancellation arbitration, and reconciliation are wave-one requirements.
- Whether replay planning, evaluation, default-conversation convergence, model-facing loop management, or hook persistence belong in wave one.
- Whether repeat connectors should be created automatically.

The proposed answers for this rebuild are recorded in `docs/LOOP_MVP_REBUILD_CONTRACT.md`. Changing one of those answers should update that contract before source changes.

## Safe Retrieval Practice

Do not cherry-pick `bd2eb21`, `5c75c65`, `18df6e3`, `2f78be1`, or `943ae01` into the rebuild.

Use narrow inspection instead:

```powershell
git show 943ae01:path/to/file
git diff b87dbd3..943ae01 -- path/to/file
```

Port behavior in a new commit only after the relevant contract and public-boundary test are accepted. Documentation and `docs/AGENT_LOOP.drawio` should move in the same behavior batch rather than as an end-of-feature cleanup.
