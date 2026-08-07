import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";
import fs from "node:fs";
import { performance } from "node:perf_hooks";
import test from "node:test";
import vm from "node:vm";

const builderSource = fs.readFileSync(
  new URL("../../src/EmbodySense.Web/wwwroot/loop-builder.js", import.meta.url),
  "utf8",
);
const loopsHtml = fs.readFileSync(
  new URL("../../src/EmbodySense.Web/wwwroot/index.html", import.meta.url),
  "utf8",
);

test("catalog loading is authenticated and projects the system loop as read-only", async () => {
  const app = await loadLoopBuilder();

  const catalogRequest = app.server.calls.find(
    (call) => call.method === "GET" && call.url === "/api/loops",
  );
  assert.equal(catalogRequest.options.credentials, "same-origin");
  assert.equal(
    catalogRequest.options.headers["X-EmbodySense-Session"],
    undefined,
  );
  assert.match(app.elements.loopList.textContent, /Default conversation/);
  assert.match(app.elements.loopList.textContent, /Research pass/);
  assert.equal(app.elements.loopName.disabled, true);
  assert.equal(app.elements.saveButton.disabled, true);
  assert.equal(app.elements.deleteButton.disabled, true);
  assert.equal(app.elements.saveState.textContent, "System managed");
  assert.equal(findByClass(app.elements.loopCanvas, "node-card").length, 5);
  assert.match(
    app.elements.loopCanvas.textContent,
    /Accept user message.*Assemble runtime context.*Dispatch provider inference.*Persist transcript.*Complete loop run/,
  );
  assert.match(
    app.elements.loopCanvas.textContent,
    /accept-message-to-context.*context-to-inference.*inference-to-transcript.*transcript-to-complete-run/,
  );
  assert.doesNotMatch(
    app.elements.loopCanvas.textContent,
    /Manual trigger|Respond in role|Deterministic complete/,
  );
  assert.equal(
    app.elements.loopHeaderMeta.textContent,
    "default · Schema v1 · 5 nodes · 4 edges",
  );
  assert.equal(
    app.elements.canvasStepCount.textContent,
    "5 system nodes · 4 edges",
  );
  assert.match(
    app.elements.validationBanner.textContent,
    /does not certify the nodes and edges as an exact execution-order contract/,
  );

  await app.elements.loopSettingsButton.click();
  assert.match(
    app.elements.inspectorContent.textContent,
    /Human message.*Workspace startup context.*conversation\.turn.*workspace\.command.*Generic graph dispatch: Not implemented/,
  );

  await selectCustomLoop(app);

  assert.equal(app.elements.loopName.disabled, false);
  assert.equal(app.elements.deleteButton.disabled, false);
  assert.equal(app.elements.saveButton.disabled, true);
  assert.equal(app.elements.saveState.textContent, "Saved · v2");
});

test("retention stays lazy and performs only an explicit policy-permitted bounded cleanup", async () => {
  const server = new FakeFetchServer(createCatalog());
  let postureReads = 0;
  server.on("GET", "/api/loops/receipt-retention", () => {
    postureReads++;
    return {
      status: 200,
      body: createRetentionPosture(),
    };
  });
  server.on("POST", "/api/loops/receipt-retention/cleanup", (call) => ({
    status: 409,
    body: {
      status: "Degraded",
      health: "Degraded",
      isCommitted: false,
      exhaustionReason: "None",
      cleanupBlockReason: "AmbiguousEvidence",
      compactedArtifactCount: 0,
      compactedArtifactUtf8Bytes: 0,
      detail: "Evidence is ambiguous, so no raw receipt was removed.",
    },
  }));
  const app = await loadLoopBuilder({ server });

  assert.equal(postureReads, 0);
  await app.elements.retentionTab.click();

  assert.equal(postureReads, 1);
  assert.match(app.elements.retentionContent.textContent, /healthy/i);
  assert.match(
    app.elements.retentionContent.textContent,
    /Exact replay horizon/,
  );
  const cleanup = findByTag(app.elements.retentionContent, "button").find(
    (button) => /Clean eligible expired evidence/.test(button.textContent),
  );
  await cleanup.click();

  const cleanupCall = server.calls.find(
    (call) =>
      call.method === "POST" &&
      call.url === "/api/loops/receipt-retention/cleanup",
  );
  assert.equal(cleanupCall.body.maximumArtifactCount, 64);
  assert.equal(cleanupCall.body.maximumArtifactUtf8Bytes, 4 * 1024 * 1024);
  assert.match(app.window.confirmations.at(-1), /explicit request/i);
  assert.match(
    app.elements.retentionNotice.textContent,
    /no raw receipt was removed/i,
  );
  assert.equal(postureReads, 2);
});

test("retention cleanup retains its operation identity when an ambiguous transport failure is retried", async () => {
  const server = new FakeFetchServer(createCatalog());
  const localStorage = new FakeStorage();
  let cleanupAttempts = 0;
  server.on("GET", "/api/loops/receipt-retention", () => ({
    status: 200,
    body: createRetentionPosture(),
  }));
  server.on("POST", "/api/loops/receipt-retention/cleanup", () => {
    cleanupAttempts++;
    if (cleanupAttempts === 1)
      throw new Error("Connection dropped before a response.");
    return {
      status: 200,
      body: {
        status: "NothingEligible",
        health: "Healthy",
        isCommitted: false,
        exhaustionReason: "None",
        cleanupBlockReason: "None",
        compactedArtifactCount: 0,
        compactedArtifactUtf8Bytes: 0,
        detail: "No evidence is eligible for cleanup.",
      },
    };
  });
  const first = await loadLoopBuilder({ server, localStorage });
  await first.elements.retentionTab.click();
  const firstCleanup = findByTag(
    first.elements.retentionContent,
    "button",
  ).find((button) =>
    /Clean eligible expired evidence/.test(button.textContent),
  );

  await firstCleanup.click();
  const storageKey = `embodysense.pending-receipt-cleanup.v1.${encodeURIComponent("C:/workspace")}`;
  assert.ok(localStorage.getItem(storageKey));

  const second = await loadLoopBuilder({ server, localStorage });
  await second.elements.retentionTab.click();
  const secondCleanup = findByTag(
    second.elements.retentionContent,
    "button",
  ).find((button) =>
    /Clean eligible expired evidence/.test(button.textContent),
  );
  await secondCleanup.click();

  const cleanupCalls = server.calls.filter(
    (call) =>
      call.method === "POST" &&
      call.url === "/api/loops/receipt-retention/cleanup",
  );
  assert.equal(cleanupCalls.length, 2);
  assert.equal(
    cleanupCalls[0].body.operationId,
    cleanupCalls[1].body.operationId,
  );
  assert.equal(localStorage.getItem(storageKey), null);
});

test("retention cleanup remains disabled when Web Locks cannot coordinate its shared identity", async () => {
  const server = new FakeFetchServer(createCatalog());
  server.on("GET", "/api/loops/receipt-retention", () => ({
    status: 200,
    body: createRetentionPosture(),
  }));
  const app = await loadLoopBuilder({ server, locks: {} });

  await app.elements.retentionTab.click();
  const cleanup = findByClass(
    app.elements.retentionContent,
    "retention-cleanup-button",
  )[0];

  assert.equal(cleanup.disabled, true);
  assert.match(
    app.elements.retentionNotice.textContent,
    /cannot durably coordinate/i,
  );
  await cleanup.click();
  assert.equal(
    server.calls.some(
      (call) =>
        call.method === "POST" &&
        call.url === "/api/loops/receipt-retention/cleanup",
    ),
    false,
  );
});

test("retention cleanup remains disabled when its shared identity cannot be persisted", async () => {
  const server = new FakeFetchServer(createCatalog());
  const localStorage = new FakeStorage();
  localStorage.setItem = () => {
    throw new Error("Storage quota exceeded.");
  };
  server.on("GET", "/api/loops/receipt-retention", () => ({
    status: 200,
    body: createRetentionPosture(),
  }));
  const app = await loadLoopBuilder({ server, localStorage });

  await app.elements.retentionTab.click();
  const cleanup = findByClass(
    app.elements.retentionContent,
    "retention-cleanup-button",
  )[0];

  assert.equal(cleanup.disabled, true);
  assert.match(
    app.elements.retentionNotice.textContent,
    /cannot durably coordinate/i,
  );
  assert.equal(
    server.calls.some(
      (call) =>
        call.method === "POST" &&
        call.url === "/api/loops/receipt-retention/cleanup",
    ),
    false,
  );
});

test("an authoritative cleanup outcome remains visible when its shared identity cannot be retired", async () => {
  const server = new FakeFetchServer(createCatalog());
  const localStorage = new FakeStorage();
  const scope = encodeURIComponent("C:/workspace".normalize("NFC"));
  const storageKey = `embodysense.pending-receipt-cleanup.v1.${scope}`;
  const removeItem = localStorage.removeItem.bind(localStorage);
  localStorage.removeItem = (key) => {
    if (key === storageKey) throw new Error("Storage cleanup failed.");
    removeItem(key);
  };
  server.on("GET", "/api/loops/receipt-retention", () => ({
    status: 200,
    body: createRetentionPosture(),
  }));
  server.on("POST", "/api/loops/receipt-retention/cleanup", () => ({
    status: 200,
    body: {
      status: "NothingEligible",
      health: "Healthy",
      isCommitted: false,
      exhaustionReason: "None",
      cleanupBlockReason: "None",
      compactedArtifactCount: 0,
      compactedArtifactUtf8Bytes: 0,
      detail: "No evidence is eligible for cleanup.",
    },
  }));
  const app = await loadLoopBuilder({ server, localStorage });

  await app.elements.retentionTab.click();
  const cleanup = findByClass(
    app.elements.retentionContent,
    "retention-cleanup-button",
  )[0];
  await cleanup.click();

  assert.match(app.elements.retentionNotice.textContent, /nothing eligible/i);
  assert.match(app.elements.retentionNotice.textContent, /remains reserved/i);
  assert.equal(
    findByClass(app.elements.retentionContent, "retention-cleanup-button")[0]
      .disabled,
    true,
  );
  assert.ok(localStorage.getItem(storageKey));
  assert.equal(
    server.calls.filter(
      (call) =>
        call.method === "POST" &&
        call.url === "/api/loops/receipt-retention/cleanup",
    ).length,
    1,
  );
});

test("a superseded retention read cannot replace a newer blocked posture or re-enable cleanup", async () => {
  const server = new FakeFetchServer(createCatalog());
  const pendingReads = [];
  server.on(
    "GET",
    "/api/loops/receipt-retention",
    () => new Promise((resolve) => pendingReads.push(resolve)),
  );
  const app = await loadLoopBuilder({ server });

  const firstRead = app.elements.retentionTab.click();
  await flushAsyncWork();
  const secondRead = app.elements.retentionTab.click();
  await flushAsyncWork();
  assert.equal(pendingReads.length, 2);

  pendingReads[1]({
    status: 200,
    body: createRetentionPosture({
      health: "corrupt",
      cleanupBlockReason: "CorruptEvidence",
    }),
  });
  await secondRead;
  pendingReads[0]({ status: 200, body: createRetentionPosture() });
  await firstRead;

  const cleanup = findByClass(
    app.elements.retentionContent,
    "retention-cleanup-button",
  )[0];
  assert.match(app.elements.retentionContent.textContent, /corrupt/i);
  assert.equal(cleanup.disabled, true);
  assert.equal(app.elements.refreshRetentionButton.disabled, false);
});

test("retention posture failure remains visible with its actionable server detail", async () => {
  const server = new FakeFetchServer(createCatalog());
  server.on("GET", "/api/loops/receipt-retention", () => ({
    status: 503,
    body: {
      detail:
        "The retention audit sink is unavailable; retry after audit recovery.",
    },
  }));
  const app = await loadLoopBuilder({ server });

  await app.elements.retentionTab.click();

  assert.match(
    app.elements.retentionNotice.textContent,
    /retention audit sink is unavailable/i,
  );
  assert.doesNotMatch(
    app.elements.retentionNotice.textContent,
    /Read the current bounded retention posture/i,
  );
  assert.match(
    app.elements.retentionContent.textContent,
    /Select Refresh posture to retry/i,
  );
});

for (const [health, blockReason, exhaustionReason, cleanupEnabled, label] of [
  ["healthy", "None", "None", true, "Healthy"],
  ["exhausted", "None", "ArtifactCountLimit", true, "Exhausted"],
  ["corrupt", "CorruptEvidence", "None", false, "Corrupt"],
  ["audit-unavailable", "AuditUnavailable", "None", false, "Audit Unavailable"],
  [
    "ownership-conflict",
    "OwnershipUnresolved",
    "None",
    false,
    "Ownership Conflict",
  ],
  ["degraded", "PendingEvidence", "None", false, "Degraded"],
  [
    "recovery-pending",
    "OwnershipUnresolved",
    "None",
    false,
    "Recovery Pending",
  ],
]) {
  test(`retention renders ${health} and enables cleanup only when posture permits it`, async () => {
    const server = new FakeFetchServer(createCatalog());
    server.on("GET", "/api/loops/receipt-retention", () => ({
      status: 200,
      body: createRetentionPosture({
        health,
        cleanupBlockReason: blockReason,
        exhaustionReason,
        cleanupRecoveryAvailableAtUtc:
          health === "recovery-pending" ? "2999-08-01T12:00:00Z" : null,
      }),
    }));
    const app = await loadLoopBuilder({ server });

    await app.elements.retentionTab.click();

    const cleanup = findByClass(
      app.elements.retentionContent,
      "retention-cleanup-button",
    )[0];
    assert.match(
      app.elements.retentionContent.textContent,
      new RegExp(label, "i"),
    );
    assert.equal(cleanup.disabled, !cleanupEnabled);
  });
}

test("retention exposes an explicit recovery retry after the ownership window", async () => {
  const server = new FakeFetchServer(createCatalog());
  server.on("GET", "/api/loops/receipt-retention", () => ({
    status: 200,
    body: createRetentionPosture({
      health: "recovery-pending",
      cleanupBlockReason: "OwnershipUnresolved",
      cleanupRecoveryAvailableAtUtc: "2020-08-01T12:00:00Z",
    }),
  }));
  server.on("POST", "/api/loops/receipt-retention/cleanup", () => ({
    status: 200,
    body: {
      status: "NothingEligible",
      health: "Healthy",
      isCommitted: false,
      exhaustionReason: "None",
      cleanupBlockReason: "None",
      compactedArtifactCount: 0,
      compactedArtifactUtf8Bytes: 0,
      detail: "The stale cleanup journal recovered without removing evidence.",
    },
  }));
  const app = await loadLoopBuilder({ server });

  await app.elements.retentionTab.click();
  const retry = findByClass(
    app.elements.retentionContent,
    "retention-cleanup-button",
  )[0];
  assert.equal(retry.disabled, false);
  assert.match(retry.textContent, /Retry cleanup recovery/i);
  await retry.click();

  assert.equal(
    server.calls.filter(
      (call) =>
        call.method === "POST" &&
        call.url === "/api/loops/receipt-retention/cleanup",
    ).length,
    1,
  );
});

test("a rejected system runner contract is shown as invalid throughout the graph", async () => {
  const catalog = createCatalog();
  catalog.systemDefault.executionContract.graphSemantics = "unknown";
  catalog.systemDefault.executionContract.detail =
    "The default conversation graph does not match the dedicated runner contract.";
  catalog.systemDefault.graph.edges.push(
    createSystemEdge(
      "rejected-branch",
      "accept-user-message",
      "dispatch-provider-inference",
      "success",
      "A noncanonical branch that the dedicated runner rejects.",
    ),
  );
  for (const graphNode of catalog.systemDefault.graph.nodes)
    graphNode.executionSemantics = "unknown";
  for (const edge of catalog.systemDefault.graph.edges)
    edge.executionSemantics = "unknown";

  const app = await loadLoopBuilder({ catalog });

  assert.equal(
    app.elements.validationBanner.className,
    "validation-banner visible error",
  );
  assert.equal(
    app.elements.validationBanner.textContent,
    "The default conversation graph does not match the dedicated runner contract.",
  );
  assert.match(
    app.elements.validationBanner.attributes.get("aria-label"),
    /Definition needs attention/,
  );
  assert.doesNotMatch(
    app.elements.loopCanvas.textContent,
    /Validated runner contract/,
  );
  assert.match(
    app.elements.loopCanvas.textContent,
    /Runner contract not validated/,
  );
  assert.match(
    app.elements.loopCanvas.textContent,
    /rejected-branch.*accept-user-message → dispatch-provider-inference/,
  );
  assert.equal(
    findByClass(app.elements.loopCanvas, "system-connector").length,
    5,
  );
});

test("initialization refresh hydrates a loop builder that booted disabled", async () => {
  const server = new FakeFetchServer(createCatalog());
  let initialized = false;
  server.on("GET", "/api/status", () => ({
    status: 200,
    body: { workspaceRoot: "C:/workspace", initialized },
  }));
  const app = await loadLoopBuilder({ server });

  assert.equal(app.elements.createLoopButton.disabled, true);
  assert.match(
    app.elements.validationBanner.textContent,
    /Complete workspace initialization/,
  );
  initialized = true;
  await app.window.embodySenseLoopBuilder.refreshWorkspace();

  assert.equal(app.elements.createLoopButton.disabled, false);
  assert.equal(app.elements.loopSearch.disabled, false);
  assert.match(app.elements.loopList.textContent, /Default conversation/);
  assert.match(app.elements.loopList.textContent, /Research pass/);
});

test("the uninitialized Loops deep link explains exact effects and supports an explicit decline", async () => {
  const server = new FakeFetchServer(createCatalog());
  server.on("GET", "/api/status", () => ({
    status: 200,
    body: {
      workspaceRoot: "C:/deliberate-workspace",
      initialized: false,
      initializationState: "uninitialized",
    },
  }));
  const app = await loadLoopBuilder({ server });

  assert.equal(app.elements.loopInitializationPanel.hidden, false);
  assert.equal(
    app.elements.loopInitializationRoot.textContent,
    "C:/deliberate-workspace",
  );
  assert.match(
    loopsHtml,
    /create <code>\.agent\/<\/code> identity, role, context, memory,[\s\S]*permissions, audit, loop, task, skill, hook, recipe, log,[\s\S]*and export scaffolding/,
  );
  assert.match(
    loopsHtml,
    /<code>private\/<\/code>,[\s\S]*<code>shared\/<\/code>,[\s\S]*<code>generated\/<\/code>, and[\s\S]*<code>system\/<\/code>/,
  );
  assert.match(
    loopsHtml,
    /No custom loop is created, and no loop or model inference runs[\s\S]*as a side effect/,
  );
  assert.match(
    loopsHtml,
    /id="loopInitializationAnnouncement"[\s\S]*role="status"[\s\S]*aria-live="polite"[\s\S]*aria-atomic="true"/,
  );

  await app.elements.declineLoopsInitializationButton.click();

  assert.match(
    app.elements.loopInitializationStatus.textContent,
    /declined.*Nothing was changed.*no loop ran/i,
  );
  assert.equal(
    server.calls.some((call) => call.method === "POST"),
    false,
  );
});

test("Loops initializes through the existing workspace boundary and hydrates only after authoritative success", async () => {
  const server = new FakeFetchServer(createCatalog());
  let initialized = false;
  server.on("GET", "/api/status", () => ({
    status: 200,
    body: {
      workspaceRoot: "C:/workspace",
      initialized,
      initializationState: initialized ? "initialized" : "uninitialized",
    },
  }));
  server.on("POST", "/api/workspace/init", () => {
    initialized = true;
    return {
      status: 200,
      body: {
        workspaceRoot: "C:/workspace",
        initialized: true,
        initializationState: "initialized",
        initializationOutcome: "initialized",
      },
    };
  });
  const app = await loadLoopBuilder({ server });

  await app.elements.initializeLoopsWorkspaceButton.click();

  assert.equal(
    server.calls.filter(
      (call) => call.method === "POST" && call.url === "/api/workspace/init",
    ).length,
    1,
  );
  assert.equal(app.elements.loopInitializationPanel.hidden, true);
  assert.equal(app.elements.createLoopButton.disabled, false);
  assert.notEqual(app.elements.roleId.textContent, "Loading");
  assert.match(app.elements.loopList.textContent, /Research pass/);
  assert.match(
    app.elements.loopInitializationAnnouncement.textContent,
    /initialization completed.*no loop ran/i,
  );
  assert.equal(
    server.calls.some(
      (call) =>
        call.method === "POST" &&
        (call.url === "/api/loops" || call.url.includes("loop-runs")),
    ),
    false,
  );
});

test("double click and two tabs serialize one workspace initialization submission", async () => {
  const server = new FakeFetchServer(createCatalog());
  const locks = new FakeLockManager();
  let initialized = false;
  let releaseInitialization;
  server.on("GET", "/api/status", () => ({
    status: 200,
    body: {
      workspaceRoot: "C:/workspace",
      initialized,
      initializationState: initialized ? "initialized" : "uninitialized",
    },
  }));
  server.on(
    "POST",
    "/api/workspace/init",
    () =>
      new Promise((resolve) => {
        releaseInitialization = () => {
          initialized = true;
          resolve({
            status: 200,
            body: {
              workspaceRoot: "C:/workspace",
              initialized: true,
              initializationState: "initialized",
              initializationOutcome: "initialized",
            },
          });
        };
      }),
  );
  const first = await loadLoopBuilder({ server, locks });
  const second = await loadLoopBuilder({ server, locks });

  const firstAttempt = first.elements.initializeLoopsWorkspaceButton.click();
  await flushAsyncWork();
  const duplicateClick = first.elements.initializeLoopsWorkspaceButton.click();
  const secondAttempt = second.elements.initializeLoopsWorkspaceButton.click();
  await flushAsyncWork();
  assert.equal(
    server.calls.filter(
      (call) => call.method === "POST" && call.url === "/api/workspace/init",
    ).length,
    1,
  );

  releaseInitialization();
  await Promise.all([firstAttempt, duplicateClick, secondAttempt]);

  assert.equal(
    server.calls.filter(
      (call) => call.method === "POST" && call.url === "/api/workspace/init",
    ).length,
    1,
  );
  assert.equal(first.elements.loopInitializationPanel.hidden, true);
  assert.equal(second.elements.loopInitializationPanel.hidden, true);
  assert.match(
    second.elements.loopInitializationAnnouncement.textContent,
    /already initialized/i,
  );
});

test("partial failure stays locked and offers a recoverable retry distinct from decline", async () => {
  const server = new FakeFetchServer(createCatalog());
  let partial = false;
  server.on("GET", "/api/status", () => ({
    status: 200,
    body: {
      workspaceRoot: "C:/workspace",
      initialized: false,
      initializationState: partial ? "partial" : "uninitialized",
    },
  }));
  server.on("POST", "/api/workspace/init", () => {
    partial = true;
    return {
      status: 500,
      body: { detail: "A scaffold write failed." },
    };
  });
  const app = await loadLoopBuilder({ server });

  await app.elements.initializeLoopsWorkspaceButton.click();

  assert.equal(app.elements.createLoopButton.disabled, true);
  assert.equal(app.elements.loopInitializationPanel.hidden, false);
  assert.equal(
    app.elements.initializeLoopsWorkspaceButton.textContent,
    "Retry initialization",
  );
  assert.match(
    app.elements.loopInitializationStatus.textContent,
    /failed after creating part.*No loop ran.*Retry to create/i,
  );
});

test("corrupt protected initialization sentinels require explicit cleanup instead of a futile retry", async () => {
  const server = new FakeFetchServer(createCatalog());
  let initialized = false;
  let requiresCleanup = true;
  server.on("GET", "/api/status", () => ({
    status: 200,
    body: {
      workspaceRoot: "C:/workspace",
      initialized,
      initializationState: initialized ? "initialized" : "partial",
      initializationRequiresCleanup: !initialized && requiresCleanup,
    },
  }));
  server.on("POST", "/api/workspace/init", () => {
    initialized = true;
    return {
      status: 200,
      body: {
        workspaceRoot: "C:/workspace",
        initialized: true,
        initializationState: "initialized",
        initializationRequiresCleanup: false,
        initializationOutcome: "initialized",
      },
    };
  });
  const app = await loadLoopBuilder({ server });

  assert.equal(app.elements.createLoopButton.disabled, true);
  assert.equal(app.elements.loopInitializationPanel.hidden, false);
  assert.equal(
    app.elements.initializeLoopsWorkspaceButton.textContent,
    "Check after cleanup",
  );
  assert.equal(app.elements.initializeLoopsWorkspaceButton.disabled, false);
  assert.match(
    app.elements.loopInitializationStatus.textContent,
    /unusable protected.*ROLE\.md.*permissions\.json.*workspace-initialized\.json.*Back up.*remove the invalid file or directory.*retrying without cleanup cannot replace/i,
  );
  await app.elements.initializeLoopsWorkspaceButton.click();
  assert.match(
    app.elements.loopInitializationStatus.textContent,
    /still requires cleanup.*Back up.*remove the unusable protected.*check again.*No loop ran.*no protected file was replaced/i,
  );
  assert.equal(
    server.calls.some(
      (call) => call.method === "POST" && call.url === "/api/workspace/init",
    ),
    false,
  );

  requiresCleanup = false;
  await app.elements.initializeLoopsWorkspaceButton.click();

  assert.equal(
    server.calls.filter(
      (call) => call.method === "POST" && call.url === "/api/workspace/init",
    ).length,
    1,
  );
  assert.equal(app.elements.loopInitializationPanel.hidden, true);
  assert.equal(app.elements.createLoopButton.disabled, false);
});

test("plain initialization failure keeps authoring locked and offers an exact retry without assuming success", async () => {
  const server = new FakeFetchServer(createCatalog());
  server.on("GET", "/api/status", () => ({
    status: 200,
    body: {
      workspaceRoot: "C:/workspace",
      initialized: false,
      initializationState: "uninitialized",
    },
  }));
  server.on("POST", "/api/workspace/init", () => ({
    status: 500,
    body: { detail: "The workspace root is temporarily unavailable." },
  }));
  const app = await loadLoopBuilder({ server });

  await app.elements.initializeLoopsWorkspaceButton.click();

  assert.equal(app.elements.createLoopButton.disabled, true);
  assert.equal(app.elements.loopInitializationPanel.hidden, false);
  assert.equal(
    app.elements.initializeLoopsWorkspaceButton.textContent,
    "Initialize workspace",
  );
  assert.equal(app.elements.initializeLoopsWorkspaceButton.disabled, false);
  assert.match(
    app.elements.loopInitializationStatus.textContent,
    /failed before the workspace became ready.*Nothing is unlocked.*no loop ran.*temporarily unavailable/i,
  );
  assert.doesNotMatch(
    app.elements.loopInitializationAnnouncement.textContent,
    /completed|already initialized/i,
  );
  assert.equal(
    server.calls.filter(
      (call) => call.method === "POST" && call.url === "/api/workspace/init",
    ).length,
    1,
  );
  assert.equal(
    server.calls.some((call) => call.url === "/api/loops"),
    false,
  );
});

test("a disconnect during initialization ignores stale completion and reconciles exact status on reconnect", async () => {
  const server = new FakeFetchServer(createCatalog());
  let initialized = false;
  let releaseInitialization;
  server.on("GET", "/api/status", () => ({
    status: 200,
    body: {
      workspaceRoot: "C:/workspace",
      initialized,
      initializationState: initialized ? "initialized" : "uninitialized",
    },
  }));
  server.on(
    "POST",
    "/api/workspace/init",
    () =>
      new Promise((resolve) => {
        releaseInitialization = () => {
          initialized = true;
          resolve({
            status: 200,
            body: {
              workspaceRoot: "C:/workspace",
              initialized: true,
              initializationState: "initialized",
              initializationOutcome: "initialized",
            },
          });
        };
      }),
  );
  const app = await loadLoopBuilder({ server });

  const attempt = app.elements.initializeLoopsWorkspaceButton.click();
  await flushAsyncWork();
  app.window.embodySenseLoopBuilder.suspendSession();
  releaseInitialization();
  await attempt;

  assert.match(
    app.elements.loopInitializationStatus.textContent,
    /disconnected.*No completion is assumed/i,
  );
  assert.equal(app.elements.createLoopButton.disabled, true);

  await app.window.embodySenseLoopBuilder.rehydrateSession({
    signal: new AbortController().signal,
    workspaceRoot: "C:/workspace",
  });
  app.window.embodySenseLoopBuilder.resumeSession();

  assert.equal(app.elements.loopInitializationPanel.hidden, true);
  assert.equal(app.elements.createLoopButton.disabled, false);
  assert.match(
    app.elements.loopInitializationAnnouncement.textContent,
    /Connection restored.*authoritative Loops state is loaded/i,
  );
});

test("initialization refresh queues behind a stale activation status read", async () => {
  const server = new FakeFetchServer(createCatalog());
  let releaseStaleStatus;
  let statusReads = 0;
  server.on("GET", "/api/status", () => {
    statusReads++;
    if (statusReads === 1) {
      return new Promise((resolve) => {
        releaseStaleStatus = () =>
          resolve({
            status: 200,
            body: { workspaceRoot: "C:/workspace", initialized: false },
          });
      });
    }
    return {
      status: 200,
      body: { workspaceRoot: "C:/workspace", initialized: true },
    };
  });
  const app = await loadLoopBuilder({ server, loopsViewHidden: true });
  const activation = app.window.embodySenseLoopBuilder.activate();
  for (let attempt = 0; attempt < 20 && !releaseStaleStatus; attempt++)
    await new Promise((resolve) => setTimeout(resolve, 5));

  const initializationRefresh =
    app.window.embodySenseLoopBuilder.refreshWorkspace();
  releaseStaleStatus();
  await Promise.all([activation, initializationRefresh]);

  assert.equal(statusReads, 2);
  assert.equal(app.elements.createLoopButton.disabled, false);
  assert.match(app.elements.loopList.textContent, /Research pass/);
});

test("hidden Loops defers catalog and evidence requests until first activation", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server, loopsViewHidden: true });

  assert.equal(
    server.calls.some((call) => call.url === "/api/loops"),
    false,
  );
  assert.equal(
    server.calls.some((call) => call.url.startsWith("/api/loop-runs")),
    false,
  );
  await app.window.embodySenseLoopBuilder.activate();

  assert.equal(
    server.calls.some((call) => call.url === "/api/loops"),
    true,
  );
  assert.equal(
    server.calls.some((call) => call.url.startsWith("/api/loop-runs")),
    true,
  );
});

test("a loop route loaded during session recovery waits for promotion before activation", async () => {
  let hubReads = 0;
  const sharedHub = { connected: true, on() {} };
  const app = await loadLoopBuilder({
    embodySenseSession: {
      getState: () => ({ connected: false }),
      getHub: async () => {
        hubReads++;
        return sharedHub;
      },
    },
  });

  assert.equal(hubReads, 0);
  assert.equal(
    app.server.calls.filter((call) => call.url === "/api/loops").length,
    0,
  );

  app.window.embodySenseLoopBuilder.resumeSession();
  await flushAsyncWork();

  assert.equal(hubReads, 1);
  assert.equal(
    app.server.calls.filter((call) => call.url === "/api/loops").length,
    1,
  );
  assert.equal(app.elements.roleId.textContent, app.server.catalog.roleId);
});

test("revisiting Loops preserves an unsaved draft without reloading the catalog", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server, loopsViewHidden: true });
  await app.window.embodySenseLoopBuilder.activate();
  await selectCustomLoop(app);
  app.elements.loopDescription.value = "Unsaved reviewer notes";
  await app.elements.loopDescription.input();
  const catalogRequests = server.calls.filter(
    (call) => call.url === "/api/loops",
  ).length;

  await app.window.embodySenseLoopBuilder.activate();

  assert.equal(app.elements.loopDescription.value, "Unsaved reviewer notes");
  assert.equal(app.elements.saveButton.disabled, false);
  assert.equal(
    server.calls.filter((call) => call.url === "/api/loops").length,
    catalogRequests,
  );
});

test("session rehydration reloads authoritative loop evidence without overwriting an unsaved draft", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  app.elements.loopDescription.value = "Unsaved recovery draft";
  await app.elements.loopDescription.input();
  const catalogRequests = server.calls.filter(
    (call) => call.url === "/api/loops",
  ).length;
  const runRequests = server.calls.filter((call) =>
    call.url.startsWith("/api/loop-runs"),
  ).length;

  const outcome = await app.window.embodySenseLoopBuilder.rehydrateSession({
    approvals: [],
    workspaceRoot: "C:/workspace",
  });

  assert.equal(outcome.refreshed, true);
  assert.equal(app.elements.loopDescription.value, "Unsaved recovery draft");
  assert.equal(app.elements.saveButton.disabled, false);
  assert.equal(
    server.calls.filter((call) => call.url === "/api/loops").length,
    catalogRequests + 1,
  );
  assert.ok(
    server.calls.filter((call) => call.url.startsWith("/api/loop-runs"))
      .length > runRequests,
  );
});

