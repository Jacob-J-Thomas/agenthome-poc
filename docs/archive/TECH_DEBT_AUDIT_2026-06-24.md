# EmbodySense Tech Debt Audit - 2026-06-24

This is a read-only technical-debt review of the current `agenthome-poc` repository state, with documentation debt ignored unless it materially affects source truth, governance, or product scope. Findings are anchored to the live source, `AGENTS.md`, and `docs/OPINIONATED_PROJECT_AXIOMS.md`.

## Snapshot Caveats

- The checkout was dirty before the audit began and continued changing while the audit was running. Notably, Codex app-server tests moved from `tests/EmbodySense.Core.Clients.Tests/CodexAppServer/` to `tests/EmbodySense.IntegrationTests/CodexAppServer/` during the review.
- I did not stage, unstage, commit, or edit production/test source. This report is the only repository file added by this pass.
- Build/test verification was run from an isolated temporary copy at `C:\tmp\agenthome-poc-techdebt-ba192948a3a04e15b01dd62b199916ff` to avoid writing build, restore, browser, or coverage artifacts into the live checkout where another agent is active. These commands prove that copied snapshot, not that the still-dirty live checkout remained unchanged afterward.
- Several subagent findings were rechecked against current source before inclusion. One stale subagent concern about missing production reference guards was rejected because the live checkout now contains `tests/EmbodySense.IntegrationTests/Architecture/ProjectReferenceGuardTests.cs`.

## Verification Performed

- `dotnet build EmbodySense.sln /p:RestoreIgnoreFailedSources=true` in the temp copy: passed, 0 warnings, 0 errors.
- `dotnet test EmbodySense.sln --no-build /p:RestoreIgnoreFailedSources=true` in the temp copy: passed, 174 tests.
- `npm.cmd test` in the temp copy: passed, 4 frontend tests.
- `dotnet test EmbodySense.sln --no-build --collect:"XPlat Code Coverage" /p:RestoreIgnoreFailedSources=true` in the temp copy: passed.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-coverage.ps1` in the temp copy: passed with current package coverage:
  - `EmbodySense.Cli`: 93.1%
  - `EmbodySense.Cli.Command`: 96.38%
  - `EmbodySense.Core.Application`: 93.57%
  - `EmbodySense.Core.Clients`: 90.83%
  - `EmbodySense.Core.Common`: 97.73%
  - `EmbodySense.Core.Persistence`: 91.88%
  - `EmbodySense.Core.Startup`: 92.45%
  - `EmbodySense.Web`: 95.08%

## Tier Rubric

- **Tier 1 - Critical / strategic debt:** Can undermine security, governance, instruction hierarchy, core product direction, or major future architecture.
- **Tier 2 - High debt:** Likely to cause reliability, scaling, operational, or maintainability problems as usage expands.
- **Tier 3 - Medium debt:** Manageable now but worth paying down before the surface area grows.
- **Tier 4 - Low / watchlist:** Not urgent, but useful to track to avoid drift.

## Executive Summary

The codebase is in a healthy transitional state in several ways: the temp-copy snapshot built, the .NET and frontend tests passed, coverage was above the repo's 90% line threshold, production project references currently follow the intended layering, and the frontend mostly avoids HTML injection by using DOM APIs and `textContent`.

The highest-risk debt is not basic correctness. It is governance and product-scope alignment:

- Workspace docs and restored user/assistant transcript are collapsed into provider `developerInstructions`, creating an instruction-priority and persistent prompt-injection risk.
- The localhost Web UI mints its control-plane token from an anonymous endpoint and uses global approvals across all authenticated clients.
- The harness tells models to use `.agent/MEMORY.md` as the primary durable store, but default governed file permissions do not actually allow that write path without approval.
- Several axiom-level capabilities are absent or hard-coded off rather than exposed as governed, configurable harness capabilities.
- Several read paths are intentionally broad and useful right now, but they are unbounded: configuration snapshots, workspace file tools, audit tailing, and conversation-memory scans can grow into performance, disclosure, or reliability problems.

## Tier 1 Findings

### T1-01 - Workspace Docs and Restored Transcript Are Promoted Into Provider `developerInstructions`

**Evidence**

- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerContextBuilder.cs:35-39` selects prior system/user/assistant messages before the latest user message.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerContextBuilder.cs:47-58` formats that restored context into a single developer-instruction string.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs:237-246` sends the result as `developerInstructions`.
- `src/EmbodySense.Core.Persistence/Workspace/WorkspaceContextStore.cs:8-16` loads `AGENTS.md` and `.agent/*` documents into startup context.
- `src/EmbodySense.Core.Application/Context/AgentContextProvider.cs:40-48` wraps those documents in a system message.
- `tests/EmbodySense.IntegrationTests/CodexAppServer/CodexAppServerInferenceTests.cs:186-211` currently codifies this behavior.

**Debt**

Workspace documents first become a system message, then previous system/user/assistant messages are embedded into a higher-priority provider instruction channel. The builder labels those chunks, but they still arrive inside `developerInstructions`, which can make mutable workspace text or stale user text look like privileged context to the provider.

**Why It Matters**

This creates a persistent prompt-injection and instruction-hierarchy hazard. Workspace docs and previous transcripts can contain adversarial, obsolete, or lower-authority instructions. When loaded later, they are not merely restored as user-visible context; they are promoted into the same channel that carries harness rules.

**Remediation Direction**

Keep harness invariants in `developerInstructions`, but pass workspace documents and restored conversation as clearly lower-authority, quoted, untrusted context. Truncate on document/message boundaries, preserve roles, and add tests proving mutable workspace docs and previous user text cannot become developer-level instruction.

