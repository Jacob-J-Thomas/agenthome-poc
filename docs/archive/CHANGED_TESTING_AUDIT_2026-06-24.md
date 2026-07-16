# Changed Testing Audit - 2026-06-24

## Scope

This audit reviews the current changed set after the latest testing sweep. It focuses on staged, unstaged, and untracked changes related to frontend tests, E2E tests, integration tests, unit-test project wiring, and verification infrastructure.

The review deliberately did not stage, unstage, commit, or edit source/test code. Findings about documentation are omitted unless they materially affect verification or review safety.

## Snapshot Caveats

- The repository index and working tree are not aligned. Some test moves are staged, while their replacements are still untracked.
- `docs/TECH_DEBT_AUDIT_2026-06-24.md` already exists from the prior audit and remains untracked.
- This report treats all staged, unstaged, and untracked files as the current changed state, but calls out index-specific risks separately.

Current high-signal changed testing files:

- `EmbodySense.sln`
- `package.json`
- `scripts/verify-coverage.ps1`
- `tests/frontend/app.test.mjs`
- `tests/EmbodySense.E2ETests/**`
- `tests/EmbodySense.IntegrationTests/Architecture/TestBoundaryGuardTests.cs`
- `tests/EmbodySense.IntegrationTests/CodexAppServer/CodexAppServerInferenceTests.cs`
- `tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj`
- staged deletion of `tests/EmbodySense.Core.Clients.Tests/CodexAppServer/CodexAppServerInferenceTests.cs`

## Verification Performed

Verification was run from an isolated temp copy of the dirty working tree so build outputs, E2E browser state, and process artifacts would not land in the active checkout.

- `dotnet build EmbodySense.sln /p:RestoreIgnoreFailedSources=true`: passed with 0 warnings and 0 errors.
- `npm.cmd test`: passed all 4 frontend tests.
- `dotnet test EmbodySense.sln --no-build /p:RestoreIgnoreFailedSources=true`: failed. All non-E2E assemblies passed, but `EmbodySense.E2ETests.Web.BrowserFlowTests.Headless_browser_initializes_workspace_and_restores_history` timed out waiting for the workspace status to include `Initialized`.
- A single rerun of the failing `BrowserFlowTests` test passed.
- A follow-up stress run of the E2E project failed on the first pass with the same `Initialized` timeout.

Coverage verification was not treated as meaningful because the all-up .NET suite is not currently green. Running coverage on top of a known failing test surface would risk creating more stale `TestResults` without proving the changed state.

## Review Method

The main review inspected diffs, changed files, adjacent production seams, and verification output. Three read-only adversarial subagents independently reviewed:

- frontend tests and web UI coverage gaps,
- E2E/integration tests and solution wiring,
- unit-test migration, coverage, and build verification infrastructure.

Their findings were checked against live files and folded into the tiers below. Some lower-priority observations are included as testing debt even when the production behavior is currently correct, because these tests would fail to catch likely regressions.

## Tier 0 - Must Fix Before Treating the Changed Set as Green

### T0-01 - The all-up .NET test command is currently flaky/failing

**Evidence**

- The new E2E project is included in the default solution test path at `EmbodySense.sln:44`.
- `dotnet test EmbodySense.sln --no-build /p:RestoreIgnoreFailedSources=true` failed in an isolated copy with:
  - failing test: `EmbodySense.E2ETests.Web.BrowserFlowTests.Headless_browser_initializes_workspace_and_restores_history`
  - failure: timeout waiting for `document.getElementById('workspaceStatus').textContent.includes('Initialized')`
- The same test passed when run once by itself, then failed again on the first pass of an E2E stress run.
- The test waits only for initial text at `tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs:29`, clicks the initialize button at `tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs:30`, and then waits for `Initialized` at `tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs:31`.
- The production UI disables the initialize button until the SignalR hub is connected at `src/EmbodySense.Web/wwwroot/app.js:80`, and only re-applies enabled state after `connectHub()` completes at `src/EmbodySense.Web/wwwroot/app.js:84-91`.

