# EmbodySense

EmbodySense is a C#/.NET agent harness project, evolved from the original AgentHome proof of concept. It explores how a local AI agent runtime can move beyond a plain chat loop into a governed product surface: localhost Web UI, persisted workspace state, graph-authored custom loops, model-accessible tools, permissions, approvals, audit trails, and repeatable verification.

## Project Framing

This repository is both an implementation and an architecture lab for applied agent systems. The practical question behind the project is: what does it take to let an LLM operate inside a real local workspace while keeping the human in control of scope, authority, evidence, and recovery?

The current implementation is not packaged as an end-user product. It is a working prototype and design record for:

- a localhost browser client as the primary interaction surface;
- a reusable C# runtime layer that keeps Web and CLI clients behind the same startup facade;
- graph-authored custom loops with persisted definitions, validation, invocation, run records, and timeline/audit evidence;
- governed model-accessible workspace tools with explicit loop authority, permission checks, human approvals, and auditable outcomes;
- durable workspace context and memory files under `.agent/`;
- public-boundary tests for Core, Web, integration, and frontend behavior.

## Scope Vs Status

Product scope is anchored in [`docs/OPINIONATED_PROJECT_AXIOMS.md`](docs/OPINIONATED_PROJECT_AXIOMS.md) and the user's latest direction. Treat that file as the hardest local scope reference for architecture, agent capabilities, tooling, governance, and implementation sequencing decisions.

This README is a usage and implementation-status document. It is not a scope contract, not a roadmap, and not a reason to narrow a broad harness request into whichever commands happen to exist today.

Implementation sequencing is user-directed. Archived planning notes and previous roadmap-style documents are historical evidence only; do not treat them as scope, project management, standing authorization, or a fixed work order.

If README status text, diagrams, comments, memories, or the source tree appear to conflict with the axioms or with the user's latest direction, call out the conflict before implementing. Do not fill gaps by assuming the status text is the desired scope.

For example, "agent tooling" should not be reduced to human-only slash commands unless the user explicitly asks for slash-command tooling. In EmbodySense scope discussions, agent tooling generally means model-accessible, governed capabilities with permissions, approvals, and auditability.

## Implementation Status Snapshot

This section is descriptive only. Verify it against source before relying on it, and do not use it as product scope.

The source is organized around project-enforced Core, Web, and CLI boundaries:

- `src/EmbodySense.Core.Common` owns dependency-free shared primitives such as workspace paths, workspace seed records, and filesystem path comparison.
- `src/EmbodySense.Core.Application` owns the reusable harness loop, client I/O contract, session flow, context formatting, inference abstractions and models, memory/audit/permission/tool contracts, and governance decisions.
- `src/EmbodySense.Core.Clients` owns edge adapters for provider protocols and local workspace interaction. `CodexAppServer` contains the Codex app-server JSON-RPC adapter; `LocalWorkspace` contains governed local filesystem execution used by model tool requests.
- `src/EmbodySense.Core.Persistence` owns long-lived `.agent/` and workspace state access, including audit events, conversation transcripts, permissions loading, workspace context document reads, and scaffold file writes.
- `src/EmbodySense.Core.Startup` owns concrete composition. It wires Application abstractions to Clients and Persistence, owns the runtime factory/facade, startup inference wrapper, workspace initializer, interface-client status/audit readers, console-loop and tool-approval adapter contracts, and default workspace seed content.
- `src/EmbodySense.Web` owns the primary localhost browser client. It serves the static Web UI, binds only to localhost hosts, exposes session/status/workspace/approval REST endpoints plus a SignalR session hub for browser turns, cancellation, status pushes, and approval pushes, and consumes Core only through the `Core.Startup` API.
- `src/EmbodySense.Cli.Command` owns human-facing CLI commands, console adapters, and human approval prompts. It consumes Core only through the `Core.Startup` API for runtime, status, audit, workspace, console-loop, and approval contracts.
- `src/EmbodySense.Cli` is the executable startup shell. It references `Cli.Command` and dispatches process arguments.
- `tests/EmbodySense.*.Tests` projects mirror production project boundaries where practical. `tests/EmbodySense.IntegrationTests` covers behavior that intentionally crosses project boundaries, and `tests/EmbodySense.Tests.Support` contains shared test fixtures.