### T1-02 - Web Session Bootstrap Mints the Control-Plane Token Anonymously

**Evidence**

- `src/EmbodySense.Web/Controllers/SessionController.cs:8-26` allows anonymous access and returns the session token.
- `src/EmbodySense.Web/Services/WebSessionSecurity.cs:53-63` treats that token as sufficient for protected APIs and hub access.
- `src/EmbodySense.Web/wwwroot/app.js:29-34` fetches the anonymous token during boot.
- `src/EmbodySense.Web/WebRunOptions.cs:13-14` defaults binding to localhost-only hosts.
- `src/EmbodySense.Web/Services/WebSessionAuthenticationHandler.cs:26-34` and `src/EmbodySense.Web/Services/WebSessionSecurity.cs:30-51` enforce localhost host/origin checks for authenticated requests.

**Debt**

The Web UI has a real authentication layer, but the credential is available to any local caller that can reach `/api/session`. The source comment at `SessionController.cs:25` correctly calls this out as not hardened.

**Why It Matters**

The token gates workspace initialization, configuration inspection, SignalR access, and approval decisions. Host and origin checks reduce this to local callers in normal operation, but the bootstrap still does not distinguish the user's intended browser from another local process or eligible local browser context.

**Remediation Direction**

Replace anonymous bootstrap with an explicit local pairing flow: one-time code, server-launched browser URL, short token lifetime, token rotation, and bootstrap host/origin checks. Consider an HttpOnly/SameSite cookie for normal HTTP APIs and a short-lived hub token only where WebSocket constraints require query authentication.

### T1-03 - Web Approvals Are Global Across Authenticated Clients

**Evidence**

- `src/EmbodySense.Web/Hubs/WebSessionHub.cs:23-27` sends all pending approvals to every new authenticated connection.
- `src/EmbodySense.Web/Hubs/WebSessionHub.cs:30-35` broadcasts status to all clients.
- `src/EmbodySense.Web/Services/SignalRWebClientNotifier.cs:18-22` broadcasts approval changes to `Clients.All`.
- `src/EmbodySense.Web/Services/WebApprovalCoordinator.cs:51-69` lets any authenticated client with a request id decide an approval.
- `src/EmbodySense.Web/Services/WebApprovalCoordinator.cs:63` records all decisions as shared `human.web`.

**Debt**

The implementation is effectively single-user, but the protocol treats all authenticated clients as one shared authority.

**Why It Matters**

Any paired tab or local client can see target paths and approve/reject any pending operation. That can be acceptable during early localhost development, but it is not a durable governance model for a harness whose safety story depends on approval auditability.

**Remediation Direction**

Bind approvals to a paired browser session, SignalR group, or nonce. Include session identity in `DecisionBy`. Make cross-client approval visibility explicit and auditable if it remains intentional.

### T1-04 - Durable Memory Guidance Conflicts With Default Governed Permissions

**Evidence**

- `src/EmbodySense.Core.Application/Context/AgentContextProvider.cs:43-44` tells the model to use `.agent/MEMORY.md` as the primary place to store, update, create, and retrieve most memories.
- `src/EmbodySense.Core.Startup/Workspace/WorkspaceDefaults.cs:67-70` repeats that memory policy in generated workspace guidance.
- `src/EmbodySense.Core.Application/Governance/Permissions/Models/PermissionsDocument.cs:24-34` approves `.agent/tasks`, `.agent/exports`, `.agent/skills`, and `.agent/recipes`, but not `.agent/MEMORY.md` or `.agent` memory writes.
- `src/EmbodySense.Core.Application/Governance/Tools/ToolPermissionService.cs:74-83` evaluates file writes against the parent directory.

**Debt**

The model is instructed to update `.agent/MEMORY.md`, but the default governed tool policy routes such writes to approval because the parent `.agent` directory is not approved. Broad `.agent` write access would be unsafe, but the current policy does not provide a first-class memory-write mechanism either.

**Why It Matters**

This makes a default, axiom-level capability feel enabled in the prompt while operationally gated like an exceptional file write. It can lead to failed memory updates, unnecessary approval prompts, or pressure to broaden `.agent` permissions too far.

**Remediation Direction**

Add file-level permissions or a dedicated governed memory command that can update `.agent/MEMORY.md` safely, with audit metadata and conflict handling. Avoid solving this by granting blanket mutable access to `.agent`.

### T1-05 - Axiom-7 Capabilities Are Not Yet Exposed as Governed Harness Capabilities

**Evidence**

- `docs/OPINIONATED_PROJECT_AXIOMS.md:21` says the agent should always have access by default to memory, conversation history, MCP plugins/integrations, skills, agent documents, cron scheduling, hooks, subagent runs, complex planning, and heartbeats/wake inputs, with many off by default but easy to enable.
- `src/EmbodySense.Core.Application/Governance/Tools/Models/ToolCommand.cs:3-10` exposes only file-oriented governed commands.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerRequestHandler.cs:25-48` declines native app-server commands, file changes, permission requests, MCP elicitation, and user-input requests.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs:277-289` disables shell, multi-agent, and web search at the app-server config layer.
- `src/EmbodySense.Core.Application/Harness/HarnessCommandService.cs:67-83` only handles exit/new/history/help-style commands.

**Debt**

Several axiom-level capabilities exist as directory scaffolding, docs, or future concepts, but they are not yet available as model-accessible, governed capabilities with configuration, permissions, approvals, and auditability.

**Why It Matters**