**Why this matters**

The browser test can click while the button is still disabled. In a real browser, a disabled button click does nothing, so the test then waits for a state transition that was never requested. Because timing varies, it sometimes passes and sometimes fails. This makes the default `dotnet test EmbodySense.sln` command unreliable.

This is more severe than ordinary E2E fragility because the README now presents the solution test command as the all-up .NET verification path, and the solution now includes the E2E project.

**Suggested remediation**

Wait for the UI to be actionable before clicking, for example an expression that checks both `workspaceStatus` and `!initButton.disabled`. Prefer a helper such as `ClickWhenEnabledAsync("initButton")` that polls enabled state before dispatching a real click. Keep the stress rerun until repeated E2E passes are boring.

### T0-02 - The staged index can commit the app-server test deletion without the replacement

**Evidence**

- `git diff --cached --name-status` includes:
  - `D tests/EmbodySense.Core.Clients.Tests/CodexAppServer/CodexAppServerInferenceTests.cs`
  - `M tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj`
  - `A tests/EmbodySense.E2ETests/...`
  - `A tests/frontend/app.test.mjs`
- `git status --short --untracked-files=all` shows the replacement app-server integration test as untracked:
  - `?? tests/EmbodySense.IntegrationTests/CodexAppServer/CodexAppServerInferenceTests.cs`
- The staged Core.Clients test project now references only Core.Clients, Core.Common, and Tests.Support at `tests/EmbodySense.Core.Clients.Tests/EmbodySense.Core.Clients.Tests.csproj:15-17`.

**Why this matters**

The working tree contains a replacement for the moved app-server protocol/audit tests, but the index does not. If the current staged index were committed as-is, the old 319-line Codex app-server test suite would be removed from the commit and its replacement would not land.

This is not a runtime code bug. It is a review/commit integrity bug, and it is especially dangerous in this repo because the index is user-owned and should not be normalized casually by an agent.

**Suggested remediation**

Before committing, explicitly review the staged vs untracked state and include the moved replacement test in the same commit as the deletion, or intentionally unstage the deletion. Do not rely on the current index shape.

## Tier 1 - High-Risk Testing Debt

### T1-01 - Browser E2E is now in the default solution but depends on hard-coded local browser paths

**Evidence**

- `EmbodySense.sln:44` adds `EmbodySense.E2ETests` to the solution.
- `tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs:286-303` searches only fixed Windows Edge/Chrome install locations and throws if none exist.

**Why this matters**

`dotnet test EmbodySense.sln` is now machine-shape dependent. It can fail on minimal Windows images, non-standard browser installs, non-Windows CI, or developer machines without Edge/Chrome in those exact paths.

The local project is Windows-oriented today, but default verification should still distinguish "browser prerequisite missing" from "application failed." Otherwise contributors and agents will see noisy red runs unrelated to product behavior.

**Suggested remediation**

Add a browser executable override such as `EMBODYSENSE_BROWSER_PATH` or `BROWSER_PATH`. Add a clear trait/category for browser E2E and a deliberate skip story when prerequisites are absent. If browser E2E must remain in the default solution, make missing prerequisites fail with a crisp prerequisite message or configure CI to satisfy them.

### T1-02 - Frontend XSS tests can pass even if production regresses to unsafe HTML insertion

**Evidence**

- The frontend tests assert no injected `img` or `script` nodes at `tests/frontend/app.test.mjs:30` and `tests/frontend/app.test.mjs:78`.
- The fake DOM only walks fake child nodes through `findByTag` at `tests/frontend/app.test.mjs:178-189`.
- `FakeElement.textContent` is modeled at `tests/frontend/app.test.mjs:336-343`, but the fake DOM has no `innerHTML` implementation that parses or rejects markup.
- Current production rendering uses `textContent` in important paths such as `src/EmbodySense.Web/wwwroot/app.js:442`, `src/EmbodySense.Web/wwwroot/app.js:456`, and `src/EmbodySense.Web/wwwroot/app.js:549`.