test("session rehydration waits for its authoritative refresh when another refresh is active", async () => {
  const server = new FakeFetchServer(createCatalog());
  let statusReads = 0;
  let releaseActiveRefresh;
  server.on("GET", "/api/status", () => {
    statusReads++;
    if (statusReads === 2) {
      return new Promise((resolve) => {
        releaseActiveRefresh = () =>
          resolve({
            status: 200,
            body: { workspaceRoot: "C:/workspace", initialized: true },
          });
      });
    }
    return {
      status: 200,
      body: { workspaceRoot: "C:/workspace", initialized: true },
    };
  });
  const app = await loadLoopBuilder({ server });
  const activeRefresh = app.window.embodySenseLoopBuilder.refreshWorkspace();
  for (let attempt = 0; attempt < 20 && !releaseActiveRefresh; attempt++)
    await new Promise((resolve) => setTimeout(resolve, 5));

  let recoveryFinished = false;
  const recovery = app.window.embodySenseLoopBuilder
    .rehydrateSession({ approvals: [], workspaceRoot: "C:/workspace" })
    .then((outcome) => {
      recoveryFinished = true;
      return outcome;
    });
  await Promise.resolve();
  assert.equal(recoveryFinished, false);

  releaseActiveRefresh();
  const [recoveryOutcome] = await Promise.all([recovery, activeRefresh]);

  assert.equal(recoveryOutcome.refreshed, true);
  assert.equal(statusReads, 3);
});

test("session rehydration stops on a changed workspace and retains the draft for manual recovery", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);
  app.elements.loopDescription.value = "Draft scoped to the original workspace";
  await app.elements.loopDescription.input();

  const outcome = await app.window.embodySenseLoopBuilder.rehydrateSession({
    approvals: [],
    workspaceRoot: "C:/different-workspace",
  });

  assert.equal(outcome.requiresManualAction, true);
  assert.equal(
    app.elements.loopDescription.value,
    "Draft scoped to the original workspace",
  );
  assert.equal(app.elements.loopDescription.disabled, true);
  assert.match(app.elements.validationBanner.textContent, /workspace changed/i);
});

test("a transient first activation failure retries without rebinding events", async () => {
  const server = new FakeFetchServer(createCatalog());
  let catalogAttempts = 0;
  server.on("GET", "/api/loops", () => {
    catalogAttempts++;
    return catalogAttempts === 1
      ? { status: 503, body: { detail: "Catalog temporarily unavailable." } }
      : { status: 200, body: createCatalog() };
  });
  const app = await loadLoopBuilder({ server, loopsViewHidden: true });

  await app.window.embodySenseLoopBuilder.activate();
  assert.match(
    app.elements.validationBanner.textContent,
    /Catalog temporarily unavailable/,
  );
  assert.ok(
    findByTag(app.elements.validationBanner, "button").some(
      (button) => button.textContent === "Retry",
    ),
  );

  await app.window.embodySenseLoopBuilder.refreshWorkspace();

  assert.equal(catalogAttempts, 2);
  assert.match(app.elements.loopList.textContent, /Research pass/);
  assert.equal(
    app.elements.createLoopButton.listeners.get("click") != null,
    true,
  );
});

test("run-evidence activation retry preserves the draft and clears its failure state", async () => {
  const server = new FakeFetchServer(createCatalog());
  let runAttempts = 0;
  server.on("GET", "/api/loop-runs?maximumCount=50", () => {
    runAttempts++;
    return runAttempts === 1
      ? { status: 503, body: { detail: "Evidence temporarily unavailable." } }
      : { status: 200, body: { items: [], continuationCursor: null } };
  });
  const app = await loadLoopBuilder({ server, loopsViewHidden: true });

  await app.window.embodySenseLoopBuilder.activate();
  assert.match(
    app.elements.validationBanner.textContent,
    /Run evidence unavailable/,
  );
  await selectCustomLoop(app);
  app.elements.loopDescription.value = "Unsaved retry notes";
  await app.elements.loopDescription.input();

  await app.window.embodySenseLoopBuilder.refreshWorkspace();

  assert.equal(runAttempts, 2);
  assert.equal(
    server.calls.filter((call) => call.url === "/api/loops").length,
    1,
  );
  assert.equal(app.elements.loopDescription.value, "Unsaved retry notes");
  assert.equal(app.elements.saveButton.disabled, false);
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /Run evidence unavailable|Retry/,
  );
});

test("a status refresh failure becomes retryable and cached-catalog recovery restores the UI", async () => {
  const server = new FakeFetchServer(createCatalog());
  let statusAvailable = true;
  server.on("GET", "/api/status", () =>
    statusAvailable
      ? {
          status: 200,
          body: { workspaceRoot: "C:/workspace", initialized: true },
        }
      : { status: 503, body: { detail: "Status temporarily unavailable." } },
  );
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  app.elements.loopDescription.value = "Unsaved status retry notes";
  await app.elements.loopDescription.input();

  statusAvailable = false;
  await app.window.embodySenseLoopBuilder.refreshWorkspace();
  assert.equal(vm.runInContext("loopBuilderActivated", app.context), false);
  assert.equal(app.elements.loopDescription.disabled, true);
  assert.match(
    app.elements.validationBanner.textContent,
    /Status temporarily unavailable.*Retry/,
  );

  statusAvailable = true;
  await app.window.embodySenseLoopBuilder.refreshWorkspace();
  assert.equal(vm.runInContext("loopBuilderActivated", app.context), true);
  assert.equal(
    server.calls.filter((call) => call.url === "/api/loops").length,
    1,
  );
  assert.equal(
    app.elements.loopDescription.value,
    "Unsaved status retry notes",
  );
  assert.equal(app.elements.loopDescription.disabled, false);
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /unavailable|Retry/i,
  );
});

test("a queued refresh failure leaves the builder retryable", async () => {
  const server = new FakeFetchServer(createCatalog());
  let statusReads = 0;
  let releaseActiveRefresh;
  server.on("GET", "/api/status", () => {
    statusReads++;
    if (statusReads === 2) {
      return new Promise((resolve) => {
        releaseActiveRefresh = () =>
          resolve({
            status: 200,
            body: { workspaceRoot: "C:/workspace", initialized: true },
          });
      });
    }
    if (statusReads === 3)
      return { status: 503, body: { detail: "Queued refresh failed." } };
    return {
      status: 200,
      body: { workspaceRoot: "C:/workspace", initialized: true },
    };
  });
  const app = await loadLoopBuilder({ server });

  const activeRefresh = app.window.embodySenseLoopBuilder.refreshWorkspace();
  for (let attempt = 0; attempt < 20 && !releaseActiveRefresh; attempt++)
    await new Promise((resolve) => setTimeout(resolve, 5));
  const queuedRefresh = app.window.embodySenseLoopBuilder.refreshWorkspace();
  releaseActiveRefresh();
  await Promise.all([activeRefresh, queuedRefresh]);

  assert.equal(statusReads, 3);
  assert.equal(vm.runInContext("loopBuilderActivated", app.context), false);
  assert.match(
    app.elements.validationBanner.textContent,
    /Queued refresh failed.*Retry/,
  );

  await app.window.embodySenseLoopBuilder.refreshWorkspace();
  assert.equal(vm.runInContext("loopBuilderActivated", app.context), true);
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /failed|Retry/i,
  );
});

test("Retry joins a still-draining refresh chain instead of starting a competing refresh", async () => {
  const server = new FakeFetchServer(createCatalog());
  let statusReads = 0;
  let releaseFailedRefresh;
  let releaseQueuedRefresh;
  server.on("GET", "/api/status", () => {
    statusReads++;
    if (statusReads === 2) {
      return new Promise((resolve) => {
        releaseFailedRefresh = () =>
          resolve({ status: 503, body: { detail: "First refresh failed." } });
      });
    }
    if (statusReads === 3) {
      return new Promise((resolve) => {
        releaseQueuedRefresh = () =>
          resolve({
            status: 200,
            body: { workspaceRoot: "C:/workspace", initialized: true },
          });
      });
    }
    return {
      status: 200,
      body: { workspaceRoot: "C:/workspace", initialized: true },
    };
  });
  const app = await loadLoopBuilder({ server });

  const failedRefresh = app.window.embodySenseLoopBuilder.refreshWorkspace();
  for (let attempt = 0; attempt < 20 && !releaseFailedRefresh; attempt++)
    await new Promise((resolve) => setTimeout(resolve, 5));
  const queuedRefresh = app.window.embodySenseLoopBuilder.refreshWorkspace();
  releaseFailedRefresh();
  for (let attempt = 0; attempt < 20 && !releaseQueuedRefresh; attempt++)
    await new Promise((resolve) => setTimeout(resolve, 5));
  const retry = findByTag(app.elements.validationBanner, "button").find(
    (button) => button.textContent === "Retry",
  );
  assert.ok(retry);

  const retryRefresh = retry.click();
  await Promise.resolve();
  assert.equal(statusReads, 3);
  releaseQueuedRefresh();
  await Promise.all([failedRefresh, queuedRefresh, retryRefresh]);

  assert.equal(statusReads, 3);
  assert.equal(vm.runInContext("loopBuilderActivated", app.context), true);
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /failed|Retry/i,
  );
});

test("selecting another loop during first evidence hydration does not fail activation", async () => {
  const server = new FakeFetchServer(createCatalog());
  let releaseWorkspaceRuns;
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50",
    () =>
      new Promise((resolve) => {
        releaseWorkspaceRuns = () =>
          resolve({
            status: 200,
            body: { items: [], continuationCursor: null },
          });
      }),
  );
  const app = await loadLoopBuilder({ server, loopsViewHidden: true });
  const activation = app.window.embodySenseLoopBuilder.activate();
  for (let attempt = 0; attempt < 20 && !releaseWorkspaceRuns; attempt++)
    await new Promise((resolve) => setTimeout(resolve, 5));

  await selectCustomLoop(app);
  releaseWorkspaceRuns();
  await activation;

  assert.equal(vm.runInContext("loopBuilderActivated", app.context), true);
  assert.equal(app.elements.loopName.value, "Research pass");
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /Retry|unavailable/i,
  );
});

test("server-controlled loop text is rendered as text and cannot create executable markup", async () => {
  const unsafe =
    '<img src=x onerror="globalThis.compromised=true"><script>globalThis.compromised=true</script>';
  const catalog = createCatalog();
  catalog.customDefinitions[0].displayName = unsafe;
  catalog.customDefinitions[0].inferenceSteps[0].instruction = unsafe;
  const app = await loadLoopBuilder({ catalog });

  assert.match(app.elements.loopList.textContent, /<script>/);
  assert.equal(findByTag(app.elements.loopList, "script").length, 0);
  assert.equal(findByTag(app.elements.loopList, "img").length, 0);
  await selectCustomLoop(app);
  assert.match(app.elements.loopCanvas.textContent, /<img/);
  assert.equal(findByTag(app.elements.loopCanvas, "script").length, 0);
  assert.equal(findByTag(app.elements.loopCanvas, "img").length, 0);
  assert.equal(app.context.compromised, undefined);
});

test("trigger controls support invocation, preset, and no-prompt admission with optional conversation context", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);

  let source = findByTag(app.elements.inspectorContent, "select")[0];
  assert.equal(source.value, "invocation");
  assert.match(
    app.elements.loopCanvas.textContent,
    /Invoking user prompt · conversation excluded/,
  );

  source.value = "preset";
  await source.change();
  const preset = findControlByLabel(
    app.elements.inspectorContent,
    "Preset prompt",
    "textarea",
  );
  preset.value = "Use the accepted issue statement.";
  await preset.input();
  const conversation = findControlByLabel(
    app.elements.inspectorContent,
    "Include invoking conversation history",
    "input",
  );
  conversation.checked = true;
  await conversation.change();
  assert.match(
    app.elements.loopCanvas.textContent,
    /Preset prompt · conversation included/,
  );

  source = findByTag(app.elements.inspectorContent, "select")[0];
  source.value = "none";
  await source.change();
  assert.equal(findByTag(app.elements.inspectorContent, "textarea").length, 0);
  assert.match(
    app.elements.loopCanvas.textContent,
    /No prompt · conversation included/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Trigger admission does not append it again or write durable memory/,
  );
});

test("prototype-aligned search, insertion, and zoom controls update the projected builder", async () => {
  const app = await loadLoopBuilder();

  app.elements.loopSearch.value = "research";
  await app.elements.loopSearch.input();
  assert.match(app.elements.loopList.textContent, /Research pass/);
  assert.doesNotMatch(
    app.elements.loopList.textContent,
    /Default conversation/,
  );

  app.elements.loopSearch.value = "";
  await app.elements.loopSearch.input();
  await selectCustomLoop(app);
  app.elements.loopSearch.value = "pass";
  await app.elements.loopSearch.input();
  app.elements.loopName.value = "Renamed working loop";
  await app.elements.loopName.input();
  assert.doesNotMatch(
    app.elements.loopList.textContent,
    /Renamed working loop/,
  );
  app.elements.loopSearch.value = "renamed working";
  await app.elements.loopSearch.input();
  assert.match(app.elements.loopList.textContent, /Renamed working loop/);
  app.elements.loopSearch.value = "research pass";
  await app.elements.loopSearch.input();
  assert.doesNotMatch(
    app.elements.loopList.textContent,
    /Renamed working loop/,
  );
  app.elements.loopSearch.value = "";
  await app.elements.loopSearch.input();
  const insertionControls = findByClass(
    app.elements.loopCanvas,
    "connector-add",
  );
  assert.equal(insertionControls.length, 2);
  await insertionControls[1].click();

  assert.match(app.elements.loopCanvas.textContent, /Step 2/);
  assert.match(app.elements.loopList.textContent, /2 steps/);
  assert.match(
    app.elements.validationBanner.textContent,
    /Every inference step needs a name and instruction/,
  );
  await app.elements.zoomInButton.click();
  assert.equal(app.elements.zoomLevel.textContent, "110%");
});

test("Inference and Exit expose inherited or custom context without redundant fixed context", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);

  await nodeCard(app, "inference").click();
  let policySource = findControlByLabel(
    app.elements.inspectorContent,
    "Policy source",
    "select",
  );
  assert.equal(policySource.value, "inherit");
  assert.equal(
    findControlByLabel(app.elements.inspectorContent, "Trigger prompt", "input")
      .disabled,
    true,
  );
  policySource.value = "custom";
  await policySource.change();
  assert.equal(
    findControlByLabel(app.elements.inspectorContent, "Trigger prompt", "input")
      .disabled,
    false,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Retain for later loop reasoning/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Publish to the invoking conversation/,
  );

  await nodeCard(app, "exit").click();
  policySource = findControlByLabel(
    app.elements.inspectorContent,
    "Policy source",
    "select",
  );
  assert.equal(policySource.value, "inherit");
  policySource.value = "custom";
  await policySource.change();
  assert.equal(
    findControlByLabel(
      app.elements.inspectorContent,
      "Previous iteration result",
      "input",
    ).disabled,
    false,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Evidence is independent of context/,
  );
  assert.doesNotMatch(
    `${loopsHtml}\n${builderSource}\n${app.elements.inspectorContent.textContent}`,
    /Additional fixed context/i,
  );
});

test("Exit continuation is model-gated and its iteration value is presented as a ceiling", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);
  await nodeCard(app, "exit").click();

  const continuation = findControlByLabel(
    app.elements.inspectorContent,
    "Allow continuation requests",
    "input",
  );
  continuation.checked = true;
  await continuation.change();

  assert.match(
    app.elements.inspectorContent.textContent,
    /The ceiling never causes a repeat by itself/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /A hard ceiling, not a target/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /exactly one Complete or Repeat token \(case-insensitive\)/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Invalid or uncertain decisions never repeat/,
  );
  const ceiling = findControlByLabel(
    app.elements.inspectorContent,
    "Maximum additional iterations",
    "input",
  );
  ceiling.value = "3";
  await ceiling.change();
  assert.match(
    app.elements.loopCanvas.textContent,
    /Model-gated · up to 3 additional/,
  );
  assert.match(app.elements.loopCanvas.textContent, /ceiling 3/);
});

test("loop settings expose inherited provider, model, tools, and context defaults", async () => {
  const catalog = createCatalog();
  catalog.tools.customAssignable = ["read"];
  const app = await loadLoopBuilder({ catalog });
  await selectCustomLoop(app);

  await app.elements.loopSettingsButton.click();

  assert.equal(app.elements.inspectorTitle.textContent, "Loop settings");
  assert.match(
    app.elements.inspectorContent.textContent,
    /OpenAiCodex · gpt-5-test/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Provider and model cannot be overridden per loop/,
  );
  assert.match(app.elements.inspectorContent.textContent, /Workspace tools/);
  assert.ok(findControlByLabel(app.elements.inspectorContent, "Read", "input"));
  assert.doesNotMatch(
    app.elements.inspectorContent.textContent,
    /Allow inference nodes to request the governed (list|search) command/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Inference: 4 context-in sources/,
  );
});

test("a restored draft exposes stale tool assignments so reduced authority can be repaired", async () => {
  const server = new FakeFetchServer(createCatalog());
  const sessionStorage = new FakeStorage();
  const firstView = await loadLoopBuilder({ server, sessionStorage });

  await firstView.elements.createLoopButton.click();
  await firstView.elements.loopSettingsButton.click();
  const assignedSearch = findControlByLabel(
    firstView.elements.inspectorContent,
    "Search",
    "input",
  );
  assignedSearch.checked = true;
  await assignedSearch.change();

  server.catalog.tools.customAssignable = ["read"];
  const restoredView = await loadLoopBuilder({ server, sessionStorage });
  await restoredView.elements.loopSettingsButton.click();
  const staleSearch = findControlByLabel(
    restoredView.elements.inspectorContent,
    "Search",
    "input",
  );

  assert.equal(staleSearch.checked, true);
  assert.match(
    restoredView.elements.inspectorContent.textContent,
    /Search.*outside the current role authority.*Uncheck it before saving/s,
  );

  staleSearch.checked = false;
  await staleSearch.change();

  const storedDraft = JSON.parse([...sessionStorage.values.values()][0]);
  assert.deepEqual(storedDraft.draft.toolAssignments, []);
  assert.equal(restoredView.elements.saveButton.disabled, false);
});

test("a restored draft rejects duplicate tool assignments instead of rendering ambiguous authority controls", async () => {
  const server = new FakeFetchServer(createCatalog());
  const sessionStorage = new FakeStorage();
  const firstView = await loadLoopBuilder({ server, sessionStorage });

  await firstView.elements.createLoopButton.click();
  const [storageKey, storedValue] = [...sessionStorage.values.entries()][0];
  const storedDraft = JSON.parse(storedValue);
  storedDraft.draft.toolAssignments = ["search", "search"];
  sessionStorage.setItem(storageKey, JSON.stringify(storedDraft));

  const restoredView = await loadLoopBuilder({ server, sessionStorage });

  assert.equal(sessionStorage.values.size, 0);
  assert.equal(restoredView.elements.saveState.textContent, "System managed");
  assert.doesNotMatch(
    restoredView.elements.loopList.textContent,
    /Untitled loop/,
  );
});

test("initial and user-requested run evidence failures remain visibly unavailable", async () => {
  const initialServer = new FakeFetchServer(createCatalog());
  initialServer.on("GET", "/api/loop-runs?maximumCount=50", () => ({
    status: 503,
    body: { detail: "Corrupt retained run evidence." },
  }));
  const initial = await loadLoopBuilder({ server: initialServer });

  assert.match(
    initial.elements.validationBanner.textContent,
    /Run evidence unavailable/,
  );
  assert.match(
    initial.elements.validationBanner.textContent,
    /Corrupt retained run evidence/,
  );

  const requestedServer = new FakeFetchServer(createCatalog());
  const requested = await loadLoopBuilder({ server: requestedServer });
  await selectCustomLoop(requested);
  requestedServer.on("GET", "/api/loop-runs?maximumCount=50", () => ({
    status: 503,
    body: { detail: "Run history cannot be read." },
  }));
  await requested.elements.runsTab.click();
  await flushAsyncWork();

  assert.match(
    requested.elements.validationBanner.textContent,
    /Run evidence unavailable/,
  );
  assert.match(
    requested.elements.validationBanner.textContent,
    /Run history cannot be read/,
  );
});

test("unsupported loop persistence schema cleanup guidance remains visible", async () => {
  const server = new FakeFetchServer(createCatalog());
  server.on("GET", "/api/loop-runs?maximumCount=50", () => ({
    status: 503,
    body: {
      error: "unsupported_loop_persistence_schema",
      detail:
        "The custom loop run discovery index schema version 2 is unsupported. Delete `.custom-loop-run-index.json` and retry the operation.",
    },
  }));

  const app = await loadLoopBuilder({ server });

  assert.match(
    app.elements.validationBanner.textContent,
    /Run evidence unavailable/,
  );
  assert.match(
    app.elements.validationBanner.textContent,
    /Delete `\.custom-loop-run-index\.json`/,
  );
});

test("SignalR transport sends keepalives for long-running invocations and stops them on close", async () => {
  const app = await loadLoopBuilder();
  const sockets = [];
  class FakeWebSocket {
    static OPEN = 1;
    constructor(url) {
      this.url = url;
      this.readyState = 0;
      this.sent = [];
      sockets.push(this);
    }
    send(message) {
      this.sent.push(message);
    }
    open() {
      this.readyState = FakeWebSocket.OPEN;
      this.onopen?.();
    }
    message(data) {
      this.onmessage?.({ data });
    }
    closeFromServer() {
      this.readyState = 3;
      this.onclose?.();
    }
  }
  app.context.WebSocket = FakeWebSocket;
  const Connection = vm.runInContext("JsonSignalRConnection", app.context);
  const connection = new Connection("ws://127.0.0.1/hubs/session");

  const start = connection.start();
  sockets[0].open();
  await Promise.resolve();
  sockets[0].message(`{}\u001e`);
  await start;
  const keepAlive = app.window.intervalHandlers[0];
  assert.equal(keepAlive.delay, 10000);

  keepAlive.handler();
  assert.deepEqual(JSON.parse(sockets[0].sent.at(-1).slice(0, -1)), {
    type: 6,
  });
  sockets[0].closeFromServer();
  assert.equal(keepAlive.cancelled, true);
});

test("SignalR transport identifies a disconnect before invocation dispatch and removes its pending completion", async () => {
  const app = await loadLoopBuilder();
  class FakeWebSocket {
    static OPEN = 1;
  }
  app.context.WebSocket = FakeWebSocket;
  const Connection = vm.runInContext("JsonSignalRConnection", app.context);
  const connection = new Connection("ws://127.0.0.1/hubs/session");
  connection.connected = true;
  connection.socket = {
    readyState: FakeWebSocket.OPEN,
    send: () => {
      throw new Error("The socket closed before send.");
    },
  };

  await assert.rejects(
    connection.invoke("InvokeLoop", {}),
    (error) =>
      error.name === "SignalRPreDispatchError" &&
      /closed before send/i.test(error.message),
  );

  assert.equal(connection.invocations.size, 0);
});

test("a new loop remains local until explicit Save sends the complete version-one definition", async () => {
  const catalog = createCatalog();
  const created = createCustomDefinition({
    id: "loop-created",
    definitionVersion: 1,
    displayName: "Untitled loop",
  });
  const server = new FakeFetchServer(catalog);
  server.on("POST", "/api/loops", ({ body }) => {
    assert.equal(typeof body.operationId, "string");
    const committed = {
      ...created,
      ...clone(body.definition),
      inferenceSteps: body.definition.inferenceSteps.map((step, index) => ({
        ...clone(step),
        id: `step-created-${index + 1}`,
      })),
      lastMutationOperationId: body.operationId,
    };
    server.catalog.customDefinitions.push(clone(committed));
    return { status: 201, body: authoringResponse("Created", committed) };
  });
  const app = await loadLoopBuilder({ server });

  await app.elements.createLoopButton.click();
  assert.equal(app.elements.loopName.value, "Untitled loop");
  assert.equal(
    server.calls.some(
      (call) => call.method === "POST" && call.url === "/api/loops",
    ),
    false,
  );
  assert.match(app.elements.loopList.textContent, /Draft.*Not durable/);
  app.elements.loopName.value = "Issue research";
  await app.elements.loopName.input();

  const source = findControlByLabel(
    app.elements.inspectorContent,
    "Prompt source",
    "select",
  );
  source.value = "preset";
  await source.change();
  const preset = findControlByLabel(
    app.elements.inspectorContent,
    "Preset prompt",
    "textarea",
  );
  preset.value = "Research the configured issue.";
  await preset.input();
  const conversation = findControlByLabel(
    app.elements.inspectorContent,
    "Include invoking conversation history",
    "input",
  );
  conversation.checked = true;
  await conversation.change();

  await nodeCard(app, "inference").click();
  const policySource = findControlByLabel(
    app.elements.inspectorContent,
    "Policy source",
    "select",
  );
  policySource.value = "custom";
  await policySource.change();
  const publish = findControlByLabel(
    app.elements.inspectorContent,
    "Publish to the invoking conversation",
    "input",
  );
  publish.checked = true;
  await publish.change();

  await nodeCard(app, "exit").click();
  const continuation = findControlByLabel(
    app.elements.inspectorContent,
    "Allow continuation requests",
    "input",
  );
  continuation.checked = true;
  await continuation.change();
  const decision = findControlByLabel(
    app.elements.inspectorContent,
    "Decision instruction",
    "textarea",
  );
  decision.value = "Return Repeat only when another research pass is needed.";
  await decision.input();
  const ceiling = findControlByLabel(
    app.elements.inspectorContent,
    "Maximum additional iterations",
    "input",
  );
  ceiling.value = "2";
  await ceiling.change();

  await app.elements.saveButton.click();
  const save = server.calls.find(
    (call) => call.method === "POST" && call.url === "/api/loops",
  );
  assert.equal(save.options.credentials, "same-origin");
  assert.equal(save.options.headers["X-EmbodySense-Session"], undefined);
  assert.equal(typeof save.body.operationId, "string");
  assert.equal(save.body.definition.displayName, "Issue research");
  assert.equal(save.body.definition.inferenceSteps[0].id, null);
  assert.deepEqual(save.body.definition.triggerPolicy, {
    promptSource: "preset",
    presetPrompt: "Research the configured issue.",
    includeInvokingConversation: true,
  });
  assert.equal(
    save.body.definition.inferenceSteps[0].contextPolicy.mode,
    "custom",
  );
  assert.equal(
    save.body.definition.inferenceSteps[0].contextPolicy.customPolicy.contextOut
      .publishToInvokingConversation,
    true,
  );
  assert.equal(save.body.definition.exitPolicy.maxAdditionalIterations, 2);
  assert.equal(
    save.body.definition.exitPolicy.decisionInstruction,
    "Return Repeat only when another research pass is needed.",
  );
  assert.doesNotMatch(JSON.stringify(save.body), /additionalFixedContext/i);
  assert.equal(app.elements.saveState.textContent, "Saved · v1");
});

test("uncertain first-save retries reuse the exact request and operation id", async () => {
  const server = new FakeFetchServer(createCatalog());
  let created = null;
  let committedOperationId = null;
  let attempts = 0;
  server.on("POST", "/api/loops", ({ body }) => {
    attempts++;
    if (attempts === 1) {
      committedOperationId = body.operationId;
      created = createDefinitionFromFirstSave(body, "loop-replayed");
      server.catalog.customDefinitions.push(clone(created));
      throw new TypeError("Create response was lost.");
    }

    assert.equal(body.operationId, committedOperationId);
    return { status: 200, body: authoringResponse("Replayed", created) };
  });
  const app = await loadLoopBuilder({ server });

  await app.elements.createLoopButton.click();
  app.elements.loopName.value = "Recovered loop";
  await app.elements.loopName.input();
  await app.elements.saveButton.click();
  assert.match(
    app.elements.validationBanner.textContent,
    /First save outcome is uncertain.*Create response was lost/,
  );
  assert.equal(app.elements.saveButton.textContent, "Retry save");
  assert.equal(app.elements.reloadButton.disabled, true);

  await app.elements.saveButton.click();

  const createCalls = server.calls.filter(
    (call) => call.method === "POST" && call.url === "/api/loops",
  );
  assert.equal(createCalls.length, 2);
  assert.equal(
    createCalls[0].body.operationId,
    createCalls[1].body.operationId,
  );
  assert.deepEqual(createCalls[0].body, createCalls[1].body);
  assert.equal(
    server.catalog.customDefinitions.filter(
      (definition) => definition.id === created.id,
    ).length,
    1,
  );
  assert.equal(app.elements.loopName.value, "Recovered loop");
  assert.equal(
    app.elements.toast.textContent,
    "Loop saved for the first time.",
  );
});

test("first Save exposes saving and conflict states before an explicit fresh-operation retry", async () => {
  const server = new FakeFetchServer(createCatalog());
  let releaseConflict;
  let attempts = 0;
  server.on("POST", "/api/loops", ({ body }) => {
    attempts++;
    if (attempts === 1) {
      return new Promise((resolve) => {
        releaseConflict = () =>
          resolve({
            status: 409,
            body: {
              status: "Conflict",
              isCommitted: false,
              definition: null,
              validationErrors: [],
              conflict: null,
              detail: "The operation identity was already bound.",
            },
          });
      });
    }

    const created = createDefinitionFromFirstSave(body, "loop-after-conflict");
    server.catalog.customDefinitions.push(clone(created));
    return { status: 201, body: authoringResponse("Created", created) };
  });
  const app = await loadLoopBuilder({ server });
  await app.elements.createLoopButton.click();

  const firstSave = app.elements.saveButton.click();
  assert.equal(app.elements.saveState.textContent, "Saving draft");
  assert.equal(app.elements.loopName.disabled, true);
  assert.equal(app.elements.createLoopButton.disabled, true);
  releaseConflict();
  await firstSave;

  assert.match(app.elements.saveState.textContent, /First save conflict/);
  assert.match(
    app.elements.validationBanner.textContent,
    /operation conflicted/i,
  );
  assert.equal(app.elements.loopName.disabled, false);
  assert.equal(app.elements.saveButton.disabled, false);
  const firstOperationId = server.calls.find(
    (call) => call.method === "POST" && call.url === "/api/loops",
  ).body.operationId;

  await app.elements.saveButton.click();

  const createCalls = server.calls.filter(
    (call) => call.method === "POST" && call.url === "/api/loops",
  );
  assert.equal(createCalls.length, 2);
  assert.notEqual(createCalls[1].body.operationId, firstOperationId);
  assert.equal(app.elements.saveState.textContent, "Saved · v1");
});

test("a definitive audit-unavailable first save remains a local failed draft", async () => {
  const server = new FakeFetchServer(createCatalog());
  const durableDefinitionCount = server.catalog.customDefinitions.length;
  server.on("POST", "/api/loops", () => ({
    status: 503,
    body: {
      status: "AuditUnavailable",
      isCommitted: false,
      definition: null,
      validationErrors: [],
      conflict: null,
      detail:
        "The mutation was not attempted because its audit intent could not be recorded.",
    },
  }));
  const app = await loadLoopBuilder({ server });

  await app.elements.createLoopButton.click();
  await app.elements.saveButton.click();

  assert.match(app.elements.saveState.textContent, /First save failed/);
  assert.doesNotMatch(app.elements.saveState.textContent, /uncertain/i);
  assert.equal(app.elements.reloadButton.disabled, false);
  assert.equal(app.elements.saveButton.disabled, false);
  assert.match(
    app.elements.validationBanner.textContent,
    /audit intent could not be recorded/i,
  );
  assert.equal(server.catalog.customDefinitions.length, durableDefinitionCount);
});

test("a tab-scoped draft survives Loops navigation and reload, stays out of other tabs, and Discard performs no mutation", async () => {
  const server = new FakeFetchServer(createCatalog());
  const sameTabStorage = new FakeStorage();
  const firstView = await loadLoopBuilder({
    server,
    sessionStorage: sameTabStorage,
  });

  await firstView.elements.createLoopButton.click();
  firstView.elements.loopName.value = "Reload-safe local draft";
  await firstView.elements.loopName.input();
  firstView.elements.loopDescription.value = "Not durable yet.";
  await firstView.elements.loopDescription.input();
  await firstView.window.embodySenseLoopBuilder.activate();

  assert.equal(firstView.elements.loopName.value, "Reload-safe local draft");
  assert.equal(
    server.calls.some((call) =>
      ["POST", "PUT", "DELETE"].includes(call.method),
    ),
    false,
  );

  const reloadedView = await loadLoopBuilder({
    server,
    sessionStorage: sameTabStorage,
  });
  assert.equal(reloadedView.elements.loopName.value, "Reload-safe local draft");
  assert.equal(reloadedView.elements.loopDescription.value, "Not durable yet.");
  assert.match(reloadedView.elements.saveState.textContent, /Unsaved draft/);
  const guardedClose = {
    prevented: false,
    preventDefault() {
      this.prevented = true;
    },
    returnValue: null,
  };
  reloadedView.window.eventListeners.get("beforeunload")(guardedClose);
  assert.equal(guardedClose.prevented, true);
  assert.equal(guardedClose.returnValue, "");

  const otherTab = await loadLoopBuilder({
    server,
    sessionStorage: new FakeStorage(),
  });
  assert.equal(otherTab.elements.saveState.textContent, "System managed");
  assert.doesNotMatch(
    otherTab.elements.loopList.textContent,
    /Reload-safe local draft/,
  );

  await reloadedView.elements.reloadButton.click();
  assert.equal(reloadedView.elements.saveState.textContent, "System managed");
  assert.equal(sameTabStorage.values.size, 0);
  const unguardedClose = {
    prevented: false,
    preventDefault() {
      this.prevented = true;
    },
    returnValue: null,
  };
  reloadedView.window.eventListeners.get("beforeunload")(unguardedClose);
  assert.equal(unguardedClose.prevented, false);
  assert.equal(unguardedClose.returnValue, null);
  assert.equal(
    server.calls.some((call) =>
      ["POST", "PUT", "DELETE"].includes(call.method),
    ),
    false,
  );
});

