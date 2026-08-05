import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";
import vm from "node:vm";

const appSource = fs.readFileSync(
  new URL("../../src/EmbodySense.Web/wwwroot/app.js", import.meta.url),
  "utf8",
);
const indexSource = fs.readFileSync(
  new URL("../../src/EmbodySense.Web/wwwroot/index.html", import.meta.url),
  "utf8",
);
const loopsRedirectSource = fs.readFileSync(
  new URL("../../src/EmbodySense.Web/wwwroot/loops.html", import.meta.url),
  "utf8",
);
const recordSeparator = "\u001e";

test("the shared shell owns primary navigation while Builder and Runs stay local to Loops", () => {
  assert.match(indexSource, /class="app-rail"/);
  assert.match(indexSource, /data-app-view="chat"/);
  assert.match(indexSource, /data-app-view="loops"/);
  assert.match(
    indexSource,
    /data-app-view="configuration"\s+data-config-tab="permissions"/,
  );
  assert.match(indexSource, /class="workspace-tabs"/);
  assert.match(indexSource, /id="builderTab"/);
  assert.match(indexSource, /id="runsTab"/);
  assert.match(loopsRedirectSource, /\?view=loops/);
});

test("shared-shell navigation switches views and keeps the refresh route aligned", async () => {
  const app = await loadApp();
  const loopsTab = app.appTabs.find((tab) => tab.dataset.appView === "loops");
  const chatTab = app.appTabs.find((tab) => tab.dataset.appView === "chat");

  await loopsTab.click();
  assert.equal(app.elements.chatView.hidden, true);
  assert.equal(app.elements.loopsView.hidden, false);
  assert.match(app.context.window.location.href, /\?view=loops$/);

  await chatTab.click();
  assert.equal(app.elements.chatView.hidden, false);
  assert.equal(app.elements.loopsView.hidden, true);
  assert.match(app.context.window.location.href, /\?view=chat$/);
});

test("inherited object property names cannot select a configuration renderer", async () => {
  const app = await loadApp({
    locationHref: "http://127.0.0.1:4378/?view=constructor",
  });

  assert.equal(app.elements.chatView.hidden, false);
  assert.equal(app.elements.configurationView.hidden, true);
  assert.equal(
    configTab(app, "overview").attributes.get("aria-selected"),
    "false",
  );
});

test("workspace initialization wakes an activated loop builder", async () => {
  let refreshes = 0;
  const loopBuilder = {
    activate() {},
    refreshWorkspace() {
      refreshes++;
    },
  };
  const app = await loadApp({
    loopBuilder,
    status: {
      workspaceRoot: "C:/workspace",
      initialized: false,
      client: "web",
      cliRole: "CLI remains available.",
    },
  });

  assert.equal(app.elements.connectionDot.className.includes("ready"), false);
  vm.runInContext(
    "applyStatus({ workspaceRoot: 'C:/workspace', initialized: true, client: 'web', cliRole: 'CLI remains available.' })",
    app.context,
  );

  assert.equal(refreshes, 1);
  assert.equal(app.elements.connectionDot.className.includes("ready"), true);
});

test("the initial initialized status wakes a loop builder that observed the prior state", async () => {
  let refreshes = 0;
  const loopBuilder = {
    activate() {},
    refreshWorkspace() {
      refreshes++;
    },
  };

  await loadApp({ loopBuilder });

  assert.equal(refreshes, 1);
});

test("leaving Loops suspends its surface activity", async () => {
  let activations = 0;
  let deactivations = 0;
  const loopBuilder = {
    activate() {
      activations++;
    },
    deactivate() {
      deactivations++;
    },
    refreshWorkspace() {},
  };
  const app = await loadApp({ loopBuilder });
  const loopsTab = app.appTabs.find((tab) => tab.dataset.appView === "loops");
  const overviewTab = app.appTabs.find(
    (tab) => tab.dataset.configTab === "overview",
  );

  await loopsTab.click();
  await overviewTab.click();

  assert.equal(activations, 1);
  assert.equal(deactivations, 1);
});

test("history_loaded replaces the transcript using role labels and text content", async () => {
  const app = await loadApp();
  app.elements.transcript.replaceChildren();

  const unsafeContent = "<img src=x onerror=alert(1)>";
  app.socket.serverSendInvocation("StreamEvent", {
    type: "history_loaded",
    messages: [
      { role: "user", content: "restore this" },
      { role: "assistant", content: unsafeContent },
      { role: "unknown", content: "system fallback" },
    ],
  });
  await flushAsyncWork();

  assert.equal(app.elements.transcript.children.length, 3);
  assert.equal(messageRole(app.elements.transcript.children[0]), "User");
  assert.equal(messageRole(app.elements.transcript.children[1]), "Assistant");
  assert.equal(
    messageContent(app.elements.transcript.children[1]),
    unsafeContent,
  );
  assert.equal(messageRole(app.elements.transcript.children[2]), "System");
  assert.equal(findByTag(app.elements.transcript, "img").length, 0);
});

test("transcript hydration failure leaves the connected chat usable", async () => {
  const app = await loadApp({
    transcriptError: "Corrupt retained loop evidence.",
  });

  assert.equal(app.elements.clientStatus.textContent, "Web primary");
  assert.equal(app.elements.sendButton.disabled, false);
  assert.equal(app.elements.verboseToggle.disabled, false);
  assert.match(
    app.elements.transcript.textContent,
    /Transcript unavailable: Corrupt retained loop evidence/,
  );
});