This is the biggest product-scope gap after the immediate security/governance issues. The harness is deliberately preventing unsafe native app-server behavior, which is good, but it has not yet replaced those denied paths with EmbodySense-governed equivalents for the expected capability set.

**Remediation Direction**

Design a capability registry in `Core.Application`/`Core.Startup` that makes each axiom-level capability explicit: disabled by default where appropriate, enabled through obvious workspace configuration, routed through permission/approval checks, and audited. Avoid re-enabling raw app-server native tools as the shortcut.

## Tier 2 Findings

### T2-01 - Governed Workspace File Tools Lack Resource Budgets

**Evidence**

- `src/EmbodySense.Core.Clients/LocalWorkspace/LocalWorkspaceClient.cs:35-43` reads entire files into memory.
- `src/EmbodySense.Core.Clients/LocalWorkspace/LocalWorkspaceClient.cs:61-70` recursively searches all files in a directory.
- `src/EmbodySense.Core.Clients/LocalWorkspace/LocalWorkspaceClient.cs:135-144` reads every searched file fully and returns matching lines.
- `src/EmbodySense.Core.Application/Governance/Tools/ToolResultFormatter.cs:15-24` returns full tool outputs to the model.

**Debt**

Permission and audit checks exist, but there are no file-size limits, binary detection, match caps, output truncation, timeout budgets, or result pagination.

**Why It Matters**

A governed read/search can still consume excessive memory, scan huge trees, emit too much context to the model, or expose more data than the user intended after a single approval.

**Remediation Direction**

Add configurable budgets for file size, total bytes read, maximum files, maximum matches, and output length. Mark truncated results clearly in both model output and audit metadata. Treat binary or unknown encodings separately from text.

### T2-02 - Configuration API Is Sensitive and Unbounded

**Evidence**

- `src/EmbodySense.Web/Controllers/ConfigurationController.cs:22-25` returns the complete snapshot in one call.
- `src/EmbodySense.Core.Startup/Configuration/WorkspaceConfigurationReader.cs:21-24` loads permissions, documents, audit, and conversation history every time.
- `src/EmbodySense.Core.Startup/Configuration/WorkspaceConfigurationReader.cs:86` reads raw `permissions.json`.
- `src/EmbodySense.Core.Startup/Configuration/WorkspaceConfigurationReader.cs:191` reads full agent document contents.
- `src/EmbodySense.Core.Startup/Configuration/WorkspaceConfigurationReader.cs:224-257` reads all audit lines.
- `src/EmbodySense.Core.Startup/Configuration/WorkspaceConfigurationReader.cs:296-309` enumerates transcript files.
- `src/EmbodySense.Core.Startup/Configuration/WorkspaceConfigurationReader.cs:339-381` reads all transcript messages.

**Debt**

The read-only configuration dashboard is useful, but it currently has no paging, detail endpoints, byte limits, redaction, or secret classification.

**Why It Matters**

As the harness becomes a primary surface, this endpoint can become slow and can expose memory, model config, transcript content, absolute paths, and audit metadata more broadly than intended to any authenticated web session.

**Remediation Direction**

Split summary and detail APIs. Add paging/tail parameters for audit/history. Add byte caps for documents and raw JSON. Redact likely secret-bearing fields and require explicit reveal for full content.

### T2-03 - Query-String Tokens Are Accepted Outside the Hub Use Case

**Evidence**

- `src/EmbodySense.Web/wwwroot/app.js:94-98` puts `access_token` in the SignalR WebSocket URL.
- `src/EmbodySense.Web/Services/WebSessionSecurity.cs:57-63` accepts either the header token or query-string token for any request.

**Debt**

Browser WebSocket clients often need query tokens, but this implementation accepts query tokens generically instead of scoping them to `/hubs/session`.

**Why It Matters**

Query tokens are more likely to leak through copied URLs, diagnostics, logs, and future referrers. Generic acceptance widens that leak surface.

**Remediation Direction**

Restrict query-token auth to the hub path and prefer headers or cookies for normal API calls. Redact token-bearing URLs from any future logging.

### T2-04 - Audit Storage Is Append-Only in Intent, Not Robust in Implementation

**Evidence**

- `src/EmbodySense.Core.Persistence/Audit/AuditLog.cs:43-50` appends via `File.AppendAllTextAsync`.
- `src/EmbodySense.Core.Persistence/Audit/AuditLog.cs:64-69` reads all lines before taking the requested tail.

**Debt**

There is no append serialization across concurrent writers or processes, no file locking strategy, and tailing reads the whole log. A malformed line also throws from `AuditLog.ReadTailAsync`, whereas the configuration reader is more tolerant.

**Why It Matters**

The audit log is part of the governance story. Dropped, interleaved, failed, or unreadable audit writes weaken the ability to explain what the harness did.

**Remediation Direction**

Use a serialized append queue per process plus cross-process file locking where needed. Implement streaming tail reads. Skip/report malformed lines rather than making the whole tail unreadable.

### T2-05 - Conversation Memory Has Race and Scale Hazards

**Evidence**

- `src/EmbodySense.Core.Persistence/Memory/ConversationMemoryStore.cs:113-127` appends messages directly to `current.ndjson`.
- `src/EmbodySense.Core.Persistence/Memory/ConversationMemoryStore.cs:154-158` computes the next sequence by loading current entries.
- `src/EmbodySense.Core.Persistence/Memory/ConversationMemoryStore.cs:173-183` treats malformed entries as fatal.
- `src/EmbodySense.Core.Persistence/Memory/ConversationMemoryStore.cs:39-43` loads every conversation file to list conversations.

**Debt**

