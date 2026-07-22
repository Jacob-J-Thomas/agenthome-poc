# Agent instructions for this repository

You are working on EmbodySense.

## Scope authority

- Treat `docs/OPINIONATED_PROJECT_AXIOMS.md` as the hardest repo-local scope anchor for EmbodySense product direction, harness capabilities, architecture, tooling, governance, and implementation sequencing decisions.
- Read the axiom file before making design, scope, architecture, tooling, governance, or implementation sequencing decisions for this application.
- Do not infer product scope from README usage notes, AGENTS instructions, stale status text, code comments, diagrams, or the current implementation shape.
- README and AGENTS text can describe how to operate or contribute to the repo, but they do not narrow the product vision or define the intended final scope.
- Treat archived planning notes, prior roadmap-style documents, memories, and previous implementation plans as historical evidence only. The user owns project management and sequencing; do not treat those artifacts as gospel or as standing authorization to implement a sequence.
- If source code, README, AGENTS, diagrams, memories, or prior implementation notes conflict with the axioms or with the user's latest direction, stop and report the conflict before implementing.
- If a requested harness capability is broad or ambiguous, perform a read-only design pass first and explicitly tie the design back to the axioms before editing code.
- Do not reduce "agent tooling" to human-only slash commands unless the user explicitly asks for slash-command tooling. Agent tooling normally means model-accessible, governed capabilities with permissions, approvals, and auditability.

## Code style

- Prefer single-line method calls and argument lists.
- Do not split method arguments across multiple lines unless there are more than 3 arguments, or keeping one line would make the code genuinely hard to read.
- When a call must be split, use the smallest readable split and avoid cascading vertical formatting through nearby code.
- Almost always keep each class, record, struct, interface, and enum in its own file, with the file named after the type. Extract independently meaningful private helper types instead of accumulating several types in one file.
- Place model and DTO types under an appropriate `Models/` folder, and give each non-private model or DTO its own named file.
- A small model or DTO that is truly private to one containing class may remain in that class's file when it has no independent meaning and only a limited number of such private types have accumulated. Extract it once it grows, multiplies, or becomes useful outside that class.

## Implementation discipline

- Keep changes direct and aligned with the existing C# solution unless the axioms and the user's request justify a broader design.
- Avoid dependencies unless they buy something concrete.
- Do not describe aspirational agent-loop behavior as implemented unless it exists in source.
- If documentation claims a capability that the source does not contain, treat that as a documentation/source mismatch and report it before filling in code.
- If source contains partial or accidental work, do not treat it as project direction without user confirmation.
- The current CLI `run` path prompts before initializing workspace scaffolding when the workspace is not already initialized, uses `codex app-server --stdio`, streams app-server `item/agentMessage/delta`, and exposes governed workspace actions through `embodysense.command`. Do not describe or reintroduce `codex exec` as the live run path without an explicit user decision.
- The current CLI `run` path loads the nearest `AGENTS.md` found by walking upward from `--workdir`, plus `.agent/AGENT.md`, `.agent/SOUL.md`, `.agent/PERSONALITY.md`, `.agent/CONTEXT.md`, `.agent/MEMORY.md`, and `.agent/models.json` under `--workdir` as startup runtime context when those files exist and are non-empty.
- The runtime context should tell the agent that `.agent/MEMORY.md` is the primary durable memory registry for storing, updating, creating, and retrieving most memories.
- Conversation history under `.agent/memory/conversations/` is supporting transcript evidence. Query it only for specific cases such as exact wording, chronology, or recovering context that has not yet been distilled into `.agent/MEMORY.md`.
- Native Codex app-server command, file-change, permission, MCP elicitation, and user-input requests are currently declined and audited by the harness. Governed workspace actions should flow through `embodysense.command` and `ToolBroker`.
- Do not reintroduce the older fenced-JSON tool protocol; dynamic app-server tools are the only supported governed tool integration.
- Default conversation turns flow through `Core.Application.Loops.Execution.DefaultConversationLoopRunner`. `Core.Startup.Runtime.AgentRuntime` exposes the shared `RunTurnAsync` runtime facade, while Web and CLI own their surface-specific hosting and projection layers. `Core.Application.Runtime` owns runtime state and session command helpers. Do not reintroduce `Core.Application.Harness`, `AgentHarnessSession`, or a separate CLI model-turn path.
- Durable planning commands, background loop execution, cron scheduling, wake listeners, hooks, skills, subagents, MCP behavior, and `plan_*` governed tool commands are not implemented in the current source. The Web Loops surface can create, edit, validate, version, delete, synchronously invoke, pause, explicitly resume, cancel, and inspect first-wave custom loops whose persisted shape is Manual Trigger -> one to five ordered Inference steps -> Exit. Custom execution is not available through CLI/chat commands, has no arbitrary graph topology or mutating command assignments, and is never resumed automatically. Do not describe `/plan`, `.agent/tasks/plan.json`, automatic trigger delivery, scheduled/background execution, or automatic continuation as live behavior unless source is updated in the same change.
- Core, CLI, and Web control flow is enforced by class-library references. `Core.Common` owns shared dependency-free value types and primitives. `Core.Application` owns reusable ports plus orchestration and must not reference concrete `Core.Clients`, `Core.Persistence`, `Core.Startup`, Web, or CLI projects. `Core.Clients` and `Core.Persistence` may reference `Core.Application` ports. `Core.Startup` composes concrete adapters and exposes the interface-client API. `Cli.Command` and `Web` should reference only `Core.Startup` among Core projects.
- Keep `RunCommand` thin and route reusable run composition through `Core.Startup.Runtime.AgentRuntimeFactory`.
- Keep CLI command and Web production code from directly referencing `Core.Application`, `Core.Common`, `Core.Clients`, or `Core.Persistence`; add or reuse a `Core.Startup` facade when interface code needs status, audit, workspace, runtime, or approval data.
- Keep every production assembly above 90% line coverage through public behavior tests. Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1` when changing harness, inference, governance, context, workspace, or CLI command behavior. Use `verify-coverage.ps1` only as the lower-level checker for coverage files produced by the current run, preferably via `verify.ps1`.

## Documentation maintenance

- Keep `README.md` aligned with the real CLI behavior.
- Keep `docs/AGENT_LOOP.drawio` aligned with the real implementation whenever the default conversation loop, inference path, workspace scaffolding, permissions, or audit behavior changes.
- Treat `docs/AGENT_LOOP.drawio` as editable source for diagrams.net / draw.io, not as a generated screenshot.
- Do not let README runtime-status language read like scope. Label status snapshots as status, and route scope questions back to the axioms.

## Repository orientation

- `docs/OPINIONATED_PROJECT_AXIOMS.md`: product-direction and scope anchor.
- `README.md`: human-facing usage notes and implementation-status snapshot, not scope authority.
- `docs/AGENT_LOOP.drawio`: editable draw.io source diagram that must match implemented loop behavior.
- `EmbodySense.sln`: solution entry point for Visual Studio and solution builds.
- `src/`: application source.
- `tests/`: repository test projects.