test("fresh boot promotes authenticated chat while configuration hydration remains in flight", async () => {
  let releaseConfiguration;
  const configurationGate = new Promise((resolve) => {
    releaseConfiguration = resolve;
  });
  const app = await loadApp({ configurationGate });

  assert.equal(app.elements.workspaceStatus.textContent, "Initialized");
  assert.equal(app.elements.clientStatus.textContent, "Web primary");
  assert.equal(app.elements.sendButton.disabled, false);
  assert.equal(app.elements.refreshConfigButton.disabled, true);

  releaseConfiguration();
  await vm.runInContext("sessionRecoveryPromise", app.context);

  assert.equal(app.elements.refreshConfigButton.disabled, false);
});

test("boot hydrates the complete active runtime transcript instead of the bounded configuration snapshot", async () => {
  const activeTranscript = Array.from({ length: 201 }, (_, index) => ({
    role: index % 2 === 0 ? "user" : "assistant",
    content: `active message ${index}`,
  }));
  activeTranscript[200].content = "x".repeat(5000);
  const app = await loadApp({
    activeTranscript,
    configuration: {
      status: { initialized: true },
      runtime: { surface: "web", model: "gpt-test", codexSandbox: "read-only" },
      audit: {
        path: "audit/events.ndjson",
        exists: false,
        events: [],
        readProblems: [],
      },
      conversationHistory: {
        directoryPath: ".agent/memory/conversations",
        currentPath: "current.ndjson",
        archivePath: "archive",
        readProblems: [],
        transcripts: [
          {
            conversationId: "current",
            isCurrent: true,
            messages: [
              {
                role: "user",
                content: "bounded inspection copy that must not hydrate Chat",
              },
            ],
          },
        ],
      },
      paths: [],
      concepts: [],
      documents: [],
      permissions: {
        exists: false,
        parsed: false,
        version: null,
        scope: "",
        defaultAccess: "ask",
        readProblems: [],
        approved: [],
        denied: [],
        rawJson: "",
      },
    },
  });

  assert.equal(app.elements.transcript.children.length, 201);
  assert.equal(
    messageContent(app.elements.transcript.children[0]),
    "active message 0",
  );
  assert.equal(
    messageContent(app.elements.transcript.children[200]).length,
    5000,
  );
});

test("reconnect preserves the visible transcript while runtime hydration is temporarily unavailable", async () => {
  const app = await loadApp({
    activeTranscript: [{ role: "user", content: "Visible conversation" }],
  });
  assert.equal(
    messageContent(app.elements.transcript.children[0]),
    "Visible conversation",
  );
  FakeWebSocket.currentTranscript = null;

  await vm.runInContext("connectHub()", app.context);

  assert.equal(app.elements.transcript.children.length, 1);
  assert.equal(
    messageContent(app.elements.transcript.children[0]),
    "Visible conversation",
  );
});

test("verified custom-loop publication rehydrates once per operation without appending duplicates", async () => {
  const app = await loadApp({
    activeTranscript: [{ role: "user", content: "Original prompt" }],
  });
  const initialHydrations = app.socket.sentInvocations(
    "GetCurrentTranscript",
  ).length;
  FakeWebSocket.currentTranscript = [
    { role: "user", content: "Original prompt" },
    { role: "assistant", content: "Published loop output" },
  ];

  app.socket.serverSendInvocation("ConversationChanged", {
    operationId: "publication-1",
    conversationId: "conversation-1",
    messageCount: 2,
  });
  await flushAsyncWork();
  app.socket.serverSendInvocation("ConversationChanged", {
    operationId: "publication-1",
    conversationId: "conversation-1",
    messageCount: 2,
  });
  await flushAsyncWork();

  assert.equal(
    app.socket.sentInvocations("GetCurrentTranscript").length,
    initialHydrations + 1,
  );
  assert.equal(app.elements.transcript.children.length, 2);
  assert.equal(
    messageContent(app.elements.transcript.children[1]),
    "Published loop output",
  );
});

test("publication synchronization retries after deferred runtime disposal returns no transcript", async () => {
  const scheduledRetries = [];
  let resolvePublicationRetry;
  const publicationRetryScheduled = new Promise((resolve) => {
    resolvePublicationRetry = resolve;
  });
  const app = await loadApp({
    activeTranscript: [{ role: "user", content: "Original prompt" }],
    windowSetTimeout(handler, delay) {
      const scheduled = { handler, delay, cancelled: false };
      scheduledRetries.push(scheduled);
      if (delay === 25) {
        resolvePublicationRetry(scheduled);
      }

      return scheduled;
    },
    windowClearTimeout(scheduled) {
      scheduled.cancelled = true;
    },
  });
  const initialHydrations = app.socket.sentInvocations(
    "GetCurrentTranscript",
  ).length;
  FakeWebSocket.currentTranscript = null;

  app.socket.serverSendInvocation("ConversationChanged", {
    operationId: "publication-after-disposal",
    conversationId: "conversation-1",
    messageCount: 2,
  });
  await publicationRetryScheduled;
  const retry = assertSingle(
    scheduledRetries.filter(
      (scheduled) => scheduled.delay === 25 && !scheduled.cancelled,
    ),
  );
  FakeWebSocket.currentTranscript = [
    { role: "user", content: "Original prompt" },
    { role: "assistant", content: "Published after deferred disposal" },
  ];
  retry.cancelled = true;
  const transcriptReplaced = app.elements.transcript.waitForNextReplacement();
  retry.handler();
  await transcriptReplaced;

  assert.equal(
    app.socket.sentInvocations("GetCurrentTranscript").length,
    initialHydrations + 2,
  );
  assert.equal(app.elements.transcript.children.length, 2);
  assert.equal(
    messageContent(app.elements.transcript.children[1]),
    "Published after deferred disposal",
  );
});