Conversation memory is file-backed and simple, but it lacks locking, append atomicity, sequence allocation safety, indexing, and tolerant recovery for partially corrupted transcript files.

**Why It Matters**

The Web host serializes turns today, but future surfaces, crashes, or external edits can create duplicate sequence numbers, partial entries, or history commands that fail because one transcript line is malformed.

**Remediation Direction**

Add a conversation store lock, append journal semantics, tolerant per-line error reporting, and a lightweight transcript index for listing. Consider making transcript writes transaction-like before broader web/TUI concurrency arrives.

### T2-06 - Runtime Turn Serialization Lives in Web, Not the Core Runtime Contract

**Evidence**

- `src/EmbodySense.Web/WebAgentRuntimeHost.cs:16-17` owns `_runtimeGate` and `_turnGate`.
- `src/EmbodySense.Web/WebAgentRuntimeHost.cs:69-99` serializes web turns.
- `src/EmbodySense.Core.Startup/Runtime/AgentRuntime.cs:43-68` exposes message and command entry points without its own turn gate.
- `src/EmbodySense.Core.Application/Harness/AgentHarnessSession.cs:11` uses a mutable `_messages` list.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs:17-24` has mutable transport/thread state around a single app-server stream.

**Debt**

The Web surface currently protects the runtime from overlapping turns, but the reusable runtime and app-server adapter do not make that invariant explicit.

**Why It Matters**

The architecture goal is reusable core behavior for CLI, TUI, browser-localhost, and future clients. Any future surface can bypass the web-only gate and corrupt message order, steal app-server responses, or race conversation memory.

**Remediation Direction**

Move turn serialization or a clear single-turn lease into `AgentRuntime` or a core runtime facade. Document and test the invariant at the reusable boundary rather than each interface edge.

### T2-07 - App-Server Adapter Lacks Timeouts and Protocol Hardening

**Evidence**

- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs:72-122` waits until both turn-start response and completion are observed.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs:170-189` waits for a thread id.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs:214-234` waits for initialization.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs:343-356` parses every line as JSON without tolerant handling.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerProcessTransport.cs:83-99` stores stderr in an unbounded `StringBuilder`.

**Debt**

The adapter relies on caller cancellation and well-formed server behavior. There are no operation-specific timeouts, protocol version checks, bounded stderr capture, or recovery strategy for malformed frames.

**Why It Matters**

An app-server protocol change, partial frame, stuck turn, or noisy stderr stream can hang a turn, blow up memory, or surface raw internal errors to the user.

**Remediation Direction**

Add explicit timeouts for initialize/thread/turn phases, bounded stderr buffers, protocol/capability validation, and better diagnostics for unexpected frames. Consider isolating protocol parsing from runtime state handling.

### T2-08 - Coverage Verification Can Use Stale Files and Has a Hard-Coded Production Package Set

**Evidence**

- `scripts/verify-coverage.ps1:19-21` selects the newest existing `coverage.cobertura.xml` under each test project's `TestResults`.
- `scripts/verify-coverage.ps1:33-42` hard-codes the expected production packages.
- `scripts/verify-coverage.ps1:45-49` does derive source project directories for path normalization, but not for the expected package set.
- `scripts/verify-coverage.ps1:97-99` ignores packages not in that hard-coded list.

**Debt**

The current coverage gate passes, but the verifier is not tied to a specific test run and its expected production package set is maintained manually.

**Why It Matters**

Old coverage files can mask a failed, skipped, or partial current coverage run. A new production assembly can be added without automatically becoming part of the 90% gate unless the script is updated.

**Remediation Direction**

Create a single verify entrypoint that cleans or isolates `TestResults`, runs coverage into a deterministic directory, derives expected production assemblies from the solution/source projects, and fails on missing or unexpected production packages.

### T2-09 - Browser/E2E Tests Are Valuable but Environment-Fragile

**Evidence**

- `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:153-160` uses `GetFreePort()` and releases the port before Kestrel binds.
- `tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs:77-84` repeats the same free-port pattern.
- `tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs:292-309` hard-codes Windows Edge/Chrome install paths and throws if none is found.
- `tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs:153-175` polls browser expressions with fixed sleeps.
- `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:191-208` polls `/api/status` with fixed sleeps.

**Debt**

The E2E suite now runs as part of `EmbodySense.sln`, but some tests depend on local browser installation details, free-port races, and timing loops.

**Why It Matters**

The tests passed locally in the temp copy, but they can become flaky under CI load, different OS images, or concurrent local process activity.

**Remediation Direction**

Bind Kestrel to port `0` and read the actual address. Use environment-aware browser discovery or explicit skip conditions. Centralize wait helpers with diagnostics. Decide which E2E tests are normal solution gates and which are opt-in smoke tests.

### T2-10 - There Is No CI/Verify Orchestration for the Whole Quality Surface

**Evidence**

- `.github/workflows/` exists but is empty in the current checkout.
- `package.json:4-5` runs only frontend Node tests.
- `dotnet test EmbodySense.sln` does not run `npm.cmd test`.
- `scripts/verify-coverage.ps1` verifies coverage only after a separate coverage run.

**Debt**

The repo has several verification commands, but no single source-backed CI or local verify command that runs build, .NET tests, frontend tests, coverage collection, and coverage verification together.

**Why It Matters**

Different agents can claim different verification surfaces. The frontend can regress while `dotnet test` is green, or coverage can be verified against stale files.

**Remediation Direction**

Add a root `verify` script and CI workflow. Separate fast/unit checks from browser/E2E checks if needed, but make the expected gate explicit.

### T2-11 - Inference Is Still Codex-Only and Not Config-Driven

