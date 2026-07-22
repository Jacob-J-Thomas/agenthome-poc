# EmbodySense

EmbodySense is a C# agent harness project. The repository is still small, but the project scope is not defined by the current command surface.

## Scope Vs Status

Product scope is anchored in [`docs/OPINIONATED_PROJECT_AXIOMS.md`](docs/OPINIONATED_PROJECT_AXIOMS.md) and the user's latest direction. Treat that file as the hardest local scope reference for architecture, agent capabilities, tooling, governance, and implementation sequencing decisions.

This README is a usage and implementation-status document. It is not a scope contract, not a roadmap, and not a reason to narrow a broad harness request into whichever commands happen to exist today.

Implementation sequencing is user-directed. Archived planning notes and previous roadmap-style documents are historical evidence only; do not treat them as scope, project management, standing authorization, or a fixed work order.

If README status text, diagrams, comments, memories, or the source tree appear to conflict with the axioms or with the user's latest direction, call out the conflict before implementing. Do not fill gaps by assuming the status text is the desired scope.

For example, "agent tooling" should not be reduced to human-only slash commands unless the user explicitly asks for slash-command tooling. In EmbodySense scope discussions, agent tooling generally means model-accessible, governed capabilities with permissions, approvals, and auditability.

## Implementation Status Snapshot

This section is descriptive only. Verify it against source before relying on it, and do not use it as product scope.

The source is organized around project-enforced Core, Web, and CLI boundaries:

- `src/EmbodySense.Core.Common` owns dependency-free shared primitives and value types for inference, memory, loops, context, audit, permissions, tools, local workspace execution, workspace paths, workspace seed records, and filesystem path comparison.
- `src/EmbodySense.Core.Application` owns reusable ports plus orchestration and behavior, including the default conversation loop runner, custom-loop authoring/admission/ordered execution, lifecycle and recovery services, trace retention, runtime conversation state, runtime command helpers, context formatting, inference abstractions, and governance services.
- `src/EmbodySense.Core.Clients` owns edge adapters for provider protocols and local workspace interaction. `CodexAppServer` contains the Codex app-server JSON-RPC adapter; `LocalWorkspace` contains governed local filesystem execution used by model tool requests. It depends on Application ports and Common value types.
- `src/EmbodySense.Core.Persistence` owns long-lived `.agent/` and workspace state access, including audit events, conversation transcripts, system and custom loop definitions, custom run traces and tombstones, idempotent invocation/control receipts, the custom-loop execution gate and single-host lock, permissions loading, workspace context document reads, and scaffold file writes. It depends on Application storage ports and Common value types.
- `src/EmbodySense.Core.Startup` owns concrete composition. It wires Application abstractions to Clients and Persistence, owns the runtime and loop-authoring/inspection facades, run-scoped custom inference composition, startup inference wrapper, workspace initializer, interface-client status/audit readers, tool-approval adapter contracts, and default workspace seed content.
- `src/EmbodySense.Web` owns the primary localhost browser client. It serves the chat and Loops surfaces, binds only to localhost hosts, exposes authenticated session/status/workspace/approval/loop/run endpoints plus a SignalR session hub for browser turns and synchronous custom-loop control, and consumes Core only through the `Core.Startup` API.
- `src/EmbodySense.Cli.Command` owns human-facing CLI commands, the CLI runtime host, console adapters, and human approval prompts. It consumes Core only through the `Core.Startup` API for runtime, status, audit, workspace, and approval contracts.
- `src/EmbodySense.Cli` is the executable startup shell. It references `Cli.Command` and dispatches process arguments.
- `tests/EmbodySense.*.Tests` projects mirror production project boundaries where practical. `tests/EmbodySense.IntegrationTests` covers behavior that intentionally crosses project boundaries, and `tests/EmbodySense.Tests.Support` contains shared test fixtures.

Dependency height is enforced by explicit project references and architecture guard tests: Common does not reference orchestration or concrete adapters; Application does not reference concrete clients, persistence, startup, web, or CLI projects; Clients and Persistence implement Application ports; Startup composes concrete adapters; Web and CLI projects sit at the executable/interface edge and reference only `Core.Startup` among Core projects. Architecture guards reject direct `Core.Application`, `Core.Common`, `Core.Clients`, or `Core.Persistence` namespace usage from Web and CLI command production code.

Project and namespace separation are ownership and compile-time dependency boundaries, not security boundaries by themselves. Security-sensitive behavior must be enforced by runtime policy checks, explicit approvals, and auditable actions.

