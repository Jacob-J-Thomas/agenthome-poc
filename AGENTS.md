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

## Delivery work rules

- Use the active native GitHub hierarchy `Campaign -> Phase -> Unit of Work -> Bolt`; Bolts have no sub-issues, review findings remain parentless until human triage, and native dependency relationships are authoritative. Follow `docs/AIDLC_DELIVERY_CONVENTIONS.md`.
- Implement one coherent Bolt in normally one pull request. Never use a pull request closing keyword for a Campaign, Phase, or Unit of Work, and do not recursively create implementation issues from review comments.
- Use `$aidlc-pipeline`: one full review and one targeted re-review by default, a third pass only for a new credible P0/P1, and explicit human authorization for any further pass, scope expansion, replacement attempt beyond the configured budget, or merge.

## Code style

- Prefer single-line method calls and argument lists.
- Do not split method arguments across multiple lines unless there are more than 3 arguments, or keeping one line would make the code genuinely hard to read.
- When a call must be split, use the smallest readable split and avoid cascading vertical formatting through nearby code.
- For C#, use `PascalCase` for public types, members, positional-record properties, and compile-time constants; `camelCase` for parameters and locals; `_camelCase` for private instance, static, and readonly fields; `ITypeName` for interfaces; and `TName` for type parameters.
- For intentionally unused lambda and anonymous-method parameters, prefer `_`, including the single-parameter form. Although a lone `_` remains an addressable C# parameter, this repository treats it as the explicit unused-value convention; do not replace it with a synthetic name solely to satisfy `camelCase`.
- Run `dotnet format whitespace EmbodySense.sln --verify-no-changes --no-restore` and `dotnet format style EmbodySense.sln --verify-no-changes --no-restore --severity warn --diagnostics IDE1006` for C# changes.
- Keep domain-specific namespace dependencies explicit in each consuming C# file. Reserve authored `global using` directives for namespaces that are intentionally ubiquitous across an entire project, place them in a project-root `GlobalUsings.cs`, and document why that project-wide dependency is appropriate.
- Run `npm run lint` and `npm run format:check` for frontend changes.
- Keep each class, record, struct, interface, and enum in its own file, with the file named after the type. Extract every behavior-bearing private helper type, including helpers that coordinate, synchronize, mutate, validate, dispose, or own lifecycle state, into its own matching file.
- Place model and DTO types under an appropriate `Models/` folder, and give each non-private model or DTO its own named file.
- A small model or DTO that is truly private to one containing class may remain in that class's file when it has no independent meaning, no behavior beyond property storage, and only a limited number of such private types have accumulated. Extract it once it grows, multiplies, or becomes useful outside that class. Generated files and partial declarations are the only source-layout exceptions: generated files require both a conventional generated suffix and an auto-generated marker, while a partial fragment may contain only its filename-matching type plus necessary partial ancestor containers.

## Implementation discipline