test("an uncertain first save survives reload and retries the exact request", async () => {
  const server = new FakeFetchServer(createCatalog());
  const sessionStorage = new FakeStorage();
  let firstRequest = null;
  let attempts = 0;
  server.on("POST", "/api/loops", ({ body }) => {
    attempts++;
    if (attempts === 1) {
      firstRequest = clone(body);
      throw new TypeError("Connection closed after dispatch.");
    }

    assert.deepEqual(body, firstRequest);
    const created = createDefinitionFromFirstSave(body, "loop-after-reload");
    server.catalog.customDefinitions.push(clone(created));
    return { status: 201, body: authoringResponse("Created", created) };
  });
  const firstView = await loadLoopBuilder({ server, sessionStorage });
  await firstView.elements.createLoopButton.click();
  firstView.elements.loopName.value = "Retry after reload";
  await firstView.elements.loopName.input();
  await firstView.elements.saveButton.click();

  const reloadedView = await loadLoopBuilder({ server, sessionStorage });
  assert.equal(reloadedView.elements.loopName.value, "Retry after reload");
  assert.equal(reloadedView.elements.saveButton.textContent, "Retry save");
  assert.match(
    reloadedView.elements.validationBanner.textContent,
    /uncertain/i,
  );
  await reloadedView.elements.saveButton.click();

  const requests = server.calls.filter(
    (call) => call.method === "POST" && call.url === "/api/loops",
  );
  assert.equal(requests.length, 2);
  assert.deepEqual(requests[0].body, requests[1].body);
  assert.equal(reloadedView.elements.saveState.textContent, "Saved · v1");
  assert.equal(sessionStorage.values.size, 0);
});

test("an uncertain first save rejects inspector mutations and retries the original request", async () => {
  const server = new FakeFetchServer(createCatalog());
  let firstRequest = null;
  let committed = null;
  let attempts = 0;
  server.on("POST", "/api/loops", ({ body }) => {
    attempts++;
    if (attempts === 1) {
      firstRequest = clone(body);
      committed = createDefinitionFromFirstSave(body, "loop-uncertain-locked");
      server.catalog.customDefinitions.push(clone(committed));
      throw new TypeError("The committed response was lost.");
    }

    assert.deepEqual(body, firstRequest);
    return { status: 200, body: authoringResponse("Replayed", committed) };
  });
  const app = await loadLoopBuilder({ server });
  await app.elements.createLoopButton.click();
  await app.elements.saveButton.click();

  assert.equal(app.elements.saveButton.textContent, "Retry save");
  const inference = nodeCard(app, "inference");
  await inference.click();
  const instruction = findControlByLabel(
    app.elements.inspectorContent,
    "Prompt-visible instruction",
    "textarea",
  );
  assert.equal(instruction.disabled, true);
  instruction.value = "This edit must not replace the uncertain request.";
  await instruction.input();

  await app.elements.loopSettingsButton.click();
  const readAssignment = findControlByLabel(
    app.elements.inspectorContent,
    "Read",
    "input",
  );
  assert.equal(readAssignment.disabled, true);
  readAssignment.checked = true;
  await readAssignment.change();

  await app.elements.saveButton.click();

  const requests = server.calls.filter(
    (call) => call.method === "POST" && call.url === "/api/loops",
  );
  assert.equal(requests.length, 2);
  assert.deepEqual(requests[1].body, firstRequest);
  assert.equal(server.catalog.customDefinitions.length, 2);
  assert.equal(app.elements.saveState.textContent, "Saved · v1");
});

test("reload reconciles an uncertain committed first save from the read-only catalog without another mutation", async () => {
  const server = new FakeFetchServer(createCatalog());
  const sessionStorage = new FakeStorage();
  server.on("POST", "/api/loops", ({ body }) => {
    const created = createDefinitionFromFirstSave(body, "loop-reconciled");
    server.catalog.customDefinitions.push(clone(created));
    throw new TypeError("The committed response was lost.");
  });
  const firstView = await loadLoopBuilder({ server, sessionStorage });
  await firstView.elements.createLoopButton.click();
  firstView.elements.loopName.value = "Catalog reconciled";
  await firstView.elements.loopName.input();
  await firstView.elements.saveButton.click();

  const reloadedView = await loadLoopBuilder({ server, sessionStorage });

  assert.equal(reloadedView.elements.loopName.value, "Catalog reconciled");
  assert.equal(reloadedView.elements.saveState.textContent, "Saved · v1");
  assert.equal(
    server.calls.filter(
      (call) => call.method === "POST" && call.url === "/api/loops",
    ).length,
    1,
  );
  assert.equal(sessionStorage.values.size, 0);
});

test("a proved first-save response remains saved when the follow-up catalog refresh disconnects", async () => {
  const server = new FakeFetchServer(createCatalog());
  let catalogReads = 0;
  server.on("GET", "/api/loops", () => {
    catalogReads++;
    return catalogReads === 1
      ? { status: 200, body: clone(server.catalog) }
      : { status: 503, body: { detail: "Catalog reconnect required." } };
  });
  server.on("POST", "/api/loops", ({ body }) => {
    const created = createDefinitionFromFirstSave(body, "loop-proved-save");
    server.catalog.customDefinitions.push(clone(created));
    return { status: 201, body: authoringResponse("Created", created) };
  });
  const app = await loadLoopBuilder({ server });
  await app.elements.createLoopButton.click();
  app.elements.loopName.value = "Proved save";
  await app.elements.loopName.input();

  await app.elements.saveButton.click();

  assert.equal(app.elements.saveState.textContent, "Saved · v1");
  assert.equal(app.elements.saveButton.textContent, "Save");
  assert.equal(app.elements.saveButton.disabled, true);
  assert.match(
    app.elements.validationBanner.textContent,
    /saved.*could not be refreshed/i,
  );
  assert.equal(
    server.calls.filter(
      (call) => call.method === "POST" && call.url === "/api/loops",
    ).length,
    1,
  );
});

test("independent tab drafts do not consume quota and a later quota rejection rotates the losing draft operation", async () => {
  const catalog = createCatalog();
  catalog.customDefinitions = [];
  catalog.limits.maxDefinitionsPerWorkspace = 1;
  const server = new FakeFetchServer(catalog);
  server.on("POST", "/api/loops", ({ body }) => {
    if (server.catalog.customDefinitions.length >= 1) {
      return {
        status: 409,
        body: {
          status: "LimitExceeded",
          isCommitted: false,
          definition: null,
          validationErrors: [],
          conflict: null,
          detail:
            "The workspace custom-loop definition limit has been reached.",
        },
      };
    }

    const created = createDefinitionFromFirstSave(body, "loop-quota-winner");
    server.catalog.customDefinitions.push(clone(created));
    return { status: 201, body: authoringResponse("Created", created) };
  });
  const firstTab = await loadLoopBuilder({
    server,
    sessionStorage: new FakeStorage(),
  });
  const secondTab = await loadLoopBuilder({
    server,
    sessionStorage: new FakeStorage(),
  });

  await firstTab.elements.createLoopButton.click();
  await secondTab.elements.createLoopButton.click();
  assert.equal(server.catalog.customDefinitions.length, 0);
  assert.match(firstTab.elements.loopList.textContent, /Not durable/);
  assert.match(secondTab.elements.loopList.textContent, /Not durable/);

  await firstTab.elements.saveButton.click();
  await secondTab.elements.saveButton.click();

  assert.equal(server.catalog.customDefinitions.length, 1);
  assert.match(secondTab.elements.saveState.textContent, /First save failed/);
  assert.match(
    secondTab.elements.validationBanner.textContent,
    /definition limit has been reached/,
  );
  assert.equal(secondTab.elements.invokeButton.disabled, true);
  assert.match(secondTab.elements.loopList.textContent, /Not durable/);

  const deniedRequest = server.calls.filter(
    (call) => call.method === "POST" && call.url === "/api/loops",
  )[1].body;
  server.catalog.customDefinitions = [];

  await secondTab.elements.saveButton.click();

  const retriedRequest = server.calls.filter(
    (call) => call.method === "POST" && call.url === "/api/loops",
  )[2].body;
  assert.notEqual(retriedRequest.operationId, deniedRequest.operationId);
  assert.equal(server.catalog.customDefinitions.length, 1);
  assert.equal(secondTab.elements.saveState.textContent, "Saved · v1");
});

test("save retries reuse the exact request after an ambiguous committed response", async () => {
  const server = new FakeFetchServer(createCatalog());
  let committedRequest = null;
  let attempts = 0;
  server.on("PUT", "/api/loops/loop-research", ({ body }) => {
    attempts++;
    const updated = {
      ...server.catalog.customDefinitions[0],
      ...clone(body.definition),
      definitionVersion: 3,
    };
    if (attempts === 1) {
      committedRequest = clone(body);
      server.catalog.customDefinitions = [updated];
      throw new TypeError("Save response was lost.");
    }

    assert.deepEqual(body, committedRequest);
    return { status: 200, body: authoringResponse("Replayed", updated) };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  app.elements.loopDescription.value = "Updated exactly once.";
  await app.elements.loopDescription.input();

  await app.elements.saveButton.click();
  assert.match(
    app.elements.validationBanner.textContent,
    /Save response was lost/,
  );
  await app.elements.saveButton.click();

  const saves = server.calls.filter(
    (call) => call.method === "PUT" && call.url === "/api/loops/loop-research",
  );
  assert.equal(saves.length, 2);
  assert.deepEqual(saves[0].body, saves[1].body);
  assert.match(app.elements.saveState.textContent, /Saved.*v3/);
});

test("client validation blocks incomplete definitions before save", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);

  app.elements.loopName.value = "";
  await app.elements.loopName.input();
  assert.equal(
    app.elements.validationBanner.textContent,
    "Loop name is required.",
  );
  assert.equal(
    app.elements.validationBanner.attributes.get("aria-label"),
    "Definition needs attention: Loop name is required.",
  );
  assert.equal(app.elements.saveButton.disabled, true);

  app.elements.loopName.value = "Research pass";
  await app.elements.loopName.input();
  assert.match(
    app.elements.validationBanner.textContent,
    /Draft is valid and ready to save/,
  );
  assert.equal(
    app.elements.validationBanner.attributes.has("aria-label"),
    false,
  );
  const source = findControlByLabel(
    app.elements.inspectorContent,
    "Prompt source",
    "select",
  );
  source.value = "preset";
  await source.change();
  assert.equal(
    app.elements.validationBanner.textContent,
    "Preset trigger prompt is required.",
  );
  assert.equal(app.elements.saveButton.disabled, true);

  source.value = "invocation";
  await source.change();
  await nodeCard(app, "exit").click();
  const continuation = findControlByLabel(
    app.elements.inspectorContent,
    "Allow continuation requests",
    "input",
  );
  continuation.checked = true;
  await continuation.change();
  assert.equal(
    app.elements.validationBanner.textContent,
    "Exit decision instruction is required when continuation is enabled.",
  );
  assert.equal(app.elements.saveButton.disabled, true);
});

test("a stale save conflict stays visible with the current server version and reload guidance", async () => {
  const server = new FakeFetchServer(createCatalog());
  server.on("PUT", "/api/loops/loop-research", () => {
    server.catalog.customDefinitions[0] = createCustomDefinition({
      definitionVersion: 4,
      description: "Updated by another editor.",
    });
    return {
      status: 409,
      body: {
        status: "Conflict",
        isCommitted: false,
        validationErrors: [],
        conflict: {
          loopId: "loop-research",
          expectedDefinitionVersion: 2,
          actualDefinitionVersion: 4,
        },
        detail: "The loop changed after this editor loaded it.",
      },
    };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  app.elements.loopDescription.value = "A locally edited description.";
  await app.elements.loopDescription.input();

  await app.elements.saveButton.click();

  assert.match(
    app.elements.validationBanner.textContent,
    /changed after this editor loaded/i,
  );
  assert.match(app.elements.validationBanner.textContent, /server version 4/i);
  assert.match(app.elements.validationBanner.textContent, /Reload/i);
  assert.equal(app.elements.reloadButton.disabled, false);

  await app.elements.reloadButton.click();

  assert.equal(
    app.elements.loopDescription.value,
    "Updated by another editor.",
  );
  assert.match(app.elements.saveState.textContent, /Saved.*v4/);
  assert.equal(
    app.elements.toast.textContent,
    "Latest loop definition loaded.",
  );
});

test("definition mutation locks editing, navigation, and reload until the response is applied", async () => {
  const server = new FakeFetchServer(createCatalog());
  let releaseSave;
  const saveReleased = new Promise((resolve) => {
    releaseSave = resolve;
  });
  server.on("PUT", "/api/loops/loop-research", async ({ body }) => {
    await saveReleased;
    const updated = {
      ...server.catalog.customDefinitions[0],
      ...clone(body.definition),
      definitionVersion: 3,
    };
    server.catalog.customDefinitions = [updated];
    return { status: 200, body: authoringResponse("Updated", updated) };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  app.elements.loopDescription.value = "An edited description.";
  await app.elements.loopDescription.input();

  const saving = app.elements.saveButton.click();
  await Promise.resolve();

  assert.equal(app.elements.loopName.disabled, true);
  assert.equal(app.elements.loopDescription.disabled, true);
  assert.equal(app.elements.reloadButton.disabled, true);
  assert.equal(app.elements.createLoopButton.disabled, true);
  assert.equal(app.elements.builderTab.disabled, true);
  assert.equal(app.elements.runsTab.disabled, true);
  assert.equal(app.elements.builderView.inert, true);
  assert.equal(app.elements.loopList.inert, true);
  assert.ok(app.elements.loopList.children.every((item) => item.disabled));

  releaseSave();
  await saving;
  assert.equal(app.elements.loopName.disabled, false);
  assert.equal(app.elements.builderView.inert, false);
  assert.equal(app.elements.loopList.inert, false);
  assert.equal(app.elements.saveState.textContent, "Saved · v3");
});

test("delete explicitly preserves historical run evidence and sends an expected version", async () => {
  const server = new FakeFetchServer(createCatalog());
  server.on("DELETE", "/api/loops/loop-research", () => {
    server.catalog.customDefinitions = [];
    return { status: 200, body: authoringResponse("Deleted", null) };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);

  await app.elements.deleteButton.click();

  assert.match(
    app.window.confirmations[0],
    /Historical run evidence will remain available/,
  );
  const deletion = server.calls.find(
    (call) =>
      call.method === "DELETE" && call.url === "/api/loops/loop-research",
  );
  assert.equal(deletion.body.expectedDefinitionVersion, 2);
  assert.equal(typeof deletion.body.operationId, "string");
  assert.equal(
    app.elements.toast.textContent,
    "Loop deleted. Historical run evidence was preserved.",
  );
  assert.equal(app.elements.loopName.value, "Default conversation");
});

test("delete surfaces committed audit warnings returned by the server", async () => {
  const server = new FakeFetchServer(createCatalog());
  server.on("DELETE", "/api/loops/loop-research", () => {
    server.catalog.customDefinitions = [];
    return {
      status: 200,
      body: {
        ...authoringResponse("CommittedWithAuditWarning", null),
        detail: "Loop deletion committed, but the outcome audit needs review.",
      },
    };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);

  await app.elements.deleteButton.click();

  assert.equal(
    app.elements.toast.textContent,
    "Loop deletion committed, but the outcome audit needs review.",
  );
});

test("delete retries reuse the exact request after an ambiguous committed response", async () => {
  const server = new FakeFetchServer(createCatalog());
  let committedRequest = null;
  let attempts = 0;
  server.on("DELETE", "/api/loops/loop-research", ({ body }) => {
    attempts++;
    if (attempts === 1) {
      committedRequest = clone(body);
      server.catalog.customDefinitions = [];
      throw new TypeError("Delete response was lost.");
    }

    assert.deepEqual(body, committedRequest);
    return { status: 200, body: authoringResponse("Replayed", null) };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);

  await app.elements.deleteButton.click();
  assert.match(
    app.elements.validationBanner.textContent,
    /Delete response was lost/,
  );
  await app.elements.deleteButton.click();

  const deletions = server.calls.filter(
    (call) =>
      call.method === "DELETE" && call.url === "/api/loops/loop-research",
  );
  assert.equal(deletions.length, 2);
  assert.deepEqual(deletions[0].body, deletions[1].body);
  assert.equal(app.elements.loopName.value, "Default conversation");
});

test("Runs projects durable timeline and context evidence from the authenticated API", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      definitionVersion: 2,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: run.completedAtUtc,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
    },
  ];
  server.runDetails.set(run.id, run);
  server.traceQuota = {
    ...createTraceQuota(1),
    activeReservationCount: 1,
    reservedCapacityUtf8Bytes: 8192,
    accountedUtf8Bytes: 24576,
    availableAccountedUtf8Bytes: 1073717248,
  };
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);

  await app.elements.runsTab.click();
  await flushAsyncWork();

  assert.equal(app.elements.runCount.textContent, "1");
  assert.match(app.elements.runList.textContent, /run-test/);
  assert.match(app.elements.runTitle.textContent, /run-test/);
  assert.match(app.elements.runTimeline.textContent, /Node attempt started/);
  assert.match(app.elements.runTimeline.textContent, /Exact bounded output/);
  assert.match(
    app.elements.runTimeline.textContent,
    /provider codex · model test-model/,
  );
  assert.match(
    app.elements.runTimeline.textContent,
    /canonical output 20\/20 chars · complete/,
  );
  assert.match(
    app.elements.runTimeline.textContent,
    /loop reasoning evidence only/,
  );
  assert.equal(app.elements.inspectorTitle.textContent, "Run evidence");
  assert.match(app.elements.inspectorContent.textContent, /manifest-test/);
  assert.match(app.elements.inspectorContent.textContent, /TriggerPrompt/);
  assert.match(app.elements.inspectorContent.textContent, /WorkspaceRoleFile/);
  assert.match(app.elements.inspectorContent.textContent, /TrustedInstruction/);
  assert.match(
    app.elements.inspectorContent.textContent,
    /Sensitive trace storage16\.0 KiB/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Status and checkpointCompleted · iteration 1 · step-research · attempt 1/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /execution 2s elapsed · 29m 59s remaining of 30m/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /next proved boundary Terminal checkpoint/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /last committed sequence 4 · latest event 4 Node Outcome Observed/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /pending approvals visible to this connection 0/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Provider and modelcodex · test-model/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Admission identityembodysense\.web/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Operation op-run-test/,
  );
  assert.match(app.elements.inspectorContent.textContent, /Request hash a{64}/);
  assert.match(app.elements.inspectorContent.textContent, /source id trigger/);
  assert.match(app.elements.inspectorContent.textContent, /hash hash-trigger/);
  assert.match(
    app.elements.inspectorContent.textContent,
    /event 2 · iteration 1 · node step-research · attempt 1/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /resolved context in · role included · trigger included/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Provider usage and costUnavailable/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /does not report token usage or cost; no estimate is fabricated/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Tool requests, governance, and model-visible results/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Tool request 1 · Read · Outcome Observed · Succeeded/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /current role ceiling Read/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /approval Not Required/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /authority detail Read is inside the effective assignment set/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /permission detail Read is allowed/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /permission policy permission-hash/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /approval detail No approval was required/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Exact governed tool result/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /8\.0+ KiB reserved across 1 trace reservation/,
  );
  assert.doesNotMatch(app.elements.inspectorContent.textContent, /active run/i);
  assert.match(app.elements.traceQuota.textContent, /1\/250 live/);
  assert.match(
    app.elements.traceQuota.textContent,
    /0\/20000 deletion receipts/,
  );
  assert.match(app.elements.traceQuota.textContent, /reserved/);
  assert.doesNotMatch(
    app.elements.inspectorContent.textContent,
    /chain-of-thought/i,
  );
});

test("conversation publication disposition is table-driven, definite, and phase-aware", async (t) => {
  const publicationId = "publication-operation";
  const event = (sequence, kind, publishedToInvokingConversation = null) => ({
    sequence,
    eventId: `publication-${sequence}`,
    timestampUtc: `2026-07-16T12:00:0${sequence}Z`,
    kind,
    iteration: 1,
    stepId: "exit",
    attempt: 1,
    detail: "Publication protocol evidence.",
    contextBlocks: [],
    canonicalOutput: null,
    publishedToInvokingConversation,
    conversationPublicationId: publicationId,
  });
  const cases = [
    {
      name: "no publication requested",
      dispositions: [],
      events: [],
      expected: "No conversation publication requested",
    },
    {
      name: "omitted without a bound conversation",
      dispositions: [
        publicationDisposition("OmittedNoInvokingConversation", true),
      ],
      events: [event(5, "ConversationPublished", false)],
      expected: "Omitted No Invoking Conversation",
    },
    {
      name: "intent is pending",
      dispositions: [publicationDisposition("Pending", false)],
      events: [
        event(3, "ExitDecisionCompleted", true),
        event(4, "ConversationPublicationStarted"),
      ],
      expected: "Pending",
      phase: "intent committed",
    },
    {
      name: "publication succeeds once",
      dispositions: [publicationDisposition("Published", true)],
      events: [
        event(3, "ExitDecisionCompleted", true),
        event(4, "ConversationPublicationStarted"),
        event(5, "ConversationPublished", true),
      ],
      expected: "Published",
      phase: "terminal outcome recorded",
    },
    {
      name: "a prior commit is reconciled",
      dispositions: [publicationDisposition("AlreadyPublished", true)],
      events: [event(5, "ConversationPublished", true)],
      expected: "Already Published",
    },
    {
      name: "a definite failure remains distinct",
      dispositions: [publicationDisposition("DefinitelyFailed", true)],
      events: [event(5, "ConversationPublished", false)],
      expected: "Definitely Failed",
    },
    {
      name: "an uncertain outcome requires review",
      dispositions: [publicationDisposition("Uncertain", false)],
      events: [event(5, "ConversationPublished", false)],
      expected: "Uncertain",
    },
    {
      name: "duplicate terminal evidence is an integrity warning",
      dispositions: [
        publicationDisposition("DuplicateTerminalOutcomes", false, true),
      ],
      events: [
        event(5, "ConversationPublished", true),
        event(6, "ConversationPublished", true),
      ],
      expected: "Integrity warning: Duplicate Terminal Outcomes",
    },
    {
      name: "conflicting terminal evidence is an integrity warning",
      dispositions: [
        publicationDisposition("ConflictingTerminalOutcomes", false, true),
      ],
      events: [
        event(5, "ConversationPublished", true),
        event(6, "ConversationPublished", false),
      ],
      expected: "Integrity warning: Conflicting Terminal Outcomes",
    },
  ];

  for (const scenario of cases) {
    await t.test(scenario.name, async () => {
      const server = new FakeFetchServer(createCatalog());
      const run = createRunSnapshot();
      run.events = scenario.events;
      run.conversationPublicationDispositions = scenario.dispositions;
      server.runs = [runSummary(run)];
      server.runDetails.set(run.id, run);
      const app = await loadLoopBuilder({ server });
      await selectCustomLoop(app);

      await app.elements.runsTab.click();
      await flushAsyncWork();

      assert.match(
        app.elements.inspectorContent.textContent,
        new RegExp(scenario.expected),
      );
      assert.doesNotMatch(
        app.elements.inspectorContent.textContent,
        /not published/i,
      );
      if (scenario.phase)
        assert.match(
          app.elements.runTimeline.textContent,
          new RegExp(scenario.phase),
        );
    });
  }
});

test("multiple conversation publication operations keep canonical grouping and durable order", async () => {
  const event = (
    sequence,
    operationId,
    kind,
    publishedToInvokingConversation = null,
  ) => ({
    sequence,
    eventId: `${operationId}-${sequence}`,
    timestampUtc: `2026-07-16T12:00:0${sequence}Z`,
    kind,
    iteration: operationId === "publication-first" ? 1 : 2,
    stepId: "exit",
    attempt: 1,
    detail: "Publication protocol evidence.",
    contextBlocks: [],
    canonicalOutput: null,
    publishedToInvokingConversation,
    conversationPublicationId: operationId,
  });
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.events = [
    event(2, "publication-first", "ExitDecisionCompleted", true),
    event(3, "publication-first", "ConversationPublicationStarted"),
    event(4, "publication-first", "ConversationPublished", true),
    event(5, "publication-second", "ExitDecisionCompleted", true),
    event(6, "publication-second", "ConversationPublicationStarted"),
    event(7, "publication-second", "ConversationPublished", true),
  ];
  run.conversationPublicationDispositions = [
    publicationDisposition("Published", true, false, "publication-first"),
    publicationDisposition("Published", true, false, "publication-second"),
  ];
  server.runs = [runSummary(run)];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);

  await app.elements.runsTab.click();
  await flushAsyncWork();

  const inspector = app.elements.inspectorContent.textContent;
  assert.ok(
    inspector.indexOf("publication-first") <
      inspector.indexOf("publication-second"),
  );
  assert.equal(inspector.match(/publication-first/g)?.length, 1);
  assert.equal(inspector.match(/publication-second/g)?.length, 1);
  assert.equal(inspector.match(/Published · definite/g)?.length, 2);
  assert.equal(
    app.elements.runTimeline.textContent.match(/terminal outcome recorded/g)
      ?.length,
    2,
  );
});

test("standalone integrity evidence reports governance as intentionally not evaluated", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  const integrity = {
    ...createToolEvidenceSnapshot(),
    phase: "IntegrityFailed",
    requestOrdinal: 2,
    brokerRequestId: null,
    governance: null,
    outcome: null,
    canonicalResultReturnedToModel: null,
    canonicalResultHash: null,
    canonicalResultCharacterCount: null,
    returnedToModel: false,
    reservedUtf8Bytes: 18432,
  };
  run.events[2] = {
    ...run.events[2],
    kind: "ToolIntegrityFailed",
    detail: "Repeated request retained without actuation.",
    toolEvidence: integrity,
  };
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      definitionVersion: 2,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: run.completedAtUtc,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);

  await app.elements.runsTab.click();
  await flushAsyncWork();

  assert.match(
    app.elements.runTimeline.textContent,
    /governance not evaluated · non-actuating integrity failure/,
  );
  assert.doesNotMatch(
    app.elements.runTimeline.textContent,
    /governance decision not yet recorded/,
  );
});

test("run discovery progressively loads cursor pages without losing the selected run", async () => {
  const server = new FakeFetchServer(createCatalog());
  const selected = createRunSnapshot();
  const firstSummary = {
    id: selected.id,
    loopId: selected.loopId,
    admissionOperationId: selected.admissionOperationId,
    definitionVersion: 2,
    status: selected.status,
    createdAtUtc: selected.createdAtUtc,
    updatedAtUtc: selected.updatedAtUtc,
    completedAtUtc: selected.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  };
  const olderSummary = {
    ...firstSummary,
    id: "run-older-page",
    admissionOperationId: "op-run-older-page",
    createdAtUtc: "2026-07-19T10:00:00Z",
    updatedAtUtc: "2026-07-19T10:00:00Z",
  };
  server.runDetails.set(selected.id, selected);
  server.on("GET", "/api/loop-runs?maximumCount=50", () => ({
    status: 200,
    body: { items: [firstSummary], continuationCursor: "cursor-one" },
  }));
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research",
    () => ({
      status: 200,
      body: { items: [firstSummary], continuationCursor: "cursor-one" },
    }),
  );
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research&cursor=cursor-one",
    () => ({
      status: 200,
      body: { items: [olderSummary], continuationCursor: null },
    }),
  );
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);

  await app.elements.runsTab.click();
  await flushAsyncWork();

  assert.match(app.elements.runTitle.textContent, /run-test/);
  assert.equal(app.elements.loadMoreRunsButton.hidden, false);

  await app.elements.loadMoreRunsButton.click();
  await flushAsyncWork();

  assert.match(app.elements.runList.textContent, /run-older-page/);
  assert.match(app.elements.runTitle.textContent, /run-test/);
  assert.equal(app.elements.loadMoreRunsButton.hidden, true);
  const pageCall = server.calls.find((call) =>
    call.url.includes("cursor=cursor-one"),
  );
  assert.equal(pageCall.options.credentials, "same-origin");
  assert.equal(pageCall.options.headers["X-EmbodySense-Session"], undefined);
});

test("refreshing a selected continuation-page run recovers an externally deleted trace as a tombstone", async () => {
  const server = new FakeFetchServer(createCatalog());
  const newest = createRunSnapshot();
  const older = createRunSnapshot();
  older.id = "run-older-selected";
  older.admissionOperationId = "op-run-older-selected";
  older.createdAtUtc = "2026-07-15T10:00:00Z";
  older.updatedAtUtc = "2026-07-15T10:00:02Z";
  older.completedAtUtc = "2026-07-15T10:00:02Z";
  const newestSummary = {
    id: newest.id,
    loopId: newest.loopId,
    admissionOperationId: newest.admissionOperationId,
    definitionVersion: 2,
    status: newest.status,
    createdAtUtc: newest.createdAtUtc,
    updatedAtUtc: newest.updatedAtUtc,
    completedAtUtc: newest.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  };
  const olderSummary = {
    id: older.id,
    loopId: older.loopId,
    admissionOperationId: older.admissionOperationId,
    definitionVersion: 2,
    status: older.status,
    createdAtUtc: older.createdAtUtc,
    updatedAtUtc: older.updatedAtUtc,
    completedAtUtc: older.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  };
  server.runDetails.set(newest.id, newest);
  server.runDetails.set(older.id, older);
  server.on("GET", "/api/loop-runs?maximumCount=50", () => ({
    status: 200,
    body: { items: [newestSummary], continuationCursor: "cursor-one" },
  }));
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research",
    () => ({
      status: 200,
      body: { items: [newestSummary], continuationCursor: "cursor-one" },
    }),
  );
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research&cursor=cursor-one",
    () => ({
      status: 200,
      body: { items: [olderSummary], continuationCursor: null },
    }),
  );
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  await app.elements.loadMoreRunsButton.click();
  const olderButton = app.elements.runList.children.find((item) =>
    item.textContent.includes(older.id),
  );
  assert.ok(olderButton);
  await olderButton.click();
  assert.match(app.elements.runTitle.textContent, new RegExp(older.id));

  const liveTrace = createTraceSnapshot(older);
  const tombstone = {
    runId: older.id,
    loopId: older.loopId,
    admissionOperationId: older.admissionOperationId,
    terminalStatus: older.status,
    definitionVersion: older.admittedDefinition.definitionVersion,
    definitionHash: older.admittedDefinition.contentHash,
    originalTraceHash: liveTrace.persistedArtifactHash,
    originalTraceUtf8Bytes: liveTrace.persistedArtifactUtf8Bytes,
    createdAtUtc: older.createdAtUtc,
    completedAtUtc: older.completedAtUtc,
    deletedAtUtc: "2026-07-20T12:05:00Z",
    deletionActor: "embodysense.web",
    deletionSurface: "web",
    deletionOperationId: "trace-delete-external",
    intentAuditCorrelationId: "trace-delete-intent-external",
    outcomeAuditCorrelationId: "trace-delete-outcome-external",
    outcomeIntegrity: "Complete",
  };
  server.runDetails.delete(older.id);
  server.traceDetails.set(older.id, {
    ...liveTrace,
    kind: "Tombstone",
    persistedArtifactUtf8Bytes: 1024,
    isDeleted: true,
    tombstone,
  });

  const refreshed = await app.context.loadRuns({ preferredRunId: older.id });

  assert.equal(refreshed, true);
  assert.match(
    app.elements.runTitle.textContent,
    new RegExp(`Deleted trace ${older.id}`),
  );
  assert.match(app.elements.runList.textContent, /trace deleted/);
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /Run evidence unavailable/,
  );
});

test("selecting a retained continuation-page summary recovers an externally deleted trace as a tombstone", async () => {
  const server = new FakeFetchServer(createCatalog());
  const newest = createRunSnapshot();
  const older = createRunSnapshot();
  older.id = "run-older-unselected";
  older.admissionOperationId = "op-run-older-unselected";
  older.createdAtUtc = "2026-07-14T10:00:00Z";
  older.updatedAtUtc = "2026-07-14T10:00:02Z";
  older.completedAtUtc = "2026-07-14T10:00:02Z";
  const newestSummary = {
    id: newest.id,
    loopId: newest.loopId,
    admissionOperationId: newest.admissionOperationId,
    definitionVersion: 2,
    status: newest.status,
    createdAtUtc: newest.createdAtUtc,
    updatedAtUtc: newest.updatedAtUtc,
    completedAtUtc: newest.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  };
  const olderSummary = {
    id: older.id,
    loopId: older.loopId,
    admissionOperationId: older.admissionOperationId,
    definitionVersion: 2,
    status: older.status,
    createdAtUtc: older.createdAtUtc,
    updatedAtUtc: older.updatedAtUtc,
    completedAtUtc: older.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  };
  server.runDetails.set(newest.id, newest);
  server.runDetails.set(older.id, older);
  server.on("GET", "/api/loop-runs?maximumCount=50", () => ({
    status: 200,
    body: { items: [newestSummary], continuationCursor: "cursor-one" },
  }));
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research",
    () => ({
      status: 200,
      body: { items: [newestSummary], continuationCursor: "cursor-one" },
    }),
  );
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research&cursor=cursor-one",
    () => ({
      status: 200,
      body: { items: [olderSummary], continuationCursor: null },
    }),
  );
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  await app.elements.loadMoreRunsButton.click();

  const liveTrace = createTraceSnapshot(older);
  const tombstone = {
    runId: older.id,
    loopId: older.loopId,
    admissionOperationId: older.admissionOperationId,
    terminalStatus: older.status,
    definitionVersion: older.admittedDefinition.definitionVersion,
    definitionHash: older.admittedDefinition.contentHash,
    originalTraceHash: liveTrace.persistedArtifactHash,
    originalTraceUtf8Bytes: liveTrace.persistedArtifactUtf8Bytes,
    createdAtUtc: older.createdAtUtc,
    completedAtUtc: older.completedAtUtc,
    deletedAtUtc: "2026-07-20T12:05:00Z",
    deletionActor: "embodysense.web",
    deletionSurface: "web",
    deletionOperationId: "trace-delete-external",
    intentAuditCorrelationId: "trace-delete-intent-external",
    outcomeAuditCorrelationId: "trace-delete-outcome-external",
    outcomeIntegrity: "Complete",
  };
  server.runDetails.delete(older.id);
  server.traceDetails.set(older.id, {
    ...liveTrace,
    kind: "Tombstone",
    persistedArtifactUtf8Bytes: 1024,
    isDeleted: true,
    tombstone,
  });

  const olderButton = app.elements.runList.children.find((item) =>
    item.textContent.includes(older.id),
  );
  assert.ok(olderButton);
  await olderButton.click();

  assert.match(
    app.elements.runTitle.textContent,
    new RegExp(`Deleted trace ${older.id}`),
  );
  assert.match(app.elements.runList.textContent, /trace deleted/);
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /Run detail unavailable/,
  );
});