Dependency height is enforced by explicit project references and architecture guard tests: application contracts do not reference concrete clients, persistence, startup, web, or CLI projects; startup composes concrete adapters; Web and CLI projects sit at the executable/interface edge and reference only `Core.Startup` among Core projects. Architecture guards reject direct `Core.Application`, `Core.Common`, `Core.Clients`, or `Core.Persistence` namespace usage from Web and CLI command production code.

Project and namespace separation are ownership and compile-time dependency boundaries, not security boundaries by themselves. Security-sensitive behavior must be enforced by runtime policy checks, explicit approvals, and auditable actions.

The primary implemented client is now the localhost Web UI in `src/EmbodySense.Web`. It hosts a browser client on `127.0.0.1` by default, serves static files from `wwwroot`, requires a same-origin session token for protected REST and SignalR calls, lets the user explicitly initialize an uninitialized workspace, handles supported harness slash commands, exposes a Verbose toggle for visible-context debug output, streams assistant output through the `/hubs/session` SignalR hub, supports browser turn cancellation, and pushes governed tool approval requests to connected browsers. The Web Loops surface can create, graph-edit, save, reload, archive/delete, and synchronously invoke supported editable custom loops with server-side validation and run/audit/timeline recording. The `/api/approvals/*` endpoints remain as a token-guarded fallback for approval inspection and decisions.

The CLI `run` path remains supported as a verification and conformance client. It prompts before initializing an uninitialized workspace, loads workspace instructions and seeded agent documents as runtime context, exposes governed file commands to the model through Codex app-server dynamic tools, streams app-server events into the console, and can run with explicit visible-context debug output:

1. `Cli.Command.RunCommand` checks whether `--workdir` is initialized, asks the user to confirm workspace scaffolding when it is not, and then hands reusable service composition to `Core.Startup.Runtime.AgentRuntimeFactory` and `AgentRuntime`.
2. `AgentRuntimeFactory` wires `Common.Workspace.WorkspacePaths`, `PermissionPolicyStore`, `ToolPermissionService`, `ToolBroker`, `LocalWorkspaceClient`, `AuditLog`, `ConversationMemoryStore`, `WorkspaceContextStore`, `AgentContextProvider`, `Startup.Inference.LlmInferenceClient`, and `AgentHarnessSession` behind the `AgentRuntime` facade.
3. `Startup.Inference.LlmInferenceClient` selects `Clients.CodexAppServer.CodexAppServerInferenceClient` for the OpenAI Codex surface.
4. `WorkspaceContextStore` reads the nearest `AGENTS.md` found by walking upward from `--workdir`, plus `.agent/AGENT.md`, `.agent/SOUL.md`, `.agent/PERSONALITY.md`, `.agent/CONTEXT.md`, `.agent/MEMORY.md`, and `.agent/models.json` under `--workdir` when those files exist and are non-empty. `AgentContextProvider` formats that context into the seeded runtime system message.
5. `CodexAppServerInferenceClient` owns the Codex app-server JSON-RPC flow. `CodexAppServerContextBuilder` builds thread developer instructions from restored runtime context, `CodexAppServerRequestHandler` declines and audits unsupported native app-server requests, and `CodexAppServerToolBridge` exposes the `embodysense.command` dynamic tool.
6. `Persistence.Memory.ConversationMemoryStore` starts each run with a fresh active transcript by moving any non-empty `conversations/current.ndjson` into `conversations/archive/` before the loop accepts prompts.
7. `Core.Application.Harness.AgentHarnessLoop` writes through `IHarnessClient`, tracks loop/session transitions and CLI verbose mode in `HarnessLoopState`, and uses `HarnessCommandService` as the shared implementation for help, exit, transcript loading, visible-context verbose toggles, and fresh-session commands. Live CLI turns render role labels with the default `User: ` prompt and an `Assistant:` response header. `HarnessCommandHandler` adapts command handling for inline console selection and clears/repaints the CLI transcript when a saved conversation is loaded, while `Core.Startup.Runtime.AgentRuntime` adapts it for Web's deferred `/history` selection flow and returns restored transcript messages through the Startup facade. The Web client does not run the console loop; `WebSessionHub` sends browser messages through `WebAgentRuntimeHost`, which asks `AgentRuntime` to handle supported harness commands before falling through to `AgentRuntime.SendUserMessageAsync`.
8. `AgentHarnessSession` starts with seeded runtime context only. A user can explicitly load an old transcript with `/history`, `/conversations`, or `/load` before the first model turn, or start another fresh transcript in-place with `/new`.
9. Codex app-server `item/agentMessage/delta` notifications are written to the console as they arrive, and `turn/completed` supplies the final assistant message retained by the session and inference audit metadata.
10. Codex app-server `item/tool/call` requests for `embodysense.command` route through `Application.Governance.Tools.ToolBroker`. File-system commands resolve targets against `--workdir`, reject paths outside the workspace root, reject paths that pass through reparse points, evaluate `.agent/permissions.json`, prompt for human approval when required, delegate allowed filesystem work to `Clients.LocalWorkspace.LocalWorkspaceClient`, and append audit events for permission checks, approval requests/decisions, and execution outcomes. CLI approvals use `ConsoleToolApprovalPrompt`; browser approvals use `WebApprovalCoordinator`.