test("assistant deltas update one active message and final text resets the active message", async () => {
  const app = await loadApp();
  app.elements.transcript.replaceChildren();

  app.socket.serverSendInvocation("StreamEvent", {
    type: "assistant_delta",
    text: "Hel",
  });
  app.socket.serverSendInvocation("StreamEvent", {
    type: "assistant_delta",
    text: "lo",
  });
  app.socket.serverSendInvocation("StreamEvent", {
    type: "assistant_final",
    text: "Hello.",
  });
  app.socket.serverSendInvocation("StreamEvent", {
    type: "assistant_delta",
    text: "Next",
  });
  await flushAsyncWork();

  assert.equal(app.elements.transcript.children.length, 2);
  assert.equal(messageContent(app.elements.transcript.children[0]), "Hello.");
  assert.equal(messageContent(app.elements.transcript.children[1]), "Next");
});

test("configuration overview reports Codex compatibility and tabs keep raw JSON inert", async () => {
  const rawJson = '{"note":"<script>bad()</script>"}';
  const app = await loadApp({
    configuration: {
      status: { initialized: true },
      runtime: {
        surface: "web",
        model: "gpt-test",
        codexExecutablePath: "C:/codex.exe",
        codexSandbox: "read-only",
        codexRuntime: {
          compatibility: "model-unavailable",
          resolvedExecutablePath: "C:/codex.exe",
          version: "codex-cli old",
          configuredModel: "gpt-test",
          source: "explicit --codex-path",
          detail: "Update Codex before starting a turn.",
        },
      },
      audit: {
        path: "audit/events.ndjson",
        exists: true,
        events: [],
        readProblems: [],
      },
      conversationHistory: {
        directoryPath: ".agent/memory/conversations",
        currentPath: "current.ndjson",
        archivePath: "archive",
        transcripts: [],
        readProblems: [],
      },
      paths: [],
      concepts: [],
      documents: [],
      permissions: {
        exists: true,
        parsed: true,
        version: 1,
        scope: "workspace",
        defaultAccess: "ask",
        readProblems: [],
        approved: [
          {
            path: "shared/**",
            requiresApproval: true,
            effect: "allow",
            operations: ["read"],
            detail: "Read requires approval.",
          },
        ],
        denied: [],
        rawJson,
      },
    },
  });

  assert.match(app.elements.configContent.textContent, /model-unavailable/);
  assert.match(app.elements.configContent.textContent, /codex-cli old/);
  assert.match(app.elements.configContent.textContent, /C:\/codex\.exe/);
  assert.match(
    app.elements.configContent.textContent,
    /Update Codex before starting a turn\./,
  );

  await configTab(app, "permissions").click();

  assert.equal(
    configTab(app, "permissions").attributes.get("aria-selected"),
    "true",
  );
  assert.match(app.elements.configContent.textContent, /shared\/\*\*/);
  assert.match(
    app.elements.configContent.textContent,
    /<script>bad\(\)<\/script>/,
  );
  assert.equal(findByTag(app.elements.configContent, "script").length, 0);
});

test("agent configuration expands the renamed role guide", async () => {
  const app = await loadApp({
    configuration: {
      status: { initialized: true },
      runtime: { surface: "web", model: "gpt-test", codexSandbox: "read-only" },
      audit: {
        path: "audit/events.ndjson",
        exists: false,
        events: [],
        readProblems: [],
      },
      conversationHistory: {
        directoryPath: ".agent/memory/conversations",
        currentPath: "current.ndjson",
        archivePath: "archive",
        transcripts: [],
        readProblems: [],
      },
      paths: [],
      concepts: [],
      documents: [
        {
          name: "Role guide",
          category: "Role",
          path: ".agent/ROLE.md",
          exists: true,
          sizeBytes: 10,
          lastModifiedUtc: null,
          content: "role guide",
        },
      ],
      permissions: {
        exists: false,
        parsed: false,
        version: null,
        scope: "",
        defaultAccess: "ask",
        readProblems: [],
        approved: [],
        denied: [],
        rawJson: "",
      },
    },
  });

  await configTab(app, "agent").click();

  const details = assertSingle(
    findByTag(app.elements.configContent, "details"),
  );
  assert.equal(details.open, true);
  assert.match(details.textContent, /Role guide/);
});

test("verbose toggle invokes hub and verbose context renders as system text", async () => {
  const app = await loadApp();
  app.elements.transcript.replaceChildren();

  app.elements.verboseToggle.checked = true;
  await app.elements.verboseToggle.change();
  app.socket.serverSendInvocation("StreamEvent", {
    type: "verbose_context",
    text: "visible <script>context</script>",
  });
  await flushAsyncWork();

  assert.deepEqual(
    app.socket
      .sentInvocations("SetVerboseMode")
      .map((invocation) => invocation.arguments),
    [[true]],
  );
  assert.equal(app.elements.transcript.children.length, 1);
  assert.equal(messageRole(app.elements.transcript.children[0]), "System");
  assert.equal(
    messageContent(app.elements.transcript.children[0]),
    "visible <script>context</script>",
  );
  assert.equal(findByTag(app.elements.transcript, "script").length, 0);
});