test("run discovery keeps each loop cursor scoped while an older-page request is still pending", async () => {
  const catalog = createCatalog();
  const secondDefinition = createCustomDefinition({
    id: "loop-second",
    displayName: "Second pass",
    contentHash: "sha256:second",
  });
  catalog.customDefinitions.push(secondDefinition);
  const server = new FakeFetchServer(catalog);
  const firstRun = createRunSnapshot();
  const secondRun = createRunSnapshot();
  secondRun.id = "run-second";
  secondRun.loopId = secondDefinition.id;
  secondRun.admissionOperationId = "op-run-second";
  secondRun.admittedDefinition = secondDefinition;
  const firstSummary = {
    id: firstRun.id,
    loopId: firstRun.loopId,
    admissionOperationId: firstRun.admissionOperationId,
    definitionVersion: 2,
    status: firstRun.status,
    createdAtUtc: firstRun.createdAtUtc,
    updatedAtUtc: firstRun.updatedAtUtc,
    completedAtUtc: firstRun.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  };
  const secondSummary = {
    id: secondRun.id,
    loopId: secondRun.loopId,
    admissionOperationId: secondRun.admissionOperationId,
    definitionVersion: 2,
    status: secondRun.status,
    createdAtUtc: secondRun.createdAtUtc,
    updatedAtUtc: secondRun.updatedAtUtc,
    completedAtUtc: secondRun.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  };
  server.runDetails.set(firstRun.id, firstRun);
  server.runDetails.set(secondRun.id, secondRun);
  server.on("GET", "/api/loop-runs?maximumCount=50", () => ({
    status: 200,
    body: { items: [], continuationCursor: null },
  }));
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research",
    () => ({
      status: 200,
      body: { items: [firstSummary], continuationCursor: "cursor-first" },
    }),
  );
  server.on("GET", "/api/loop-runs?maximumCount=50&loopId=loop-second", () => ({
    status: 200,
    body: { items: [secondSummary], continuationCursor: "cursor-second" },
  }));
  let releaseFirstPage;
  const firstPageReleased = new Promise((resolve) => {
    releaseFirstPage = resolve;
  });
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research&cursor=cursor-first",
    async () => {
      await firstPageReleased;
      return { status: 200, body: { items: [], continuationCursor: null } };
    },
  );
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-second&cursor=cursor-second",
    () => ({ status: 200, body: { items: [], continuationCursor: null } }),
  );
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  const loadingFirstPage = app.elements.loadMoreRunsButton.click();
  await Promise.resolve();
  const secondLoopButton = app.elements.loopList.children.find((item) =>
    item.textContent.includes("Second pass"),
  );
  assert.ok(secondLoopButton);
  await secondLoopButton.click();
  assert.equal(app.elements.loadMoreRunsButton.hidden, false);
  assert.equal(app.elements.loadMoreRunsButton.disabled, false);
  releaseFirstPage();
  await loadingFirstPage;
  await app.elements.loadMoreRunsButton.click();

  assert.ok(
    server.calls.some((call) =>
      call.url.endsWith("loopId=loop-second&cursor=cursor-second"),
    ),
  );
});

test("run discovery fetches the selected loop directly when workspace-newest evidence belongs to other loops", async () => {
  const server = new FakeFetchServer(createCatalog());
  const selected = createRunSnapshot();
  const selectedSummary = {
    id: selected.id,
    loopId: selected.loopId,
    admissionOperationId: selected.admissionOperationId,
    definitionVersion: 2,
    status: selected.status,
    createdAtUtc: selected.createdAtUtc,
    updatedAtUtc: selected.updatedAtUtc,
    completedAtUtc: selected.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  };
  const unrelatedSummary = {
    ...selectedSummary,
    id: "run-unrelated-newer",
    loopId: "loop-other",
    admissionOperationId: "op-unrelated-newer",
    createdAtUtc: "2026-07-20T10:00:00Z",
    updatedAtUtc: "2026-07-20T10:00:00Z",
  };
  server.runDetails.set(selected.id, selected);
  server.on("GET", "/api/loop-runs?maximumCount=50", () => ({
    status: 200,
    body: { items: [unrelatedSummary], continuationCursor: "workspace-cursor" },
  }));
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research",
    () => ({
      status: 200,
      body: { items: [selectedSummary], continuationCursor: null },
    }),
  );
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);

  await app.elements.runsTab.click();
  await flushAsyncWork();

  assert.match(app.elements.runTitle.textContent, /run-test/);
  assert.ok(
    server.calls.some(
      (call) =>
        call.url === "/api/loop-runs?maximumCount=50&loopId=loop-research",
    ),
  );
  assert.equal(app.elements.loadMoreRunsButton.hidden, false);
});

test("workspace pagination discovers archived loops older than the first global page", async () => {
  const server = new FakeFetchServer(createCatalog());
  const active = createRunSnapshot();
  const activeSummary = {
    id: active.id,
    loopId: active.loopId,
    admissionOperationId: active.admissionOperationId,
    definitionVersion: 2,
    status: active.status,
    createdAtUtc: active.createdAtUtc,
    updatedAtUtc: active.updatedAtUtc,
    completedAtUtc: active.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  };
  const archivedSummary = {
    ...activeSummary,
    id: "run-archived",
    loopId: "loop-deleted",
    admissionOperationId: "op-run-archived",
    createdAtUtc: "2026-07-01T10:00:00Z",
    updatedAtUtc: "2026-07-01T10:00:00Z",
  };
  server.runDetails.set(active.id, active);
  server.on("GET", "/api/loop-runs?maximumCount=50", () => ({
    status: 200,
    body: { items: [activeSummary], continuationCursor: "workspace-cursor" },
  }));
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research",
    () => ({
      status: 200,
      body: { items: [activeSummary], continuationCursor: null },
    }),
  );
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&cursor=workspace-cursor",
    () => ({
      status: 200,
      body: { items: [archivedSummary], continuationCursor: null },
    }),
  );
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  assert.doesNotMatch(app.elements.loopList.textContent, /loop-deleted/);
  assert.equal(app.elements.loadMoreRunsButton.hidden, false);
  await app.elements.loadMoreRunsButton.click();

  assert.match(
    app.elements.loopList.textContent,
    /Deleted loop · loop-deleted/,
  );
  assert.ok(
    server.calls.some(
      (call) =>
        call.url === "/api/loop-runs?maximumCount=50&cursor=workspace-cursor",
    ),
  );
});

test("run discovery rejects stale responses from an earlier visit to the same loop", async () => {
  const catalog = createCatalog();
  const secondDefinition = createCustomDefinition({
    id: "loop-second",
    displayName: "Second pass",
    contentHash: "sha256:second",
  });
  catalog.customDefinitions.push(secondDefinition);
  const server = new FakeFetchServer(catalog);
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  const staleSummary = {
    id: run.id,
    loopId: run.loopId,
    admissionOperationId: run.admissionOperationId,
    definitionVersion: 2,
    status: "Admitted",
    createdAtUtc: run.createdAtUtc,
    updatedAtUtc: run.createdAtUtc,
    completedAtUtc: null,
    iteration: 0,
    nextStepIndex: 0,
    failureCode: null,
    isDeleted: false,
  };
  const currentSummary = {
    ...staleSummary,
    status: "Running",
    updatedAtUtc: "2026-07-20T12:00:02Z",
    iteration: 1,
    nextStepIndex: 1,
  };
  server.runDetails.set(run.id, run);
  server.on("GET", "/api/loop-runs?maximumCount=50", () => ({
    status: 200,
    body: { items: [], continuationCursor: null },
  }));
  let alphaReads = 0;
  let releaseStale;
  const staleReleased = new Promise((resolve) => {
    releaseStale = resolve;
  });
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research",
    async () => {
      alphaReads++;
      if (alphaReads === 2) {
        await staleReleased;
        return {
          status: 200,
          body: { items: [staleSummary], continuationCursor: "stale-cursor" },
        };
      }
      return {
        status: 200,
        body: {
          items: [alphaReads === 1 ? staleSummary : currentSummary],
          continuationCursor: alphaReads === 1 ? null : "current-cursor",
        },
      };
    },
  );
  server.on("GET", "/api/loop-runs?maximumCount=50&loopId=loop-second", () => ({
    status: 200,
    body: { items: [], continuationCursor: null },
  }));
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  const staleLoad = app.context.loadRuns({ silent: true });
  await Promise.resolve();
  const secondLoopButton = app.elements.loopList.children.find((item) =>
    item.textContent.includes("Second pass"),
  );
  await secondLoopButton.click();
  const firstLoopButton = app.elements.loopList.children.find((item) =>
    item.textContent.includes("Research pass"),
  );
  await firstLoopButton.click();
  releaseStale();
  await staleLoad;

  assert.match(app.elements.runSubtitle.textContent, /Running/);
  assert.equal(
    vm.runInContext("runContinuationCursor", app.context),
    "current-cursor",
  );
});

test("run monitoring refreshes the newest page without rewinding older-evidence pagination", async () => {
  const server = new FakeFetchServer(createCatalog());
  const selected = createRunSnapshot();
  const firstSummary = {
    id: selected.id,
    loopId: selected.loopId,
    admissionOperationId: selected.admissionOperationId,
    definitionVersion: 2,
    status: selected.status,
    createdAtUtc: selected.createdAtUtc,
    updatedAtUtc: selected.updatedAtUtc,
    completedAtUtc: selected.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  };
  const secondSummary = {
    ...firstSummary,
    id: "run-second-page",
    admissionOperationId: "op-run-second-page",
    createdAtUtc: "2026-07-19T10:00:00Z",
    updatedAtUtc: "2026-07-19T10:00:00Z",
  };
  const thirdSummary = {
    ...firstSummary,
    id: "run-third-page",
    admissionOperationId: "op-run-third-page",
    createdAtUtc: "2026-07-18T10:00:00Z",
    updatedAtUtc: "2026-07-18T10:00:00Z",
  };
  server.runDetails.set(selected.id, selected);
  server.on("GET", "/api/loop-runs?maximumCount=50", () => ({
    status: 200,
    body: { items: [firstSummary], continuationCursor: "cursor-one" },
  }));
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research",
    () => ({
      status: 200,
      body: { items: [firstSummary], continuationCursor: "cursor-one" },
    }),
  );
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research&cursor=cursor-one",
    () => ({
      status: 200,
      body: { items: [secondSummary], continuationCursor: "cursor-two" },
    }),
  );
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research&cursor=cursor-two",
    () => ({
      status: 200,
      body: { items: [thirdSummary], continuationCursor: null },
    }),
  );
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  await app.elements.loadMoreRunsButton.click();
  await flushAsyncWork();

  await vm.runInContext("loadRuns({ silent: true })", app.context);
  await app.elements.loadMoreRunsButton.click();
  await flushAsyncWork();

  assert.match(app.elements.runList.textContent, /run-second-page/);
  assert.match(app.elements.runList.textContent, /run-third-page/);
  assert.equal(
    server.calls.filter((call) =>
      call.url.includes("loopId=loop-research&cursor=cursor-one"),
    ).length,
    1,
  );
  assert.equal(
    server.calls.filter((call) =>
      call.url.includes("loopId=loop-research&cursor=cursor-two"),
    ).length,
    1,
  );
});