**Evidence**

- `src/EmbodySense.Core.Startup/Runtime/AgentRuntimeFactory.cs:39-46` hard-codes `LlmInferenceSurface.OpenAiCodex`.
- `src/EmbodySense.Core.Application/Inference/Models/LlmInferenceSurface.cs:3-7` only contains `AzureAiFoundry` and `OpenAiCodex`.
- `src/EmbodySense.Core.Startup/Inference/LlmInferenceClientFactory.cs:20-25` returns `NotSupportedInferenceClient` for Azure and other surfaces.
- `src/EmbodySense.Core.Startup/Workspace/WorkspaceDefaults.cs:166-184` seeds `.agent/models.json` as placeholders, not as live configuration.

**Debt**

The model/provider abstraction exists in type names, but runtime composition does not consume workspace model configuration and only one provider is actually wired.

**Why It Matters**

This is strategic roadmap debt rather than an immediate correctness defect. The axioms want model/API agnosticism and local-inference readiness, but they also say provider support should change carefully and rarely. The debt is that the current provider boundary is not yet exercised as a real configuration contract.

**Remediation Direction**

Introduce a `Core.Startup` provider registry/factory driven by validated workspace configuration. Keep unsupported providers explicit, but make `.agent/models.json` either live or clearly not part of runtime behavior. Plan for per-turn provider selection after the basic provider boundary is real.

## Tier 3 Findings

### T3-01 - Workspace Configuration Reader Is a Large Multi-Concern Aggregator

**Evidence**

- `src/EmbodySense.Core.Startup/Configuration/WorkspaceConfigurationReader.cs` owns path summaries, permissions parsing, document reads, audit parsing, conversation-history parsing, metadata formatting, and concept construction in one class.

**Debt**

The class is convenient for the first dashboard, but it combines many future change axes: security/redaction, paging, parsing, path taxonomy, document classification, and UI contract shape.

**Why It Matters**

Any future config tab or redaction rule risks touching the same large file. That makes security-oriented changes harder to review cleanly.

**Remediation Direction**

Split into focused readers: permissions summary, document catalog, audit summary/tail, conversation summary/detail, and concept registry. Keep `WorkspaceConfigurationReader` as a thin aggregator.

### T3-02 - Web Frontend Uses a Hand-Rolled SignalR Client

**Evidence**

- `src/EmbodySense.Web/wwwroot/app.js:684-814` implements `JsonSignalRConnection`.
- `src/EmbodySense.Web/wwwroot/app.js:719-727` performs handshake handling manually.
- `src/EmbodySense.Web/wwwroot/app.js:730-740` has no invocation timeout.
- `src/EmbodySense.Web/wwwroot/app.js:747-770` parses frames without error recovery.
- `src/EmbodySense.Web/wwwroot/app.js:101-110` reconnects with a fixed 1-second loop and no backoff/jitter.

**Debt**

The custom client avoids a dependency, but it reimplements a protocol surface that already has a maintained client library.

**Why It Matters**

Missing invocation timeouts, duplicate reconnect loops, malformed-frame handling, and protocol drift can make the UI feel stuck or unreliable as streaming and approval flows grow.

**Remediation Direction**

Either adopt the official SignalR JavaScript client or make this custom client a deliberately tested module with invocation timeouts, reconnect backoff, close semantics, and frame-parse error handling.

### T3-03 - Web Error Responses Expose Internal Exception Text

**Evidence**

- `src/EmbodySense.Web/Hubs/WebSessionHub.cs:63-65` streams caught exception messages directly to the browser.
- `src/EmbodySense.Web/wwwroot/app.js:37-44` throws raw HTTP response text for non-OK API responses.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs:350-353` can include raw app-server stderr in exceptions.

**Debt**

Localhost reduces risk, but detailed internal paths, process errors, or app-server diagnostics can leak into the user-visible transcript.

**Why It Matters**

The transcript is a user-facing conversation surface. Mixing internal diagnostics with normal assistant/error messages can expose sensitive paths and make later transcript restoration noisier.

**Remediation Direction**

Map expected failures to stable user-facing messages. Put detailed diagnostics in audit/log/config detail views with explicit reveal semantics.

### T3-04 - Web Surface Lacks Security Headers / CSP

**Evidence**

- `src/EmbodySense.Web/Program.cs:68-73` configures static files, auth, controllers, and hub routes with no security-header middleware.
- `src/EmbodySense.Web/wwwroot/app.js:1-31` holds the session token in JavaScript state.

**Debt**

The frontend uses `textContent` well today, but there is no defense-in-depth browser policy.

**Why It Matters**

As more UI code and possibly generated content enter the page, CSP and related headers reduce blast radius if an injection path appears.

**Remediation Direction**

Add `Content-Security-Policy`, `frame-ancestors 'none'`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, and a clear local-dev policy for websocket connections.

### T3-05 - Runtime Composition Is Rigid Despite Legal Layering

**Evidence**

- `src/EmbodySense.Core.Startup/Runtime/AgentRuntimeFactory.cs:53-67` directly constructs workspace paths, permission store, permission service, audit log, workspace client, tool broker, conversation memory, context provider, inference client, and session.

**Debt**

This keeps `RunCommand` thin and preserves project references, but the factory is becoming a service locator by hand.

**Why It Matters**

Provider selection, tool capability config, alternative persistence, test fakes, browser/TUI differences, and future planning services will all compete for this constructor.

**Remediation Direction**

Introduce a small composition options object or internal DI-style builder in `Core.Startup`. Keep CLI/Web referencing only Startup facades, but make the concrete graph configurable and testable.

### T3-06 - App-Server Capabilities Are Hard-Coded Off Instead of Configurable

**Evidence**

- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs:277-289` disables shell tool, multi-agent, and web search.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerRequestHandler.cs:25-48` declines native command/file/permission/MCP/user-input requests.

**Debt**

This is a concrete instance of T1-05 at the app-server boundary. The current behavior matches the live AGENTS constraints, but the axiom direction says many capabilities should be off by default but easy to turn on through explicit configuration.

**Why It Matters**

Hard-coded denial is safer now, but it can make later capability work invasive and encourage bypasses instead of governed enablement.

**Remediation Direction**

Add explicit workspace capability configuration with restrictive defaults, approval gates, and audit events. Keep denials as the default policy, not as unchangeable code paths.

### T3-07 - CLI/Web Option Parsing Is Hand-Rolled and Permissive

**Evidence**

- `src/EmbodySense.Cli.Command/Models/CliArguments.cs:38-57` returns the next token as an option value without checking whether it is another option.
- `src/EmbodySense.Web/WebRunOptions.cs:46-57` has the same pattern.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs:478-485` silently downgrades unknown sandbox strings to `read-only`.