test("approval panel renders pending requests and dispatches approve and reject decisions", async () => {
  const app = await loadApp();

  app.socket.serverSendInvocation("ApprovalsChanged", [
    {
      requestId: "req-1",
      command: "read",
      operation: "file",
      targetPath: "shared/note.txt",
      resolvedPath: "C:/workspace/shared/note.txt",
      matchedPath: "shared/**",
      reason: "Need to inspect the note.",
    },
  ]);
  await flushAsyncWork();

  assert.equal(app.elements.approvalCount.textContent, "1 pending");
  assert.match(app.elements.approvals.textContent, /read file/);
  assert.match(app.elements.approvals.textContent, /shared\/note\.txt/);

  const buttons = findByTag(app.elements.approvals, "button");
  assert.equal(
    buttons
      .find((button) => button.textContent === "Approve")
      .attributes.get("aria-label"),
    "Approve read file for shared/note.txt",
  );
  assert.equal(
    buttons
      .find((button) => button.textContent === "Reject")
      .attributes.get("aria-label"),
    "Reject read file for shared/note.txt",
  );
  await buttons.find((button) => button.textContent === "Approve").click();
  await buttons.find((button) => button.textContent === "Reject").click();

  assert.deepEqual(
    app.socket
      .sentInvocations("DecideApproval")
      .map((invocation) => invocation.arguments),
    [
      ["req-1", { approved: true }],
      ["req-1", { approved: false }],
    ],
  );
});

test("pending chat approvals remain visible and actionable outside the Chat view", async () => {
  const app = await loadApp();
  const loopsTab = app.appTabs.find((tab) => tab.dataset.appView === "loops");
  await loopsTab.click();

  app.socket.serverSendInvocation("ApprovalsChanged", [
    {
      requestId: "req-away",
      command: "read",
      operation: "file",
      targetPath: "shared/note.txt",
      resolvedPath: "C:/workspace/shared/note.txt",
      matchedPath: "shared/**",
      reason: "Need to inspect the note.",
    },
  ]);
  await flushAsyncWork();

  assert.equal(app.elements.chatView.hidden, true);
  assert.equal(app.elements.chatApprovalAlert.hidden, false);
  assert.equal(
    app.elements.chatApprovalAlert.textContent,
    "1 chat approval · Review",
  );
  let focusedApprovalHeading = false;
  app.elements.chatApprovalsTitle.focus = () => {
    focusedApprovalHeading = true;
  };

  await app.elements.chatApprovalAlert.click();

  assert.equal(app.elements.chatView.hidden, false);
  assert.equal(app.elements.loopsView.hidden, true);
  assert.match(app.context.window.location.href, /\?view=chat$/);
  assert.equal(focusedApprovalHeading, true);

  app.socket.serverSendInvocation("ApprovalsChanged", []);
  await flushAsyncWork();
  assert.equal(app.elements.chatApprovalAlert.hidden, true);
});

test("browser session credentials stay in same-site cookies and never enter websocket URLs", async () => {
  const app = await loadApp();
  const sessionRequest = app.fetchCalls.find(
    (call) => call.url === "/api/session",
  );

  assert.equal(sessionRequest.options.credentials, "same-origin");
  assert.equal(
    sessionRequest.options.headers["X-EmbodySense-Session"],
    undefined,
  );
  assert.equal(app.socket.url, "ws://127.0.0.1:4378/hubs/session");
  assert.doesNotMatch(app.socket.url, /access_token|process-generation/i);
});

test("concurrent disconnect signals collapse into one renewal hub start and rehydration", async () => {
  let loopRehydrations = 0;
  const loopBuilder = {
    activate() {},
    deactivate() {},
    refreshWorkspace() {},
    rehydrateSession() {
      loopRehydrations++;
      return { refreshed: true };
    },
  };
  const app = await loadApp({ loopBuilder });
  const initialLoopRehydrations = loopRehydrations;
  let releaseRenewal;
  let renewalCalls = 0;
  const renewal = new Promise((resolve) => {
    releaseRenewal = resolve;
  });
  const normalFetch = createFetch({ generationId: "process-generation-2" });
  app.context.fetch = async (url, options) => {
    if (url === "/api/session") {
      renewalCalls++;
      return await renewal;
    }
    return await normalFetch(url, options);
  };

  app.socket.serverClose();
  const duplicateOne = vm.runInContext(
    "startSessionRecovery('duplicate-one', { newGeneration: true })",
    app.context,
  );
  const duplicateTwo = vm.runInContext(
    "startSessionRecovery('duplicate-two', { newGeneration: true })",
    app.context,
  );
  await flushAsyncWork();

  assert.equal(renewalCalls, 1);
  assert.equal(FakeWebSocket.instances.length, 1);
  releaseRenewal(jsonResponse({ generationId: "process-generation-2" }));
  await Promise.all([duplicateOne, duplicateTwo]);

  assert.equal(renewalCalls, 1);
  assert.equal(FakeWebSocket.instances.length, 2);
  assert.equal(loopRehydrations, initialLoopRehydrations + 1);
  assert.equal(
    FakeWebSocket.instances[1].sentInvocations("GetCurrentTranscript").length,
    1,
  );
  assert.equal(
    FakeWebSocket.instances[1].sentInvocations("GetPendingApprovals").length,
    1,
  );
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().connected",
      app.context,
    ),
    true,
  );
});