test("deleting a trace loaded from a continuation page keeps its tombstone selected", async () => {
  const server = new FakeFetchServer(createCatalog());
  const newest = createRunSnapshot();
  const older = createRunSnapshot();
  older.id = "run-older-page";
  older.admissionOperationId = "op-run-older-page";
  older.createdAtUtc = "2026-07-15T12:00:00Z";
  older.updatedAtUtc = older.createdAtUtc;
  older.completedAtUtc = older.createdAtUtc;
  const newestSummary = {
    id: newest.id,
    loopId: newest.loopId,
    admissionOperationId: newest.admissionOperationId,
    definitionVersion: 2,
    status: newest.status,
    createdAtUtc: newest.createdAtUtc,
    updatedAtUtc: newest.updatedAtUtc,
    completedAtUtc: newest.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  };
  const olderSummary = {
    id: older.id,
    loopId: older.loopId,
    admissionOperationId: older.admissionOperationId,
    definitionVersion: 2,
    status: older.status,
    createdAtUtc: older.createdAtUtc,
    updatedAtUtc: older.updatedAtUtc,
    completedAtUtc: older.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  };
  server.runDetails.set(newest.id, newest);
  server.runDetails.set(older.id, older);
  server.traceDetails.set(older.id, createTraceSnapshot(older));
  server.on("GET", "/api/loop-runs?maximumCount=50", () => ({
    status: 200,
    body: { items: [newestSummary], continuationCursor: "cursor-one" },
  }));
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research",
    () => ({
      status: 200,
      body: { items: [newestSummary], continuationCursor: "cursor-one" },
    }),
  );
  server.on(
    "GET",
    "/api/loop-runs?maximumCount=50&loopId=loop-research&cursor=cursor-one",
    () => ({
      status: 200,
      body: { items: [olderSummary], continuationCursor: null },
    }),
  );
  server.on("POST", `/api/loop-runs/${older.id}/trace/delete`, ({ body }) => {
    const tombstone = {
      runId: older.id,
      loopId: older.loopId,
      admissionOperationId: older.admissionOperationId,
      terminalStatus: older.status,
      definitionVersion: 2,
      definitionHash: older.admittedDefinition.contentHash,
      originalTraceHash: "f".repeat(64),
      originalTraceUtf8Bytes: 16384,
      createdAtUtc: older.createdAtUtc,
      completedAtUtc: older.completedAtUtc,
      deletedAtUtc: "2026-07-16T12:05:00Z",
      deletionActor: "embodysense.web",
      deletionSurface: "web",
      deletionOperationId: body.operationId,
      intentAuditCorrelationId: "trace-delete-intent-older",
      outcomeAuditCorrelationId: "trace-delete-outcome-older",
      outcomeIntegrity: "Complete",
    };
    server.traceDetails.set(older.id, {
      ...createTraceSnapshot(older),
      kind: "Tombstone",
      isDeleted: true,
      tombstone,
    });
    server.runDetails.delete(older.id);
    return {
      status: 200,
      body: {
        status: "Deleted",
        isCommitted: true,
        detail: "Deleted.",
        tombstone,
      },
    };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  await app.elements.loadMoreRunsButton.click();
  const olderButton = app.elements.runList.children.find((item) =>
    item.textContent.includes(older.id),
  );
  assert.ok(olderButton);
  await olderButton.click();

  const deleteButton = app.elements.runActions.children.find(
    (child) => child.textContent === "Delete sensitive trace",
  );
  assert.ok(deleteButton);
  await deleteButton.click();

  assert.match(
    app.elements.runTitle.textContent,
    /Deleted trace run-older-page/,
  );
  assert.equal(
    app.elements.runList.children
      .find((item) => item.className.includes("selected"))
      .textContent.includes("trace deleted"),
    true,
  );
  assert.equal(
    server.calls.filter((call) =>
      call.url.includes("loopId=loop-research&cursor=cursor-one"),
    ).length,
    1,
  );
});

test("live run monitoring binds the exact admission operation instead of another recent run", async () => {
  const server = new FakeFetchServer(createCatalog());
  const older = createRunSnapshot();
  older.id = "run-older";
  older.admissionOperationId = "op-older";
  const preferred = createRunSnapshot();
  preferred.id = "run-preferred";
  preferred.admissionOperationId = "op-preferred";
  server.runs = [
    {
      id: older.id,
      loopId: older.loopId,
      admissionOperationId: older.admissionOperationId,
      definitionVersion: 2,
      status: older.status,
      createdAtUtc: older.createdAtUtc,
      updatedAtUtc: older.updatedAtUtc,
      completedAtUtc: older.completedAtUtc,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
    },
    {
      id: preferred.id,
      loopId: preferred.loopId,
      admissionOperationId: preferred.admissionOperationId,
      definitionVersion: 2,
      status: preferred.status,
      createdAtUtc: preferred.createdAtUtc,
      updatedAtUtc: preferred.updatedAtUtc,
      completedAtUtc: preferred.completedAtUtc,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
    },
  ];
  server.runDetails.set(older.id, older);
  server.runDetails.set(preferred.id, preferred);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  await app.context.waitForRunOperation(Promise.resolve({}), {
    preferredAdmissionOperationId: preferred.admissionOperationId,
  });

  assert.match(app.elements.runTitle.textContent, /run-preferred/);
  assert.match(app.elements.inspectorContent.textContent, /run run-preferred/);
});

test("a rejected invocation with an existing run leaves run selection empty", async () => {
  const server = new FakeFetchServer(createCatalog());
  const existing = createRunSnapshot();
  existing.id = "run-existing";
  server.runs = [
    {
      id: existing.id,
      loopId: existing.loopId,
      admissionOperationId: existing.admissionOperationId,
      definitionVersion: 2,
      status: existing.status,
      createdAtUtc: existing.createdAtUtc,
      updatedAtUtc: existing.updatedAtUtc,
      completedAtUtc: existing.completedAtUtc,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(existing.id, existing);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  app.context.testHub = {
    connected: true,
    invoke: () =>
      Promise.resolve({
        admissionStatus: "NonterminalRunExists",
        run: existing,
        detail: "This loop already has a nonterminal run.",
      }),
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Try this run.";
  await app.elements.startRunButton.click();

  assert.equal(app.elements.runTitle.textContent, "No run selected");
  assert.equal(
    app.elements.runList.children.some((item) =>
      item.className.includes("selected"),
    ),
    false,
  );
  assert.match(
    app.elements.validationBanner.textContent,
    /Run was not admitted: This loop already has a nonterminal run/,
  );
});

test("an admission audit failure selects the durable parked run and surfaces its integrity warning", async () => {
  const server = new FakeFetchServer(createCatalog());
  const parked = createRunSnapshot();
  parked.id = "run-parked";
  parked.status = "NeedsReview";
  parked.failureCode = "InvocationReceiptAuditUnavailable";
  parked.failureDetail =
    "Admission was parked because the invocation receipt could not be completed.";
  server.runs = [
    {
      id: parked.id,
      loopId: parked.loopId,
      admissionOperationId: parked.admissionOperationId,
      definitionVersion: 2,
      status: parked.status,
      createdAtUtc: parked.createdAtUtc,
      updatedAtUtc: parked.updatedAtUtc,
      completedAtUtc: null,
      iteration: 0,
      nextStepIndex: 0,
      failureCode: parked.failureCode,
      isDeleted: false,
    },
  ];
  server.runDetails.set(parked.id, parked);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      const admittedParked = {
        ...parked,
        admissionOperationId: input.operationId,
      };
      server.runs[0] = {
        ...server.runs[0],
        admissionOperationId: input.operationId,
      };
      server.runDetails.set(admittedParked.id, admittedParked);
      return Promise.resolve({
        admissionStatus: "AuditUnavailable",
        run: admittedParked,
        detail:
          "Run admission was parked because its invocation audit needs review.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Inspect this run.";
  await app.elements.startRunButton.click();

  assert.match(app.elements.runTitle.textContent, /run-parked/);
  assert.match(app.elements.runSubtitle.textContent, /Needs Review/);
  assert.match(
    app.elements.validationBanner.textContent,
    /admission was parked.*audit needs review/i,
  );
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /Run was not admitted/,
  );
});

test("a rejected Resume response is shown as a failure instead of a success toast", async () => {
  const server = new FakeFetchServer(createCatalog());
  const paused = createRunSnapshot();
  paused.status = "Paused";
  paused.completedAtUtc = null;
  server.runs = [
    {
      id: paused.id,
      loopId: paused.loopId,
      admissionOperationId: paused.admissionOperationId,
      definitionVersion: 2,
      status: paused.status,
      createdAtUtc: paused.createdAtUtc,
      updatedAtUtc: paused.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(paused.id, paused);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  app.context.testHub = {
    connected: true,
    invoke: () =>
      Promise.resolve({
        status: "WorkspaceExecutionBusy",
        run: paused,
        detail: "Another loop is actively executing.",
      }),
  };
  vm.runInContext("hub = testHub", app.context);

  const resumeButton = app.elements.runActions.children.find(
    (child) => child.textContent === "Resume",
  );
  assert.ok(resumeButton);
  await resumeButton.click();

  assert.match(
    app.elements.validationBanner.textContent,
    /Resume failed: Another loop is actively executing/,
  );
  assert.equal(app.elements.toast.textContent, "");
});

test("a committed Resume audit warning refreshes the durable run instead of reporting failure", async () => {
  const server = new FakeFetchServer(createCatalog());
  const paused = createRunSnapshot();
  paused.status = "Paused";
  paused.completedAtUtc = null;
  const resumed = {
    ...paused,
    status: "Running",
    lifecycleVersion: paused.lifecycleVersion + 1,
  };
  server.runs = [
    {
      id: paused.id,
      loopId: paused.loopId,
      admissionOperationId: paused.admissionOperationId,
      definitionVersion: 2,
      status: paused.status,
      createdAtUtc: paused.createdAtUtc,
      updatedAtUtc: paused.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(paused.id, paused);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  app.context.testHub = {
    connected: true,
    invoke: () => {
      server.runs[0] = {
        ...server.runs[0],
        status: resumed.status,
        updatedAtUtc: resumed.updatedAtUtc,
      };
      server.runDetails.set(resumed.id, resumed);
      return Promise.resolve({
        status: "AuditWarning",
        run: resumed,
        detail: "Resume committed, but its outcome audit needs review.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  const resumeButton = app.elements.runActions.children.find(
    (child) => child.textContent === "Resume",
  );
  assert.ok(resumeButton);
  await resumeButton.click();

  assert.equal(
    app.elements.toast.textContent,
    "Resume committed, but its outcome audit needs review.",
  );
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /Resume failed/,
  );
  assert.match(app.elements.runSubtitle.textContent, /Running/);
});

test("Resume preserves unreadable operations but retires identities proved absent", async () => {
  const server = new FakeFetchServer(createCatalog());
  const paused = createRunSnapshot();
  paused.status = "Paused";
  paused.completedAtUtc = null;
  server.runs = [
    {
      id: paused.id,
      loopId: paused.loopId,
      admissionOperationId: paused.admissionOperationId,
      definitionVersion: 2,
      status: paused.status,
      createdAtUtc: paused.createdAtUtc,
      updatedAtUtc: paused.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(paused.id, paused);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  const operationIds = [];
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      operationIds.push(input.operationId);
      if (operationIds.length === 1)
        return Promise.reject(
          new Error(
            "Failed to invoke 'ResumeLoop': unsupported_loop_persistence_schema: Delete `.custom-loop-run-index.json` and retry the operation.",
          ),
        );
      if (operationIds.length === 2)
        return Promise.resolve({
          status: "WorkspaceHostUnavailable",
          run: paused,
          operationId: input.operationId,
          detail: "Hosting is temporarily unavailable.",
        });
      return Promise.resolve({
        status: "InvalidState",
        run: paused,
        operationId: input.operationId,
        detail: "Definitive retry response.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  let resumeButton = app.elements.runActions.children.find(
    (child) => child.textContent === "Resume",
  );
  assert.ok(resumeButton);
  await resumeButton.click();
  assert.match(
    app.elements.validationBanner.textContent,
    /unsupported_loop_persistence_schema.*Delete `.custom-loop-run-index\.json`/i,
  );

  resumeButton = app.elements.runActions.children.find(
    (child) => child.textContent === "Resume",
  );
  assert.ok(resumeButton);
  await resumeButton.click();
  assert.match(
    app.elements.validationBanner.textContent,
    /Hosting is temporarily unavailable/,
  );

  resumeButton = app.elements.runActions.children.find(
    (child) => child.textContent === "Resume",
  );
  assert.ok(resumeButton);
  await resumeButton.click();

  assert.equal(operationIds.length, 3);
  assert.equal(operationIds[1], operationIds[0]);
  assert.notEqual(operationIds[2], operationIds[0]);
  assert.match(
    app.elements.validationBanner.textContent,
    /Definitive retry response/,
  );
});

test("lifecycle identities survive reload for pause cancel resume and multiple runs", async () => {
  const localStorage = new FakeStorage();
  const locks = new FakeLockManager();
  const first = await loadLoopBuilder({ localStorage, locks });
  const pause = await first.context.getOrCreatePendingLifecycleRequest(
    "pause",
    "run-one",
    3,
  );
  const cancel = await first.context.getOrCreatePendingLifecycleRequest(
    "cancel",
    "run-one",
    3,
  );
  const resume = await first.context.getOrCreatePendingLifecycleRequest(
    "resume",
    "run-two",
    7,
  );
  const storageKey = vm.runInContext(
    "pendingLifecycleStorageKey",
    first.context,
  );
  const stored = localStorage.getItem(storageKey);

  assert.ok(stored);
  assert.equal(JSON.parse(stored).schemaVersion, 1);
  assert.deepEqual(
    JSON.parse(stored).requests.map((request) => request.kind),
    ["pause", "cancel", "resume"],
  );
  assert.doesNotMatch(stored, /prompt|context/i);

  const secondServer = new FakeFetchServer(createCatalog());
  secondServer.runDetails.set("run-one", {
    ...createRunSnapshot(),
    id: "run-one",
    lifecycleVersion: 3,
  });
  secondServer.runDetails.set("run-two", {
    ...createRunSnapshot(),
    id: "run-two",
    lifecycleVersion: 7,
  });
  const second = await loadLoopBuilder({
    server: secondServer,
    localStorage,
    locks,
  });
  const restoredPause = await second.context.getOrCreatePendingLifecycleRequest(
    "pause",
    "run-one",
    3,
  );
  const restoredCancel =
    await second.context.getOrCreatePendingLifecycleRequest(
      "cancel",
      "run-one",
      3,
    );
  const restoredResume =
    await second.context.getOrCreatePendingLifecycleRequest(
      "resume",
      "run-two",
      7,
    );

  assert.equal(restoredPause.operationId, pause.operationId);
  assert.equal(restoredCancel.operationId, cancel.operationId);
  assert.equal(restoredResume.operationId, resume.operationId);
});

test("concurrent tabs coordinate one lifecycle operation identity", async () => {
  const localStorage = new FakeStorage();
  const locks = new FakeLockManager();
  const first = await loadLoopBuilder({ localStorage, locks });
  const second = await loadLoopBuilder({ localStorage, locks });

  const [firstRequest, secondRequest] = await Promise.all([
    first.context.getOrCreatePendingLifecycleRequest("pause", "run-shared", 4),
    second.context.getOrCreatePendingLifecycleRequest("pause", "run-shared", 4),
  ]);

  assert.equal(secondRequest.operationId, firstRequest.operationId);
  const storageKey = vm.runInContext(
    "pendingLifecycleStorageKey",
    first.context,
  );
  assert.equal(JSON.parse(localStorage.getItem(storageKey)).requests.length, 1);
});

test("registry setup failures remain isolated by operation family", async () => {
  const scope = encodeURIComponent("C:/workspace".normalize("NFC"));
  const corruptLifecycleStorage = new FakeStorage();
  corruptLifecycleStorage.setItem(
    `embodysense.pending-loop-lifecycle.v1.${scope}`,
    "{ malformed",
  );
  const invocationAvailable = await loadLoopBuilder({
    localStorage: corruptLifecycleStorage,
  });

  assert.ok(
    vm.runInContext(
      "pendingInvocationRegistryLockName",
      invocationAvailable.context,
    ),
  );
  assert.equal(
    vm.runInContext(
      "pendingLifecycleRegistryLockName",
      invocationAvailable.context,
    ),
    null,
  );

  const corruptInvocationStorage = new FakeStorage();
  corruptInvocationStorage.setItem(
    `embodysense.pending-loop-invocations.v1.${scope}`,
    "{ malformed",
  );
  const lifecycleAvailable = await loadLoopBuilder({
    localStorage: corruptInvocationStorage,
  });

  assert.equal(
    vm.runInContext(
      "pendingInvocationRegistryLockName",
      lifecycleAvailable.context,
    ),
    null,
  );
  assert.ok(
    vm.runInContext(
      "pendingLifecycleRegistryLockName",
      lifecycleAvailable.context,
    ),
  );
});

test("definitive HTTP lifecycle errors retire their operation identity", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  const operationIds = [];
  server.on("POST", `/api/loop-runs/${run.id}/pause`, ({ body }) => {
    operationIds.push(body.operationId);
    return {
      status: 409,
      body: {
        status: "Conflict",
        run,
        operationId: body.operationId,
        detail: "The lifecycle version is stale.",
      },
    };
  });
  const app = await loadLoopBuilder({ server });
  app.context.testRun = run;
  vm.runInContext("selectedRun = testRun", app.context);

  await app.context.controlRun("pause");
  await app.context.controlRun("pause");

  assert.equal(operationIds.length, 2);
  assert.notEqual(operationIds[1], operationIds[0]);
  assert.equal(
    vm.runInContext("pendingLifecycleRequests.size", app.context),
    0,
  );
});

test("receipt-pending lifecycle failures retain their operation identity", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  const operationIds = [];
  server.on("POST", `/api/loop-runs/${run.id}/cancel`, ({ body }) => {
    operationIds.push(body.operationId);
    server.controlReceipts.set(body.operationId, {
      operationId: body.operationId,
      kind: "Cancel",
      runId: run.id,
      expectedLifecycleVersion: run.lifecycleVersion,
      state: "Pending",
      outcome: "Unknown",
      completionDurablyProved: false,
    });
    return {
      status: 503,
      body: {
        status: "Failed",
        run,
        operationId: body.operationId,
        detail:
          "The cancellation signal failed; the control receipt remains pending so the same operation can retry.",
      },
    };
  });
  const app = await loadLoopBuilder({ server });
  app.context.testRun = run;
  vm.runInContext("selectedRun = testRun", app.context);

  await app.context.controlRun("cancel");
  await app.context.controlRun("cancel");

  assert.equal(operationIds.length, 2);
  assert.equal(operationIds[1], operationIds[0]);
  assert.equal(
    vm.runInContext("pendingLifecycleRequests.size", app.context),
    1,
  );
});

test("receipt I/O failures retain their lifecycle operation identity", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  const operationIds = [];
  server.on("POST", `/api/loop-runs/${run.id}/pause`, ({ body }) => {
    operationIds.push(body.operationId);
    return {
      status: 503,
      body: {
        status: "Failed",
        run,
        operationId: body.operationId,
        detail: "A receipt I/O failure occurred.",
      },
    };
  });
  server.on(
    "GET",
    "/api/loop-runs/controls/00000000-0000-4000-8000-000000000001",
    () => ({
      status: 503,
      body: { detail: "Receipt storage is temporarily unavailable." },
    }),
  );
  const app = await loadLoopBuilder({ server });
  app.context.testRun = run;
  vm.runInContext("selectedRun = testRun", app.context);

  await app.context.controlRun("pause");
  await app.context.controlRun("pause");

  assert.deepEqual(operationIds, [
    "00000000-0000-4000-8000-000000000001",
    "00000000-0000-4000-8000-000000000001",
  ]);
  assert.equal(
    vm.runInContext("pendingLifecycleRequests.size", app.context),
    1,
  );
});

test("stalled lifecycle receipt reads stop at one bounded deadline and retain their identities", async () => {
  const localStorage = new FakeStorage();
  const app = await loadLoopBuilder({ localStorage });
  const storageKey = vm.runInContext("pendingLifecycleStorageKey", app.context);
  localStorage.setItem(
    storageKey,
    JSON.stringify({
      schemaVersion: 1,
      requests: [
        {
          kind: "pause",
          runId: "run-stalled",
          expectedLifecycleVersion: 4,
          operationId: "operation-stalled",
        },
      ],
    }),
  );
  app.server.on(
    "GET",
    "/api/loop-runs/controls/operation-stalled",
    () => new Promise(() => {}),
  );

  const startedAt = performance.now();
  await app.context.reconcilePendingLifecycleRequests(startedAt + 25);

  assert.ok(performance.now() - startedAt < 1000);
  assert.equal(
    vm.runInContext("pendingLifecycleRequests.size", app.context),
    1,
  );
  assert.equal(
    JSON.parse(localStorage.getItem(storageKey)).requests[0].operationId,
    "operation-stalled",
  );
});

test("startup preserves receipt-pending identities after lifecycle advancement", async () => {
  const localStorage = new FakeStorage();
  const scope = encodeURIComponent("C:/workspace".normalize("NFC"));
  const storageKey = `embodysense.pending-loop-lifecycle.v1.${scope}`;
  localStorage.setItem(
    storageKey,
    JSON.stringify({
      schemaVersion: 1,
      requests: [
        {
          kind: "resume",
          runId: "run-advanced",
          expectedLifecycleVersion: 4,
          operationId: "operation-before-response-loss",
        },
      ],
    }),
  );
  const server = new FakeFetchServer(createCatalog());
  server.runDetails.set("run-advanced", {
    ...createRunSnapshot(),
    id: "run-advanced",
    lifecycleVersion: 5,
  });
  server.controlReceipts.set("operation-before-response-loss", {
    operationId: "operation-before-response-loss",
    kind: "Resume",
    runId: "run-advanced",
    expectedLifecycleVersion: 4,
    state: "Pending",
    outcome: "Unknown",
    completionDurablyProved: false,
  });

  const app = await loadLoopBuilder({ server, localStorage });

  assert.equal(
    vm.runInContext("pendingLifecycleRequests.size", app.context),
    1,
  );
  assert.equal(
    JSON.parse(localStorage.getItem(storageKey)).requests[0].operationId,
    "operation-before-response-loss",
  );
});

test("startup retires only matching durably completed lifecycle receipts", async () => {
  const localStorage = new FakeStorage();
  const scope = encodeURIComponent("C:/workspace".normalize("NFC"));
  const storageKey = `embodysense.pending-loop-lifecycle.v1.${scope}`;
  localStorage.setItem(
    storageKey,
    JSON.stringify({
      schemaVersion: 1,
      requests: [
        {
          kind: "pause",
          runId: "run-complete",
          expectedLifecycleVersion: 2,
          operationId: "operation-complete",
        },
        {
          kind: "cancel",
          runId: "run-pending",
          expectedLifecycleVersion: 3,
          operationId: "operation-pending",
        },
      ],
    }),
  );
  const server = new FakeFetchServer(createCatalog());
  server.controlReceipts.set("operation-complete", {
    operationId: "operation-complete",
    kind: "Pause",
    runId: "run-complete",
    expectedLifecycleVersion: 2,
    state: "Complete",
    outcome: "Paused",
    completionDurablyProved: true,
  });
  server.controlReceipts.set("operation-pending", {
    operationId: "operation-pending",
    kind: "Cancel",
    runId: "run-pending",
    expectedLifecycleVersion: 3,
    state: "Pending",
    outcome: "Unknown",
    completionDurablyProved: false,
  });

  const app = await loadLoopBuilder({ server, localStorage });

  assert.equal(
    vm.runInContext("pendingLifecycleRequests.size", app.context),
    1,
  );
  assert.equal(
    JSON.parse(localStorage.getItem(storageKey)).requests[0].operationId,
    "operation-pending",
  );
});

test("structured completion retires partial-success responses without reading receipt detail", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  let operationId = null;
  server.on("POST", `/api/loop-runs/${run.id}/cancel`, ({ body }) => {
    operationId = body.operationId;
    server.controlReceipts.set(operationId, {
      operationId,
      kind: "Cancel",
      runId: run.id,
      expectedLifecycleVersion: run.lifecycleVersion,
      state: "Complete",
      outcome: "NeedsReview",
      completionDurablyProved: true,
    });
    return {
      status: 503,
      body: {
        status: "NeedsReview",
        run,
        operationId,
        detail: "This misleading detail says the receipt remains pending.",
      },
    };
  });
  const app = await loadLoopBuilder({ server });
  app.context.testRun = run;
  vm.runInContext("selectedRun = testRun", app.context);

  await app.context.controlRun("cancel");

  assert.ok(operationId);
  assert.equal(
    vm.runInContext("pendingLifecycleRequests.size", app.context),
    0,
  );
});

test("the lifecycle registry remains bounded when safe reconciliation is unavailable", async () => {
  const localStorage = new FakeStorage();
  const app = await loadLoopBuilder({ localStorage });
  const storageKey = vm.runInContext("pendingLifecycleStorageKey", app.context);
  localStorage.setItem(
    storageKey,
    JSON.stringify({
      schemaVersion: 1,
      requests: Array.from({ length: 100 }, (_, index) => ({
        kind: "pause",
        runId: "run-advancing",
        expectedLifecycleVersion: index + 1,
        operationId: `operation-${String(index + 1).padStart(3, "0")}`,
      })),
    }),
  );

  await assert.rejects(
    app.context.getOrCreatePendingLifecycleRequest(
      "cancel",
      "run-advancing",
      101,
    ),
    /100 unresolved lifecycle requests/i,
  );

  assert.equal(
    vm.runInContext("pendingLifecycleRequests.size", app.context),
    100,
  );
  assert.equal(
    JSON.parse(localStorage.getItem(storageKey)).requests.length,
    100,
  );
});

test("completed lifecycle receipts are reconciled before enforcing the registry bound", async () => {
  const localStorage = new FakeStorage();
  const app = await loadLoopBuilder({ localStorage });
  const storageKey = vm.runInContext("pendingLifecycleStorageKey", app.context);
  const requests = Array.from({ length: 100 }, (_, index) => ({
    kind: "pause",
    runId: "run-advancing",
    expectedLifecycleVersion: index + 1,
    operationId: `operation-${String(index + 1).padStart(3, "0")}`,
  }));
  localStorage.setItem(
    storageKey,
    JSON.stringify({ schemaVersion: 1, requests }),
  );
  app.server.controlReceipts.set("operation-001", {
    operationId: "operation-001",
    kind: "Pause",
    runId: "run-advancing",
    expectedLifecycleVersion: 1,
    state: "Complete",
    outcome: "Paused",
    completionDurablyProved: true,
  });

  const reserved = await app.context.getOrCreatePendingLifecycleRequest(
    "cancel",
    "run-advancing",
    101,
  );

  assert.equal(reserved.kind, "cancel");
  assert.equal(
    vm.runInContext("pendingLifecycleRequests.size", app.context),
    100,
  );
  assert.equal(
    JSON.parse(localStorage.getItem(storageKey)).requests.some(
      (request) => request.operationId === "operation-001",
    ),
    false,
  );
});

test("authoritative lifecycle responses survive browser cleanup failures", async () => {
  const localStorage = new FakeStorage();
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  const paused = {
    ...run,
    status: "PauseRequested",
    lifecycleVersion: run.lifecycleVersion + 1,
  };
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: run.admittedDefinition.definitionVersion,
      lifecycleVersion: run.lifecycleVersion,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: run.iteration,
      nextStepIndex: run.nextStepIndex,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  server.on("POST", `/api/loop-runs/${run.id}/pause`, ({ body }) => {
    server.runs[0] = {
      ...server.runs[0],
      lifecycleVersion: paused.lifecycleVersion,
      status: paused.status,
    };
    server.runDetails.set(run.id, paused);
    localStorage.removeItem = () => {
      throw new Error("Storage cleanup failed.");
    };
    return {
      status: 200,
      body: {
        status: "PauseRequested",
        run: paused,
        operationId: body.operationId,
        detail: "Pause recorded.",
      },
    };
  });
  const app = await loadLoopBuilder({ server, localStorage });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  app.context.testRun = run;
  vm.runInContext("selectedRun = testRun", app.context);

  await app.context.controlRun("pause");

  assert.equal(app.elements.toast.textContent, "Pause recorded.");
  assert.match(
    app.elements.validationBanner.textContent,
    /returned, but its durable receipt is still pending or unreadable/i,
  );
  assert.equal(
    vm.runInContext("selectedRun.lifecycleVersion", app.context),
    paused.lifecycleVersion,
  );
});

test("a lost invocation connection reconciles the admitted run and continues monitoring", async () => {
  const server = new FakeFetchServer(createCatalog());
  let invocationOperationId = null;
  let invocationRunReads = 0;
  let invocationReceiptReads = 0;
  server.on("GET", "/api/loop-runs?maximumCount=50", () => {
    if (!invocationOperationId)
      return { status: 200, body: clone(server.runs) };
    invocationRunReads++;
    return {
      status: 200,
      body: invocationRunReads === 1 ? [] : clone(server.runs),
    };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      invocationOperationId = input.operationId;
      const admitted = createRunSnapshot();
      admitted.id = "run-reconciled";
      admitted.admissionOperationId = invocationOperationId;
      admitted.status = "Running";
      admitted.completedAtUtc = null;
      server.runs = [
        {
          id: admitted.id,
          loopId: admitted.loopId,
          admissionOperationId: admitted.admissionOperationId,
          definitionVersion: 2,
          status: admitted.status,
          createdAtUtc: admitted.createdAtUtc,
          updatedAtUtc: admitted.updatedAtUtc,
          completedAtUtc: null,
          iteration: 1,
          nextStepIndex: 1,
          failureCode: null,
          isDeleted: false,
        },
      ];
      server.runDetails.set(admitted.id, admitted);
      server.on(
        "GET",
        `/api/loop-runs/invocations/${invocationOperationId}`,
        () => {
          invocationReceiptReads++;
          return {
            status: 200,
            body:
              invocationReceiptReads === 1
                ? {
                    operationId: invocationOperationId,
                    loopId: admitted.loopId,
                    state: "Pending",
                    outcome: "Unknown",
                    admissionStatus: "",
                    runId: null,
                    createdAtUtc: admitted.createdAtUtc,
                    updatedAtUtc: admitted.updatedAtUtc,
                    detail: "",
                  }
                : {
                    operationId: invocationOperationId,
                    loopId: admitted.loopId,
                    state: "Complete",
                    outcome: "Admitted",
                    admissionStatus: "Admitted",
                    runId: admitted.id,
                    createdAtUtc: admitted.createdAtUtc,
                    updatedAtUtc: admitted.updatedAtUtc,
                    detail: "The run was admitted.",
                  },
          };
        },
      );
      return Promise.reject(
        new Error("WebSocket closed before invocation completion."),
      );
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Run despite the connection loss.";
  await app.elements.startRunButton.click();

  assert.equal(invocationRunReads, 2);
  assert.equal(invocationReceiptReads, 2);
  assert.match(app.elements.runTitle.textContent, /run-reconciled/);
  assert.match(
    app.elements.validationBanner.textContent,
    /durable invocation receipt identified the exact admitted run/i,
  );
  assert.ok(
    app.window.delayedHandlers.some(
      (timer) => timer.delay === 1000 && !timer.cancelled,
    ),
  );
});

test("an in-progress invocation response polls its receipt until the exact admitted run is available", async () => {
  const server = new FakeFetchServer(createCatalog());
  let invocationReceiptReads = 0;
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      const admitted = createRunSnapshot();
      admitted.id = "run-after-in-progress";
      admitted.admissionOperationId = input.operationId;
      admitted.status = "Running";
      admitted.completedAtUtc = null;
      server.runs = [
        {
          id: admitted.id,
          loopId: admitted.loopId,
          admissionOperationId: admitted.admissionOperationId,
          definitionVersion: 2,
          status: admitted.status,
          createdAtUtc: admitted.createdAtUtc,
          updatedAtUtc: admitted.updatedAtUtc,
          completedAtUtc: null,
          iteration: 1,
          nextStepIndex: 1,
          failureCode: null,
          isDeleted: false,
        },
      ];
      server.runDetails.set(admitted.id, admitted);
      server.on(
        "GET",
        `/api/loop-runs/invocations/${input.operationId}`,
        () => {
          invocationReceiptReads++;
          return {
            status: 200,
            body:
              invocationReceiptReads === 1
                ? {
                    operationId: input.operationId,
                    loopId: input.loopId,
                    state: "Pending",
                    outcome: "Unknown",
                    admissionStatus: "",
                    runId: null,
                    createdAtUtc: admitted.createdAtUtc,
                    updatedAtUtc: admitted.updatedAtUtc,
                    detail: "",
                  }
                : {
                    operationId: input.operationId,
                    loopId: input.loopId,
                    state: "Complete",
                    outcome: "Admitted",
                    admissionStatus: "Admitted",
                    runId: admitted.id,
                    createdAtUtc: admitted.createdAtUtc,
                    updatedAtUtc: admitted.updatedAtUtc,
                    detail: "The run was admitted.",
                  },
          };
        },
      );
      return Promise.resolve({
        admissionStatus: "OperationInProgress",
        run: null,
        detail: "The same invocation is still executing.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Wait for the durable result.";
  await app.elements.startRunButton.click();

  assert.equal(invocationReceiptReads, 2);
  assert.match(app.elements.runTitle.textContent, /run-after-in-progress/);
  assert.match(
    app.elements.validationBanner.textContent,
    /durable invocation receipt identified the exact admitted run/i,
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    0,
  );
});

test("receipt polling preserves a newer run selection while verifying the admitted operation", async () => {
  const server = new FakeFetchServer(createCatalog());
  const admitted = createRunSnapshot();
  admitted.id = "run-reconciled-after-poll";
  const selected = createRunSnapshot();
  selected.id = "run-selected-during-poll";
  selected.admissionOperationId = "operation-selected-during-poll";
  server.runs = [admitted, selected].map((run) => ({
    id: run.id,
    loopId: run.loopId,
    admissionOperationId: run.admissionOperationId,
    definitionVersion: 2,
    lifecycleVersion: run.lifecycleVersion,
    status: run.status,
    createdAtUtc: run.createdAtUtc,
    updatedAtUtc: run.updatedAtUtc,
    completedAtUtc: run.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  }));
  server.runDetails.set(admitted.id, admitted);
  server.runDetails.set(selected.id, selected);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  let receiptReads = 0;
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      admitted.admissionOperationId = input.operationId;
      server.runs[0].admissionOperationId = input.operationId;
      server.on(
        "GET",
        `/api/loop-runs/invocations/${input.operationId}`,
        () => {
          receiptReads++;
          return receiptReads === 1
            ? { status: 404, body: { detail: "Receipt is still pending." } }
            : {
                status: 200,
                body: {
                  operationId: input.operationId,
                  loopId: input.loopId,
                  state: "Complete",
                  outcome: "Admitted",
                  admissionStatus: "Admitted",
                  runId: admitted.id,
                  detail: "The run was admitted.",
                },
              };
        },
      );
      return Promise.resolve({
        admissionStatus: "OperationInProgress",
        run: null,
        detail: "The operation is still pending.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value =
    "Preserve my selection during receipt polling.";
  const invocation = app.context.startRun();
  for (let attempt = 0; attempt < 20 && receiptReads < 1; attempt++)
    await new Promise((resolve) => setTimeout(resolve, 5));
  assert.equal(receiptReads, 1);
  await app.context.selectRun(selected.id);
  await invocation;

  assert.equal(vm.runInContext("selectedRunId", app.context), selected.id);
  assert.equal(vm.runInContext("selectedRun.id", app.context), selected.id);
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    0,
  );
  assert.match(
    app.elements.validationBanner.textContent,
    /durable invocation receipt identified the exact admitted run/i,
  );
});

test("receipt mismatch preserves a newer run selection made while polling", async () => {
  const server = new FakeFetchServer(createCatalog());
  const selected = createRunSnapshot();
  selected.id = "run-selected-before-receipt-mismatch";
  selected.admissionOperationId = "operation-selected-before-receipt-mismatch";
  server.runs = [
    {
      id: selected.id,
      loopId: selected.loopId,
      admissionOperationId: selected.admissionOperationId,
      definitionVersion: 2,
      lifecycleVersion: selected.lifecycleVersion,
      status: selected.status,
      createdAtUtc: selected.createdAtUtc,
      updatedAtUtc: selected.updatedAtUtc,
      completedAtUtc: selected.completedAtUtc,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(selected.id, selected);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  let receiptReads = 0;
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      server.on(
        "GET",
        `/api/loop-runs/invocations/${input.operationId}`,
        () => {
          receiptReads++;
          return receiptReads === 1
            ? { status: 404, body: { detail: "Receipt is still pending." } }
            : {
                status: 200,
                body: {
                  operationId: "operation-owned-by-another-request",
                  loopId: input.loopId,
                  state: "Complete",
                  outcome: "Rejected",
                  admissionStatus: "Conflict",
                  runId: null,
                },
              };
        },
      );
      return Promise.resolve({
        admissionStatus: "OperationInProgress",
        run: null,
        detail: "The operation is still pending.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value =
    "Preserve my selection after a mismatched receipt.";
  const invocation = app.context.startRun();
  for (let attempt = 0; attempt < 20 && receiptReads < 1; attempt++)
    await new Promise((resolve) => setTimeout(resolve, 5));
  assert.equal(receiptReads, 1);
  await app.context.selectRun(selected.id);
  await invocation;

  assert.equal(vm.runInContext("selectedRunId", app.context), selected.id);
  assert.equal(vm.runInContext("selectedRun.id", app.context), selected.id);
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    1,
  );
  assert.match(
    app.elements.validationBanner.textContent,
    /durable invocation evidence did not match/i,
  );
});

test("a lost invocation connection reports a durable rejection without selecting unrelated history", async () => {
  const server = new FakeFetchServer(createCatalog());
  const unrelated = createRunSnapshot();
  unrelated.id = "run-unrelated";
  server.runs = [
    {
      id: unrelated.id,
      loopId: unrelated.loopId,
      admissionOperationId: unrelated.admissionOperationId,
      definitionVersion: 2,
      status: unrelated.status,
      createdAtUtc: unrelated.createdAtUtc,
      updatedAtUtc: unrelated.updatedAtUtc,
      completedAtUtc: unrelated.completedAtUtc,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(unrelated.id, unrelated);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      server.invocationReceipts.set(input.operationId, {
        operationId: input.operationId,
        loopId: input.loopId,
        state: "Complete",
        outcome: "Rejected",
        admissionStatus: "Invalid",
        runId: null,
        createdAtUtc: "2026-07-20T12:00:00Z",
        updatedAtUtc: "2026-07-20T12:00:01Z",
        detail: "The saved definition hash no longer matches.",
      });
      return Promise.reject(
        new Error("WebSocket closed before rejection arrived."),
      );
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Reject this stale request.";
  await app.elements.startRunButton.click();

  assert.match(
    app.elements.validationBanner.textContent,
    /saved definition hash no longer matches/i,
  );
  assert.equal(app.elements.runTitle.textContent, "No run selected");
});

test("a lost invocation connection preserves a parked run referenced by an audit-unavailable receipt", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      const parked = createRunSnapshot();
      parked.id = "run-parked-after-disconnect";
      parked.admissionOperationId = input.operationId;
      parked.status = "NeedsReview";
      parked.completedAtUtc = null;
      parked.failureCode = "InvocationReceiptAuditUnavailable";
      parked.failureDetail =
        "Admission was parked because the invocation receipt could not be completed.";
      server.runs = [
        {
          id: parked.id,
          loopId: parked.loopId,
          admissionOperationId: parked.admissionOperationId,
          definitionVersion: 2,
          status: parked.status,
          createdAtUtc: parked.createdAtUtc,
          updatedAtUtc: parked.updatedAtUtc,
          completedAtUtc: null,
          iteration: 0,
          nextStepIndex: 0,
          failureCode: parked.failureCode,
          isDeleted: false,
        },
      ];
      server.runDetails.set(parked.id, parked);
      server.invocationReceipts.set(input.operationId, {
        operationId: input.operationId,
        loopId: input.loopId,
        state: "Complete",
        outcome: "Rejected",
        admissionStatus: "AuditUnavailable",
        runId: parked.id,
        createdAtUtc: parked.createdAtUtc,
        updatedAtUtc: parked.updatedAtUtc,
        detail:
          "Run admission was parked because its invocation audit needs review.",
      });
      return Promise.reject(
        new Error("WebSocket closed before the audit warning arrived."),
      );
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Preserve this parked run.";
  await app.elements.startRunButton.click();

  assert.match(
    app.elements.runTitle.textContent,
    /run-parked-after-disconnect/,
  );
  assert.match(app.elements.runSubtitle.textContent, /Needs Review/);
  assert.match(
    app.elements.validationBanner.textContent,
    /admission was parked.*audit needs review/i,
  );
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /Run was not admitted/,
  );
});

test("an admitted receipt that names unrelated run evidence remains unknown and preserves the operation for retry", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  let operationId = null;
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      operationId = input.operationId;
      const unrelated = createRunSnapshot();
      unrelated.id = "run-wrong-operation";
      unrelated.admissionOperationId = "invoke-someone-else";
      server.runs = [
        {
          id: unrelated.id,
          loopId: unrelated.loopId,
          admissionOperationId: unrelated.admissionOperationId,
          definitionVersion: 2,
          status: unrelated.status,
          createdAtUtc: unrelated.createdAtUtc,
          updatedAtUtc: unrelated.updatedAtUtc,
          completedAtUtc: unrelated.completedAtUtc,
          iteration: 1,
          nextStepIndex: 1,
          failureCode: null,
          isDeleted: false,
        },
      ];
      server.runDetails.set(unrelated.id, unrelated);
      server.invocationReceipts.set(input.operationId, {
        operationId: input.operationId,
        loopId: input.loopId,
        state: "Complete",
        outcome: "Admitted",
        admissionStatus: "Admitted",
        runId: unrelated.id,
        createdAtUtc: unrelated.createdAtUtc,
        updatedAtUtc: unrelated.updatedAtUtc,
        detail: "The run was admitted.",
      });
      return Promise.reject(
        new Error("WebSocket closed before invocation completion."),
      );
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Do not trust an unrelated run.";
  await app.elements.startRunButton.click();

  assert.equal(app.elements.runTitle.textContent, "No run selected");
  assert.match(
    app.elements.validationBanner.textContent,
    new RegExp(
      `matching run evidence.*${operationId}.*could not be verified`,
      "i",
    ),
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    1,
  );
});

test("a connection setup failure is reported before dispatch without creating an ambiguous operation", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  vm.runInContext(
    "getHub = async () => { throw new Error('The SignalR handshake failed.'); }",
    app.context,
  );

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Never dispatched.";
  await app.elements.startRunButton.click();

  assert.match(
    app.elements.invokeError.textContent,
    /could not be sent.*live connection was not established.*handshake failed/i,
  );
  assert.equal(app.elements.invokeError.hidden, false);
  assert.equal(app.elements.appShell.inert, true);
  assert.equal(app.elements.invokeModal.attributes.get("aria-hidden"), "false");
  assert.doesNotMatch(
    app.elements.invokeError.textContent,
    /outcome.*unknown/i,
  );
  assert.equal(
    server.calls.filter((call) =>
      call.url.startsWith("/api/loop-runs/invocations/"),
    ).length,
    0,
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    0,
  );
});

test("secure invocation preparation failures remain visible and retryable inside the modal", async () => {
  const app = await loadLoopBuilder({
    crypto: {
      subtle: null,
      randomUUID: () => "00000000-0000-4000-8000-000000000001",
    },
  });
  await selectCustomLoop(app);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Prepare this securely.";
  await app.elements.startRunButton.click();

  assert.match(
    app.elements.invokeError.textContent,
    /could not be prepared safely.*secure request identity hashing is unavailable/i,
  );
  assert.equal(app.elements.invokeError.hidden, false);
  assert.equal(app.elements.appShell.inert, true);
  assert.equal(app.elements.startRunButton.disabled, false);
  assert.equal(app.elements.closeInvokeButton.disabled, false);
  assert.equal(app.elements.cancelInvokeButton.disabled, false);
});

test("a cached hub disconnect before invocation send is not reconciled as an ambiguous operation", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  const preDispatchError = vm.runInContext(
    "new SignalRPreDispatchError('SignalR connection is not available.')",
    app.context,
  );
  app.context.testHub = {
    connected: true,
    invoke: () => Promise.reject(preDispatchError),
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Disconnect before send.";
  await app.elements.startRunButton.click();

  assert.match(
    app.elements.validationBanner.textContent,
    /could not be sent.*live connection was not established.*connection is not available/i,
  );
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /outcome.*unknown/i,
  );
  assert.equal(
    server.calls.filter((call) =>
      call.url.startsWith("/api/loop-runs/invocations/"),
    ).length,
    0,
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    0,
  );
});

test("an unsupported persistence schema Hub error preserves cleanup guidance and the operation for retry", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  const operationIds = [];
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      operationIds.push(input.operationId);
      return Promise.reject(
        new Error(
          "Failed to invoke 'InvokeLoop': unsupported_loop_persistence_schema: Delete `.custom-loop-run-index.json` and retry the operation.",
        ),
      );
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Retry after cleaning the index.";
  await app.elements.startRunButton.click();

  assert.match(
    app.elements.validationBanner.textContent,
    /unsupported_loop_persistence_schema.*Delete `.custom-loop-run-index\.json`.*retrying the exact request/i,
  );
  assert.match(
    app.elements.validationBanner.textContent,
    /Run execution requires persistence cleanup/i,
  );
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /Run was not admitted/i,
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    1,
  );

  app.context.testHub.invoke = (_target, input) => {
    operationIds.push(input.operationId);
    return Promise.resolve({
      admissionStatus: "Invalid",
      run: null,
      detail: "Definitive retry response.",
    });
  };
  vm.runInContext("openInvokeModal()", app.context);
  await app.elements.startRunButton.click();

  assert.equal(operationIds.length, 2);
  assert.equal(operationIds[1], operationIds[0]);
  assert.match(
    app.elements.validationBanner.textContent,
    /Definitive retry response/,
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    0,
  );
});

test("unavailable invocation evidence keeps the outcome unknown and reuses the exact operation on retry", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  const operationIds = [];
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      operationIds.push(input.operationId);
      server.on(
        "GET",
        `/api/loop-runs/invocations/${input.operationId}`,
        () => ({
          status: 503,
          body: { detail: "Receipt storage is temporarily unavailable." },
        }),
      );
      return Promise.reject(new Error("WebSocket closed."));
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Retry this exact prompt.";
  await app.elements.startRunButton.click();
  assert.match(
    app.elements.validationBanner.textContent,
    /outcome is unknown/i,
  );
  assert.match(
    app.elements.validationBanner.textContent,
    new RegExp(operationIds[0]),
  );

  app.context.testHub.invoke = (_target, input) => {
    operationIds.push(input.operationId);
    return Promise.resolve({
      admissionStatus: "Invalid",
      run: null,
      detail: "Definitive retry response.",
    });
  };
  vm.runInContext("openInvokeModal()", app.context);
  assert.equal(app.elements.invocationPrompt.value, "Retry this exact prompt.");
  await app.elements.startRunButton.click();

  assert.equal(operationIds.length, 2);
  assert.equal(operationIds[1], operationIds[0]);
  assert.match(
    app.elements.validationBanner.textContent,
    /Definitive retry response/,
  );
});

test("a receipt-unavailable retry response remains unknown and preserves the operation identity", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  const operationIds = [];
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      operationIds.push(input.operationId);
      server.on(
        "GET",
        `/api/loop-runs/invocations/${input.operationId}`,
        () => ({
          status: 503,
          body: { detail: "Receipt storage is temporarily unavailable." },
        }),
      );
      return Promise.reject(new Error("WebSocket closed."));
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Preserve this uncertain receipt.";
  await app.elements.startRunButton.click();

  app.context.testHub.invoke = (_target, input) => {
    operationIds.push(input.operationId);
    return Promise.resolve({
      admissionStatus: "ReceiptUnavailable",
      run: null,
      detail: "The invocation receipt could not be read safely.",
    });
  };
  vm.runInContext("openInvokeModal()", app.context);
  await app.elements.startRunButton.click();

  assert.equal(operationIds.length, 2);
  assert.equal(operationIds[1], operationIds[0]);
  assert.match(
    app.elements.validationBanner.textContent,
    /outcome is unknown/i,
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    1,
  );
});

test("a previously dispatched retry preserves its operation identity when workspace hosting is unavailable", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  const operationIds = [];
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      operationIds.push(input.operationId);
      server.on(
        "GET",
        `/api/loop-runs/invocations/${input.operationId}`,
        () => ({
          status: 503,
          body: { detail: "Receipt storage is temporarily unavailable." },
        }),
      );
      return Promise.reject(new Error("WebSocket closed."));
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Preserve this host-unavailable retry.";
  await app.elements.startRunButton.click();

  app.context.testHub.invoke = (_target, input) => {
    operationIds.push(input.operationId);
    return Promise.resolve({
      admissionStatus: "WorkspaceHostUnavailable",
      run: null,
      detail: "The workspace host is temporarily unavailable.",
    });
  };
  vm.runInContext("openInvokeModal()", app.context);
  await app.elements.startRunButton.click();

  assert.equal(operationIds.length, 2);
  assert.equal(operationIds[1], operationIds[0]);
  assert.match(
    app.elements.validationBanner.textContent,
    /outcome is unknown/i,
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    1,
  );
});

test("canonically equivalent invocation prompts reuse one unresolved operation identity", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  const attempts = [];
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      attempts.push(input);
      server.on(
        "GET",
        `/api/loop-runs/invocations/${input.operationId}`,
        () => ({
          status: 503,
          body: { detail: "Receipt storage is temporarily unavailable." },
        }),
      );
      return Promise.reject(new Error("WebSocket closed."));
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Review café evidence.";
  await app.elements.startRunButton.click();

  app.context.testHub.invoke = (_target, input) => {
    attempts.push(input);
    return Promise.resolve({
      admissionStatus: "Invalid",
      run: null,
      detail: "Definitive retry response.",
    });
  };
  vm.runInContext("openInvokeModal()", app.context);
  app.elements.invocationPrompt.value = "Review cafe\u0301 evidence.";
  await app.elements.startRunButton.click();

  assert.equal(attempts.length, 2);
  assert.equal(attempts[1].operationId, attempts[0].operationId);
  assert.equal(attempts[1].invocationPrompt, "Review café evidence.");
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    0,
  );
});

test("invocation is locked before asynchronous request hashing completes", async () => {
  let releaseDigest;
  const delayedCrypto = {
    subtle: {
      digest(algorithm, data) {
        return new Promise((resolve) => {
          releaseDigest = async () =>
            resolve(await webcrypto.subtle.digest(algorithm, data));
        });
      },
    },
    randomUUID: () => "00000000-0000-4000-8000-000000000001",
  };
  const app = await loadLoopBuilder({ crypto: delayedCrypto });
  await selectCustomLoop(app);
  const attempts = [];
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      attempts.push(input);
      return Promise.resolve({
        admissionStatus: "Invalid",
        run: null,
        detail: "Definitive response.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);
  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Hash this request once.";

  const first = app.context.startRun();
  assert.equal(app.elements.startRunButton.disabled, true);
  assert.equal(app.elements.closeInvokeButton.disabled, false);
  assert.equal(app.elements.cancelInvokeButton.disabled, false);
  assert.equal(app.elements.invocationPrompt.disabled, true);
  const second = app.context.startRun();
  await second;
  assert.equal(attempts.length, 0);
  releaseDigest();
  await first;

  assert.equal(attempts.length, 1);
  assert.equal(vm.runInContext("invocationInFlight", app.context), false);
  assert.doesNotMatch(app.elements.invokeModal.className, /open/);
  assert.equal(app.elements.appShell.inert, false);
});

test("cancelling stalled preparation prevents its later dispatch without blocking a new attempt", async () => {
  let releaseFirstDigest;
  let digestCalls = 0;
  const delayedFirstCrypto = {
    subtle: {
      digest(algorithm, data) {
        digestCalls++;
        if (digestCalls > 1) return webcrypto.subtle.digest(algorithm, data);
        return new Promise((resolve) => {
          releaseFirstDigest = async () =>
            resolve(await webcrypto.subtle.digest(algorithm, data));
        });
      },
    },
    randomUUID: () =>
      `00000000-0000-4000-8000-${String(digestCalls).padStart(12, "0")}`,
  };
  const app = await loadLoopBuilder({ crypto: delayedFirstCrypto });
  await selectCustomLoop(app);
  const attempts = [];
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      attempts.push(input);
      return Promise.resolve({
        admissionStatus: "Invalid",
        run: null,
        detail: "Definitive response.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);
  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Cancel the stalled request.";

  const cancelledAttempt = app.context.startRun();
  await app.elements.cancelInvokeButton.click();
  assert.doesNotMatch(app.elements.invokeModal.className, /open/);
  assert.equal(app.elements.appShell.inert, false);
  assert.equal(vm.runInContext("invocationInFlight", app.context), false);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Dispatch only the replacement.";
  await app.elements.startRunButton.click();
  assert.equal(attempts.length, 1);

  releaseFirstDigest();
  await cancelledAttempt;
  assert.equal(attempts.length, 1);
  assert.equal(vm.runInContext("invocationInFlight", app.context), false);
});

test("cancelling stalled connection setup cannot clobber or dispatch through its replacement hub", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);
  vm.runInContext(
    `
    testConnections = [];
    testInvocations = [];
    releaseFirstConnection = null;
    invocationRequestKey = async () => "a".repeat(64);
    createHubUrl = () => "ws://127.0.0.1/agent-hub";
    JsonSignalRConnection = class {
      constructor() {
        this.index = testConnections.length;
        this.connected = false;
        this.handlers = new Map();
        this.onclose = null;
        this.stopped = false;
        testConnections.push(this);
      }
      on(target, handler) {
        this.handlers.set(target, handler);
      }
      async start() {
        if (this.index === 0) await new Promise(resolve => { releaseFirstConnection = resolve; });
        this.connected = true;
      }
      async invoke(_target, input) {
        testInvocations.push(input);
        return { admissionStatus: "Invalid", run: null, detail: "Definitive response." };
      }
      stop() {
        this.stopped = true;
        this.connected = false;
        this.onclose?.();
      }
    };
  `,
    app.context,
  );
  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value =
    "Cancel while the first connection starts.";

  assert.equal(
    vm.runInContext(
      "dirty || isSystemLoop() || invocationInFlight",
      app.context,
    ),
    false,
  );
  const cancelledAttempt = app.context.startRun();
  for (
    let attempt = 0;
    attempt < 20 && !vm.runInContext("releaseFirstConnection", app.context);
    attempt++
  )
    await new Promise((resolve) => setTimeout(resolve, 5));
  assert.equal(vm.runInContext("testConnections.length", app.context), 1);
  assert.equal(
    typeof vm.runInContext("releaseFirstConnection", app.context),
    "function",
  );
  await app.elements.cancelInvokeButton.click();

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value =
    "Dispatch through the replacement connection.";
  await app.elements.startRunButton.click();
  assert.equal(vm.runInContext("testInvocations.length", app.context), 1);
  assert.equal(
    vm.runInContext("hub === testConnections[1]", app.context),
    true,
  );
  vm.runInContext(
    "testConnections[1].handlers.get('ApprovalsChanged')([{ requestId: 'replacement-approval' }])",
    app.context,
  );
  assert.equal(app.elements.approvalCount.textContent, "1 pending");

  vm.runInContext(
    `
    releaseFirstConnection();
    testConnections[0].handlers.get("ApprovalsChanged")([]);
  `,
    app.context,
  );
  await cancelledAttempt;

  assert.equal(
    vm.runInContext("testConnections[0].stopped", app.context),
    true,
  );
  assert.equal(
    vm.runInContext("hub === testConnections[1]", app.context),
    true,
  );
  assert.equal(vm.runInContext("testInvocations.length", app.context), 1);
  assert.equal(vm.runInContext("invocationInFlight", app.context), false);
  assert.equal(app.elements.approvalCount.textContent, "1 pending");
});

test("a runtime model change allocates a new operation without discarding the older pending identity", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  const operationIds = [];
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      operationIds.push(input.operationId);
      server.on(
        "GET",
        `/api/loop-runs/invocations/${input.operationId}`,
        () => ({
          status: 503,
          body: { detail: "Receipt storage is temporarily unavailable." },
        }),
      );
      return Promise.reject(new Error("WebSocket closed."));
    },
  };
  vm.runInContext("hub = testHub", app.context);
  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Run under the configured model.";
  await app.elements.startRunButton.click();

  vm.runInContext("catalog.runtimeModel.model = 'gpt-5-updated'", app.context);
  app.context.testHub.invoke = (_target, input) => {
    operationIds.push(input.operationId);
    return Promise.resolve({
      admissionStatus: "Invalid",
      run: null,
      detail: "The new runtime request was definitively rejected.",
    });
  };
  vm.runInContext("openInvokeModal()", app.context);
  await app.elements.startRunButton.click();

  assert.equal(operationIds.length, 2);
  assert.notEqual(operationIds[1], operationIds[0]);
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    1,
  );
  assert.equal(
    vm.runInContext(
      "pendingInvocationRequests.values().next().value.operationId",
      app.context,
    ),
    operationIds[0],
  );
  assert.match(
    app.elements.validationBanner.textContent,
    /new runtime request was definitively rejected/i,
  );
});

test("a request conflict reconciles an older admitted receipt before releasing its operation", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  let operationId = null;
  let receiptAvailable = false;
  server.on(
    "GET",
    "/api/loop-runs/invocations/00000000-0000-4000-8000-000000000001",
    () => {
      if (!receiptAvailable)
        return {
          status: 503,
          body: { detail: "Receipt storage is temporarily unavailable." },
        };
      return { status: 200, body: server.invocationReceipts.get(operationId) };
    },
  );
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      operationId = input.operationId;
      return Promise.reject(new Error("WebSocket closed."));
    },
  };
  vm.runInContext("hub = testHub", app.context);
  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Reconcile the older model receipt.";
  await app.elements.startRunButton.click();

  app.context.testHub.invoke = (_target, input) => {
    assert.equal(input.operationId, operationId);
    const admitted = createRunSnapshot();
    admitted.id = "run-admitted-before-model-change";
    admitted.admissionOperationId = operationId;
    server.runs = [
      {
        id: admitted.id,
        loopId: admitted.loopId,
        admissionOperationId: admitted.admissionOperationId,
        definitionVersion: 2,
        lifecycleVersion: admitted.lifecycleVersion,
        status: admitted.status,
        createdAtUtc: admitted.createdAtUtc,
        updatedAtUtc: admitted.updatedAtUtc,
        completedAtUtc: admitted.completedAtUtc,
        iteration: 1,
        nextStepIndex: 1,
        failureCode: null,
        isDeleted: false,
      },
    ];
    server.runDetails.set(admitted.id, admitted);
    server.invocationReceipts.set(operationId, {
      operationId,
      loopId: admitted.loopId,
      state: "Complete",
      outcome: "Admitted",
      admissionStatus: "Admitted",
      runId: admitted.id,
      createdAtUtc: admitted.createdAtUtc,
      updatedAtUtc: admitted.updatedAtUtc,
      detail: "The earlier runtime admitted this run.",
    });
    receiptAvailable = true;
    return Promise.resolve({
      admissionStatus: "Conflict",
      run: null,
      detail: "The operation belongs to different canonical runtime identity.",
    });
  };
  vm.runInContext("openInvokeModal()", app.context);
  await app.elements.startRunButton.click();

  assert.match(
    app.elements.runTitle.textContent,
    /run-admitted-before-model-change/,
  );
  assert.match(
    app.elements.validationBanner.textContent,
    /durable invocation receipt identified the exact admitted run/i,
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    0,
  );
});

test("an old admitted replay is selected through its exact run endpoint beyond the newest page", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  let operationId = null;
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      operationId = input.operationId;
      server.on("GET", `/api/loop-runs/invocations/${operationId}`, () => ({
        status: 503,
        body: { detail: "Receipt storage is temporarily unavailable." },
      }));
      return Promise.reject(new Error("WebSocket closed."));
    },
  };
  vm.runInContext("hub = testHub", app.context);
  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Replay an older admitted run.";
  await app.elements.startRunButton.click();

  const oldRun = createRunSnapshot();
  oldRun.id = "run-older-than-first-page";
  oldRun.admissionOperationId = operationId;
  oldRun.createdAtUtc = "2026-06-01T00:00:00Z";
  oldRun.updatedAtUtc = "2026-06-01T00:00:02Z";
  oldRun.completedAtUtc = "2026-06-01T00:00:02Z";
  server.runDetails.set(oldRun.id, oldRun);
  server.traceDetails.set(oldRun.id, createTraceSnapshot(oldRun));
  server.runs = Array.from({ length: 50 }, (_, index) => {
    const run = createRunSnapshot();
    run.id = `run-newer-${String(index).padStart(2, "0")}`;
    run.admissionOperationId = `operation-newer-${String(index).padStart(2, "0")}`;
    return {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      lifecycleVersion: run.lifecycleVersion,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: run.completedAtUtc,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    };
  });
  app.context.testHub.invoke = (_target, input) => {
    assert.equal(input.operationId, operationId);
    return Promise.resolve({
      admissionStatus: "Admitted",
      run: oldRun,
      detail: "The original durable invocation was replayed.",
    });
  };
  vm.runInContext("openInvokeModal()", app.context);
  await app.elements.startRunButton.click();

  assert.match(app.elements.runTitle.textContent, /run-older-than-first-page/);
  assert.equal(
    server.runs.some((run) => run.id === oldRun.id),
    false,
  );
  assert.ok(
    server.calls.some((call) => call.url === `/api/loop-runs/${oldRun.id}`),
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    0,
  );
});