**Why this matters**

The production code currently looks safe on the inspected paths. The debt is that the tests do not prove it. If a future change uses `innerHTML` or another raw HTML sink, the fake DOM will not create real nodes, and the existing "no script/img" assertions may still pass.

Because restored transcript and configuration content can contain untrusted user/model/workspace data, this is one of the highest-value frontend test properties to prove with a real DOM.

**Suggested remediation**

Use `jsdom`, `happy-dom`, or the existing headless browser harness for injection tests. If staying zero-dependency, make the fake element throw on `innerHTML` assignment and add assertions that dangerous sinks are not used. Keep at least one real browser or real DOM test around restored transcript and configuration raw JSON rendering.

### T1-03 - The E2E helper layer has unbounded process/socket waits that can hang the suite

**Evidence**

- Browser DevTools WebSocket connect uses `CancellationToken.None` at `tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs:133`.
- Browser WebSocket close uses `CancellationToken.None` at `tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs:187`.
- SignalR WebSocket connect uses `CancellationToken.None` at `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:165` and `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:266`.
- SignalR WebSocket close uses `CancellationToken.None` at `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:293`.
- The external Web process kill path waits without a timeout at `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:218-219`.

**Why this matters**

E2E tests are allowed to be slower, but they should not be able to hang a whole run indefinitely. WebSocket connect/close and process termination are exactly the places where environmental failure often manifests as a hang rather than an exception.

This risk is magnified because the E2E project is now part of the solution test command.

**Suggested remediation**

Centralize bounded helpers for WebSocket connect, WebSocket close, process startup, and process shutdown. Every helper should take a timeout, cancel/kill on timeout, and include captured logs or last observed state in the failure message.

### T1-04 - The external Web-process E2E can hide failures and can deadlock under chatty output

**Evidence**

- The external process redirects stdout and stderr at `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:172-177`.
- The redirected streams are never drained while the test polls `/api/status` at `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:191-208`.
- The test only asserts one `/api/status` response at `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:129-133`.

**Why this matters**

If the child process writes enough output to fill the redirected pipe, it can block before the health probe completes. Even without a deadlock, the test can miss useful startup errors because stderr/stdout are not read or attached to failure output.

The test name promises an external process E2E, but the assertion is currently a thin "served status once" check. The process could emit errors, serve one response, and die without the test noticing.

**Suggested remediation**

Drain stdout and stderr asynchronously. Include both streams in assertion failures. Assert the process is still alive after the probe. For an external-process E2E, also hit `/`, `/app.js`, `/api/session`, one authenticated endpoint, and the hub handshake through the external process.

## Tier 2 - Meaningful Gaps and False Confidence

### T2-01 - Coverage verification can certify stale or partial coverage output

**Evidence**

- `scripts/verify-coverage.ps1:14-16` silently skips a test project if its `TestResults` directory is absent.
- `scripts/verify-coverage.ps1:19-21` picks the newest existing `coverage.cobertura.xml` under each project.
- `scripts/verify-coverage.ps1:33-42` hard-codes expected production packages.
- `scripts/verify-coverage.ps1:97-99` silently ignores packages that are not on that expected list.

**Why this matters**

The script is useful, but it is not tied to a specific current test invocation. A failed, skipped, or partial current coverage run can be masked by older `TestResults` left behind in one or more projects. A new production assembly can also escape the 90 percent gate until the hard-coded package list is updated.

This matters more now because the test surface has split into unit, integration, Web, E2E, and frontend domains.

**Suggested remediation**

Make the coverage command write into a unique run directory or clean all `TestResults` first. Pass the expected run directory into the verifier. Derive expected production assemblies from `src/**/*.csproj` or the solution instead of maintaining a hard-coded list.

### T2-02 - The moved app-server tests still do not cover the real process transport

**Evidence**