test("transient recovery uses one bounded jittered timer and stops after the attempt limit", async () => {
  const app = await loadApp();
  const scheduled = [];
  app.context.window.setTimeout = (handler, delay) => {
    const timer = { handler, delay, cancelled: false };
    scheduled.push(timer);
    return timer;
  };
  app.context.window.clearTimeout = (timer) => {
    timer.cancelled = true;
  };
  app.context.fetch = async () => {
    throw new Error("host unavailable");
  };
  vm.runInContext("Math.random = () => 0", app.context);

  app.socket.serverClose();
  await flushAsyncWork();
  await vm.runInContext("startSessionRecovery('duplicate')", app.context);
  let active = scheduled.filter((timer) => !timer.cancelled);
  assert.equal(active.length, 1);
  assert.equal(active[0].delay, 188);

  for (let attempt = 1; attempt < 6; attempt++) {
    const timer = active[0];
    timer.cancelled = true;
    timer.handler();
    await flushAsyncWork();
    active = scheduled.filter((item) => !item.cancelled);
    assert.ok(active.length <= 1);
  }

  assert.equal(active.length, 0);
  assert.equal(app.elements.retryConnectionButton.hidden, false);
  assert.equal(app.elements.clientStatus.textContent, "Web recovery stopped");
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().attempts",
      app.context,
    ),
    6,
  );
});

test("a changed workspace stops recovery without replacing visible local state", async () => {
  const app = await loadApp({
    activeTranscript: [{ role: "user", content: "keep visible state" }],
  });
  app.elements.messageInput.value = "unsent local draft";
  const changedFetch = createFetch({
    generationId: "process-generation-2",
    status: {
      workspaceRoot: "C:/different-workspace",
      initialized: true,
      client: "web",
      cliRole: "CLI remains available.",
    },
  });
  app.context.fetch = changedFetch;

  app.socket.serverClose();
  await vm.runInContext("sessionRecoveryPromise", app.context);

  assert.equal(app.elements.retryConnectionButton.hidden, false);
  assert.equal(app.elements.clientStatus.textContent, "Web workspace changed");
  assert.equal(app.elements.messageInput.value, "unsent local draft");
  assert.match(app.elements.transcript.textContent, /keep visible state/);
  assert.equal(FakeWebSocket.instances.length, 2);
  assert.equal(FakeWebSocket.instances[1].readyState, 3);
});

test("a session policy rejection enters manual posture without transient retries", async () => {
  const app = await loadApp();
  const scheduled = [];
  app.context.window.setTimeout = (handler, delay) => {
    const timer = { handler, delay, cancelled: false };
    scheduled.push(timer);
    return timer;
  };
  app.context.window.clearTimeout = (timer) => {
    timer.cancelled = true;
  };
  app.context.fetch = async (url) =>
    url === "/api/session"
      ? errorResponse(403, "origin rejected")
      : errorResponse(500, "unexpected request");

  app.socket.serverClose();
  await vm.runInContext("sessionRecoveryPromise", app.context);

  assert.equal(scheduled.filter((timer) => !timer.cancelled).length, 0);
  assert.equal(app.elements.retryConnectionButton.hidden, false);
  assert.equal(app.elements.clientStatus.textContent, "Web recovery stopped");
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().terminal",
      app.context,
    ),
    true,
  );
});

test("a blank process generation enters manual posture", async () => {
  const app = await loadApp();
  app.context.fetch = async (url) =>
    url === "/api/session"
      ? jsonResponse({ generationId: "   " })
      : errorResponse(500, "unexpected request");

  app.socket.serverClose();
  await vm.runInContext("sessionRecoveryPromise", app.context);

  assert.equal(app.elements.retryConnectionButton.hidden, false);
  assert.equal(app.elements.clientStatus.textContent, "Web recovery stopped");
  assert.equal(FakeWebSocket.instances.length, 1);
});

test("a stale-auth response starts recovery without replaying the rejected request", async () => {
  const app = await loadApp();
  const requests = [];
  const recoveredFetch = createFetch({ generationId: "process-generation-2" });
  app.context.fetch = async (url, options = {}) => {
    requests.push({ url, options });
    if (url === "/api/configuration" && options.method === "POST") {
      return errorResponse(401, "stale session");
    }
    return await recoveredFetch(url, options);
  };

  await assert.rejects(
    vm.runInContext(
      "fetchJson('/api/configuration', { method: 'POST' })",
      app.context,
    ),
    /was not replayed/,
  );
  await flushAsyncWork();

  assert.equal(
    requests.filter(
      (request) =>
        request.url === "/api/configuration" &&
        request.options.method === "POST",
    ).length,
    1,
  );
  assert.equal(
    requests.filter((request) => request.url === "/api/session").length,
    1,
  );
});

test("a replacement connection that closes during loop hydration is never promoted", async () => {
  let rehydrations = 0;
  let hydrationStarted;
  const hydrationPending = new Promise((resolve) => {
    hydrationStarted = resolve;
  });
  const loopBuilder = {
    activate() {},
    deactivate() {},
    refreshWorkspace() {},
    rehydrateSession() {
      rehydrations++;
      if (rehydrations === 1) return { refreshed: true };
      hydrationStarted();
      return new Promise(() => {});
    },
    resumeSession() {},
    suspendSession() {},
  };
  const app = await loadApp({ loopBuilder });
  installWindowTimers(app);

  app.socket.serverClose();
  await hydrationPending;
  const candidate = FakeWebSocket.instances[1];
  candidate.serverClose();
  await vm.runInContext("sessionRecoveryPromise", app.context);

  assert.equal(candidate.readyState, 3);
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().connected",
      app.context,
    ),
    false,
  );
});