test("a verified exact admission is released when broader run evidence refresh is unavailable", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  const admitted = createRunSnapshot();
  admitted.id = "run-exact-with-partial-evidence";
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      admitted.admissionOperationId = input.operationId;
      server.runDetails.set(admitted.id, admitted);
      server.on("GET", "/api/loop-runs/quota", () => ({
        status: 503,
        body: { detail: "Quota evidence is temporarily unavailable." },
      }));
      return Promise.resolve({
        admissionStatus: "Admitted",
        run: admitted,
        detail: "The run was admitted.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value =
    "Admit despite unrelated refresh failure.";
  await app.elements.startRunButton.click();

  assert.match(
    app.elements.runTitle.textContent,
    /run-exact-with-partial-evidence/,
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    0,
  );
});

test("the unresolved invocation limit refuses new work without evicting an older operation", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);
  for (let index = 0; index < 100; index++) {
    const requestKey = index.toString(16).padStart(64, "0");
    app.context.requestKey = requestKey;
    app.context.pendingRequest = {
      loopId: "loop-research",
      expectedDefinitionVersion: 2,
      expectedDefinitionHash: "sha256:test",
      invocationPrompt: `Prompt ${index}`,
      operationId: `operation-${String(index).padStart(3, "0")}`,
    };
    vm.runInContext(
      "rememberPendingInvocationRequest(requestKey, pendingRequest)",
      app.context,
    );
  }
  const oldestOperation = vm.runInContext(
    "pendingInvocationRequests.values().next().value.operationId",
    app.context,
  );
  let invocationAttempts = 0;
  app.context.testHub = {
    connected: true,
    invoke: () => {
      invocationAttempts++;
      return Promise.resolve({
        admissionStatus: "Invalid",
        run: null,
        detail: "Should not dispatch.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "A 101st unresolved request.";
  await app.elements.startRunButton.click();

  assert.equal(invocationAttempts, 0);
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    100,
  );
  assert.equal(
    vm.runInContext(
      "pendingInvocationRequests.values().next().value.operationId",
      app.context,
    ),
    oldestOperation,
  );
  assert.match(
    app.elements.invokeError.textContent,
    /100 invocation outcomes are still unresolved/i,
  );
});

test("the unresolved invocation limit reconciles completed receipts before refusing new work", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  for (let index = 0; index < 100; index++) {
    const requestKey = index.toString(16).padStart(64, "0");
    const operationId = `operation-${String(index).padStart(3, "0")}`;
    app.context.requestKey = requestKey;
    app.context.pendingRequest = {
      loopId: "loop-research",
      expectedDefinitionVersion: 2,
      expectedDefinitionHash: "sha256:test",
      invocationPrompt: `Prompt ${index}`,
      operationId,
    };
    vm.runInContext(
      "rememberPendingInvocationRequest(requestKey, pendingRequest)",
      app.context,
    );
    if (index === 0) {
      server.invocationReceipts.set(operationId, {
        operationId,
        loopId: "loop-research",
        state: "Complete",
        outcome: "Rejected",
        admissionStatus: "Invalid",
        runId: null,
        detail: "The request was definitively rejected.",
      });
    }
  }
  let invocationAttempts = 0;
  app.context.testHub = {
    connected: true,
    invoke: () => {
      invocationAttempts++;
      return Promise.resolve({
        admissionStatus: "Invalid",
        run: null,
        detail: "The new request was definitively rejected.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "A new request after reconciliation.";
  await app.elements.startRunButton.click();

  assert.equal(invocationAttempts, 1);
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    99,
  );
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /100 invocation outcomes are still unresolved/i,
  );
});

test("the unresolved invocation limit retains admitted receipts until exact run evidence is verified", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  for (let index = 0; index < 100; index++) {
    const requestKey = index.toString(16).padStart(64, "0");
    const operationId = `operation-${String(index).padStart(3, "0")}`;
    app.context.requestKey = requestKey;
    app.context.pendingRequest = {
      loopId: "loop-research",
      expectedDefinitionVersion: 2,
      expectedDefinitionHash: "sha256:test",
      invocationPrompt: `Prompt ${index}`,
      operationId,
    };
    vm.runInContext(
      "rememberPendingInvocationRequest(requestKey, pendingRequest)",
      app.context,
    );
    if (index === 0) {
      server.invocationReceipts.set(operationId, {
        operationId,
        loopId: "loop-research",
        state: "Complete",
        outcome: "Admitted",
        admissionStatus: "Admitted",
        runId: "run-missing-exact-evidence",
        detail: "The request was admitted.",
      });
    }
  }
  let invocationAttempts = 0;
  app.context.testHub = {
    connected: true,
    invoke: () => {
      invocationAttempts++;
      return Promise.resolve({
        admissionStatus: "Invalid",
        run: null,
        detail: "Should not dispatch.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "Do not evict an unverified admission.";
  await app.elements.startRunButton.click();

  assert.equal(invocationAttempts, 0);
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    100,
  );
  assert.match(
    app.elements.invokeError.textContent,
    /100 invocation outcomes are still unresolved/i,
  );
});

test("the unresolved invocation limit retains audit-unavailable receipts until exact run evidence is verified", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  for (let index = 0; index < 100; index++) {
    const requestKey = index.toString(16).padStart(64, "0");
    const operationId = `operation-${String(index).padStart(3, "0")}`;
    app.context.requestKey = requestKey;
    app.context.pendingRequest = {
      loopId: "loop-research",
      expectedDefinitionVersion: 2,
      expectedDefinitionHash: "sha256:test",
      invocationPrompt: `Prompt ${index}`,
      operationId,
    };
    vm.runInContext(
      "rememberPendingInvocationRequest(requestKey, pendingRequest)",
      app.context,
    );
    if (index === 0) {
      server.invocationReceipts.set(operationId, {
        operationId,
        loopId: "loop-research",
        state: "Complete",
        outcome: "Rejected",
        admissionStatus: "AuditUnavailable",
        runId: "run-missing-audit-evidence",
        detail: "Admission was parked for review.",
      });
    }
  }
  let invocationAttempts = 0;
  app.context.testHub = {
    connected: true,
    invoke: () => {
      invocationAttempts++;
      return Promise.resolve({
        admissionStatus: "Invalid",
        run: null,
        detail: "Should not dispatch.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value =
    "Do not evict unverified audit evidence.";
  await app.elements.startRunButton.click();

  assert.equal(invocationAttempts, 0);
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    100,
  );
  assert.match(
    app.elements.invokeError.textContent,
    /100 invocation outcomes are still unresolved/i,
  );
});

test("the unresolved invocation limit releases a completed audit-unavailable rejection without a run", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  for (let index = 0; index < 100; index++) {
    const requestKey = index.toString(16).padStart(64, "0");
    const operationId = `operation-${String(index).padStart(3, "0")}`;
    app.context.requestKey = requestKey;
    app.context.pendingRequest = {
      loopId: "loop-research",
      expectedDefinitionVersion: 2,
      expectedDefinitionHash: "sha256:test",
      invocationPrompt: `Prompt ${index}`,
      operationId,
    };
    vm.runInContext(
      "rememberPendingInvocationRequest(requestKey, pendingRequest)",
      app.context,
    );
    if (index === 0) {
      server.invocationReceipts.set(operationId, {
        operationId,
        loopId: "loop-research",
        state: "Complete",
        outcome: "Rejected",
        admissionStatus: "AuditUnavailable",
        runId: null,
        detail: "The rejected outcome could not be audited.",
      });
    }
  }
  let invocationAttempts = 0;
  app.context.testHub = {
    connected: true,
    invoke: () => {
      invocationAttempts++;
      return Promise.resolve({
        admissionStatus: "Invalid",
        run: null,
        detail: "The new request was definitively rejected.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value =
    "Start after the completed no-run rejection.";
  await app.elements.startRunButton.click();

  assert.equal(invocationAttempts, 1);
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    99,
  );
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /100 invocation outcomes are still unresolved/i,
  );
});

test("the unresolved invocation limit accepts an exact retained tombstone before admitting new work", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  for (let index = 0; index < 100; index++) {
    const requestKey = index.toString(16).padStart(64, "0");
    const operationId = `operation-${String(index).padStart(3, "0")}`;
    app.context.requestKey = requestKey;
    app.context.pendingRequest = {
      loopId: "loop-research",
      expectedDefinitionVersion: 2,
      expectedDefinitionHash: "sha256:test",
      invocationPrompt: `Prompt ${index}`,
      operationId,
    };
    vm.runInContext(
      "rememberPendingInvocationRequest(requestKey, pendingRequest)",
      app.context,
    );
    if (index === 0) {
      const run = createRunSnapshot();
      run.id = "run-retained-capacity-evidence";
      run.admissionOperationId = operationId;
      server.traceDetails.set(run.id, createTombstoneTrace(run));
      server.invocationReceipts.set(operationId, {
        operationId,
        loopId: run.loopId,
        state: "Complete",
        outcome: "Admitted",
        admissionStatus: "Admitted",
        runId: run.id,
        detail: "The request was admitted.",
      });
    }
  }
  let invocationAttempts = 0;
  app.context.testHub = {
    connected: true,
    invoke: () => {
      invocationAttempts++;
      return Promise.resolve({
        admissionStatus: "Invalid",
        run: null,
        detail: "The new request was definitively rejected.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value =
    "Start after exact tombstone reconciliation.";
  await app.elements.startRunButton.click();

  assert.equal(invocationAttempts, 1);
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    99,
  );
  assert.doesNotMatch(
    app.elements.validationBanner.textContent,
    /100 invocation outcomes are still unresolved/i,
  );
});

test("an unresolved invocation operation survives a tab restart without storing prompt text", async () => {
  const localStorage = new FakeStorage();
  const locks = new FakeLockManager();
  const firstServer = new FakeFetchServer(createCatalog());
  const first = await loadLoopBuilder({
    server: firstServer,
    localStorage,
    locks,
  });
  await selectCustomLoop(first);
  let originalOperationId = null;
  first.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      originalOperationId = input.operationId;
      firstServer.on(
        "GET",
        `/api/loop-runs/invocations/${input.operationId}`,
        () => ({
          status: 503,
          body: { detail: "Receipt storage is temporarily unavailable." },
        }),
      );
      return Promise.reject(new Error("WebSocket closed."));
    },
  };
  vm.runInContext("hub = testHub", first.context);

  await first.elements.invokeButton.click();
  first.elements.invocationPrompt.value = "Sensitive unresolved prompt.";
  await first.elements.startRunButton.click();

  const storageKey = vm.runInContext(
    "pendingInvocationStorageKey",
    first.context,
  );
  const stored = localStorage.getItem(storageKey);
  assert.ok(stored);
  assert.equal(JSON.parse(stored).schemaVersion, 1);
  assert.doesNotMatch(stored, /Sensitive unresolved prompt/);

  const secondServer = new FakeFetchServer(createCatalog());
  const second = await loadLoopBuilder({
    server: secondServer,
    localStorage,
    locks,
  });
  await selectCustomLoop(second);
  let retriedOperationId = null;
  second.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      retriedOperationId = input.operationId;
      return Promise.resolve({
        admissionStatus: "Invalid",
        run: null,
        detail: "Definitive retry response after reload.",
      });
    },
  };
  vm.runInContext("hub = testHub", second.context);

  await second.elements.invokeButton.click();
  second.elements.invocationPrompt.value = "Sensitive unresolved prompt.";
  await second.elements.startRunButton.click();

  assert.equal(retriedOperationId, originalOperationId);
  assert.match(
    second.elements.validationBanner.textContent,
    /Definitive retry response after reload/,
  );
  assert.equal(localStorage.getItem(storageKey), null);
});

test("dispatch state survives a tab restart before the invocation response settles", async () => {
  const localStorage = new FakeStorage();
  const locks = new FakeLockManager();
  const firstServer = new FakeFetchServer(createCatalog());
  const first = await loadLoopBuilder({
    server: firstServer,
    localStorage,
    locks,
  });
  await selectCustomLoop(first);
  let originalOperationId = null;
  let rejectFirstInvocation;
  first.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      originalOperationId = input.operationId;
      firstServer.on(
        "GET",
        `/api/loop-runs/invocations/${input.operationId}`,
        () => ({
          status: 503,
          body: { detail: "Receipt storage is temporarily unavailable." },
        }),
      );
      return new Promise((_resolve, reject) => {
        rejectFirstInvocation = reject;
      });
    },
  };
  vm.runInContext("hub = testHub", first.context);

  await first.elements.invokeButton.click();
  first.elements.invocationPrompt.value =
    "Recover the dispatched request after restart.";
  const firstAttempt = first.context.startRun();
  for (let attempt = 0; attempt < 20 && !originalOperationId; attempt++)
    await new Promise((resolve) => setTimeout(resolve, 5));
  assert.ok(originalOperationId);
  const storageKey = vm.runInContext(
    "pendingInvocationStorageKey",
    first.context,
  );
  const storedBeforeResponse = JSON.parse(localStorage.getItem(storageKey));
  assert.equal(storedBeforeResponse.requests[0].dispatchAttempted, true);

  const secondServer = new FakeFetchServer(createCatalog());
  const second = await loadLoopBuilder({
    server: secondServer,
    localStorage,
    locks,
  });
  await selectCustomLoop(second);
  let retriedOperationId = null;
  second.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      retriedOperationId = input.operationId;
      secondServer.on(
        "GET",
        `/api/loop-runs/invocations/${input.operationId}`,
        () => ({
          status: 503,
          body: { detail: "Receipt storage is temporarily unavailable." },
        }),
      );
      return Promise.resolve({
        admissionStatus: "WorkspaceHostUnavailable",
        run: null,
        detail: "The workspace host is temporarily unavailable.",
      });
    },
  };
  vm.runInContext("hub = testHub", second.context);

  await second.elements.invokeButton.click();
  second.elements.invocationPrompt.value =
    "Recover the dispatched request after restart.";
  await second.elements.startRunButton.click();

  assert.equal(retriedOperationId, originalOperationId);
  assert.match(
    second.elements.validationBanner.textContent,
    /outcome is unknown/i,
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", second.context),
    1,
  );
  rejectFirstInvocation(new Error("The original tab closed after dispatch."));
  await firstAttempt;
});

test("pending invocation storage is scoped to the authenticated workspace root", async () => {
  const localStorage = new FakeStorage();
  const locks = new FakeLockManager();
  const firstServer = new FakeFetchServer(createCatalog());
  firstServer.on("GET", "/api/status", () => ({
    status: 200,
    body: { workspaceRoot: "C:/workspace-one", initialized: true },
  }));
  const first = await loadLoopBuilder({
    server: firstServer,
    localStorage,
    locks,
  });
  first.context.requestKey = "a".repeat(64);
  first.context.pendingRequest = {
    loopId: "loop-research",
    expectedDefinitionVersion: 2,
    expectedDefinitionHash: "sha256:test",
    invocationPrompt: "Do not persist this prompt.",
    operationId: "operation-workspace-one",
  };
  vm.runInContext(
    "rememberPendingInvocationRequest(requestKey, pendingRequest)",
    first.context,
  );
  const firstStorageKey = vm.runInContext(
    "pendingInvocationStorageKey",
    first.context,
  );

  const secondServer = new FakeFetchServer(createCatalog());
  secondServer.on("GET", "/api/status", () => ({
    status: 200,
    body: { workspaceRoot: "C:/workspace-two", initialized: true },
  }));
  secondServer.invocationReceipts.set("operation-workspace-one", {
    operationId: "operation-workspace-one",
    loopId: "loop-research",
    state: "Complete",
    outcome: "Admitted",
    admissionStatus: "Admitted",
    runId: "run-from-copied-workspace",
  });
  const second = await loadLoopBuilder({
    server: secondServer,
    localStorage,
    locks,
  });
  const secondStorageKey = vm.runInContext(
    "pendingInvocationStorageKey",
    second.context,
  );

  assert.notEqual(secondStorageKey, firstStorageKey);
  assert.ok(localStorage.getItem(firstStorageKey));
  assert.equal(localStorage.getItem(secondStorageKey), null);
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", second.context),
    0,
  );
});

test("startup ignores an obsolete version 2 invocation registry", async () => {
  const localStorage = new FakeStorage();
  const scope = encodeURIComponent("C:/workspace".normalize("NFC"));
  const obsoleteStorageKey = `embodysense.pending-loop-invocations.v2.${scope}`;
  localStorage.setItem(
    obsoleteStorageKey,
    JSON.stringify({
      schemaVersion: 2,
      requests: [{ requestKey: "a".repeat(64) }],
    }),
  );

  const app = await loadLoopBuilder({ localStorage });

  const currentStorageKey = vm.runInContext(
    "pendingInvocationStorageKey",
    app.context,
  );
  assert.equal(
    currentStorageKey,
    `embodysense.pending-loop-invocations.v1.${scope}`,
  );
  assert.ok(localStorage.getItem(obsoleteStorageKey));
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    0,
  );
});

test("invocation dispatch fails closed when the shared registry cannot be persisted", async () => {
  const localStorage = new FakeStorage();
  localStorage.setItem = () => {
    throw new Error("Storage quota exceeded.");
  };
  const app = await loadLoopBuilder({ localStorage });
  await selectCustomLoop(app);
  let invocationAttempts = 0;
  app.context.testHub = {
    connected: true,
    invoke: () => {
      invocationAttempts++;
      return Promise.resolve({
        admissionStatus: "Invalid",
        run: null,
        detail: "Should not dispatch.",
      });
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value =
    "Do not dispatch without shared persistence.";
  await app.elements.startRunButton.click();

  assert.equal(invocationAttempts, 0);
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    0,
  );
  assert.match(
    app.elements.invokeError.textContent,
    /could not be coordinated safely across browser tabs.*storage quota exceeded/i,
  );
});

test("a pre-dispatch tab failure releases only its own shared reservation", async () => {
  const localStorage = new FakeStorage();
  const locks = new FakeLockManager();
  const first = await loadLoopBuilder({ localStorage, locks });
  const second = await loadLoopBuilder({ localStorage, locks });
  first.context.crypto.randomUUID = () =>
    "00000000-0000-4000-8000-000000000101";
  second.context.crypto.randomUUID = () =>
    "00000000-0000-4000-8000-000000000202";
  const requestKey = "b".repeat(64);
  const request = {
    loopId: "loop-research",
    expectedDefinitionVersion: 2,
    expectedDefinitionHash: "sha256:test",
    invocationPrompt: "Coordinate this request.",
  };

  const firstReservation = await first.context.reservePendingInvocationRequest(
    requestKey,
    request,
  );
  const secondReservation =
    await second.context.reservePendingInvocationRequest(requestKey, request);
  assert.equal(secondReservation.operationId, firstReservation.operationId);

  await second.context.releasePendingInvocationReservation(
    requestKey,
    secondReservation.operationId,
    secondReservation.reservationId,
  );
  first.context.synchronizePendingInvocationRequestsFromStorage();
  const retained = vm.runInContext(
    "pendingInvocationRequests.values().next().value",
    first.context,
  );

  assert.equal(retained.operationId, firstReservation.operationId);
  assert.deepEqual(
    [...retained.reservationIds],
    [firstReservation.reservationId],
  );
});

test("the 101st same-request reservation is refused without corrupting shared storage", async () => {
  const localStorage = new FakeStorage();
  const locks = new FakeLockManager();
  const first = await loadLoopBuilder({ localStorage, locks });
  const requestKey = "d".repeat(64);
  const request = {
    loopId: "loop-research",
    expectedDefinitionVersion: 2,
    expectedDefinitionHash: "sha256:test",
    invocationPrompt: "Coordinate many browser tabs.",
  };

  for (let index = 0; index < 100; index++) {
    await first.context.reservePendingInvocationRequest(requestKey, request);
  }

  await assert.rejects(
    first.context.reservePendingInvocationRequest(requestKey, request),
    /100 active browser reservations/i,
  );
  const storageKey = vm.runInContext(
    "pendingInvocationStorageKey",
    first.context,
  );
  const stored = JSON.parse(localStorage.getItem(storageKey));
  assert.equal(stored.requests.length, 1);
  assert.equal(stored.requests[0].reservationIds.length, 100);

  const restored = await loadLoopBuilder({ localStorage, locks });
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", restored.context),
    1,
  );
  assert.equal(
    vm.runInContext(
      "pendingInvocationRequests.values().next().value.reservationIds.length",
      restored.context,
    ),
    100,
  );
});

test("concurrent browser tabs coordinate one operation identity for the same request", async () => {
  const localStorage = new FakeStorage();
  const locks = new FakeLockManager();
  const first = await loadLoopBuilder({ localStorage, locks });
  const second = await loadLoopBuilder({ localStorage, locks });
  await selectCustomLoop(first);
  await selectCustomLoop(second);
  const operationIds = [];
  const releases = [];
  for (const app of [first, second]) {
    app.context.testHub = {
      connected: true,
      invoke: (_target, input) => {
        operationIds.push(input.operationId);
        return new Promise((resolve) =>
          releases.push(() =>
            resolve({
              admissionStatus: "Invalid",
              run: null,
              detail: "Definitive response.",
            }),
          ),
        );
      },
    };
    vm.runInContext("hub = testHub", app.context);
    await app.elements.invokeButton.click();
    app.elements.invocationPrompt.value = "Coordinate this shared request.";
  }

  const attempts = [first.context.startRun(), second.context.startRun()];
  for (let attempt = 0; attempt < 20 && operationIds.length < 2; attempt++)
    await new Promise((resolve) => setTimeout(resolve, 5));
  assert.equal(operationIds.length, 2);
  assert.equal(operationIds[1], operationIds[0]);
  for (const release of releases) release();
  await Promise.all(attempts);
});

test("an admitted invocation receipt reconciles against its exact retained tombstone", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  const run = createRunSnapshot();
  run.id = "run-deleted-before-retry";
  run.admissionOperationId = "operation-deleted-before-retry";
  server.traceDetails.set(run.id, createTombstoneTrace(run));
  const requestKey = "c".repeat(64);
  app.context.requestKey = requestKey;
  app.context.pendingRequest = {
    loopId: run.loopId,
    expectedDefinitionVersion: 2,
    expectedDefinitionHash: "sha256:test",
    invocationPrompt: null,
    operationId: run.admissionOperationId,
  };
  vm.runInContext(
    "rememberPendingInvocationRequest(requestKey, pendingRequest)",
    app.context,
  );

  await app.context.applyInvocationReconciliation(
    {
      kind: "admitted",
      receipt: {
        operationId: run.admissionOperationId,
        loopId: run.loopId,
        state: "Complete",
        outcome: "Admitted",
        admissionStatus: "Admitted",
        runId: run.id,
      },
    },
    {
      loopId: run.loopId,
      expectedDefinitionVersion: 2,
      expectedDefinitionHash: "sha256:test",
      invocationPrompt: null,
    },
    requestKey,
    run.admissionOperationId,
  );

  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    0,
  );
  assert.equal(vm.runInContext("selectedRunId", app.context), run.id);
  assert.equal(vm.runInContext("selectedRun", app.context), null);
  assert.equal(vm.runInContext("selectedTrace.isDeleted", app.context), true);
  assert.match(
    app.elements.validationBanner.textContent,
    /durable invocation receipt identified the exact admitted run/i,
  );
});

test("different unresolved invocation requests retain independent operation identities", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  const attempts = [];
  app.context.testHub = {
    connected: true,
    invoke: (_target, input) => {
      attempts.push({
        operationId: input.operationId,
        prompt: input.invocationPrompt,
      });
      server.on(
        "GET",
        `/api/loop-runs/invocations/${input.operationId}`,
        () => ({
          status: 503,
          body: { detail: "Receipt storage is temporarily unavailable." },
        }),
      );
      return Promise.reject(new Error("WebSocket closed."));
    },
  };
  vm.runInContext("hub = testHub", app.context);

  await app.elements.invokeButton.click();
  app.elements.invocationPrompt.value = "First unresolved request.";
  await app.elements.startRunButton.click();

  vm.runInContext("openInvokeModal()", app.context);
  app.elements.invocationPrompt.value = "Second unresolved request.";
  await app.elements.startRunButton.click();

  app.context.testHub.invoke = (_target, input) => {
    attempts.push({
      operationId: input.operationId,
      prompt: input.invocationPrompt,
    });
    return Promise.resolve({
      admissionStatus: "Invalid",
      run: null,
      detail: "The first request now has a definitive response.",
    });
  };
  vm.runInContext("openInvokeModal()", app.context);
  assert.equal(
    app.elements.invocationPrompt.value,
    "Second unresolved request.",
  );
  app.elements.invocationPrompt.value = "First unresolved request.";
  await app.elements.startRunButton.click();

  assert.equal(attempts.length, 3);
  assert.notEqual(attempts[0].operationId, attempts[1].operationId);
  assert.equal(attempts[2].operationId, attempts[0].operationId);
  assert.match(
    app.elements.validationBanner.textContent,
    /first request now has a definitive response/i,
  );
  assert.equal(
    vm.runInContext("pendingInvocationRequests.size", app.context),
    1,
  );
});

test("missing invocation evidence stops at the bounded reconciliation deadline as unknown", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });

  const result = await app.context.reconcileInvocationOperation(
    "invoke-never-visible",
  );

  assert.equal(result.kind, "unknown");
  assert.equal(
    server.calls.filter(
      (call) => call.url === "/api/loop-runs/invocations/invoke-never-visible",
    ).length,
    20,
  );
});

test("a stalled invocation receipt read is aborted at the overall reconciliation deadline", async () => {
  const server = new FakeFetchServer(createCatalog());
  server.on(
    "GET",
    "/api/loop-runs/invocations/invoke-stalled",
    () => new Promise(() => {}),
  );
  const app = await loadLoopBuilder({ server });
  const startedAt = Date.now();

  const result = await app.context.reconcileInvocationOperation(
    "invoke-stalled",
    25,
  );

  assert.equal(result.kind, "unknown");
  assert.ok(Date.now() - startedAt < 500);
  const receiptCalls = server.calls.filter(
    (call) => call.url === "/api/loop-runs/invocations/invoke-stalled",
  );
  assert.equal(receiptCalls.length, 1);
  assert.equal(receiptCalls[0].options.signal.aborted, true);
});