- The replacement app-server tests inject `ScriptedAppServerTransport` at `tests/EmbodySense.IntegrationTests/CodexAppServer/CodexAppServerInferenceTests.cs:284-310`.
- The real process transport is excluded from coverage at `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerProcessTransport.cs:9`.
- The actual `codex app-server --stdio` launch path is built at `src/EmbodySense.Core.Clients/CodexAppServer/CodexAppServerProcessTransport.cs:62-80`.

**Why this matters**

The scripted protocol tests are valuable and should stay. They prove JSON-RPC handling, dynamic tool bridging, native request denial, and audit behavior across the transport seam. They do not prove that the real `codex app-server --stdio` process launch, stderr capture, standard I/O, disposal, or executable resolution works.

Calling these tests "integration" can create a false sense that the external app-server adapter itself is covered.

**Suggested remediation**

Keep the scripted tests, but add a narrow process-transport test seam. For example, extract `ProcessStartInfo` creation into a public or internal pure builder that can be tested without launching Codex, and add an opt-in smoke test for the real executable behind a trait/environment variable.

### T2-03 - Frontend tests do not assert token propagation on protected calls

**Evidence**

- Production sends `X-EmbodySense-Session` for configuration at `src/EmbodySense.Web/wwwroot/app.js:47-64`.
- Production puts `access_token` in the SignalR hub URL at `src/EmbodySense.Web/wwwroot/app.js:94-98`.
- The frontend `createFetch` fake ignores request options at `tests/frontend/app.test.mjs:141-155`.
- `FakeWebSocket` records the URL at `tests/frontend/app.test.mjs:195-199`, but no test asserts it includes the token.

**Why this matters**

The web tests have server-side coverage that missing/invalid hub tokens are rejected, and one endpoint test asserts unauthenticated `/api/configuration` returns unauthorized. The frontend unit tests still do not prove that the browser actually attaches the token to the protected REST and hub paths.

This is a classic "both halves tested separately, seam not tested" gap.

**Suggested remediation**

Record fetch options and WebSocket URLs in the frontend harness. Assert `/api/configuration` includes `X-EmbodySense-Session: test-token`, and assert the hub URL includes `access_token=test-token`.

### T2-04 - The frontend approval test blesses contradictory decisions on one request

**Evidence**

- `tests/frontend/app.test.mjs:99-106` clicks both `Approve` and `Reject` for `req-1` and expects both `DecideApproval` invocations.
- Production wires both buttons to the same request id at `src/EmbodySense.Web/wwwroot/app.js:510-520`.
- The coordinator removes a pending approval on the first decision at `src/EmbodySense.Web/Services/WebApprovalCoordinator.cs:51-69`.
- The fake socket always returns accepted for `DecideApproval` at `tests/frontend/app.test.mjs:211-213`.

**Why this matters**

The test currently normalizes a contradictory user path: approve and reject the same request. In the real coordinator, the second decision should not succeed after the first removes the pending request.

This can hide double-submit behavior or leave the UI without coverage for the "already gone/not accepted" result.

**Suggested remediation**

Split approve and reject into separate test cases or separate pending requests. Add a duplicate-decision test that expects the UI to disable/remove actions after the first click or surface the rejection from `DecideApproval`.

### T2-05 - The main composer and cancellation paths are under-tested in frontend units

**Evidence**

- The message form submit handler spans `src/EmbodySense.Web/wwwroot/app.js:646-666`.
- The cancel handler spans `src/EmbodySense.Web/wwwroot/app.js:638-644`.
- The new frontend tests cover history rendering, assistant deltas, config tabs, and approvals, but they never dispatch a `messageForm` submit event.

**Why this matters**

The composer is the primary user workflow. It controls blank-message rejection, initialized/disconnected guards, visible user-message append, send-button disabled state, cancel-button state, `SendMessage` invocation, error rendering, and recovery after failure.

E2E covers one `/history` happy path, but not the general send/cancel behavior or failure paths.

**Suggested remediation**

Add frontend tests for blank input, uninitialized state, disconnected state, successful send, `SendMessage` failure, send-button restoration, cancel success, cancel false result, and cancellation stream event handling.