test("candidate events received after snapshots are replayed after promotion", async () => {
  let rehydrations = 0;
  let releaseHydration;
  let hydrationStarted;
  const hydrationPending = new Promise((resolve) => {
    hydrationStarted = resolve;
  });
  const loopBuilder = {
    activate() {},
    deactivate() {},
    refreshWorkspace() {},
    rehydrateSession() {
      rehydrations++;
      if (rehydrations === 1) return { refreshed: true };
      hydrationStarted();
      return new Promise((resolve) => {
        releaseHydration = resolve;
      });
    },
    resumeSession() {},
    suspendSession() {},
  };
  const app = await loadApp({ loopBuilder });

  app.socket.serverClose();
  await hydrationPending;
  const candidate = FakeWebSocket.instances[1];
  candidate.serverSendInvocation("StatusChanged", {
    workspaceRoot: "C:/workspace",
    initialized: true,
    client: "event-authoritative-web",
    cliRole: "CLI remains available.",
  });
  releaseHydration({ refreshed: true });
  await vm.runInContext("sessionRecoveryPromise", app.context);

  assert.equal(app.elements.clientRole.textContent, "event-authoritative-web");
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().connected",
      app.context,
    ),
    true,
  );
});

test("a failed loop rehydration keeps the candidate disconnected", async () => {
  let rehydrations = 0;
  const loopBuilder = {
    activate() {},
    deactivate() {},
    refreshWorkspace() {},
    rehydrateSession() {
      rehydrations++;
      return { refreshed: rehydrations === 1 };
    },
    resumeSession() {},
    suspendSession() {},
  };
  const app = await loadApp({ loopBuilder });
  const timers = installWindowTimers(app);

  app.socket.serverClose();
  await vm.runInContext("sessionRecoveryPromise", app.context);

  assert.equal(FakeWebSocket.instances[1].readyState, 3);
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().connected",
      app.context,
    ),
    false,
  );
  assert.equal(timers.filter((timer) => !timer.cancelled).length, 1);
});

test("a second host restart during recovery reacquires a fresh generation", async () => {
  const app = await loadApp();
  const timers = installWindowTimers(app);
  vm.runInContext("Math.random = () => 0", app.context);
  const normalFetch = createFetch({ generationId: "unused-generation" });
  let sessionReads = 0;
  let statusReads = 0;
  app.context.fetch = async (url, options) => {
    if (url === "/api/session") {
      sessionReads++;
      return jsonResponse({
        generationId: `process-generation-${sessionReads + 1}`,
      });
    }
    if (url === "/api/status" && ++statusReads === 1)
      return errorResponse(401, "the host restarted again");
    return await normalFetch(url, options);
  };

  app.socket.serverClose();
  await vm.runInContext("sessionRecoveryPromise", app.context);
  const retry = timers.find((timer) => !timer.cancelled && timer.delay === 188);
  assert.ok(retry);
  retry.cancelled = true;
  retry.handler();
  await flushAsyncWork();
  await vm.runInContext("sessionRecoveryPromise", app.context);

  assert.equal(sessionReads, 2);
  assert.equal(FakeWebSocket.instances.length, 3);
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().processGenerationId",
      app.context,
    ),
    "process-generation-3",
  );
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().connected",
      app.context,
    ),
    true,
  );
});

test("a loop-hydration 401 is owned by the outer generation recovery", async () => {
  let rehydrations = 0;
  const loopBuilder = {
    activate() {},
    deactivate() {},
    refreshWorkspace() {},
    rehydrateSession() {
      rehydrations++;
      if (rehydrations === 2) {
        const error = new Error("The host restarted during loop hydration.");
        error.status = 401;
        throw error;
      }
      return { refreshed: true };
    },
    resumeSession() {},
    suspendSession() {},
  };
  const app = await loadApp({ loopBuilder });
  const timers = installWindowTimers(app);
  vm.runInContext("Math.random = () => 0", app.context);

  app.socket.serverClose();
  await vm.runInContext("sessionRecoveryPromise", app.context);
  const retry = timers.find((timer) => !timer.cancelled && timer.delay === 188);
  assert.ok(retry);
  retry.cancelled = true;
  retry.handler();
  await flushAsyncWork();
  await vm.runInContext("sessionRecoveryPromise", app.context);

  assert.equal(rehydrations, 3);
  assert.equal(FakeWebSocket.instances.length, 3);
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().connected",
      app.context,
    ),
    true,
  );
});

test("a websocket that never opens hits its bounded deadline and cannot be promoted", async () => {
  const app = await loadApp();
  const timers = installWindowTimers(app);
  FakeWebSocket.autoOpen = false;

  app.socket.serverClose();
  for (
    let attempt = 0;
    attempt < 20 && FakeWebSocket.instances.length < 2;
    attempt++
  )
    await Promise.resolve();
  const openDeadline = timers.find(
    (timer) => !timer.cancelled && timer.delay === 5000,
  );
  assert.ok(openDeadline);
  openDeadline.cancelled = true;
  openDeadline.handler();
  await vm.runInContext("sessionRecoveryPromise", app.context);

  assert.equal(FakeWebSocket.instances[1].readyState, 3);
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().connected",
      app.context,
    ),
    false,
  );
});

test("pagehide cancels a pending recovery and ignores its late completion", async () => {
  const app = await loadApp();
  installWindowTimers(app);
  let releaseSession;
  app.context.fetch = (url) =>
    url === "/api/session"
      ? new Promise((resolve) => {
          releaseSession = resolve;
        })
      : Promise.reject(new Error(`Unexpected URL: ${url}`));

  app.socket.serverClose();
  for (let attempt = 0; attempt < 20 && !releaseSession; attempt++)
    await Promise.resolve();
  app.context.window.dispatchEvent({ type: "pagehide" });
  await vm.runInContext("sessionRecoveryPromise", app.context);
  releaseSession(jsonResponse({ generationId: "too-late" }));
  await flushAsyncWork();

  assert.equal(FakeWebSocket.instances.length, 1);
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().connected",
      app.context,
    ),
    false,
  );
});