test("invocation reconciliation remains bounded when the wall clock moves backward", async () => {
  let wallClock = 1000;
  class RegressingDate extends Date {
    static now() {
      return wallClock;
    }
  }
  const server = new FakeFetchServer(createCatalog());
  let receiptReads = 0;
  server.on("GET", "/api/loop-runs/invocations/invoke-clock-regression", () => {
    receiptReads++;
    wallClock = 0;
    return receiptReads === 1
      ? { status: 404, body: { detail: "Not visible yet." } }
      : new Promise(() => {});
  });
  const app = await loadLoopBuilder({ server, Date: RegressingDate });
  const startedAt = performance.now();

  const result = await app.context.reconcileInvocationOperation(
    "invoke-clock-regression",
    25,
  );

  assert.equal(result.kind, "unknown");
  assert.ok(performance.now() - startedAt < 500);
});

test("slower run detail responses cannot overwrite a newer run selection", async () => {
  const server = new FakeFetchServer(createCatalog());
  const runA = createRunSnapshot();
  runA.id = "run-a";
  const runB = createRunSnapshot();
  runB.id = "run-b";
  server.runs = [runA, runB].map((run) => ({
    id: run.id,
    loopId: run.loopId,
    admissionOperationId: run.admissionOperationId,
    definitionVersion: 2,
    status: run.status,
    createdAtUtc: run.createdAtUtc,
    updatedAtUtc: run.updatedAtUtc,
    completedAtUtc: run.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  }));
  server.runDetails.set(runA.id, runA);
  server.runDetails.set(runB.id, runB);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  let releaseRunA;
  const runAReleased = new Promise((resolve) => {
    releaseRunA = resolve;
  });
  server.on("GET", "/api/loop-runs/run-a", async () => {
    await runAReleased;
    return { status: 200, body: clone(runA) };
  });
  const runAButton = app.elements.runList.children.find((item) =>
    item.textContent.includes("run-a"),
  );
  const runBButton = app.elements.runList.children.find((item) =>
    item.textContent.includes("run-b"),
  );

  const selectingRunA = runAButton.click();
  await Promise.resolve();
  await runBButton.click();
  releaseRunA();
  await selectingRunA;

  assert.match(app.elements.runTitle.textContent, /run-b/);
  assert.match(app.elements.inspectorContent.textContent, /run run-b/);
  assert.equal(
    app.elements.runList.children
      .find((item) => item.className.includes("selected"))
      .textContent.includes("run-b"),
    true,
  );
});

test("invocation hydration cannot overwrite a newer run selection", async () => {
  const server = new FakeFetchServer(createCatalog());
  const invocationRun = createRunSnapshot();
  invocationRun.id = "run-invocation-hydration";
  invocationRun.admissionOperationId = "operation-invocation-hydration";
  const newerRun = createRunSnapshot();
  newerRun.id = "run-selected-while-hydrating";
  newerRun.admissionOperationId = "operation-selected-while-hydrating";
  server.runs = [invocationRun, newerRun].map((run) => ({
    id: run.id,
    loopId: run.loopId,
    admissionOperationId: run.admissionOperationId,
    definitionVersion: 2,
    status: run.status,
    createdAtUtc: run.createdAtUtc,
    updatedAtUtc: run.updatedAtUtc,
    completedAtUtc: run.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  }));
  server.runDetails.set(invocationRun.id, invocationRun);
  server.runDetails.set(newerRun.id, newerRun);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  let releaseQuota;
  const quotaReleased = new Promise((resolve) => {
    releaseQuota = resolve;
  });
  server.on("GET", "/api/loop-runs/quota", async () => {
    await quotaReleased;
    return { status: 200, body: createTraceQuota(2) };
  });

  const hydration = app.context.selectExactInvocationRun(
    invocationRun,
    invocationRun.loopId,
    invocationRun.admissionOperationId,
  );
  await Promise.resolve();
  await app.context.selectRun(newerRun.id);
  releaseQuota();
  await hydration;

  assert.equal(vm.runInContext("selectedRunId", app.context), newerRun.id);
  assert.equal(vm.runInContext("selectedRun.id", app.context), newerRun.id);
});

test("opening an existing nonterminal run keeps refreshing independently of its original invoker", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  const refresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  assert.ok(
    refresh,
    "expected a recurring refresh for the selected nonterminal run",
  );
  run.status = "Completed";
  run.completedAtUtc = "2026-07-16T12:00:03Z";
  server.runs[0].status = "Completed";
  server.runs[0].completedAtUtc = run.completedAtUtc;
  server.runDetails.set(run.id, run);
  refresh.cancelled = true;
  await refresh.handler();

  assert.match(app.elements.runSubtitle.textContent, /Completed/);
  assert.equal(
    app.window.delayedHandlers.filter(
      (item) => item.delay === 1000 && !item.cancelled,
    ).length,
    0,
  );
});

test("run monitoring stops while Loops is hidden and resumes when it returns", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  assert.equal(
    app.window.delayedHandlers.filter(
      (item) => item.delay === 1000 && !item.cancelled,
    ).length,
    1,
  );
  app.window.embodySenseLoopBuilder.deactivate();
  assert.equal(
    app.window.delayedHandlers.filter(
      (item) => item.delay === 1000 && !item.cancelled,
    ).length,
    0,
  );

  await app.window.embodySenseLoopBuilder.activate();
  assert.equal(
    app.window.delayedHandlers.filter(
      (item) => item.delay === 1000 && !item.cancelled,
    ).length,
    1,
  );
});

test("an unauthorized run monitor suspends polling and delegates one shared recovery", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      lifecycleVersion: run.lifecycleVersion,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  server.on("GET", `/api/loop-runs/${run.id}/monitor`, () => ({
    status: 401,
    body: { detail: "The host restarted." },
  }));
  let recoveries = 0;
  const sharedHub = { connected: true, on() {} };
  const app = await loadLoopBuilder({
    server,
    embodySenseSession: {
      getHub: async () => sharedHub,
      recover() {
        recoveries++;
      },
    },
  });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  const refresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  assert.ok(refresh);

  refresh.cancelled = true;
  await refresh.handler();

  assert.equal(recoveries, 1);
  assert.equal(
    app.window.delayedHandlers.filter(
      (item) => item.delay === 1000 && !item.cancelled,
    ).length,
    0,
  );
});

test("recovery rehydration propagates a 401 without recursively starting recovery", async () => {
  const server = new FakeFetchServer(createCatalog());
  const app = await loadLoopBuilder({ server });
  let recoveries = 0;
  app.window.embodySenseSession = {
    recover() {
      recoveries++;
    },
  };
  server.on("GET", "/api/status", () => ({
    status: 401,
    body: { detail: "The host restarted during rehydration." },
  }));

  await assert.rejects(
    app.window.embodySenseLoopBuilder.rehydrateSession({
      approvals: [],
      workspaceRoot: "C:/workspace",
    }),
    (error) => error.status === 401,
  );

  assert.equal(recoveries, 0);
});

test("rapidly leaving and returning during an in-flight run poll keeps one monitoring chain", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  let monitorStarted;
  let releaseMonitor;
  const monitorPending = new Promise((resolve) => {
    monitorStarted = resolve;
  });
  const monitorReleased = new Promise((resolve) => {
    releaseMonitor = resolve;
  });
  server.on("GET", `/api/loop-runs/${run.id}/monitor`, async () => {
    monitorStarted();
    await monitorReleased;
    return { status: 200, body: server.runs[0] };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  const refresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  refresh.cancelled = true;
  const inFlightRefresh = refresh.handler();
  await monitorPending;
  app.window.embodySenseLoopBuilder.deactivate();
  await app.window.embodySenseLoopBuilder.activate();
  assert.equal(
    app.window.delayedHandlers.filter(
      (item) => item.delay === 1000 && !item.cancelled,
    ).length,
    0,
  );

  releaseMonitor();
  await inFlightRefresh;
  assert.equal(
    app.window.delayedHandlers.filter(
      (item) => item.delay === 1000 && !item.cancelled,
    ).length,
    1,
  );
});

test("selecting another active run during an in-flight poll transfers the monitoring chain", async () => {
  const server = new FakeFetchServer(createCatalog());
  const firstRun = createRunSnapshot();
  firstRun.status = "Running";
  firstRun.completedAtUtc = null;
  const secondRun = createRunSnapshot();
  secondRun.id = "run-second-active";
  secondRun.admissionOperationId = "op-second-active";
  secondRun.status = "Running";
  secondRun.completedAtUtc = null;
  secondRun.createdAtUtc = "2026-07-20T11:59:00Z";
  secondRun.updatedAtUtc = "2026-07-20T11:59:02Z";
  server.runs = [firstRun, secondRun].map((run) => ({
    id: run.id,
    loopId: run.loopId,
    admissionOperationId: run.admissionOperationId,
    definitionVersion: 2,
    status: run.status,
    createdAtUtc: run.createdAtUtc,
    updatedAtUtc: run.updatedAtUtc,
    completedAtUtc: null,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  }));
  server.runDetails.set(firstRun.id, firstRun);
  server.runDetails.set(secondRun.id, secondRun);
  let monitorStarted;
  let releaseMonitor;
  const monitorPending = new Promise((resolve) => {
    monitorStarted = resolve;
  });
  const monitorReleased = new Promise((resolve) => {
    releaseMonitor = resolve;
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  const polledRunId = vm.runInContext("selectedRun.id", app.context);
  const alternateRun = polledRunId === firstRun.id ? secondRun : firstRun;
  server.on("GET", `/api/loop-runs/${polledRunId}/monitor`, async () => {
    monitorStarted();
    await monitorReleased;
    return {
      status: 200,
      body: server.runs.find((run) => run.id === polledRunId),
    };
  });

  const firstRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  firstRefresh.cancelled = true;
  const inFlightRefresh = firstRefresh.handler();
  await monitorPending;
  const secondRunButton = app.elements.runList.children.find((item) =>
    item.textContent.includes(alternateRun.id),
  );
  await secondRunButton.click();
  assert.equal(
    app.window.delayedHandlers.filter(
      (item) => item.delay === 1000 && !item.cancelled,
    ).length,
    0,
  );

  releaseMonitor();
  await inFlightRefresh;
  const transferredRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  assert.ok(transferredRefresh);
  transferredRefresh.cancelled = true;
  await transferredRefresh.handler();
  assert.equal(
    server.calls.some(
      (call) => call.url === `/api/loop-runs/${alternateRun.id}/monitor`,
    ),
    true,
  );
});

test("long active-run approval waits use conditional monitor reads without reloading full evidence", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  const bindingRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  assert.ok(bindingRefresh, "expected an initial validator-binding refresh");
  bindingRefresh.cancelled = true;
  await bindingRefresh.handler();
  const expensiveReadsBefore = server.calls.filter(
    (call) => call.url !== `/api/loop-runs/${run.id}/monitor`,
  ).length;

  for (let poll = 0; poll < 6; poll++) {
    const refresh = app.window.delayedHandlers.find(
      (item) => item.delay === 1000 && !item.cancelled,
    );
    assert.ok(refresh, `expected monitor poll ${poll + 1}`);
    refresh.cancelled = true;
    await refresh.handler();
  }

  const monitorCalls = server.calls.filter(
    (call) => call.url === `/api/loop-runs/${run.id}/monitor`,
  );
  assert.equal(monitorCalls.length, 7);
  assert.ok(
    monitorCalls
      .slice(1)
      .every((call) => call.options.headers["If-None-Match"]),
  );
  assert.equal(
    server.calls.filter(
      (call) => call.url !== `/api/loop-runs/${run.id}/monitor`,
    ).length,
    expensiveReadsBefore,
  );
  assert.match(app.elements.runSubtitle.textContent, /Running/);
});

test("a changed artifact-bound validator reloads full evidence even when the summary is unchanged", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      lifecycleVersion: run.lifecycleVersion,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  const bindingRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  bindingRefresh.cancelled = true;
  await bindingRefresh.handler();

  run.events = run.events.map((event, index) =>
    index === 0
      ? { ...event, detail: "Artifact-only event detail changed." }
      : event,
  );
  server.runDetails.set(run.id, run);
  server.on("GET", `/api/loop-runs/${run.id}/monitor`, () => ({
    status: 200,
    body: clone(server.runs[0]),
    headers: { ETag: '"artifact-only-change"' },
  }));
  const changedRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  changedRefresh.cancelled = true;
  await changedRefresh.handler();

  assert.match(
    app.elements.runTimeline.textContent,
    /Artifact-only event detail changed/,
  );
  assert.equal(
    vm.runInContext("selectedRunMonitorEtag", app.context),
    '"artifact-only-change"',
  );
});

test("scheduled monitoring falls back to full evidence with backoff when the endpoint is unavailable", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      lifecycleVersion: run.lifecycleVersion,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  server.on("GET", `/api/loop-runs/${run.id}/monitor`, () => ({
    status: 503,
    body: { detail: "Monitor watcher unavailable." },
  }));
  const listCallsBefore = server.calls.filter(
    (call) => call.url === "/api/loop-runs?maximumCount=50",
  ).length;

  const firstRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  firstRefresh.cancelled = true;
  await firstRefresh.handler();
  const listCallsAfterFallback = server.calls.filter(
    (call) => call.url === "/api/loop-runs?maximumCount=50",
  ).length;
  const backoffRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  backoffRefresh.cancelled = true;
  await backoffRefresh.handler();

  assert.equal(listCallsAfterFallback, listCallsBefore + 1);
  assert.equal(
    server.calls.filter((call) => call.url === "/api/loop-runs?maximumCount=50")
      .length,
    listCallsAfterFallback,
  );
  assert.equal(
    vm.runInContext("selectedRunMonitorFallbackFailureCount", app.context),
    1,
  );
  assert.match(app.elements.runTitle.textContent, new RegExp(run.id));
});

test("in-flight monitoring backs off full evidence fallback when the endpoint is unavailable", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      lifecycleVersion: run.lifecycleVersion,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  server.on("GET", `/api/loop-runs/${run.id}/monitor`, () => ({
    status: 503,
    body: { detail: "Monitor watcher unavailable." },
  }));
  const listCallsBefore = server.calls.filter(
    (call) => call.url === "/api/loop-runs?maximumCount=50",
  ).length;
  let completeInvocation;
  const invocation = new Promise((resolve) => {
    completeInvocation = resolve;
  });
  let delayCount = 0;
  app.context.setTimeout = (handler) => {
    delayCount++;
    if (delayCount === 2) completeInvocation({});
    queueMicrotask(handler);
  };

  await app.context.waitForRunOperation(invocation, { preferredRunId: run.id });

  assert.equal(
    server.calls.filter(
      (call) => call.url === `/api/loop-runs/${run.id}/monitor`,
    ).length,
    2,
  );
  assert.equal(
    server.calls.filter((call) => call.url === "/api/loop-runs?maximumCount=50")
      .length,
    listCallsBefore + 1,
  );
  assert.equal(
    vm.runInContext("selectedRunMonitorFallbackFailureCount", app.context),
    1,
  );
});

test("a lifecycle-only monitor change invalidates cached full evidence", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      lifecycleVersion: run.lifecycleVersion,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  run.lifecycleVersion++;
  server.runs[0].lifecycleVersion = run.lifecycleVersion;
  server.runDetails.set(run.id, run);
  const refresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  refresh.cancelled = true;
  await refresh.handler();

  assert.match(
    app.elements.inspectorContent.textContent,
    /lifecycle version 5/,
  );
  assert.ok(
    server.calls.some((call) => call.url === `/api/loop-runs/${run.id}`),
  );
});

test("a definition-version-only monitor change invalidates cached full evidence", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      lifecycleVersion: run.lifecycleVersion,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  run.admittedDefinition = { ...run.admittedDefinition, definitionVersion: 3 };
  server.runs[0].definitionVersion = 3;
  server.runDetails.set(run.id, run);
  const refresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  refresh.cancelled = true;
  await refresh.handler();

  assert.match(app.elements.runSubtitle.textContent, /v3/);
  assert.ok(
    server.calls.some((call) => call.url === `/api/loop-runs/${run.id}`),
  );
});

test("a changed monitor validator is retained only after full evidence refresh succeeds", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      lifecycleVersion: run.lifecycleVersion,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  run.lifecycleVersion++;
  run.status = "Completed";
  run.completedAtUtc = "2026-07-16T12:00:03Z";
  server.runs[0] = {
    ...server.runs[0],
    lifecycleVersion: run.lifecycleVersion,
    status: run.status,
    completedAtUtc: run.completedAtUtc,
  };
  server.runDetails.set(run.id, run);
  server.on("GET", `/api/loop-runs/${run.id}/monitor`, () => ({
    status: 200,
    body: clone(server.runs[0]),
    headers: { ETag: '"completed"' },
  }));
  let listAttempts = 0;
  server.on("GET", "/api/loop-runs?maximumCount=50", () => {
    listAttempts++;
    return listAttempts === 1
      ? { status: 503, body: { detail: "Temporary page failure." } }
      : {
          status: 200,
          body: { items: clone(server.runs), continuationCursor: null },
        };
  });

  const firstRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  firstRefresh.cancelled = true;
  await firstRefresh.handler();
  const retry = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  retry.cancelled = true;
  await retry.handler();

  const monitorCalls = server.calls.filter(
    (call) => call.url === `/api/loop-runs/${run.id}/monitor`,
  );
  assert.equal(monitorCalls.length, 2);
  assert.equal(monitorCalls[1].options.headers["If-None-Match"], undefined);
  assert.equal(listAttempts, 2);
  assert.match(app.elements.runSubtitle.textContent, /Completed/);
});

test("two consecutive monitor misses clear stale active evidence and stop polling", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      lifecycleVersion: run.lifecycleVersion,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  server.runs = [];
  server.runDetails.delete(run.id);

  const firstRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  firstRefresh.cancelled = true;
  await firstRefresh.handler();
  assert.match(app.elements.runTitle.textContent, new RegExp(run.id));

  const confirmingRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  confirmingRefresh.cancelled = true;
  await confirmingRefresh.handler();

  assert.equal(app.elements.runTitle.textContent, "No run selected");
  assert.match(
    app.elements.validationBanner.textContent,
    /Run evidence unavailable/,
  );
  assert.equal(
    app.window.delayedHandlers.filter(
      (item) => item.delay === 1000 && !item.cancelled,
    ).length,
    0,
  );
});

test("a transient monitor miss keeps the active run selected and resumes polling", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      lifecycleVersion: run.lifecycleVersion,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  let monitorAttempts = 0;
  server.on("GET", `/api/loop-runs/${run.id}/monitor`, () => {
    monitorAttempts++;
    return monitorAttempts === 1
      ? {
          status: 404,
          body: { detail: "Run artifact is temporarily unavailable." },
        }
      : {
          status: 200,
          body: clone(server.runs[0]),
          headers: { ETag: '"restored"' },
        };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  const missingRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  missingRefresh.cancelled = true;
  await missingRefresh.handler();
  assert.match(app.elements.runTitle.textContent, new RegExp(run.id));

  const restoredRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  restoredRefresh.cancelled = true;
  await restoredRefresh.handler();

  assert.equal(monitorAttempts, 2);
  assert.match(app.elements.runTitle.textContent, new RegExp(run.id));
  assert.equal(
    app.window.delayedHandlers.filter(
      (item) => item.delay === 1000 && !item.cancelled,
    ).length,
    1,
  );
});

test("a successful full evidence fallback resets a prior monitor miss", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      lifecycleVersion: run.lifecycleVersion,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  server.on("GET", `/api/loop-runs/${run.id}/monitor`, () => ({
    status: 404,
    body: { detail: "Run artifact is temporarily unavailable." },
  }));
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  assert.equal(await app.context.refreshSelectedRunFromMonitor(run.id), false);
  assert.equal(
    await app.context.loadRuns({ silent: true, preferredRunId: run.id }),
    true,
  );
  assert.equal(await app.context.refreshSelectedRunFromMonitor(run.id), false);

  assert.match(app.elements.runTitle.textContent, new RegExp(run.id));
  assert.equal(vm.runInContext("selectedRunMonitorMissCount", app.context), 1);
});

test("a transient live refresh failure schedules another poll and recovers", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: null,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  let monitorAttempts = 0;
  server.on("GET", `/api/loop-runs/${run.id}/monitor`, () => {
    monitorAttempts++;
    return monitorAttempts === 1
      ? { status: 503, body: { detail: "Temporary run-list failure." } }
      : {
          status: 200,
          body: clone(server.runs[0]),
          headers: { ETag: '"completed"' },
        };
  });

  const failedRefresh = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  assert.ok(failedRefresh, "expected the first live refresh");
  failedRefresh.cancelled = true;
  await failedRefresh.handler();

  const retry = app.window.delayedHandlers.find(
    (item) => item.delay === 1000 && !item.cancelled,
  );
  assert.ok(retry, "expected polling to retry after a transient failure");
  run.status = "Completed";
  run.completedAtUtc = "2026-07-16T12:00:03Z";
  server.runs[0].status = "Completed";
  server.runs[0].completedAtUtc = run.completedAtUtc;
  server.runDetails.set(run.id, run);
  retry.cancelled = true;
  await retry.handler();

  assert.equal(monitorAttempts, 2);
  assert.match(app.elements.runSubtitle.textContent, /Completed/);
  assert.equal(
    app.window.delayedHandlers.filter(
      (item) => item.delay === 1000 && !item.cancelled,
    ).length,
    0,
  );
});

test("deleted loop definitions retain a selectable archived run-history surface", async () => {
  const catalog = createCatalog();
  catalog.customDefinitions = [];
  const server = new FakeFetchServer(catalog);
  const run = createRunSnapshot();
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      definitionVersion: 2,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: run.completedAtUtc,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
      isDeleted: false,
    },
  ];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });

  const archived = app.elements.loopList.children.find((child) =>
    child.textContent.includes("Deleted loop · loop-research"),
  );
  assert.ok(archived);
  await archived.click();
  await flushAsyncWork();

  assert.equal(app.elements.builderTab.disabled, true);
  assert.match(app.elements.saveState.textContent, /Archived evidence/);
  assert.match(app.elements.runTitle.textContent, /run-test/);
  assert.match(app.elements.inspectorContent.textContent, /Research pass v2/);
});

test("terminal trace deletion reuses its operation after response loss and leaves an inspectable tombstone", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      definitionVersion: 2,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: run.completedAtUtc,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
    },
  ];
  server.runDetails.set(run.id, run);
  const liveTrace = createTraceSnapshot(run);
  server.traceDetails.set(run.id, liveTrace);
  const deletionOperationIds = [];
  server.on("POST", `/api/loop-runs/${run.id}/trace/delete`, ({ body }) => {
    assert.equal(body.expectedTraceHash, liveTrace.persistedArtifactHash);
    assert.equal(typeof body.operationId, "string");
    assert.equal(Object.hasOwn(body, "actor"), false);
    deletionOperationIds.push(body.operationId);
    const tombstone = {
      runId: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      terminalStatus: run.status,
      definitionVersion: 2,
      definitionHash: run.admittedDefinition.contentHash,
      originalTraceHash: liveTrace.persistedArtifactHash,
      originalTraceUtf8Bytes: liveTrace.persistedArtifactUtf8Bytes,
      createdAtUtc: run.createdAtUtc,
      completedAtUtc: run.completedAtUtc,
      deletedAtUtc: "2026-07-16T12:05:00Z",
      deletionActor: "embodysense.web",
      deletionSurface: "web",
      deletionOperationId: body.operationId,
      intentAuditCorrelationId: "trace-delete-intent-test",
      outcomeAuditCorrelationId: "trace-delete-outcome-test",
      outcomeIntegrity: "Complete",
    };
    server.traceDetails.set(run.id, {
      ...liveTrace,
      kind: "Tombstone",
      persistedArtifactUtf8Bytes: 1024,
      isDeleted: true,
      tombstone,
    });
    server.runs = [
      {
        id: run.id,
        loopId: run.loopId,
        admissionOperationId: run.admissionOperationId,
        definitionVersion: 2,
        status: run.status,
        createdAtUtc: run.createdAtUtc,
        updatedAtUtc: tombstone.deletedAtUtc,
        completedAtUtc: run.completedAtUtc,
        iteration: 0,
        nextStepIndex: 0,
        failureCode: null,
        isDeleted: true,
      },
    ];
    server.runDetails.delete(run.id);
    server.traceQuota = createTraceQuota(0, 1, 1024);
    if (deletionOperationIds.length === 1)
      throw new Error("Connection lost after deletion committed.");
    return {
      status: 200,
      body: {
        status: "Replayed",
        isCommitted: true,
        detail: "Deleted.",
        tombstone,
      },
    };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  await flushAsyncWork();

  const deleteButton = app.elements.runActions.children.find(
    (child) => child.textContent === "Delete sensitive trace",
  );
  assert.ok(deleteButton);
  await deleteButton.click();
  await flushAsyncWork();
  assert.match(
    app.elements.validationBanner.textContent,
    /Trace deletion failed: Connection lost after deletion committed/,
  );
  await deleteButton.click();
  await flushAsyncWork();

  assert.equal(deletionOperationIds.length, 2);
  assert.equal(deletionOperationIds[0], deletionOperationIds[1]);
  assert.match(
    app.window.confirmations.at(-1),
    /Permanently delete the sensitive trace content/,
  );
  assert.match(
    app.window.confirmations.at(-1),
    /small audited tombstone will remain/,
  );
  assert.match(app.elements.runTitle.textContent, /Deleted trace run-test/);
  assert.match(
    app.elements.runNotice.textContent,
    /Sensitive prompt, context, output, and tool evidence were explicitly deleted/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /Audited trace tombstone/,
  );
  assert.match(
    app.elements.inspectorContent.textContent,
    /trace-delete-intent-test/,
  );
  assert.match(app.elements.traceQuota.textContent, /0\/250 live/);
  assert.match(app.elements.toast.textContent, /audited tombstone remains/);
  const callsAfterDeletion = server.calls.slice(
    server.calls.findIndex(
      (call) => call.method === "POST" && call.url.endsWith("/trace/delete"),
    ) + 1,
  );
  assert.equal(
    callsAfterDeletion.some(
      (call) =>
        call.method === "GET" && call.url === `/api/loop-runs/${run.id}`,
    ),
    false,
  );
});

test("terminal trace deletion rotates its operation after a durable audit-unavailable rejection", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      definitionVersion: 2,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: run.completedAtUtc,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
    },
  ];
  server.runDetails.set(run.id, run);
  server.traceDetails.set(run.id, createTraceSnapshot(run));
  const operationIds = [];
  server.on("POST", `/api/loop-runs/${run.id}/trace/delete`, ({ body }) => {
    operationIds.push(body.operationId);
    return {
      status: 503,
      body: {
        status: "AuditUnavailable",
        isCommitted: false,
        isOutcomeCommitted: true,
        detail: "The intent audit was unavailable.",
        tombstone: null,
      },
    };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  await flushAsyncWork();

  const deleteButton = app.elements.runActions.children.find(
    (child) => child.textContent === "Delete sensitive trace",
  );
  await deleteButton.click();
  await deleteButton.click();

  assert.equal(operationIds.length, 2);
  assert.notEqual(operationIds[0], operationIds[1]);
  assert.match(
    app.elements.validationBanner.textContent,
    /intent audit was unavailable/,
  );
});

test("terminal trace deletion preserves its operation while audit rejection remains ambiguous", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  server.runs = [
    {
      id: run.id,
      loopId: run.loopId,
      definitionVersion: 2,
      status: run.status,
      createdAtUtc: run.createdAtUtc,
      updatedAtUtc: run.updatedAtUtc,
      completedAtUtc: run.completedAtUtc,
      iteration: 1,
      nextStepIndex: 1,
      failureCode: null,
    },
  ];
  server.runDetails.set(run.id, run);
  server.traceDetails.set(run.id, createTraceSnapshot(run));
  const operationIds = [];
  server.on("POST", `/api/loop-runs/${run.id}/trace/delete`, ({ body }) => {
    operationIds.push(body.operationId);
    return {
      status: 503,
      body: {
        status: "AuditUnavailable",
        isCommitted: false,
        isOutcomeCommitted: false,
        detail: "The durable rejection requires recovery.",
        tombstone: null,
      },
    };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  await flushAsyncWork();

  const deleteButton = app.elements.runActions.children.find(
    (child) => child.textContent === "Delete sensitive trace",
  );
  await deleteButton.click();
  await deleteButton.click();

  assert.equal(operationIds.length, 2);
  assert.equal(operationIds[0], operationIds[1]);
  assert.match(app.elements.validationBanner.textContent, /requires recovery/);
});

test("trace deletion completion preserves a newer run selection while caching the tombstone", async () => {
  const server = new FakeFetchServer(createCatalog());
  const runA = createRunSnapshot();
  runA.id = "run-a";
  const runB = createRunSnapshot();
  runB.id = "run-b";
  server.runs = [runA, runB].map((run) => ({
    id: run.id,
    loopId: run.loopId,
    admissionOperationId: run.admissionOperationId,
    definitionVersion: 2,
    status: run.status,
    createdAtUtc: run.createdAtUtc,
    updatedAtUtc: run.updatedAtUtc,
    completedAtUtc: run.completedAtUtc,
    iteration: 1,
    nextStepIndex: 1,
    failureCode: null,
    isDeleted: false,
  }));
  server.runDetails.set(runA.id, runA);
  server.runDetails.set(runB.id, runB);
  server.traceDetails.set(runA.id, createTraceSnapshot(runA));
  let releaseDeletion;
  const deletionReleased = new Promise((resolve) => {
    releaseDeletion = resolve;
  });
  server.on(
    "POST",
    `/api/loop-runs/${runA.id}/trace/delete`,
    async ({ body }) => {
      await deletionReleased;
      const tombstone = {
        runId: runA.id,
        loopId: runA.loopId,
        admissionOperationId: runA.admissionOperationId,
        terminalStatus: runA.status,
        definitionVersion: 2,
        definitionHash: runA.admittedDefinition.contentHash,
        originalTraceHash: createTraceSnapshot(runA).persistedArtifactHash,
        originalTraceUtf8Bytes:
          createTraceSnapshot(runA).persistedArtifactUtf8Bytes,
        createdAtUtc: runA.createdAtUtc,
        completedAtUtc: runA.completedAtUtc,
        deletedAtUtc: "2026-07-16T12:05:00Z",
        deletionActor: "embodysense.web",
        deletionSurface: "web",
        deletionOperationId: body.operationId,
        intentAuditCorrelationId: "trace-delete-intent-race",
        outcomeAuditCorrelationId: "trace-delete-outcome-race",
        outcomeIntegrity: "Complete",
      };
      server.traceQuota = createTraceQuota(1, 1, 17408);
      return {
        status: 200,
        body: {
          status: "Deleted",
          isCommitted: true,
          detail: "Deleted.",
          tombstone,
        },
      };
    },
  );
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  const runAButton = app.elements.runList.children.find((item) =>
    item.textContent.includes("run-a"),
  );
  const runBButton = app.elements.runList.children.find((item) =>
    item.textContent.includes("run-b"),
  );
  await runAButton.click();
  const deleteButton = app.elements.runActions.children.find(
    (child) => child.textContent === "Delete sensitive trace",
  );

  const deletingRunA = deleteButton.click();
  await Promise.resolve();
  await runBButton.click();
  releaseDeletion();
  await deletingRunA;

  assert.match(app.elements.runTitle.textContent, /run-b/);
  assert.equal(
    app.elements.runList.children
      .find((item) => item.className.includes("selected"))
      .textContent.includes("run-b"),
    true,
  );
  assert.match(
    app.elements.runList.children.find((item) =>
      item.textContent.includes("run-a"),
    ).textContent,
    /trace deleted/i,
  );
  assert.match(app.elements.traceQuota.textContent, /1\/250 live/);
});

test("Run confirmation exposes real governed limits and never reintroduces fixed context", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);

  await app.elements.invokeButton.click();

  assert.match(app.elements.invokeModal.className, /open/);
  assert.match(app.elements.invokeSummary.textContent, /Research pass v2/);
  assert.match(
    app.elements.invokeSummary.textContent,
    /OpenAiCodex · gpt-5-test/,
  );
  assert.match(app.elements.invokeSummary.textContent, /initial user prompt/);
  assert.match(app.elements.invokeLimits.textContent, /65 model attempts/);
  assert.match(
    app.elements.invokeLimits.textContent,
    /5 governed tool requests per attempt/,
  );
  assert.match(app.elements.invokeLimits.textContent, /30 per run/);
  assert.match(
    app.elements.invokeLimits.textContent,
    /within 30m of accumulated execution time/,
  );
  assert.match(
    app.elements.invokeLimits.textContent,
    /canonical model output is capped at 8,000 characters/,
  );
  assert.match(
    app.elements.invokeLimits.textContent,
    /24,000 characters across 384 selected messages/,
  );
  assert.match(
    app.elements.invokeLimits.textContent,
    /targets are capped at 1,024 characters, arguments at 1,024/,
  );
  assert.match(
    app.elements.invokeLimits.textContent,
    /formatted result returned to the model at 64,000 characters/,
  );
  assert.match(
    app.elements.invokeLimits.textContent,
    /768 events, including 64 lifecycle\/control events, and 16\.0 MiB/,
  );
  assert.match(app.elements.invokeLimits.textContent, /list, read, search/);
  assert.equal(app.elements.invocationPromptField.hidden, false);
  assert.doesNotMatch(
    `${app.elements.invokeSummary.textContent}\n${app.elements.invokeLimits.textContent}`,
    /additional fixed context/i,
  );
});