### T2-06 - Restored transcript tests lock in a role/content-only shape that conflicts with the new chrono axiom

**Evidence**

- The changed axioms now say agents should be chrono aware and user messages should be timestamped.
- `src/EmbodySense.Web/Models/WebTranscriptMessage.cs:3` carries only `Role` and `Content`.
- `src/EmbodySense.Web/WebAgentRuntimeHost.cs:170-173` maps restored messages to only role/content before emitting `history_loaded`.
- `src/EmbodySense.Web/wwwroot/app.js:541-550` renders live/restored transcript messages with only role/content.
- `tests/frontend/app.test.mjs:25-30` asserts only role labels/content for restored transcript messages.

**Why this matters**

The change set strengthens the repo-local scope anchor around chrono awareness, while the new tests encode a reduced transcript shape. The web configuration history tab already renders timestamps for configuration transcripts, but the active restored transcript shown to the user does not carry or render them.

This is not a reason to block all test work, but it is a real test-contract debt: the tests may make it harder to evolve `history_loaded` toward the axiom without deliberate changes.

**Suggested remediation**

Carry sequence and timestamp through `WebTranscriptMessage`, render an accessible timestamp in active transcript messages, and assert it in both frontend and E2E coverage.

### T2-07 - Some integration app-server tests are not explicitly workspace-isolated

**Evidence**

- `tests/EmbodySense.IntegrationTests/CodexAppServer/CodexAppServerInferenceTests.cs:245` defaults `WorkingDirectory` to `Directory.GetCurrentDirectory()`.
- `src/EmbodySense.Core.Startup/Inference/LlmInferenceClient.cs:23` tries to create an audit log for an existing workspace at the configured working directory.

**Why this matters**

Many tests pass a fresh `TestWorkspace`, but tests that use the default current directory depend on the runner's working directory not being an initialized EmbodySense workspace. In the local copy used for this review, that did not fire. In a different runner shape, those tests could write audit events into a real workspace or become order/environment dependent.

**Suggested remediation**

Require every integration `CreateClient` call to pass either a fresh `TestWorkspace` root or an explicit inert temp directory that is known not to be an EmbodySense workspace.

### T2-08 - The architecture guard tests have blind spots despite useful intent

**Evidence**

- `tests/EmbodySense.IntegrationTests/Architecture/ProjectReferenceGuardTests.cs:15-25` hard-codes the expected production project map.
- It does not assert that every `src/**/*.csproj` is represented in that map.
- `tests/EmbodySense.IntegrationTests/Architecture/TestBoundaryGuardTests.cs:98-110` scans `tests/frontend/*.mjs` for private test exports, but not production `src/EmbodySense.Web/wwwroot/*.js`.

**Why this matters**

These guards are good directionally, but their biggest value is in catching new drift. A future production project could be added without a corresponding expected-reference entry. A future frontend test backdoor could be added to production `app.js` and this guard would not see it, because it scans only tests.

**Suggested remediation**

Derive actual production and test project sets from the filesystem, assert they match the configured maps, and scan production frontend scripts for test-only export tokens as well as test files.

## Tier 3 - Operational and Maintenance Debt

### T3-01 - E2E port allocation uses the released-port pattern

**Evidence**

- `tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs:61` chooses a port before starting the app.
- `tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs:71-78` opens a listener on port 0, reads the assigned port, closes it, and returns that released port.
- `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:124` and `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:153-160` repeat the pattern.
- xUnit parallelization is disabled inside the E2E assembly at `tests/EmbodySense.E2ETests/AssemblyInfo.cs:3`, which reduces but does not eliminate the risk from external processes on the machine.

**Why this matters**

Another local process can bind the released port before Kestrel or browser DevTools does. The risk is lower with per-assembly parallelization disabled, but the pattern remains a common source of intermittent test failures.

**Suggested remediation**