- Use the .NET 10 SDK selected by the root `global.json`. All production and test projects target `net10.0`, and `Directory.Build.props` pins C# 14; do not lower or broaden those toolchain versions in an unrelated change.
- Keep changes direct and aligned with the existing C# solution unless the axioms and the user's request justify a broader design.
- Avoid dependencies unless they buy something concrete.
- Until a user-approved release migration policy exists, experimental persisted schemas and documents must remain at version 1. Do not add automatic migrations, compatibility readers or writers, legacy aliases, or fallback behavior for superseded POC shapes; require explicit reinitialization or cleanup instead.
- Do not describe aspirational agent-loop behavior as implemented unless it exists in source.
- If documentation claims a capability that the source does not contain, treat that as a documentation/source mismatch and report it before filling in code.
- If source contains partial or accidental work, do not treat it as project direction without user confirmation.
- The current CLI `run` path prompts before initializing workspace scaffolding when the workspace is not already initialized, uses `codex app-server --stdio`, streams app-server `item/agentMessage/delta`, and exposes governed workspace actions through `embodysense.command`. Do not describe or reintroduce `codex exec` as the live run path without an explicit user decision.
- Web and CLI share deterministic Codex runtime resolution through `Core.Clients` and the `Core.Startup` status facade. `--codex-path` is authoritative; otherwise Windows discovery prefers current Codex Desktop-managed binaries before PATH candidates. The runtime records the resolved path and version, requires an explicitly configured model to appear in app-server `model/list`, and never substitutes a model silently. Keep compatibility failures actionable and visible before accepting a conversation turn.
- The current CLI `run` path loads the nearest `AGENTS.md` found by walking upward from `--workdir` and `.agent/ROLE.md` as contextual role instructions, `.agent/SOUL.md` and `.agent/PERSONALITY.md` as durable agent identity, and `.agent/CONTEXT.md`, `.agent/MEMORY.md`, and `.agent/models.json` as lower-authority startup context when those files exist and are non-empty. `.agent/AGENT.md` is intentionally unsupported; this POC requires workspace reinitialization after the rename.
- The runtime context should tell the agent that `.agent/MEMORY.md` is the primary durable memory registry for storing, updating, creating, and retrieving most memories.
- Conversation history under `.agent/memory/conversations/` is supporting transcript evidence. Query it only for specific cases such as exact wording, chronology, or recovering context that has not yet been distilled into `.agent/MEMORY.md`.
- Native Codex app-server command, file-change, permission, MCP elicitation, and user-input requests are currently declined and audited by the harness. Governed workspace actions should flow through `embodysense.command` and `ToolBroker`.
- Do not reintroduce the older fenced-JSON tool protocol; dynamic app-server tools are the only supported governed tool integration.
- Default conversation turns flow through `Core.Application.Loops.Execution.DefaultConversationLoopRunner`. `Core.Startup.Runtime.AgentRuntime` exposes the shared `RunTurnAsync` runtime facade, while Web and CLI own their surface-specific hosting and projection layers. `Core.Application.Runtime` owns runtime state and session command helpers. Do not reintroduce `Core.Application.Harness`, `AgentHarnessSession`, or a separate CLI model-turn path.
- Durable planning commands, hooks, skills, subagents, MCP behavior, and `plan_*` governed tool commands are not implemented in the current source. The Web Loops surface can create, edit, validate, version, delete, synchronously invoke, pause, explicitly resume, cancel, and inspect schema-1 custom-loop graphs. The live catalog includes manual and scheduled triggers, governed Inference, bounded deterministic topology/pure/Wait nodes, governed workspace Append/Write/Delete Actions, and Exit; runtime scheduling and wake delivery are composed through the existing governed scheduler. Custom execution is not available through CLI/chat commands and a paused run is never resumed without an explicit or authenticated governed trigger. Do not describe `/plan`, `.agent/tasks/plan.json`, ungoverned trigger delivery, or automatic continuation as live behavior unless source is updated in the same change.
- Core, CLI, and Web control flow is enforced by class-library references. `Core.Common` owns shared dependency-free value types and primitives. `Core.Application` owns reusable ports plus orchestration and must not reference concrete `Core.Clients`, `Core.Persistence`, `Core.Startup`, Web, or CLI projects. `Core.Clients` and `Core.Persistence` may reference `Core.Application` ports. `Core.Startup` composes concrete adapters and exposes the interface-client API. `Cli.Command` and `Web` should reference only `Core.Startup` among Core projects.
- Keep `RunCommand` thin and route reusable run composition through `Core.Startup.Runtime.AgentRuntimeFactory`.
- Keep CLI command and Web production code from directly referencing `Core.Application`, `Core.Common`, `Core.Clients`, or `Core.Persistence`; add or reuse a `Core.Startup` facade when interface code needs status, audit, workspace, runtime, or approval data.
- Keep every production assembly above 90% line coverage through public behavior tests. Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1` when changing harness, inference, governance, context, workspace, or CLI command behavior. Use `verify-coverage.ps1` only as the lower-level checker for coverage files produced by the current run, preferably via `verify.ps1`.

## Documentation maintenance

- Production assemblies generate XML documentation. Document public and protected contracts, their meaningful parameters and return values, cancellation and exception behavior, lifecycle/concurrency constraints, and non-obvious authority, persistence, audit, or integrity invariants. Prefer an accurate explanation of why behavior exists over comments that restate syntax. Do not introduce a documentation claim that source and tests cannot support.
- CS1591 is unsuppressed for production projects and therefore fails the warning-as-error build when a public or protected contract is undocumented. Do not add a broad `NoWarn`, authored-source exclusion, or replacement suppression. Any genuinely necessary generated-code exclusion must identify the generator and explain why its emitted API is outside the authored contract.
- Pull-request descriptions should state which contract or behavior documentation changed and identify any remaining source slices before claiming a documentation gate is complete.
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