**Debt**

Invalid CLI/Web invocations can be misparsed instead of rejected. Invalid sandbox configuration can be silently masked.

**Why It Matters**

For a governance-sensitive tool, configuration mistakes should be explicit. Silent fallback is safe in one dimension but confusing when debugging why a configured mode was ignored.

**Remediation Direction**

Add a small shared option parser or stricter validation helpers. Reject missing option values and unknown sandbox values with clear messages.

### T3-08 - Build Reproducibility Is Loose

**Evidence**

- `Directory.Build.props:3` uses `LangVersion` `latest`.
- `Directory.Build.props:6` sets `TreatWarningsAsErrors` to `false`.
- There is no `global.json` in the checkout.
- There is no `Directory.Packages.props` or package lock file in the checkout.
- Test project package references are duplicated across test `.csproj` files.

**Debt**

The build currently passes, but SDK/language/package behavior can drift by machine, and warnings can accumulate.

**Why It Matters**

This repo is increasingly dependent on many test projects and generated-like local app behavior. Reproducibility matters more as multiple agents work in parallel.

**Remediation Direction**

Add `global.json`, central package management, package lock/restore policy where useful, and warnings-as-errors in CI. Centralize common test package references and runner metadata.

### T3-09 - Test Package References Lack Standard Runner/Collector Metadata

**Evidence**

- Test projects repeatedly declare `coverlet.collector`, `Microsoft.NET.Test.Sdk`, `xunit`, and `xunit.runner.visualstudio` directly.
- The package references do not consistently declare `PrivateAssets` / `IncludeAssets`.

**Debt**

This is ordinary early-stage project debt: repeated package declarations make version drift likely and can leak test-only assets in surprising ways.

**Why It Matters**

As the test matrix grows, package updates become mechanical churn across many files.

**Remediation Direction**

Move versions to `Directory.Packages.props` or shared test props. Add `PrivateAssets="all"` and appropriate `IncludeAssets` for test runner/collector packages.

### T3-10 - E2E Process Tests Redirect Output Without Draining It

**Evidence**

- `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:172-178` starts the Web process with redirected stdout/stderr.
- `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:211-219` kills and waits for the process, but output is not drained during the run.

**Debt**

The current Web process writes little output, so the test passes. But redirected streams can deadlock if output grows enough to fill OS buffers.

**Why It Matters**

Future startup logging or debug output can turn an otherwise healthy E2E test into a hang.

**Remediation Direction**

Drain stdout/stderr asynchronously into bounded buffers and include them in failure diagnostics.

### T3-11 - Source Files Are Starting to Accumulate Multiple Responsibilities

**Evidence**

- `src/EmbodySense.Web/wwwroot/app.js` is the largest frontend file and contains boot, API calls, rendering, approvals, transcript state, and SignalR protocol code.
- `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerInferenceClient.cs` owns protocol state, transport lifecycle, request/notification parsing, thread/turn logic, and runtime directory cleanup.
- `src/EmbodySense.Core.Startup/Configuration/WorkspaceConfigurationReader.cs` owns several independent configuration readers.

**Debt**

These files are still understandable, but they are becoming change magnets.

**Why It Matters**

Future work will likely touch streaming, approvals, configuration, provider routing, and history together. Large change magnets make review and regression isolation harder.

**Remediation Direction**

Split only along real seams: frontend transport vs rendering vs state; app-server protocol parser vs session lifecycle; configuration summary vs detail readers.

## Tier 4 / Watchlist

### T4-01 - Workspace Scaffolding Is Not Transactional

**Evidence**

- `src/EmbodySense.Core.Persistence/Workspace/WorkspaceScaffolder.cs:21-29` creates directories and files sequentially.
- `src/EmbodySense.Core.Persistence/Workspace/WorkspaceScaffolder.cs:31-44` appends the init audit event only after all writes.

**Debt**

If initialization fails mid-way, the workspace can be partially scaffolded. `WorkspacePaths.IsInitialized` only checks `.agent` and `permissions.json`.

**Remediation Direction**

Make initialization idempotent with an explicit status marker or validation pass that reports partial scaffolding and repairs it intentionally.

### T4-02 - Recursive Delete Is Available Through the Governed File Tool

**Evidence**

- `src/EmbodySense.Core.Clients/LocalWorkspace/LocalWorkspaceClient.cs:116-129` deletes files and directories, including recursive directory deletion.
- Default permissions currently do not grant delete in standard writable directories, but policy can.