The primary implemented client is now the localhost Web UI in `src/EmbodySense.Web`. It hosts a browser client on `127.0.0.1` by default, serves static files from `wwwroot`, requires a same-origin session token for protected REST and SignalR calls, lets the user explicitly initialize an uninitialized workspace, handles supported harness slash commands, exposes a Verbose toggle for visible-context debug output, streams assistant output through the `/hubs/session` SignalR hub, supports browser turn cancellation, and pushes governed tool approval requests to connected browsers. Its linked `/loops.html` surface provides Builder and Runs views for the first-wave custom-loop workflow. Governed approval inspection and decisions are bound to the owning SignalR connection; the token-guarded legacy `/api/approvals/*` routes cannot inspect or decide connection-owned requests.

The CLI `run` path remains supported as a verification and conformance client. It prompts before initializing an uninitialized workspace, loads workspace instructions and seeded agent documents as runtime context, exposes governed file commands to the model through Codex app-server dynamic tools, streams app-server events into the console, and can run with explicit visible-context debug output:

1. `Cli.Command.RunCommand` checks whether `--workdir` is initialized, asks the user to confirm workspace scaffolding when it is not, and then hands reusable service composition to `Core.Startup.Runtime.AgentRuntimeFactory` and `AgentRuntime`.
2. `AgentRuntimeFactory` wires the shared workspace, permission, audit, conversation, context, and provider adapters behind `AgentRuntime`. It composes `DefaultConversationLoopRunner` for chat plus the custom definition/run/operation stores, admission gate, immutable context capture, ordered runner, lifecycle/recovery services, run-scoped inference/tool broker, and conversation publisher for synchronous custom execution.
3. `Startup.Inference.LlmInferenceClient` selects `Clients.CodexAppServer.CodexAppServerInferenceClient` for the OpenAI Codex surface.
4. `WorkspaceContextStore` reads the nearest `AGENTS.md` found by walking upward from `--workdir`, plus `.agent/AGENT.md`, `.agent/SOUL.md`, `.agent/PERSONALITY.md`, `.agent/CONTEXT.md`, `.agent/MEMORY.md`, and `.agent/models.json` under `--workdir` when those files exist and are non-empty. `AgentContextProvider` formats that context into the seeded runtime system message.
5. `CodexAppServerInferenceClient` owns the Codex app-server JSON-RPC flow. `CodexAppServerContextBuilder` builds thread developer instructions from restored runtime context, `CodexAppServerRequestHandler` declines and audits unsupported native app-server requests, and `CodexAppServerToolBridge` exposes the `embodysense.command` dynamic tool.
6. `Persistence.Memory.ConversationMemoryStore` normally starts each run with a fresh active transcript by moving any non-empty `conversations/current.ndjson` into `conversations/archive/` before the loop accepts prompts. Startup preserves the current transcript instead when custom-loop hosting is unavailable or restart recovery finds a paused run that is bound to the invoking conversation.
7. `Core.Application.Loops.Execution.DefaultConversationLoopRunner` owns the ordinary model-turn transaction: request assembly, inference invocation, transcript persistence, default-loop run persistence, visible diagnostics, and outcome classification. `Core.Startup.Runtime.AgentRuntime.RunTurnAsync` handles shared runtime commands before delegating model turns to that runner. `Cli.Command.AgentRuntimeConsoleHost` owns console prompting and streaming projection, while `WebSessionHub` and `WebAgentRuntimeHost` own browser projection.
8. `ConversationRuntimeState` starts with seeded runtime context and normally a fresh transcript, with the custom-loop recovery exceptions described above. A user can explicitly load an old transcript with `/history`, `/conversations`, or `/load` before the first model turn, or start another fresh transcript in-place with `/new`.
9. Codex app-server `item/agentMessage/delta` notifications are written to the console as they arrive, and `turn/completed` supplies the final assistant message retained by the session and inference audit metadata.
10. Codex app-server `item/tool/call` requests for `embodysense.command` route through `Application.Governance.Tools.ToolBroker`. File-system commands resolve targets against `--workdir`, reject paths outside the workspace root, reject paths that pass through reparse points, evaluate `.agent/permissions.json`, prompt for human approval when required, delegate allowed filesystem work to `Clients.LocalWorkspace.LocalWorkspaceClient`, and append audit events for permission checks, approval requests/decisions, and execution outcomes. CLI approvals use `ConsoleToolApprovalPrompt`; browser approvals use `WebApprovalCoordinator`.