For in-process Kestrel tests, bind to port 0 and read the actual bound address after startup. For browser DevTools or external process cases, add retry/backoff around startup or use a reserved-port abstraction with a narrow lifetime.

### T3-02 - Test entry points are fragmented

**Evidence**

- `package.json:4-5` defines `npm test` as frontend-only.
- The README verification text now requires separate `dotnet test`, `npm run test:frontend`, coverage collection, and `scripts/verify-coverage.ps1`.
- `.github/workflows` exists but contains no workflow files in the current checkout.

**Why this matters**

Agents and humans can easily claim "tests passed" after running only one slice. The risk is higher now because the repo has at least four meaningful test domains: .NET unit tests, .NET integration tests, .NET E2E tests, and Node frontend tests, plus coverage verification.

**Suggested remediation**

Add one repo-level verification entry point, for example `scripts/verify.ps1`, that runs the intended all-up sequence and fails fast with clear sections. If CI is not wanted yet, still provide the local script so everyone uses the same gate.

### T3-03 - Test dependency metadata is duplicated and weakly reproducible

**Evidence**

- The new E2E project repeats package versions directly at `tests/EmbodySense.E2ETests/EmbodySense.E2ETests.csproj:4-7`.
- Existing test projects do the same.
- No `global.json`, `Directory.Packages.props`, `packages.lock.json`, `nuget.config`, or `package-lock.json` was found in the repo root during this review.
- Test helper packages such as `coverlet.collector` and `xunit.runner.visualstudio` are referenced without visible `PrivateAssets`/`IncludeAssets` metadata in the inspected projects.

**Why this matters**

This is not an immediate failure, but it makes SDK/package drift and future test-project additions more error-prone. The more test projects the repo adds, the more expensive this duplication becomes.

**Suggested remediation**

Introduce central package management or a shared test props file. Consider SDK pinning with `global.json` once the project wants reproducible local/CI builds.

### T3-04 - The E2E SignalR and DevTools clients are hand-rolled protocol clients

**Evidence**

- `tests/EmbodySense.E2ETests/Web/WebClientFlowTests.cs:253-387` implements a custom SignalR JSON protocol client over `ClientWebSocket`.
- `tests/EmbodySense.E2ETests/Web/BrowserFlowTests.cs:91-331` implements a custom DevTools protocol client and browser lifecycle wrapper.

**Why this matters**

Hand-rolled protocol clients are understandable for a dependency-light POC, but they become part of the test product. Bugs in those helpers can create false negatives, false positives, hangs, or missed diagnostics. The current E2E flake is already in this layer.

**Suggested remediation**

Either harden these helpers as reusable test infrastructure with timeouts, logs, and focused unit tests, or replace them with proven libraries where the maintenance tradeoff is worth it.

## Rejected or Downgraded Observations

- The moved Codex app-server tests are not lost in the working tree. They are present at `tests/EmbodySense.IntegrationTests/CodexAppServer/CodexAppServerInferenceTests.cs`; the serious issue is the staged/untracked mismatch.
- The current inspected frontend rendering paths use `textContent`; this audit does not claim a live XSS bug in the changed state.
- The E2E free-port pattern is not Tier 1 by itself because E2E assembly parallelization is disabled. It remains a real flake risk from external machine activity.
- Documentation wording changes were not reviewed as documentation debt, except where they affect verification claims or testing scope.

## Suggested Remediation Sequence

1. Fix the browser E2E initialization race, then rerun the full solution test command repeatedly enough to trust it.
2. Resolve the index mismatch before any commit: keep the app-server test deletion and replacement together.
3. Add bounded timeouts/log capture to E2E WebSocket and process helpers.
4. Decide how browser E2E should behave when Edge/Chrome is absent, then encode that as an env override, trait, skip, or CI prerequisite.
5. Strengthen frontend tests around real DOM injection, token propagation, composer/cancel, and duplicate approval decisions.
6. Make coverage verification run-scoped and derive expected production packages.
7. Add a single repo-level verification command that runs .NET, frontend, E2E, and coverage gates in the intended order.