**Debt**

This is governed, not currently open by default. Still, recursive delete is a high-blast-radius operation once any policy grants it.

**Remediation Direction**

Add an extra confirmation tier or separate capability for recursive delete, with previews and audit details before execution.

### T4-03 - Production Layering Is Currently Good, But Needs Continued Guarding

**Evidence**

- `tests/EmbodySense.IntegrationTests/Architecture/ProjectReferenceGuardTests.cs:15-25` captures the intended production project graph.
- `tests/EmbodySense.IntegrationTests/Architecture/ProjectReferenceGuardTests.cs:42-68` checks that Web and CLI command source use Startup as their only Core API.

**Debt**

This is not a current violation. It is a watch item because the architecture relies on project references and namespace usage as guardrails.

**Remediation Direction**

Keep this test current as new interface surfaces or Core projects are added. Avoid weakening it during future refactors.

### T4-04 - Frontend Injection Handling Looks Good, But Must Stay Covered

**Evidence**

- `src/EmbodySense.Web/wwwroot/app.js` uses `createElement` and `textContent` for restored messages, config documents, raw permissions JSON, audit metadata, and approvals.
- `tests/frontend/app.test.mjs:10-31` verifies unsafe restored transcript content is not turned into markup.
- `tests/frontend/app.test.mjs:48-79` verifies raw config JSON is rendered as text.

**Debt**

This is mostly a positive finding. The watch item is that future frontend changes should preserve the DOM/textContent approach.

**Remediation Direction**

Keep private-scope frontend tests and add browser-backed smoke coverage for injection-sensitive views once the Web UI becomes the primary surface.

## Rejected or Downgraded Subagent Findings

- **Missing production project-reference guard:** Rejected for the current snapshot. `ProjectReferenceGuardTests` now exists and covers the production graph and Web/CLI namespace boundary.
- **Frontend HTML injection as a current defect:** Rejected. Current frontend code and tests use `textContent` for the inspected flows.
- **Current coverage below threshold:** Rejected. The temp-copy coverage run passed all expected packages above 90%.

## Suggested Remediation Sequence

1. Fix instruction-priority handling for restored transcript context. This is the clearest correctness/security issue.
2. Replace anonymous Web session bootstrap with pairing and bind approvals to specific sessions.
3. Add governed memory support for `.agent/MEMORY.md` without broad `.agent` write access.
4. Define the governed capability registry for axiom-level systems before re-enabling native app-server capability paths.
5. Add resource budgets and truncation to workspace tools, configuration snapshots, audit tails, and conversation history.
6. Make provider configuration real, even if only Codex is initially supported as the default.
7. Move turn serialization into the reusable runtime contract.
8. Create a root verify script/CI workflow that runs .NET, frontend, coverage, and selected E2E gates deterministically.
9. Pay down composition and frontend/app-server file-size debt by splitting along established runtime seams.

## Remediation Pass - 2026-06-24

This pass used two adversarial read-only reviews against the live dirty checkout before editing, plus a final adversarial close-out review against the resulting diff. The reviewers agreed that the instruction-priority bug, bounded web governance hardening, resource budgets, audit tail robustness, option validation, security headers, verification orchestration, and test hygiene were safe to fix now. They also agreed that broad capability/provider/memory/concurrency architecture should be deferred rather than rushed into the dirty checkout. The final close-out found two corrections, both addressed: directory search now caps enumeration before sorting/processing, and README/AGENTS verification guidance now points to the current-run verify wrapper.

The snapshot caveats above describe the original read-only audit. This remediation pass edited source, tests, scripts, and this ledger in the live checkout, but did not stage, unstage, commit, or reset any files.

### Fixed or Partially Fixed Now