Native Codex app-server command, file-change, permission, MCP elicitation, and user-input requests are fail-closed and audited in this harness path. The app-server working directory is not the user workspace; governed workspace access is exposed through `embodysense.command`.

The older provider-neutral fenced-JSON tool protocol has been removed. Dynamic app-server tools are the only supported governed tool integration.

Implemented governed commands:

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

Human-facing transcript controls are available from the CLI harness loop and the localhost Web UI. `/history`, `/conversations`, and `/load` load a saved transcript before the first model turn in the current session, replace the visible conversation transcript, and keep the restored messages in the agent session. In the Web UI, `/history` lists stored conversations and the next submitted number loads that selection; successful loads emit a `history_loaded` stream event that replaces the browser transcript before the confirmation message is appended. `/cancel` cancels the pending selection. `/new` and `/new-session` start another fresh transcript without leaving the harness. Enabled command-trigger custom loops can be invoked from chat with `/loop-id`, where `loop-id` is the loop definition id. Verbose mode can be changed from the Web UI toggle or with `/verbose`, `/verbose on`, and `/verbose off`; it prints the visible inference context EmbodySense is about to send for the next model turn and labels it as visible context, not private model reasoning or hidden chain-of-thought. Durable planning commands, background/resumable custom-loop execution, automatic free-form custom-loop trigger routing, capability registry beyond current workspace-command and loop-definition capability IDs, automatic hooks, agent-facing conversation-history query tooling, non-obfuscated compaction artifacts, structured memory directories beyond the transcript store, MCP execution, scheduled jobs, subagents, and multi-provider orchestration are not implemented in this status snapshot.

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

The browser UI can initialize the scratch workspace explicitly when `.agent/permissions.json` is missing. It then handles supported slash commands such as `/help`, `/history`, `/verbose`, and `/new`, shows visible role labels on transcript messages, exposes a Verbose toggle for visible-context debug output, replaces the session view with restored transcript messages after a successful history load, streams assistant responses over SignalR, and pushes governed tool approval requests into the approvals panel.

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

Start the harness loop from scratch. If the workspace is not already initialized, the `run` command warns first and asks whether to create workspace scaffolding before entering the loop:

```powershell
dotnet run --project ..\src\EmbodySense.Cli -- run --workdir .
```

Add `--verbose` to print the visible inference context before each model turn. Inside the harness loop, type a message at the `User: ` prompt and press Enter. Assistant output streams under an `Assistant:` header as provider chunks arrive. Type `/help` to list commands. Type `/verbose`, `/verbose on`, or `/verbose off` to inspect or change visible-context debug output. Type `/history`, `/conversations`, or `/load` before the first model turn to list saved `.agent/memory/conversations/*.ndjson` and `.agent/memory/conversations/archive/*.ndjson` transcripts by first-prompt preview and select one to load; a successful load clears the interactive console and prints the restored transcript before the confirmation. Type `/new` or `/new-session` to start another fresh conversation without leaving the harness. Type `/exit`, `/quit`, `exit`, or `quit` to leave.

The harness reads the seeded `.agent/` documents when `run` starts. Restart the loop after editing those documents if you want the active provider thread to receive the new context.

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

The implementation flow is documented in the draw.io-compatible source diagram at [`docs/AGENT_LOOP.drawio`](docs/AGENT_LOOP.drawio). Keep that diagram aligned with implemented loop behavior when the real CLI loop, inference, workspace, permission, or audit behavior changes. The diagram is also a status artifact, not scope authority.

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