Native Codex app-server command, file-change, permission, MCP elicitation, and user-input requests are fail-closed and audited in this harness path. The app-server working directory is not the user workspace; governed workspace access is exposed through `embodysense.command`.

The older provider-neutral fenced-JSON tool protocol has been removed. Dynamic app-server tools are the only supported governed tool integration.

Default conversation/runtime governed commands:

- `list`: list a directory.
- `read`: read a file.
- `search`: search a file or directory for a text pattern.
- `write`: create or replace a file.
- `append`: append text to a file, creating it if needed.
- `delete`: delete a file or directory after delete permission or human approval.

Current app-server dynamic tool calls use `embodysense.command` with structured arguments:

```json
{"namespace":"embodysense","tool":"command","arguments":{"command":"read","path":"shared/notes.md"}}
```

Human-facing transcript controls are available from the CLI runtime host and the localhost Web UI. `/history`, `/conversations`, and `/load` load a saved transcript before the first model turn in the current session, replace the visible conversation transcript, and keep the restored messages in conversation runtime state. In the Web UI, `/history` lists stored conversations and the next submitted number loads that selection; successful loads emit a `history_loaded` stream event that replaces the browser transcript before the confirmation message is appended. `/cancel` cancels the pending selection. `/new` and `/new-session` start another fresh transcript without leaving the session. Verbose mode can be changed from the Web UI toggle or with `/verbose`, `/verbose on`, and `/verbose off`; it prints the visible inference context EmbodySense is about to send for the next model turn and labels it as visible context, not private model reasoning or hidden chain-of-thought.

### Custom Loop MVP (Web)

The Web Loops surface supports multiple role-bound custom definitions whose persisted shape is Manual Trigger -> one to five ordered Inference steps -> Exit. Trigger, Exit, connectors, and layout are system projections rather than persisted arbitrary graph topology. The system default conversation loop is shown separately and remains read-only.

Custom loops can be created, server-validated, versioned, deleted, manually invoked from an exact saved version and content hash, paused at a proved checkpoint boundary, cancelled, explicitly resumed, and inspected through durable run evidence. Invoke switches to the Runs view while execution is active; the view refreshes any selected nonterminal run even after reload or reconnect. Deleted definitions remain available as archived run-history groups, and explicit trace-deletion tombstones remain selectable after reload without restoring deleted content. At most one nonterminal run exists per loop and one custom loop actively executes in the workspace; a busy request is rejected rather than hidden in a queue. Startup recovery never dispatches work automatically: a safely interrupted run is parked as Paused, an uncertain open attempt becomes Needs Review, and a safely pending cancellation may be completed.

Trigger admission selects exactly one prompt source: the invocation prompt, a saved preset, or no prompt. It may also bind a bounded snapshot of the invoking logical conversation. Provider-thread history is transport state, not product context. Inference and Exit independently inherit the loop defaults or use typed context overrides. There is no free-form "Additional fixed context" field: authored text belongs in a trigger preset, an Inference instruction, or the Exit decision instruction.

Context output is explicit. `retainForLoopReasoning` controls whether canonical node output can inform later loop attempts. `publishToInvokingConversation` performs an idempotent expected-prefix append to the bound conversation transcript; it does not write `.agent/MEMORY.md` or another durable agent-memory artifact. Evidence retention is independent of both choices. Exit context-output controls apply to the canonical iteration result, not to the decision token itself.

A repeat ceiling never causes repetition. With continuation disabled, or after the ceiling is exhausted, Exit completes deterministically without an Exit model call. Otherwise Exit makes a tool-less decision call and repeats only for the recognized trimmed `Repeat` token. `Complete` terminates; an invalid or uncertain decision enters Needs Review and never repeats.

Custom loops expose only the server-advertised `list`, `read`, and `search` assignments; Exit is always tool-less, and filesystem permission plus browser approval rules still govern each request. The inherited provider and model are shown in Loop settings and again before Invoke; wave one has no per-loop override. Browser approvals belong to the invoking connection, expire after five minutes, and fail closed when the owner disconnects or is unavailable. The loop hub permits the approval decision to run concurrently with the long-lived invocation that is waiting for it.