test("owned run approvals expose resolved governance evidence in the loop builder", async () => {
  const app = await loadLoopBuilder();

  app.context.renderLoopApprovals([
    {
      requestId: "approval-test",
      command: "workspace",
      operation: "read",
      targetPath: "docs/issue.md",
      resolvedPath: "C:/workspace/docs/issue.md",
      matchedPath: "C:/workspace",
      reason: "The active loop requires governed workspace read access.",
    },
  ]);

  assert.equal(app.elements.approvalPanel.hidden, false);
  assert.equal(app.elements.approvalCount.textContent, "1 pending");
  assert.match(app.elements.approvals.textContent, /workspace read/i);
  assert.match(app.elements.approvals.textContent, /target docs\/issue\.md/);
  assert.match(
    app.elements.approvals.textContent,
    /resolved C:\/workspace\/docs\/issue\.md/,
  );
  assert.match(
    app.elements.approvals.textContent,
    /matched permission C:\/workspace/,
  );
  assert.match(
    app.elements.approvals.textContent,
    /governed workspace read access/,
  );
  assert.deepEqual(
    findByTag(app.elements.approvals, "button").map(
      (button) => button.textContent,
    ),
    ["Reject", "Approve"],
  );

  app.context.renderLoopApprovals([]);
  assert.equal(app.elements.approvalPanel.hidden, true);
  assert.equal(app.elements.approvalCount.textContent, "0 pending");
  assert.equal(app.elements.approvals.children.length, 0);
});

async function loadLoopBuilder(options = {}) {
  const document = new FakeDocument(loopsHtml);
  document.elementsObject.loopsView.hidden = options.loopsViewHidden ?? false;
  const server =
    options.server ?? new FakeFetchServer(options.catalog ?? createCatalog());
  const sessionStorage = options.sessionStorage ?? new FakeStorage();
  const localStorage = options.localStorage ?? new FakeStorage();
  const locks = options.locks ?? new FakeLockManager();
  let operation = 0;
  const eventListeners = new Map();
  const window = {
    confirmations: [],
    delayedHandlers: [],
    eventListeners,
    intervalHandlers: [],
    location: { href: "http://127.0.0.1:4378/loops.html" },
    localStorage,
    sessionStorage,
    embodySenseSession: options.embodySenseSession,
    addEventListener(name, handler) {
      this.eventListeners.set(name, handler);
    },
    confirm(message) {
      this.confirmations.push(message);
      return true;
    },
    setTimeout(handler, delay) {
      if (delay > 100) {
        const scheduled = { handler, delay, cancelled: false };
        this.delayedHandlers.push(scheduled);
        return scheduled;
      }
      return setTimeout(handler, delay);
    },
    clearTimeout(handle) {
      if (
        handle &&
        typeof handle === "object" &&
        Object.hasOwn(handle, "cancelled")
      )
        handle.cancelled = true;
      else clearTimeout(handle);
    },
    setInterval(handler, delay) {
      const scheduled = { handler, delay, cancelled: false };
      this.intervalHandlers.push(scheduled);
      return scheduled;
    },
    clearInterval(handle) {
      handle.cancelled = true;
    },
  };
  const context = {
    AbortController,
    console,
    crypto: options.crypto ?? {
      subtle: webcrypto.subtle,
      randomUUID: () =>
        `00000000-0000-4000-8000-${String(++operation).padStart(12, "0")}`,
    },
    Date: options.Date ?? Date,
    document,
    fetch: server.fetch.bind(server),
    navigator: { locks },
    performance,
    setTimeout,
    clearTimeout,
    structuredClone,
    TextEncoder,
    window,
  };
  context.globalThis = context;
  vm.runInNewContext(builderSource, context, { filename: "loop-builder.js" });
  await flushAsyncWork();
  document.elementsObject.approvalPanel =
    document.elementsObject.loopApprovalPanel;
  document.elementsObject.approvalCount =
    document.elementsObject.loopApprovalCount;
  document.elementsObject.approvals = document.elementsObject.loopApprovals;
  return {
    context,
    document,
    elements: document.elementsObject,
    server,
    window,
  };
}

class FakeStorage {
  constructor() {
    this.values = new Map();
  }

  getItem(key) {
    return this.values.has(key) ? this.values.get(key) : null;
  }

  removeItem(key) {
    this.values.delete(key);
  }

  setItem(key, value) {
    this.values.set(key, String(value));
  }
}

class FakeLockManager {
  constructor() {
    this.tails = new Map();
  }

  async request(name, _options, callback) {
    const previous = this.tails.get(name) ?? Promise.resolve();
    let release;
    const current = new Promise((resolve) => {
      release = resolve;
    });
    const tail = previous.then(() => current);
    this.tails.set(name, tail);
    await previous;
    try {
      return await callback({ name, mode: "exclusive" });
    } finally {
      release();
      if (this.tails.get(name) === tail) this.tails.delete(name);
    }
  }
}

async function selectCustomLoop(app) {
  const item = app.elements.loopList.children.find(
    (child) =>
      child.textContent.includes("Research pass") ||
      child.textContent.includes("Untitled loop") ||
      child.textContent.includes("<script>"),
  );
  assert.ok(item, "expected a custom loop catalog item");
  await item.click();
  await flushAsyncWork();
}

function nodeCard(app, className) {
  const card = findByClass(app.elements.loopCanvas, className).find((element) =>
    element.className.split(/\s+/).includes("node-card"),
  );
  assert.ok(card, `expected ${className} node card`);
  return card;
}

function findControlByLabel(root, labelText, tagName) {
  const label = findByTag(root, "label").find((item) =>
    item.children.some(
      (child) => child.tagName === "SPAN" && child._textContent === labelText,
    ),
  );
  assert.ok(label, `expected label containing ${labelText}`);
  const control = findByTag(label, tagName)[0];
  assert.ok(control, `expected ${tagName} control for ${labelText}`);
  return control;
}

function findByTag(root, tagName) {
  return findAll(root, (child) => child.tagName === tagName.toUpperCase());
}

function findByClass(root, className) {
  return findAll(root, (child) =>
    child.className.split(/\s+/).includes(className),
  );
}

function findAll(root, predicate) {
  const matches = [];
  for (const child of root.children ?? []) {
    if (predicate(child)) matches.push(child);
    matches.push(...findAll(child, predicate));
  }
  return matches;
}

function createCatalog() {
  return {
    roleId: "default",
    runtimeModel: { provider: "OpenAiCodex", model: "gpt-5-test" },
    tools: {
      customAssignable: ["list", "read", "search"],
      customAuthorityCeiling: "workspaceReadOnly",
    },
    systemDefault: {
      schemaVersion: 1,
      id: "default-conversation",
      displayName: "Default conversation",
      description: "System-managed conversation loop.",
      roleId: "default",
      trigger: "human-message",
      memoryScope: "workspace-startup-context",
      capabilityIds: [
        "conversation.turn",
        "conversation.history",
        "agent.context",
        "provider.inference",
        "workspace.command",
        "approval.request",
        "audit.write",
      ],
      reviewPolicy: "review-at-authority-boundaries",
      failurePolicy: "record-failure-and-surface-to-user",
      state: "enabled",
      editMode: "system-locked",
      graph: {
        entryNodeId: "accept-user-message",
        terminalNodeIds: ["complete-run"],
        nodes: [
          createSystemNode(
            "accept-user-message",
            "Accept user message",
            "trigger",
            ["conversation.turn"],
            "Receives the current human message as the trigger for the governed default conversation turn.",
          ),
          createSystemNode(
            "assemble-runtime-context",
            "Assemble runtime context",
            "context-assembly",
            ["agent.context", "conversation.history"],
            "Combines startup context, restored/session transcript context, and current turn input before provider dispatch.",
          ),
          createSystemNode(
            "dispatch-provider-inference",
            "Dispatch provider inference",
            "model-inference",
            ["provider.inference"],
            "Sends the assembled turn request to the configured inference adapter.",
          ),
          createSystemNode(
            "persist-transcript",
            "Persist transcript",
            "transcript-persistence",
            ["conversation.turn", "conversation.history"],
            "Persists accepted user and assistant messages into runtime state and conversation memory.",
          ),
          createSystemNode(
            "complete-run",
            "Complete loop run",
            "run-finalization",
            ["audit.write"],
            "Records the terminal loop run status and returns the typed runtime turn result to the active surface.",
          ),
        ],
        edges: [
          createSystemEdge(
            "accept-message-to-context",
            "accept-user-message",
            "assemble-runtime-context",
            "always",
            "Accepted user input flows into context assembly.",
          ),
          createSystemEdge(
            "context-to-inference",
            "assemble-runtime-context",
            "dispatch-provider-inference",
            "success",
            "Context assembly must succeed before provider inference.",
          ),
          createSystemEdge(
            "inference-to-transcript",
            "dispatch-provider-inference",
            "persist-transcript",
            "success",
            "A completed inference response is persisted into the transcript.",
          ),
          createSystemEdge(
            "transcript-to-complete-run",
            "persist-transcript",
            "complete-run",
            "success",
            "Persisted transcript state completes the run.",
          ),
        ],
      },
      executionContract: {
        runner: "DefaultConversationLoopRunner",
        graphSemantics: "authority-topology-only",
        usesGenericGraphDispatcher: false,
        detail:
          "The dedicated runner accepts this system-owned graph as its authority topology, but it does not certify the nodes and edges as an exact execution-order contract.",
      },
    },
    customDefinitions: [createCustomDefinition()],
    draftTemplate: createDraftTemplate(),
    limits: {
      maxDefinitionsPerWorkspace: 50,
      minInferenceSteps: 1,
      maxInferenceSteps: 5,
      maxAdditionalIterations: 10,
      maxModelAttemptsPerRun: 65,
      maxGovernedToolRequestsPerAttempt: 5,
      maxGovernedToolRequestsPerRun: 30,
      maxNameCharacters: 120,
      maxDescriptionCharacters: 2000,
      maxInstructionCharacters: 12000,
      maxTriggerPromptCharacters: 24000,
      maxInvokingConversationCharacters: 24000,
      maxInvokingConversationEntries: 384,
      maxGovernedToolTargetCharacters: 1024,
      maxGovernedToolArgumentCharacters: 1024,
      maxToolGovernanceDetailCharacters: 512,
      maxCanonicalModelOutputCharacters: 8000,
      maxCanonicalToolResultCharacters: 64000,
      maxLifecycleControlEventsPerRun: 64,
      maxTraceEventsPerRun: 768,
      maxLifecycleControlDetailCharacters: 1024,
      maxRunTraceUtf8Bytes: 16777216,
      maxRunExecutionMilliseconds: 1800000,
    },
  };
}

function createDraftTemplate() {
  return {
    schemaVersion: 1,
    roleId: "default",
    definition: {
      displayName: "Untitled loop",
      description: "",
      triggerPolicy: {
        promptSource: "invocation",
        presetPrompt: "",
        includeInvokingConversation: false,
      },
      inferenceSteps: [
        {
          id: null,
          name: "First step",
          instruction:
            "Use the invocation input to complete the user's requested task within this loop's governed authority.",
          contextPolicy: { mode: "inherit", customPolicy: null },
        },
      ],
      toolAssignments: [],
      exitPolicy: {
        maxAdditionalIterations: 0,
        decisionInstruction:
          "Request another iteration only when the latest result still has a concrete, recoverable gap. Otherwise complete.",
        contextPolicy: { mode: "inherit", customPolicy: null },
      },
    },
    contextDefaults: {
      inference: createContextPolicy({
        includePreviousIterationResult: true,
        publishToInvokingConversation: false,
      }),
      exit: createContextPolicy({
        includePreviousIterationResult: true,
        retainForLoopReasoning: false,
        publishToInvokingConversation: true,
      }),
    },
  };
}

function createSystemNode(id, displayName, kind, capabilityIds, description) {
  return {
    id,
    displayName,
    description,
    kind,
    editMode: "system-locked",
    capabilityIds,
    executionSemantics: "authority-topology-only",
  };
}

function createSystemEdge(id, fromNodeId, toNodeId, condition, description) {
  return {
    id,
    fromNodeId,
    toNodeId,
    condition,
    description,
    executionSemantics: "authority-topology-only",
  };
}

function createCustomDefinition(overrides = {}) {
  return {
    schemaVersion: 1,
    id: "loop-research",
    definitionVersion: 2,
    contentHash: "sha256:test",
    createdAtUtc: "2026-07-16T00:00:00Z",
    updatedAtUtc: "2026-07-16T00:00:00Z",
    displayName: "Research pass",
    description: "Inspect an issue before implementation.",
    roleId: "default",
    triggerPolicy: {
      promptSource: "invocation",
      presetPrompt: "",
      includeInvokingConversation: false,
    },
    contextDefaults: {
      inference: createContextPolicy({ publishToInvokingConversation: false }),
      exit: createContextPolicy({
        includePreviousIterationResult: true,
        retainForLoopReasoning: false,
      }),
    },
    inferenceSteps: [
      {
        id: "step-research",
        name: "Research",
        instruction: "Inspect the issue and report evidence.",
        contextPolicy: { mode: "inherit", customPolicy: null },
      },
    ],
    toolAssignments: ["list", "read", "search"],
    exitPolicy: {
      maxAdditionalIterations: 0,
      decisionInstruction: "",
      contextPolicy: { mode: "inherit", customPolicy: null },
    },
    lastMutationOperationId: "op-initial",
    ...overrides,
  };
}

function createContextPolicy(overrides = {}) {
  return {
    contextIn: {
      includeRoleContext: true,
      includeTriggerPrompt: true,
      includeInvokingConversation: true,
      includeEarlierRetainedOutputs: true,
      includePreviousIterationResult:
        overrides.includePreviousIterationResult ?? false,
    },
    contextOut: {
      retainForLoopReasoning: overrides.retainForLoopReasoning ?? true,
      publishToInvokingConversation:
        overrides.publishToInvokingConversation ?? false,
    },
  };
}

function publicationDisposition(
  disposition,
  isDefinite,
  hasIntegrityWarning = false,
  operationId = "publication-operation",
) {
  return {
    operationId,
    disposition,
    detail:
      "Canonical publication disposition supplied by the public runtime projection.",
    isDefinite,
    hasIntegrityWarning,
    eventSequences: [],
  };
}

function runSummary(run) {
  return {
    id: run.id,
    loopId: run.loopId,
    definitionVersion: run.admittedDefinition.definitionVersion,
    status: run.status,
    createdAtUtc: run.createdAtUtc,
    updatedAtUtc: run.updatedAtUtc,
    completedAtUtc: run.completedAtUtc,
    iteration: run.checkpoint.iteration,
    nextStepIndex: run.checkpoint.nextStepIndex,
    failureCode: run.failureCode,
    isDeleted: false,
  };
}

function createRunSnapshot() {
  const definition = createCustomDefinition();
  return {
    schemaVersion: 1,
    id: "run-test",
    loopId: definition.id,
    lifecycleVersion: 4,
    status: "Completed",
    createdAtUtc: "2026-07-16T12:00:00Z",
    updatedAtUtc: "2026-07-16T12:00:02Z",
    completedAtUtc: "2026-07-16T12:00:02Z",
    surface: "web",
    model: { provider: "codex", model: "test-model" },
    admissionOperationId: "op-run-test",
    admissionActor: "embodysense.web",
    admissionRequestHash: "a".repeat(64),
    admittedDefinition: definition,
    triggerPrompt: "Inspect this issue.",
    invokingConversation: null,
    context: {
      schemaVersion: 1,
      capturedAtUtc: "2026-07-16T12:00:00Z",
      manifestHash: "manifest-test",
      sourceManifest: [
        {
          order: 1,
          sourceType: "RoleInstruction",
          sourceId: "nearest-agents",
          sourcePath: "C:/workspace/AGENTS.md",
          provenance: "WorkspaceRoleFile",
          trustClass: "TrustedInstruction",
          role: "system",
          content: "Role context",
          contentHash: "hash-role",
          originalCharacterCount: 12,
          usedCharacterCount: 12,
          truncated: false,
          truncationReason: null,
          omissionReason: null,
          capturedAtUtc: "2026-07-16T12:00:00Z",
        },
      ],
      workspaceContextMessages: [{ role: "system", content: "Role context" }],
      invokingConversationMessages: [],
    },
    executionClock: {
      accumulatedRunningMilliseconds: 1800,
      activeSinceUtc: null,
    },
    checkpoint: {
      iteration: 1,
      nextStepIndex: 1,
      acceptedRepeatCount: 0,
      pendingExitDecision: false,
      earlierRetainedOutputs: [],
      previousIterationResult: null,
      currentIterationResult: null,
      toolRequestsUsed: 1,
      lastCommittedSequence: 4,
    },
    events: [
      {
        sequence: 1,
        eventId: "event-1",
        timestampUtc: "2026-07-16T12:00:00Z",
        kind: "Admitted",
        iteration: null,
        stepId: null,
        attempt: null,
        detail: "Canonical request admitted.",
        contextBlocks: [],
        canonicalOutput: null,
      },
      {
        sequence: 2,
        eventId: "event-2",
        timestampUtc: "2026-07-16T12:00:01Z",
        kind: "NodeAttemptStarted",
        iteration: 1,
        stepId: "step-research",
        attempt: 1,
        detail: "Node attempt started.",
        contextBlocks: [
          {
            source: "TriggerPrompt",
            sourceId: "trigger",
            role: "user",
            included: true,
            omissionReason: null,
            content: "Inspect this issue.",
            contentHash: "hash-trigger",
            characterCount: 19,
            truncated: false,
          },
        ],
        canonicalOutput: null,
        toolAuthority: createToolAuthoritySnapshot(),
      },
      {
        sequence: 3,
        eventId: "event-3",
        timestampUtc: "2026-07-16T12:00:01Z",
        kind: "ToolOutcomeObserved",
        iteration: 1,
        stepId: "step-research",
        attempt: 1,
        detail: "Governed tool outcome persisted.",
        contextBlocks: [],
        canonicalOutput: null,
        toolAuthority: createToolAuthoritySnapshot(),
        toolEvidence: createToolEvidenceSnapshot(),
      },
      {
        sequence: 4,
        eventId: "event-4",
        timestampUtc: "2026-07-16T12:00:02Z",
        kind: "NodeOutcomeObserved",
        iteration: 1,
        stepId: "step-research",
        attempt: 1,
        detail: "Outcome persisted.",
        contextBlocks: [],
        canonicalOutput: "Exact bounded output",
        originalOutputCharacterCount: 20,
        canonicalOutputTruncated: false,
        retainedForLoopReasoning: false,
        publishedToInvokingConversation: false,
        conversationPublicationId: null,
        provider: "codex",
        model: "test-model",
        providerResponseId: "inference-response-1",
      },
    ],
    finalOutput: "Exact bounded output",
    failureCode: null,
    failureDetail: null,
  };
}

function createToolAuthoritySnapshot() {
  return {
    roleId: "default-role",
    admittedMaximum: ["Read"],
    currentRoleCeiling: ["Read"],
    implementedCatalog: ["List", "Read", "Search"],
    effectiveAssignments: ["Read"],
    roleCeilingHash: "role-ceiling-hash",
    catalogHash: "catalog-hash",
    evaluatedAtUtc: "2026-07-16T12:00:01Z",
    isValid: true,
    detail: "Current authority was evaluated before this request.",
  };
}

function createToolEvidenceSnapshot() {
  return {
    phase: "OutcomeObserved",
    requestOrdinal: 1,
    requestCorrelationId: "tool-request-1",
    brokerRequestId: "broker-request-1",
    command: "Read",
    targetPath: "system/note.txt",
    content: null,
    pattern: null,
    resolvedTarget: "C:/workspace/system/note.txt",
    authority: createToolAuthoritySnapshot(),
    governance: {
      authorityDecision: "Allowed",
      authorityDetail: "Read is inside the effective assignment set.",
      permissionDecision: "Allow",
      permissionMatchedPath: "system/**",
      permissionDetail: "Read is allowed.",
      permissionPolicyHash: "permission-hash",
      approvalDecision: "NotRequired",
      approvalDecisionBy: null,
      approvalDetail: "No approval was required.",
    },
    outcome: "Succeeded",
    canonicalResultReturnedToModel: "Exact governed tool result",
    canonicalResultHash: "tool-result-hash",
    canonicalResultCharacterCount: 28,
    returnedToModel: true,
    reservedUtf8Bytes: 393216,
  };
}

function createTraceSnapshot(run) {
  return {
    kind: "LiveTrace",
    runId: run.id,
    loopId: run.loopId,
    status: run.status,
    definitionVersion: run.admittedDefinition.definitionVersion,
    definitionHash: run.admittedDefinition.contentHash,
    persistedArtifactHash: "f".repeat(64),
    persistedArtifactUtf8Bytes: 16384,
    originalTraceHash: "f".repeat(64),
    originalTraceUtf8Bytes: 16384,
    createdAtUtc: run.createdAtUtc,
    completedAtUtc: run.completedAtUtc,
    isDeleted: false,
    tombstone: null,
  };
}

function createTombstoneTrace(run) {
  const liveTrace = createTraceSnapshot(run);
  return {
    ...liveTrace,
    kind: "Tombstone",
    status: run.status,
    persistedArtifactUtf8Bytes: 1024,
    isDeleted: true,
    tombstone: {
      runId: run.id,
      loopId: run.loopId,
      admissionOperationId: run.admissionOperationId,
      terminalStatus: run.status,
      definitionVersion: run.admittedDefinition.definitionVersion,
      definitionHash: run.admittedDefinition.contentHash,
      originalTraceHash: liveTrace.originalTraceHash,
      originalTraceUtf8Bytes: liveTrace.originalTraceUtf8Bytes,
      createdAtUtc: run.createdAtUtc,
      completedAtUtc: run.completedAtUtc,
      deletedAtUtc: "2026-07-26T13:00:00Z",
      deletionActor: "web",
      deletionSurface: "web",
      deletionOperationId: "delete-retained-trace",
      intentAuditCorrelationId: "audit-delete-intent",
      outcomeAuditCorrelationId: "audit-delete-outcome",
      outcomeIntegrity: "Complete",
    },
  };
}

function createTraceQuota(
  liveTraceCount = 0,
  tombstoneCount = 0,
  actualStoredUtf8Bytes = liveTraceCount * 16384,
) {
  return {
    liveTraceCount,
    tombstoneCount,
    liveTraceUtf8Bytes: liveTraceCount * 16384,
    tombstoneUtf8Bytes: tombstoneCount ? actualStoredUtf8Bytes : 0,
    actualStoredUtf8Bytes,
    activeReservationCount: 0,
    reservedCapacityUtf8Bytes: 0,
    accountedUtf8Bytes: actualStoredUtf8Bytes,
    availableAccountedUtf8Bytes: 1073741824 - actualStoredUtf8Bytes,
    maximumLiveTraceCount: 250,
    maximumTombstoneCount: 10000,
    maximumWorkspaceUtf8Bytes: 1073741824,
    maximumPerTraceUtf8Bytes: 16777216,
    deletionOperationCount: 0,
    maximumDeletionOperationCount: 20000,
    isOverLimit: false,
  };
}

function authoringResponse(status, definition) {
  return {
    status,
    isCommitted: true,
    definition: definition ? clone(definition) : null,
    validationErrors: [],
    conflict: null,
    detail: null,
  };
}

function createRetentionPosture({
  health = "healthy",
  cleanupBlockReason = "None",
  exhaustionReason = "None",
  cleanupRecoveryAvailableAtUtc = null,
} = {}) {
  return {
    generatedAtUtc: "2026-08-01T12:00:00Z",
    health,
    classes: [
      {
        artifactClass: "DefinitionMutationReceipt",
        health,
        artifactCount: 2,
        artifactUtf8Bytes: 2048,
        maximumArtifactCount: 10000,
        maximumArtifactUtf8Bytes: 134217728,
        reservedArtifactCount: 64,
        reservedArtifactUtf8Bytes: 41943040,
        proofCount: 1,
        proofUtf8Bytes: 512,
        maximumProofCount: 100000,
        maximumProofUtf8Bytes: 33554432,
        activeCleanupJournalUtf8Bytes: 0,
        cleanupRecoveryAvailableAtUtc,
        completedCleanupOperationCount: 0,
        completedCleanupHistoryUtf8Bytes: 0,
        oldestExactReplayExpiresAtUtc: "2026-08-30T12:00:00Z",
        newestExactReplayExpiresAtUtc: "2026-08-31T12:00:00Z",
        exhaustionReason,
        cleanupBlockReason,
        categories: [
          { category: "Live", artifactCount: 2, utf8Bytes: 2048 },
          { category: "ExpiredIdempotency", artifactCount: 1, utf8Bytes: 512 },
        ],
        detail: "Retention evidence requires review before cleanup.",
      },
    ],
    activeCleanupJournalUtf8Bytes: 0,
    accountedWorkspaceUtf8Bytes: 2560,
    maximumWorkspaceUtf8Bytes: 536870912,
    availableWorkspaceUtf8Bytes: 536868352,
    exhaustionReason,
    cleanupBlockReason,
    detail: "Retention evidence requires review; cleanup stays explicit.",
  };
}

function createDefinitionFromFirstSave(body, id) {
  return createCustomDefinition({
    ...clone(body.definition),
    id,
    definitionVersion: 1,
    inferenceSteps: body.definition.inferenceSteps.map((step, index) => ({
      ...clone(step),
      id: `step-${id}-${index + 1}`,
    })),
    lastMutationOperationId: body.operationId,
  });
}

class FakeFetchServer {
  constructor(catalog) {
    this.catalog = clone(catalog);
    this.runs = [];
    this.runDetails = new Map();
    this.traceDetails = new Map();
    this.invocationReceipts = new Map();
    this.controlReceipts = new Map();
    this.traceQuota = null;
    this.calls = [];
    this.handlers = new Map();
  }

  on(method, url, handler) {
    this.handlers.set(`${method} ${url}`, handler);
  }

  async fetch(url, options = {}) {
    const method = options.method ?? "GET";
    const body = options.body ? JSON.parse(options.body) : null;
    const call = {
      url,
      method,
      body,
      options: { ...options, headers: { ...(options.headers ?? {}) } },
    };
    this.calls.push(call);
    const custom = this.handlers.get(`${method} ${url}`);
    if (custom) return responseFrom(await custom(call));
    if (method === "GET" && url === "/api/session")
      return responseFrom({
        status: 200,
        body: { generationId: "loop-process-generation" },
      });
    if (method === "GET" && url === "/api/status")
      return responseFrom({
        status: 200,
        body: { workspaceRoot: "C:/workspace", initialized: true },
      });
    if (method === "GET" && url === "/api/loops")
      return responseFrom({ status: 200, body: clone(this.catalog) });
    if (method === "GET" && url.startsWith("/api/loop-runs?")) {
      const query = new URLSearchParams(url.slice(url.indexOf("?") + 1));
      const loopId = query.get("loopId");
      const runs = loopId
        ? this.runs.filter((run) => run.loopId === loopId)
        : this.runs;
      return responseFrom({
        status: 200,
        body: { items: clone(runs), continuationCursor: null },
      });
    }
    if (method === "GET" && url === "/api/loop-runs/quota")
      return responseFrom({
        status: 200,
        body: clone(this.traceQuota ?? createTraceQuota(this.runs.length)),
      });
    if (method === "GET" && url.startsWith("/api/loop-runs/invocations/")) {
      const operationId = decodeURIComponent(
        url.slice("/api/loop-runs/invocations/".length),
      );
      const receipt = this.invocationReceipts.get(operationId);
      return receipt
        ? responseFrom({ status: 200, body: clone(receipt) })
        : responseFrom({
            status: 404,
            body: { detail: "Invocation receipt not found." },
          });
    }
    if (method === "GET" && url.startsWith("/api/loop-runs/controls/")) {
      const operationId = decodeURIComponent(
        url.slice("/api/loop-runs/controls/".length),
      );
      const receipt = this.controlReceipts.get(operationId);
      return receipt
        ? responseFrom({ status: 200, body: clone(receipt) })
        : responseFrom({
            status: 404,
            body: { detail: "Control receipt not found." },
          });
    }
    if (
      method === "GET" &&
      url.endsWith("/monitor") &&
      url.startsWith("/api/loop-runs/")
    ) {
      const runId = decodeURIComponent(
        url.slice("/api/loop-runs/".length, -"/monitor".length),
      );
      const summary = this.runs.find((run) => run.id === runId);
      if (!summary)
        return responseFrom({
          status: 404,
          body: { detail: "Run not found." },
        });
      const lifecycleVersion =
        this.runDetails.get(runId)?.lifecycleVersion ??
        summary.lifecycleVersion ??
        0;
      const monitor = { ...summary, lifecycleVersion };
      const etag = `"${[summary.id, summary.loopId, summary.admissionOperationId, summary.definitionVersion, lifecycleVersion, summary.status, summary.createdAtUtc, summary.updatedAtUtc, summary.completedAtUtc ?? "", summary.iteration, summary.nextStepIndex, summary.failureCode ?? "", summary.isDeleted ? 1 : 0].join("-")}"`;
      return options.headers?.["If-None-Match"] === etag
        ? responseFrom({ status: 304, body: null, headers: { ETag: etag } })
        : responseFrom({
            status: 200,
            body: clone(monitor),
            headers: { ETag: etag },
          });
    }
    if (
      method === "GET" &&
      url.endsWith("/trace") &&
      url.startsWith("/api/loop-runs/")
    ) {
      const runId = decodeURIComponent(
        url.slice("/api/loop-runs/".length, -"/trace".length),
      );
      const trace =
        this.traceDetails.get(runId) ??
        (this.runDetails.has(runId)
          ? createTraceSnapshot(this.runDetails.get(runId))
          : null);
      return trace
        ? responseFrom({ status: 200, body: clone(trace) })
        : responseFrom({ status: 404, body: { detail: "Trace not found." } });
    }
    if (method === "GET" && url.startsWith("/api/loop-runs/")) {
      const run = this.runDetails.get(
        decodeURIComponent(url.slice("/api/loop-runs/".length)),
      );
      return run
        ? responseFrom({ status: 200, body: clone(run) })
        : responseFrom({ status: 404, body: { detail: "Run not found." } });
    }
    return responseFrom({
      status: 500,
      body: { detail: `Unexpected request: ${method} ${url}` },
    });
  }
}

function responseFrom({ status, body, headers = {} }) {
  const text = body === null || body === undefined ? "" : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: {
      get: (name) =>
        Object.entries(headers).find(
          ([key]) => key.toLowerCase() === name.toLowerCase(),
        )?.[1] ?? null,
    },
    text: async () => text,
  };
}

class FakeDocument {
  constructor(html) {
    this.elements = new Map();
    this.elementsObject = {};
    for (const match of html.matchAll(/<([a-z0-9]+)[^>]*\sid="([^"]+)"/gi)) {
      const element = new FakeElement(match[1]);
      this.elements.set(match[2], element);
      this.elementsObject[match[2]] = element;
    }
  }

  getElementById(id) {
    return this.elements.get(id);
  }
  createElement(tagName) {
    return new FakeElement(tagName);
  }
  createTextNode(text) {
    return new FakeTextNode(text);
  }
}

class FakeTextNode {
  constructor(text) {
    this.tagName = "#TEXT";
    this.children = [];
    this.textContent = String(text ?? "");
    this.className = "";
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
    this.hidden = false;
    this.selected = false;
    this.type = "";
    this._value = "";
    this._textContent = "";
    this.classList = {
      toggle: (name, force) => {
        const values = new Set(this.className.split(/\s+/).filter(Boolean));
        const add = force === undefined ? !values.has(name) : force;
        if (add) values.add(name);
        else values.delete(name);
        this.className = [...values].join(" ");
      },
    };
  }

  append(...nodes) {
    this.children.push(...nodes);
  }
  replaceChildren(...nodes) {
    this.children = [];
    this._textContent = "";
    this.append(...nodes);
  }
  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }
  removeAttribute(name) {
    this.attributes.delete(name);
  }
  addEventListener(name, handler) {
    this.listeners.set(name, handler);
  }
  async dispatch(name) {
    return this.listeners.get(name)?.({
      target: this,
      preventDefault() {},
      returnValue: undefined,
    });
  }
  async click() {
    if (!this.disabled) return this.dispatch("click");
  }
  async change() {
    return this.dispatch("change");
  }
  async input() {
    return this.dispatch("input");
  }

  querySelector(selector) {
    if (selector === '[aria-selected="true"] .loop-list-name') {
      const selected = findAll(
        this,
        (child) => child.attributes?.get("aria-selected") === "true",
      )[0];
      return selected
        ? (findByClass(selected, "loop-list-name")[0] ?? null)
        : null;
    }
    if (selector.startsWith("."))
      return findByClass(this, selector.slice(1))[0] ?? null;
    return null;
  }

  set value(value) {
    this._value = String(value ?? "");
    if (this.tagName === "SELECT") {
      for (const child of this.children)
        child.selected = child.value === this._value;
    }
  }

  get value() {
    if (this.tagName === "SELECT")
      return (
        this.children.find((child) => child.selected)?.value ??
        this.children[0]?.value ??
        ""
      );
    return this._value;
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

function clone(value) {
  return structuredClone(value);
}

async function flushAsyncWork() {
  await new Promise((resolve) => setTimeout(resolve, 35));
}