test("a page restored from the back-forward cache reconnects its stopped session", async () => {
  const app = await loadApp();
  installWindowTimers(app);

  app.context.window.dispatchEvent({ type: "pagehide", persisted: true });
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().connected",
      app.context,
    ),
    false,
  );

  app.context.window.dispatchEvent({ type: "pageshow", persisted: true });
  await vm.runInContext("sessionRecoveryPromise", app.context);

  assert.equal(FakeWebSocket.instances.length, 2);
  assert.equal(
    vm.runInContext(
      "window.embodySenseSession.getState().connected",
      app.context,
    ),
    true,
  );
});

async function loadApp(overrides = {}) {
  FakeWebSocket.instances = [];
  FakeWebSocket.currentTranscript = overrides.activeTranscript ?? null;
  FakeWebSocket.transcriptError = overrides.transcriptError ?? null;
  FakeWebSocket.autoOpen = overrides.webSocketAutoOpen ?? true;
  const document = new FakeDocument(indexSource);
  const location = {
    href: overrides.locationHref ?? "http://127.0.0.1:4378/",
  };
  const fetchCalls = [];
  const windowListeners = new Map();
  const context = {
    AbortController,
    URL,
    console,
    document,
    fetch: createFetch(overrides, fetchCalls),
    setTimeout,
    clearTimeout,
    window: {
      location,
      history: {
        replaceState(_state, _unused, url) {
          location.href = new URL(url, location.href).href;
        },
      },
      embodySenseLoopBuilder: overrides.loopBuilder,
      addEventListener(type, handler) {
        const listeners = windowListeners.get(type) ?? new Set();
        listeners.add(handler);
        windowListeners.set(type, listeners);
      },
      dispatchEvent(event) {
        for (const handler of windowListeners.get(event.type) ?? [])
          handler(event);
      },
      setTimeout: overrides.windowSetTimeout ?? setTimeout,
      clearTimeout: overrides.windowClearTimeout ?? clearTimeout,
    },
    WebSocket: FakeWebSocket,
  };
  context.globalThis = context;
  vm.runInNewContext(appSource, context, { filename: "app.js" });
  for (let attempt = 0; attempt < 4; attempt++) await flushAsyncWork();
  assert.equal(FakeWebSocket.instances.length, 1);
  return {
    context,
    elements: document.elementsObject,
    appTabs: document.appTabs,
    configTabs: document.configTabs,
    fetchCalls,
    socket: FakeWebSocket.instances[0],
  };
}

function createFetch(overrides = {}, calls = []) {
  const status = overrides.status ?? {
    workspaceRoot: "C:/workspace",
    initialized: true,
    client: "web",
    cliRole: "CLI remains available.",
  };
  const configuration = overrides.configuration ?? {
    status: { initialized: true },
    runtime: {
      surface: "web",
      model: "configured externally",
      codexSandbox: "read-only",
    },
    audit: {
      path: "audit/events.ndjson",
      exists: false,
      events: [],
      readProblems: [],
    },
    conversationHistory: {
      directoryPath: ".agent/memory/conversations",
      currentPath: "current.ndjson",
      archivePath: "archive",
      transcripts: [],
      readProblems: [],
    },
    paths: [],
    concepts: [],
    documents: [],
    permissions: {
      exists: false,
      parsed: false,
      version: null,
      scope: "",
      defaultAccess: "ask",
      readProblems: [],
      approved: [],
      denied: [],
      rawJson: "",
    },
  };
  return async (url, options = {}) => {
    calls.push({ url, options });
    if (url === "/api/session") {
      return jsonResponse({
        generationId: overrides.generationId ?? "process-generation-1",
      });
    }

    if (url === "/api/status") {
      return jsonResponse(status);
    }

    if (url === "/api/configuration") {
      if (overrides.configurationGate) await overrides.configurationGate;
      return jsonResponse(configuration);
    }

    return { ok: false, text: async () => `Unexpected URL: ${url}` };
  };
}

function jsonResponse(value) {
  return {
    ok: true,
    status: 200,
    json: async () => value,
    text: async () => JSON.stringify(value),
  };
}

function errorResponse(status, message) {
  return {
    ok: false,
    status,
    json: async () => ({ detail: message }),
    text: async () => message,
  };
}

async function flushAsyncWork() {
  await new Promise((resolve) => setTimeout(resolve, 20));
}

function installWindowTimers(app) {
  const scheduled = [];
  app.context.window.setTimeout = (handler, delay) => {
    const timer = { handler, delay, cancelled: false };
    scheduled.push(timer);
    return timer;
  };
  app.context.window.clearTimeout = (timer) => {
    timer.cancelled = true;
  };
  return scheduled;
}

function configTab(app, name) {
  return app.configTabs.find((tab) => tab.dataset.configTab === name);
}

function messageRole(message) {
  return message.querySelector(".message-role").textContent;
}

function messageContent(message) {
  return message.querySelector(".message-content").textContent;
}

function findByTag(root, tagName) {
  const matches = [];
  for (const child of root.children) {
    if (child.tagName === tagName.toUpperCase()) {
      matches.push(child);
    }

    matches.push(...findByTag(child, tagName));
  }

  return matches;
}

function assertSingle(items) {
  assert.equal(items.length, 1);
  return items[0];
}

class FakeWebSocket {
  static OPEN = 1;
  static instances = [];
  static currentTranscript = null;
  static transcriptError = null;
  static autoOpen = true;