Runs expose the admitted definition, immutable context-source manifest, resolved per-attempt context, canonical outputs, checkpoints, lifecycle/failure state, exact trace size/hash, and workspace quota accounting. The canonical compact trace envelope stores exact UTF-8 content once with hash and length metadata, then references shared content, context, authority, and tool-request entries. The production maximum-bounded-shape runner/codec proof includes 30 allowed governed requests plus the visible 31st over-limit denial and encodes to 15,225,475 bytes, leaving 503,165 bytes beneath the 15 MiB reservation target and 1,551,741 bytes beneath the 16 MiB hard cap. A terminal trace without an integrity warning continues accounting its reserved 8,192-byte warning slot, for 15,233,667 accounted bytes and 494,973 bytes of remaining 15 MiB headroom at that maximum shape. The store rejects a write that cannot retain its required evidence and reserves. Provider usage and cost are currently labeled unavailable because the provider-response contract carries neither; the UI never fabricates an estimate. Terminal trace deletion requires the currently inspected hash and an idempotent operation ID. It replaces sensitive trace content with an audited metadata tombstone, never reuses the run identity, and is never performed automatically. Quotas may reject a new admission rather than silently pruning evidence.

The first wave does not implement CLI/chat custom-loop invocation, automatic scheduling or background resumption, arbitrary graph topology, mutating custom-loop commands, durable planning, hooks, MCP execution, subagents, or multi-provider orchestration.

Runtime agent context currently comes from the nearest `AGENTS.md` found by walking upward from `--workdir` when that file is non-empty, plus these workspace files under `--workdir` when present:

- `.agent/AGENT.md`
- `.agent/SOUL.md`
- `.agent/PERSONALITY.md`
- `.agent/CONTEXT.md`
- `.agent/MEMORY.md`
- `.agent/models.json`

The context loader caps each individual file before placing it into the model context. It does not write raw document contents to audit metadata.

The runtime context tells the agent to store, update, create, and retrieve most durable memories in `.agent/MEMORY.md`. Conversation history under `.agent/memory/conversations/` is supporting transcript evidence and should be queried only for specific cases such as exact wording, chronology, or recovering context that has not yet been distilled into `.agent/MEMORY.md`.

Newly initialized workspaces seed a fuller `.agent/` home: `AGENT.md` operating guidance, slow-changing `SOUL.md` and `PERSONALITY.md`, a structured `CONTEXT.md` template, primary `MEMORY.md` registry guidance, `models.json` role placeholders, and generated memory, permission, and audit explainers. The seeded text encourages agents to grow durable local capability through inspectable memory, skills, recipes, scripts, and configuration notes while being explicit that hooks, subagents, planning, MCP execution, and model routing are not implemented unless the source and configuration make them real. Automatic document-review hooks are not implemented in this status snapshot.

## Run

For scratch-workspace testing, start in `scratch/` and use the project path relative to that directory. The Web UI is the primary local client:

```powershell
cd C:\Users\98jak\source\repos\agenthome-poc\scratch
dotnet run --project ..\src\EmbodySense.Web -- --workdir .
```

Open the printed localhost URL in a browser. The default URL is `http://127.0.0.1:4378`.

The CLI remains available for verification and client-conformance checks:

```powershell
dotnet run --project ..\src\EmbodySense.Cli -- --help
dotnet run --project ..\src\EmbodySense.Cli -- run --workdir .
```

## Test

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