- **T1-01 - Fixed.** `developerInstructions` now contains only harness invariants. Restored workspace docs and prior transcript messages are sent as lower-authority turn input with explicit untrusted-context framing and message-boundary truncation.
- **T1-02 - Partially fixed; pairing deferred.** `/api/session` still exists as the temporary anonymous bootstrap, but it now enforces the same localhost host/origin checks before returning a token. Full one-time pairing, token rotation, and cookie/hub-token split are deferred because they are a product/UX design change, not a safe audit cleanup patch.
- **T1-03 - Fixed for web-turn approvals; broader pairing deferred.** Approvals raised during a SignalR web turn are scoped to that owner connection, only visible to that connection, and record `human.web:<connectionId>` in the approval decision. Unowned REST-created approvals remain globally decidable for compatibility; a paired-browser identity model should replace that when T1-02 is designed.
- **T2-01 - Fixed with default budgets.** Governed workspace reads/searches now cap large reads, directory file enumeration, oversized-file consideration, search matches, long match lines, and formatter output. Truncation is surfaced in tool output and execution metadata.
- **T2-02 - Partially fixed; API split deferred.** The configuration snapshot now caps documents/raw JSON, tails audit events, caps transcript files/messages, and redacts likely secret-bearing keys. The full summary/detail endpoint split and explicit reveal semantics are deferred because the broad read-only dashboard is an intentional current surface and needs UI/API design.
- **T2-03 - Fixed.** Query-string session tokens are accepted only for `/hubs/session`, preserving SignalR/WebSocket compatibility without allowing token-bearing URLs on normal APIs.
- **T2-04 - Partially fixed; cross-process locking deferred.** Audit appends are serialized in-process, tail reads are streamed, and malformed tail lines are skipped. Cross-process file locking remains deferred because CLI/Web/process coordination needs a deliberate locking policy.
- **T2-07 - Partially fixed; parser extraction deferred.** App-server reads now have protocol timeouts, oversized-frame rejection, and bounded stderr capture. A separate protocol parser/state-machine refactor is deferred until the app-server adapter is next expanded.
- **T2-08 - Fixed.** `verify-coverage.ps1` derives expected production packages from `src/` projects and can reject stale coverage via a minimum timestamp. The new verify wrapper clears old `TestResults` before collecting coverage, and README/AGENTS now point to the wrapper as the normal path.
- **T2-09 - Partially fixed; installed-browser smoke made opt-in.** The browser harness now captures bounded stdout/stderr, uses fresh browser profiles, retries startup, and has harder headless launch flags. The default verify gate runs deterministic HTTP/SignalR/frontend/E2E coverage and skips the installed-browser smoke unless `EMBODYSENSE_RUN_BROWSER_E2E=1` or `scripts/verify.ps1 -RunBrowserE2E` is used. This is intentional because the local Edge process repeatedly failed with a GPU-process fatal unrelated to app behavior.
- **T2-10 - Fixed.** `scripts/verify.ps1`, `npm run verify`, `.github/workflows/verify.yml`, README, and AGENTS now define the repo verification surface: build, frontend tests, .NET coverage collection, coverage verification, and an explicit opt-in browser smoke switch.
- **T3-03 - Fixed at the web hub.** Expected hub failures now return a stable user-facing error instead of raw exception text. Detailed diagnostics stay in logs/audit/config surfaces.
- **T3-04 - Fixed.** The web pipeline now emits CSP, `frame-ancestors 'none'`, `X-Content-Type-Options: nosniff`, and `Referrer-Policy: no-referrer`.
- **T3-07 - Fixed.** CLI/Web option parsing now rejects missing values where the next token is another option. Unknown Codex sandbox modes are rejected instead of silently downgraded.
- **T3-09 - Fixed.** Test runner and coverage collector package references now declare `PrivateAssets` and `IncludeAssets` metadata.
- **T3-10 - Fixed.** The web E2E process test drains redirected stdout/stderr into bounded buffers and includes captured output in startup timeout diagnostics.

### Deferred or Do Not Fix Now

- **T1-04 - Deferred.** This should be solved with file-level permissions or a dedicated governed memory command for `.agent/MEMORY.md`. Broad `.agent` mutable access is explicitly rejected because it would weaken the AI/human/governance boundary.
- **T1-05 and T3-06 - Deferred.** The axiom-level capability registry is real product work. Native app-server capabilities should remain denied until EmbodySense-governed equivalents exist with configuration, approval, and audit semantics.
- **T2-05 - Deferred.** Conversation memory locking, journaling, tolerant recovery, and indexes should be designed with the core turn/concurrency contract. The current Web host still serializes turns, so this is important but not the right isolated patch.
- **T2-06 - Deferred.** Moving turn serialization into `AgentRuntime` needs a reusable runtime contract that works for CLI, TUI, Web, and future clients. Adding a narrow lock without that design would obscure the architecture decision.
- **T2-09 residual - Deferred.** Port-0 Kestrel binding, richer wait helpers, and broader browser discovery remain valuable, but installed-browser execution should stay opt-in until CI/environment policy chooses a stable browser runtime.
- **T2-11 - Deferred.** Provider/model configuration should change carefully, as the axioms require. `.agent/models.json` should become live only after a provider registry design, not through a quick Codex/Azure toggle.
- **T3-01 and T3-11 - Deferred.** Large files are acknowledged, but pure splitting is not a debt payoff by itself. Split `WorkspaceConfigurationReader`, `app.js`, and `CodexAppServerInferenceClient` when doing the next functional change along those seams.
- **T3-02 - Deferred.** The custom SignalR client is acceptable for now because adding the official dependency would be a surface/dependency choice. Keep targeted timeout/reconnect/frame tests until the UI grows enough to justify the dependency.
- **T3-05 - Deferred.** Runtime composition should be made configurable alongside provider and capability registry work. Refactoring the factory alone would be churn without the real extension points.
- **T3-08 - Deferred except for verify orchestration.** CI/verify now exists, but SDK pinning, central package management, lock files, and warnings-as-errors should wait until the team chooses the reproducibility policy for local Windows and CI.
- **T4-01 - Deferred/watch.** Transactional scaffolding is useful but low priority while initialization remains small and idempotent enough. Add a marker/repair pass when workspace initialization gains more side effects.
- **T4-02 - Deferred/watch.** Recursive delete is high blast radius, but default permissions still exclude delete. Add preview/extra confirmation when delete becomes enabled by default or exposed through a higher-level capability.
- **T4-03 - Do not fix now.** This is a positive watch item. Existing architecture guard tests should stay current rather than being changed for their own sake.
- **T4-04 - Do not fix now.** This is a positive watch item. Current frontend rendering remains DOM/textContent-based with tests; preserve that pattern in future UI changes.

### Final Verification After Remediation

- `dotnet test tests\EmbodySense.E2ETests\EmbodySense.E2ETests.csproj /p:RestoreIgnoreFailedSources=true`: passed, with the installed-browser smoke skipped by default.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -SkipRestore`: passed.
- Final default coverage gate:
  - `EmbodySense.Cli`: 93.1%
  - `EmbodySense.Cli.Command`: 96.55%
  - `EmbodySense.Core.Application`: 93.59%
  - `EmbodySense.Core.Clients`: 90.96%
  - `EmbodySense.Core.Common`: 97.73%
  - `EmbodySense.Core.Persistence`: 91.89%
  - `EmbodySense.Core.Startup`: 91.65%
  - `EmbodySense.Web`: 94.65%