  constructor(url) {
    this.url = url;
    this.readyState = FakeWebSocket.OPEN;
    this.sent = [];
    FakeWebSocket.instances.push(this);
    if (FakeWebSocket.autoOpen) setTimeout(() => this.onopen?.(), 0);
  }

  send(message) {
    this.sent.push(message);
    const payload = parseFrame(message);
    if (!payload.type) {
      setTimeout(() => this.serverSend({}), 0);
      return;
    }

    if (payload.type === 1 && payload.invocationId !== undefined) {
      if (
        payload.target === "GetCurrentTranscript" &&
        FakeWebSocket.transcriptError
      ) {
        setTimeout(
          () =>
            this.serverSend({
              type: 3,
              invocationId: payload.invocationId,
              error: FakeWebSocket.transcriptError,
            }),
          0,
        );
        return;
      }
      const result =
        payload.target === "DecideApproval"
          ? { accepted: true }
          : payload.target === "GetPendingApprovals"
            ? []
            : payload.target === "GetCurrentTranscript"
              ? FakeWebSocket.currentTranscript
              : true;
      setTimeout(
        () =>
          this.serverSend({
            type: 3,
            invocationId: payload.invocationId,
            result,
          }),
        0,
      );
    }
  }

  close() {
    this.readyState = 3;
    this.onclose?.();
  }

  serverSendInvocation(target, ...args) {
    this.serverSend({ type: 1, target, arguments: args });
  }

  serverClose() {
    this.readyState = 3;
    this.onclose?.();
  }

  serverSend(message) {
    this.onmessage?.({ data: `${JSON.stringify(message)}${recordSeparator}` });
  }

  sentInvocations(target) {
    return this.sent
      .map(parseFrame)
      .filter((message) => message.type === 1 && message.target === target);
  }
}

function parseFrame(message) {
  return JSON.parse(String(message).replace(recordSeparator, ""));
}

class FakeDocument {
  constructor(html) {
    this.elements = new Map();
    this.elementsObject = {};
    this.appTabs = [...html.matchAll(/<button\b([^>]*)>/gi)]
      .filter((match) => /\bdata-app-view="[^"]+"/i.test(match[1]))
      .map((match) => {
        const element = new FakeElement("button");
        const appView = match[1].match(/\bdata-app-view="([^"]+)"/i);
        const configTab = match[1].match(/\bdata-config-tab="([^"]+)"/i);
        const id = match[1].match(/\bid="([^"]+)"/i);
        element.dataset.appView = appView?.[1];
        if (configTab) element.dataset.configTab = configTab[1];
        if (id) element.id = id[1];
        return element;
      });
    this.configTabs = this.appTabs.filter(
      (element) => element.dataset.configTab,
    );

    for (const match of html.matchAll(/<([a-z0-9]+)[^>]*\sid="([^"]+)"/gi)) {
      const element = new FakeElement(match[1]);
      this.elements.set(match[2], element);
      this.elementsObject[match[2]] = element;
    }
  }

  getElementById(id) {
    return this.elements.get(id);
  }

  querySelectorAll(selector) {
    if (selector === "[data-config-tab]") return this.configTabs;
    if (selector === "[data-app-view]") return this.appTabs;
    return [];
  }

  createElement(tagName) {
    return new FakeElement(tagName);
  }

  createDocumentFragment() {
    return new FakeElement("#fragment");
  }
}

class FakeElement {
  constructor(tagName) {
    this.tagName = tagName.toUpperCase();
    this.attributes = new Map();
    this.children = [];
    this.dataset = {};
    this.listeners = new Map();
    this.className = "";
    this.disabled = false;
    this.checked = false;
    this.open = false;
    this.scrollHeight = 0;
    this.scrollTop = 0;
    this.value = "";
    this._textContent = "";
    this.replacementWaiter = null;
    this.classList = {
      toggle: (name, force) => {
        const values = new Set(this.className.split(/\s+/).filter(Boolean));
        if (force) {
          values.add(name);
        } else {
          values.delete(name);
        }

        this.className = [...values].join(" ");
      },
    };
  }

  append(...nodes) {
    for (const node of nodes) {
      if (node.tagName === "#FRAGMENT") {
        this.children.push(...node.children);
      } else {
        this.children.push(node);
      }
    }

    this.scrollHeight = this.children.length;
  }

  replaceChildren(...nodes) {
    this.children = [];
    this._textContent = "";
    this.append(...nodes);
    const resolve = this.replacementWaiter;
    this.replacementWaiter = null;
    resolve?.();
  }

  waitForNextReplacement() {
    assert.equal(this.replacementWaiter, null);
    return new Promise((resolve) => {
      this.replacementWaiter = resolve;
    });
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  addEventListener(name, handler) {
    this.listeners.set(name, handler);
  }

  click() {
    return this.listeners.get("click")?.({ preventDefault() {} });
  }

  change() {
    return this.listeners.get("change")?.({ preventDefault() {} });
  }

  querySelector(selector) {
    if (!selector.startsWith(".")) {
      return null;
    }

    const className = selector.slice(1);
    return findFirst(this, (child) =>
      child.className.split(/\s+/).includes(className),
    );
  }

  set textContent(value) {
    this._textContent = String(value ?? "");
    this.children = [];
  }

  get textContent() {
    return (
      this._textContent +
      this.children.map((child) => child.textContent).join("")
    );
  }
}

function findFirst(root, predicate) {
  for (const child of root.children) {
    if (predicate(child)) {
      return child;
    }

    const nested = findFirst(child, predicate);
    if (nested) {
      return nested;
    }
  }

  return null;
}