The verify script builds the solution, runs the frontend Node tests, runs the .NET tests with current-run coverage collection, and verifies package-level line coverage for every production assembly. The installed-browser smoke is opt-in because local Edge/Chrome GPU startup is host-specific:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -RunBrowserE2E
```

The lower-level coverage verifier, `scripts\verify-coverage.ps1`, expects Cobertura files from the current run. Prefer `verify.ps1` so stale `TestResults` are cleared and coverage timestamps are bounded automatically. The real `codex app-server` process launcher is treated as an external process adapter; app-server protocol behavior is covered through the `Clients.CodexAppServer.ICodexAppServerTransport` seam.

## Run From Scratch

The `scratch/` folder is useful for exercising the CLI against a disposable local workspace. These commands assume your current directory is `C:\Users\98jak\source\repos\agenthome-poc\scratch`.

```powershell
cd C:\Users\98jak\source\repos\agenthome-poc\scratch
```

From there, run the Web UI through the project path relative to `scratch/`. The `..\` prefix is required because `src\EmbodySense.Web` lives one directory above `scratch`.

```powershell
dotnet run --project ..\src\EmbodySense.Web -- --workdir .
```

The browser UI can initialize the scratch workspace explicitly when `.agent/permissions.json` is missing. It then handles supported slash commands such as `/help`, `/history`, `/verbose`, and `/new`, shows visible role labels on transcript messages, exposes a Verbose toggle for visible-context debug output, replaces the session view with restored transcript messages after a successful history load, streams assistant responses over SignalR, and pushes governed tool approval requests into the approvals panel. Follow the Loops link to open `/loops.html`, where Builder creates and saves custom definitions and Runs inspects or controls their durable executions.

Run the CLI help from scratch when checking the non-primary client surface:

```powershell
dotnet run --project ..\src\EmbodySense.Cli -- --help
```

Initialize the scratch workspace:

```powershell
dotnet run --project ..\src\EmbodySense.Cli -- init .
```

Check scratch workspace status:

```powershell
dotnet run --project ..\src\EmbodySense.Cli -- status .
```

Start the CLI runtime from scratch. If the workspace is not already initialized, the `run` command warns first and asks whether to create workspace scaffolding before entering the loop:

```powershell
dotnet run --project ..\src\EmbodySense.Cli -- run --workdir .
```

Add `--verbose` to print the visible inference context before each model turn. Inside the CLI runtime, type a message at the `User: ` prompt and press Enter. Assistant output streams under an `Assistant:` header as provider chunks arrive. Type `/help` to list commands. Type `/verbose`, `/verbose on`, or `/verbose off` to inspect or change visible-context debug output. Type `/history`, `/conversations`, or `/load` before the first model turn to list saved `.agent/memory/conversations/*.ndjson` and `.agent/memory/conversations/archive/*.ndjson` transcripts by first-prompt preview and select one to load; a successful load clears the interactive console and prints the restored transcript before the confirmation. Type `/new` or `/new-session` to start another fresh conversation without leaving the session. Type `/exit`, `/quit`, `exit`, or `quit` to leave.

The default conversation loop reads the seeded `.agent/` documents when the runtime starts, so restart that runtime after editing them if you want later chat turns to receive the new startup context. Each custom-loop invocation instead captures a fresh immutable source manifest at admission; provider threads remain transport and are not reused as hidden product context.

## Web UI Options

The Web UI routes inference through the same local `codex app-server --stdio` path as CLI `run`, but exposes the interactive session through a token-guarded SignalR hub, supporting REST endpoints, and a static UI.

From `scratch/`:

```powershell
dotnet run --project ..\src\EmbodySense.Web -- --workdir . --model configured-model
```

Available Web options:

- `--model <model>` or `-m <model>`: choose the Codex model.
- `--workdir <path>` or `--working-directory <path>`: set the EmbodySense workspace root for governed tools, permissions, and audit.
- `--codex-path <path>`: use a specific Codex executable.
- `--sandbox <mode>`: set the Codex app-server sandbox mode for the inert runtime directory, such as `read-only` or `workspace-write`. Workspace file access is still governed by EmbodySense dynamic tools and `.agent/permissions.json`.
- `--host <host>`: bind the Web host to `127.0.0.1`, `localhost`, or `::1`. Remote bind hosts are rejected.
- `--port <port>`: set the localhost port. The default is `4378`.

## Harness Run Options

The `run` command routes inference through local `codex app-server --stdio` and streams Codex app-server `item/agentMessage/delta` events into the console loop.

The default-conversation and custom-loop implementation flows are documented in the draw.io-compatible source diagram at [`docs/AGENT_LOOP.drawio`](docs/AGENT_LOOP.drawio). Keep both pages aligned with implemented inference, lifecycle, context, workspace, permission, audit, and trace behavior. The diagram is also a status artifact, not scope authority.

From `scratch/`:

```powershell
dotnet run --project ..\src\EmbodySense.Cli -- run --workdir . --model configured-model
```

Available `run` options:

- `--model <model>` or `-m <model>`: choose the Codex model.
- `--workdir <path>` or `--working-directory <path>`: set the EmbodySense workspace root for governed tools, permissions, and audit.
- `--codex-path <path>`: use a specific Codex executable.
- `--sandbox <mode>`: set the Codex app-server sandbox mode for the inert runtime directory, such as `read-only` or `workspace-write`. Workspace file access is still governed by EmbodySense dynamic tools and `.agent/permissions.json`.

Before running real inference, make sure Codex is installed and authenticated:

```powershell
codex.cmd login
```
