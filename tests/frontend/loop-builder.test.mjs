import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";
import vm from "node:vm";

const builderSource = fs.readFileSync(new URL("../../src/EmbodySense.Web/wwwroot/loop-builder.js", import.meta.url), "utf8");
const loopsHtml = fs.readFileSync(new URL("../../src/EmbodySense.Web/wwwroot/index.html", import.meta.url), "utf8");

test("catalog loading is authenticated and projects the system loop as read-only", async () => {
  const app = await loadLoopBuilder();

  const catalogRequest = app.server.calls.find(call => call.method === "GET" && call.url === "/api/loops");
  assert.equal(catalogRequest.options.headers["X-EmbodySense-Session"], "loop-test-token");
  assert.match(app.elements.loopList.textContent, /Default conversation/);
  assert.match(app.elements.loopList.textContent, /Research pass/);
  assert.equal(app.elements.loopName.disabled, true);
  assert.equal(app.elements.saveButton.disabled, true);
  assert.equal(app.elements.deleteButton.disabled, true);
  assert.equal(app.elements.saveState.textContent, "System managed");

  await selectCustomLoop(app);

  assert.equal(app.elements.loopName.disabled, false);
  assert.equal(app.elements.deleteButton.disabled, false);
  assert.equal(app.elements.saveButton.disabled, true);
  assert.equal(app.elements.saveState.textContent, "Saved · v2");
});

test("loop tabs relinquish selection when another primary app view is active", async () => {
  const app = await loadLoopBuilder();
  app.elements.loopsView.hidden = true;
  app.context.renderAll();

  assert.equal(app.elements.builderTab.attributes.get("aria-selected"), "false");
  assert.equal(app.elements.runsTab.attributes.get("aria-selected"), "false");
});

test("server-controlled loop text is rendered as text and cannot create executable markup", async () => {
  const unsafe = '<img src=x onerror="globalThis.compromised=true"><script>globalThis.compromised=true</script>';
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
  assert.match(app.elements.loopCanvas.textContent, /Invoking user prompt · conversation excluded/);

  source.value = "preset";
  await source.change();
  const preset = findControlByLabel(app.elements.inspectorContent, "Preset prompt", "textarea");
  preset.value = "Use the accepted issue statement.";
  await preset.input();
  const conversation = findControlByLabel(app.elements.inspectorContent, "Include invoking conversation history", "input");
  conversation.checked = true;
  await conversation.change();
  assert.match(app.elements.loopCanvas.textContent, /Preset prompt · conversation included/);

  source = findByTag(app.elements.inspectorContent, "select")[0];
  source.value = "none";
  await source.change();
  assert.equal(findByTag(app.elements.inspectorContent, "textarea").length, 0);
  assert.match(app.elements.loopCanvas.textContent, /No prompt · conversation included/);
  assert.match(app.elements.inspectorContent.textContent, /Trigger admission does not append it again or write durable memory/);
});

test("Inference and Exit expose inherited or custom context without redundant fixed context", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);

  await nodeCard(app, "inference").click();
  let policySource = findControlByLabel(app.elements.inspectorContent, "Policy source", "select");
  assert.equal(policySource.value, "inherit");
  assert.equal(findControlByLabel(app.elements.inspectorContent, "Trigger prompt", "input").disabled, true);
  policySource.value = "custom";
  await policySource.change();
  assert.equal(findControlByLabel(app.elements.inspectorContent, "Trigger prompt", "input").disabled, false);
  assert.match(app.elements.inspectorContent.textContent, /Retain for later loop reasoning/);
  assert.match(app.elements.inspectorContent.textContent, /Publish to the invoking conversation/);

  await nodeCard(app, "exit").click();
  policySource = findControlByLabel(app.elements.inspectorContent, "Policy source", "select");
  assert.equal(policySource.value, "inherit");
  policySource.value = "custom";
  await policySource.change();
  assert.equal(findControlByLabel(app.elements.inspectorContent, "Previous iteration result", "input").disabled, false);
  assert.match(app.elements.inspectorContent.textContent, /Evidence is independent of context/);
  assert.doesNotMatch(`${loopsHtml}\n${builderSource}\n${app.elements.inspectorContent.textContent}`, /Additional fixed context/i);
});

test("Exit continuation is model-gated and its iteration value is presented as a ceiling", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);
  await nodeCard(app, "exit").click();

  const continuation = findControlByLabel(app.elements.inspectorContent, "Allow continuation requests", "input");
  continuation.checked = true;
  await continuation.change();

  assert.match(app.elements.inspectorContent.textContent, /The ceiling never causes a repeat by itself/);
  assert.match(app.elements.inspectorContent.textContent, /A hard ceiling, not a target/);
  assert.match(app.elements.inspectorContent.textContent, /exactly one Complete or Repeat token \(case-insensitive\)/);
  assert.match(app.elements.inspectorContent.textContent, /Invalid or uncertain decisions never repeat/);
  const ceiling = findControlByLabel(app.elements.inspectorContent, "Maximum additional iterations", "input");
  ceiling.value = "3";
  await ceiling.change();
  assert.match(app.elements.loopCanvas.textContent, /Model-gated · up to 3 additional/);
  assert.match(app.elements.loopCanvas.textContent, /ceiling 3/);
});

test("loop settings expose inherited provider, model, tools, and context defaults", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);

  await app.elements.loopSettingsButton.click();

  assert.equal(app.elements.inspectorTitle.textContent, "Loop settings");
  assert.match(app.elements.inspectorContent.textContent, /OpenAiCodex · gpt-5-test/);
  assert.match(app.elements.inspectorContent.textContent, /Provider and model cannot be overridden per loop/);
  assert.match(app.elements.inspectorContent.textContent, /Loop authority/);
  assert.match(app.elements.inspectorContent.textContent, /Inference: 4 context-in sources/);
});

test("create and save send versioned server-owned definition shapes", async () => {
  const catalog = createCatalog();
  const created = createCustomDefinition({ id: "loop-created", definitionVersion: 1, displayName: "Untitled loop" });
  const server = new FakeFetchServer(catalog);
  server.on("POST", "/api/loops", ({ body }) => {
    assert.equal(typeof body.operationId, "string");
    server.catalog.customDefinitions.push(clone(created));
    return { status: 201, body: authoringResponse("Created", created) };
  });
  server.on("PUT", "/api/loops/loop-created", ({ body }) => {
    const updated = { ...created, ...clone(body.definition), definitionVersion: 2 };
    server.catalog.customDefinitions = server.catalog.customDefinitions.map(item => item.id === updated.id ? updated : item);
    return { status: 200, body: authoringResponse("Updated", updated) };
  });
  const app = await loadLoopBuilder({ server });

  await app.elements.createLoopButton.click();
  assert.equal(app.elements.loopName.value, "Untitled loop");
  app.elements.loopName.value = "Issue research";
  await app.elements.loopName.input();

  let source = findControlByLabel(app.elements.inspectorContent, "Prompt source", "select");
  source.value = "preset";
  await source.change();
  const preset = findControlByLabel(app.elements.inspectorContent, "Preset prompt", "textarea");
  preset.value = "Research the configured issue.";
  await preset.input();
  const conversation = findControlByLabel(app.elements.inspectorContent, "Include invoking conversation history", "input");
  conversation.checked = true;
  await conversation.change();

  await nodeCard(app, "inference").click();
  const policySource = findControlByLabel(app.elements.inspectorContent, "Policy source", "select");
  policySource.value = "custom";
  await policySource.change();
  const publish = findControlByLabel(app.elements.inspectorContent, "Publish to the invoking conversation", "input");
  publish.checked = true;
  await publish.change();

  await nodeCard(app, "exit").click();
  const continuation = findControlByLabel(app.elements.inspectorContent, "Allow continuation requests", "input");
  continuation.checked = true;
  await continuation.change();
  const decision = findControlByLabel(app.elements.inspectorContent, "Decision instruction", "textarea");
  decision.value = "Return Repeat only when another research pass is needed.";
  await decision.input();
  const ceiling = findControlByLabel(app.elements.inspectorContent, "Maximum additional iterations", "input");
  ceiling.value = "2";
  await ceiling.change();

  await app.elements.saveButton.click();
  const save = server.calls.find(call => call.method === "PUT" && call.url === "/api/loops/loop-created");
  assert.equal(save.options.headers["X-EmbodySense-Session"], "loop-test-token");
  assert.equal(save.body.expectedDefinitionVersion, 1);
  assert.equal(typeof save.body.operationId, "string");
  assert.deepEqual(save.body.definition.triggerPolicy, {
    promptSource: "preset",
    presetPrompt: "Research the configured issue.",
    includeInvokingConversation: true
  });
  assert.equal(save.body.definition.inferenceSteps[0].contextPolicy.mode, "custom");
  assert.equal(save.body.definition.inferenceSteps[0].contextPolicy.customPolicy.contextOut.publishToInvokingConversation, true);
  assert.equal(save.body.definition.exitPolicy.maxAdditionalIterations, 2);
  assert.equal(save.body.definition.exitPolicy.decisionInstruction, "Return Repeat only when another research pass is needed.");
  assert.doesNotMatch(JSON.stringify(save.body), /additionalFixedContext/i);
  assert.equal(app.elements.saveState.textContent, "Saved · v2");
});

test("client validation blocks incomplete definitions before save", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);

  app.elements.loopName.value = "";
  await app.elements.loopName.input();
  assert.equal(app.elements.validationBanner.textContent, "Loop name is required.");
  assert.equal(app.elements.saveButton.disabled, true);

  app.elements.loopName.value = "Research pass";
  await app.elements.loopName.input();
  const source = findControlByLabel(app.elements.inspectorContent, "Prompt source", "select");
  source.value = "preset";
  await source.change();
  assert.equal(app.elements.validationBanner.textContent, "Preset trigger prompt is required.");
  assert.equal(app.elements.saveButton.disabled, true);

  source.value = "invocation";
  await source.change();
  await nodeCard(app, "exit").click();
  const continuation = findControlByLabel(app.elements.inspectorContent, "Allow continuation requests", "input");
  continuation.checked = true;
  await continuation.change();
  assert.equal(app.elements.validationBanner.textContent, "Exit decision instruction is required when continuation is enabled.");
  assert.equal(app.elements.saveButton.disabled, true);
});

test("a stale save conflict stays visible with the current server version and reload guidance", async () => {
  const server = new FakeFetchServer(createCatalog());
  server.on("PUT", "/api/loops/loop-research", () => ({
    status: 409,
    body: {
      status: "Conflict",
      isCommitted: false,
      validationErrors: [],
      conflict: { loopId: "loop-research", expectedDefinitionVersion: 2, actualDefinitionVersion: 4 },
      detail: "The loop changed after this editor loaded it."
    }
  }));
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  app.elements.loopDescription.value = "A locally edited description.";
  await app.elements.loopDescription.input();

  await app.elements.saveButton.click();

  assert.match(app.elements.validationBanner.textContent, /changed after this editor loaded/i);
  assert.match(app.elements.validationBanner.textContent, /server version 4/i);
  assert.match(app.elements.validationBanner.textContent, /Reload/i);
  assert.equal(app.elements.reloadButton.disabled, false);
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

  assert.match(app.window.confirmations[0], /Historical run evidence will remain available/);
  const deletion = server.calls.find(call => call.method === "DELETE" && call.url === "/api/loops/loop-research");
  assert.equal(deletion.body.expectedDefinitionVersion, 2);
  assert.equal(typeof deletion.body.operationId, "string");
  assert.equal(app.elements.toast.textContent, "Loop deleted. Historical run evidence was preserved.");
  assert.equal(app.elements.loopName.value, "Default conversation");
});

test("Runs projects durable timeline and context evidence from the authenticated API", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  server.runs = [{ id: run.id, loopId: run.loopId, definitionVersion: 2, status: run.status, createdAtUtc: run.createdAtUtc, updatedAtUtc: run.updatedAtUtc, completedAtUtc: run.completedAtUtc, iteration: 1, nextStepIndex: 1, failureCode: null }];
  server.runDetails.set(run.id, run);
  server.traceQuota = { ...createTraceQuota(1), activeReservationCount: 1, reservedCapacityUtf8Bytes: 8192, accountedUtf8Bytes: 24576, availableAccountedUtf8Bytes: 1073717248 };
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);

  await app.elements.runsTab.click();
  await flushAsyncWork();

  assert.equal(app.elements.runCount.textContent, "1");
  assert.match(app.elements.runList.textContent, /run-test/);
  assert.match(app.elements.runTitle.textContent, /run-test/);
  assert.match(app.elements.runTimeline.textContent, /Node attempt started/);
  assert.match(app.elements.runTimeline.textContent, /Exact bounded output/);
  assert.match(app.elements.runTimeline.textContent, /provider codex · model test-model/);
  assert.match(app.elements.runTimeline.textContent, /canonical output 20\/20 chars · complete/);
  assert.match(app.elements.runTimeline.textContent, /loop reasoning evidence only/);
  assert.equal(app.elements.inspectorTitle.textContent, "Run evidence");
  assert.match(app.elements.inspectorContent.textContent, /manifest-test/);
  assert.match(app.elements.inspectorContent.textContent, /TriggerPrompt/);
  assert.match(app.elements.inspectorContent.textContent, /WorkspaceRoleFile/);
  assert.match(app.elements.inspectorContent.textContent, /TrustedInstruction/);
  assert.match(app.elements.inspectorContent.textContent, /Sensitive trace storage16\.0 KiB/);
  assert.match(app.elements.inspectorContent.textContent, /Status and checkpointCompleted · iteration 1 · step-research · attempt 1/);
  assert.match(app.elements.inspectorContent.textContent, /execution 2s elapsed · 29m 59s remaining of 30m/);
  assert.match(app.elements.inspectorContent.textContent, /next proved boundary Terminal checkpoint/);
  assert.match(app.elements.inspectorContent.textContent, /last committed sequence 4 · latest event 4 Node Outcome Observed/);
  assert.match(app.elements.inspectorContent.textContent, /pending approvals visible to this connection 0/);
  assert.match(app.elements.inspectorContent.textContent, /Provider and modelcodex · test-model/);
  assert.match(app.elements.inspectorContent.textContent, /source id trigger/);
  assert.match(app.elements.inspectorContent.textContent, /hash hash-trigger/);
  assert.match(app.elements.inspectorContent.textContent, /event 2 · iteration 1 · node step-research · attempt 1/);
  assert.match(app.elements.inspectorContent.textContent, /resolved context in · role included · trigger included/);
  assert.match(app.elements.inspectorContent.textContent, /Provider usage and costUnavailable/);
  assert.match(app.elements.inspectorContent.textContent, /does not report token usage or cost; no estimate is fabricated/);
  assert.match(app.elements.inspectorContent.textContent, /Tool requests, governance, and model-visible results/);
  assert.match(app.elements.inspectorContent.textContent, /Tool request 1 · Read · Outcome Observed · Succeeded/);
  assert.match(app.elements.inspectorContent.textContent, /current role ceiling Read/);
  assert.match(app.elements.inspectorContent.textContent, /approval Not Required/);
  assert.match(app.elements.inspectorContent.textContent, /authority detail Read is inside the effective assignment set/);
  assert.match(app.elements.inspectorContent.textContent, /permission detail Read is allowed/);
  assert.match(app.elements.inspectorContent.textContent, /permission policy permission-hash/);
  assert.match(app.elements.inspectorContent.textContent, /approval detail No approval was required/);
  assert.match(app.elements.inspectorContent.textContent, /Exact governed tool result/);
  assert.match(app.elements.inspectorContent.textContent, /8\.0+ KiB reserved across 1 trace reservation/);
  assert.doesNotMatch(app.elements.inspectorContent.textContent, /active run/i);
  assert.match(app.elements.traceQuota.textContent, /1\/250 live/);
  assert.match(app.elements.traceQuota.textContent, /reserved/);
  assert.doesNotMatch(app.elements.inspectorContent.textContent, /chain-of-thought/i);
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
    { id: older.id, loopId: older.loopId, admissionOperationId: older.admissionOperationId, definitionVersion: 2, status: older.status, createdAtUtc: older.createdAtUtc, updatedAtUtc: older.updatedAtUtc, completedAtUtc: older.completedAtUtc, iteration: 1, nextStepIndex: 1, failureCode: null },
    { id: preferred.id, loopId: preferred.loopId, admissionOperationId: preferred.admissionOperationId, definitionVersion: 2, status: preferred.status, createdAtUtc: preferred.createdAtUtc, updatedAtUtc: preferred.updatedAtUtc, completedAtUtc: preferred.completedAtUtc, iteration: 1, nextStepIndex: 1, failureCode: null }
  ];
  server.runDetails.set(older.id, older);
  server.runDetails.set(preferred.id, preferred);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  await app.context.waitForRunOperation(Promise.resolve({}), { preferredAdmissionOperationId: preferred.admissionOperationId });

  assert.match(app.elements.runTitle.textContent, /run-preferred/);
  assert.match(app.elements.inspectorContent.textContent, /run run-preferred/);
});

test("opening an existing nonterminal run keeps refreshing independently of its original invoker", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [{ id: run.id, loopId: run.loopId, admissionOperationId: run.admissionOperationId, definitionVersion: 2, status: run.status, createdAtUtc: run.createdAtUtc, updatedAtUtc: run.updatedAtUtc, completedAtUtc: null, iteration: 1, nextStepIndex: 1, failureCode: null, isDeleted: false }];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  const refresh = app.window.delayedHandlers.find(item => item.delay === 1000 && !item.cancelled);
  assert.ok(refresh, "expected a recurring refresh for the selected nonterminal run");
  run.status = "Completed";
  run.completedAtUtc = "2026-07-16T12:00:03Z";
  server.runs[0].status = "Completed";
  server.runs[0].completedAtUtc = run.completedAtUtc;
  server.runDetails.set(run.id, run);
  refresh.cancelled = true;
  await refresh.handler();

  assert.match(app.elements.runSubtitle.textContent, /Completed/);
  assert.equal(app.window.delayedHandlers.filter(item => item.delay === 1000 && !item.cancelled).length, 0);
});

test("a transient live refresh failure schedules another poll and recovers", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  run.status = "Running";
  run.completedAtUtc = null;
  server.runs = [{ id: run.id, loopId: run.loopId, admissionOperationId: run.admissionOperationId, definitionVersion: 2, status: run.status, createdAtUtc: run.createdAtUtc, updatedAtUtc: run.updatedAtUtc, completedAtUtc: null, iteration: 1, nextStepIndex: 1, failureCode: null, isDeleted: false }];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();

  let listAttempts = 0;
  server.on("GET", "/api/loop-runs?maximumCount=50", () => {
    listAttempts++;
    return listAttempts === 1
      ? { status: 503, body: { detail: "Temporary run-list failure." } }
      : { status: 200, body: clone(server.runs) };
  });

  const failedRefresh = app.window.delayedHandlers.find(item => item.delay === 1000 && !item.cancelled);
  assert.ok(failedRefresh, "expected the first live refresh");
  failedRefresh.cancelled = true;
  await failedRefresh.handler();

  const retry = app.window.delayedHandlers.find(item => item.delay === 1000 && !item.cancelled);
  assert.ok(retry, "expected polling to retry after a transient failure");
  run.status = "Completed";
  run.completedAtUtc = "2026-07-16T12:00:03Z";
  server.runs[0].status = "Completed";
  server.runs[0].completedAtUtc = run.completedAtUtc;
  server.runDetails.set(run.id, run);
  retry.cancelled = true;
  await retry.handler();

  assert.equal(listAttempts, 2);
  assert.match(app.elements.runSubtitle.textContent, /Completed/);
  assert.equal(app.window.delayedHandlers.filter(item => item.delay === 1000 && !item.cancelled).length, 0);
});

test("deleted loop definitions retain a selectable archived run-history surface", async () => {
  const catalog = createCatalog();
  catalog.customDefinitions = [];
  const server = new FakeFetchServer(catalog);
  const run = createRunSnapshot();
  server.runs = [{ id: run.id, loopId: run.loopId, admissionOperationId: run.admissionOperationId, definitionVersion: 2, status: run.status, createdAtUtc: run.createdAtUtc, updatedAtUtc: run.updatedAtUtc, completedAtUtc: run.completedAtUtc, iteration: 1, nextStepIndex: 1, failureCode: null, isDeleted: false }];
  server.runDetails.set(run.id, run);
  const app = await loadLoopBuilder({ server });

  const archived = app.elements.loopList.children.find(child => child.textContent.includes("Deleted loop · loop-research"));
  assert.ok(archived);
  await archived.click();
  await flushAsyncWork();

  assert.equal(app.elements.builderTab.disabled, true);
  assert.match(app.elements.saveState.textContent, /Archived evidence/);
  assert.match(app.elements.runTitle.textContent, /run-test/);
  assert.match(app.elements.inspectorContent.textContent, /Research pass v2/);
});

test("terminal trace deletion is explicit, hash-bound, quota-visible, and leaves an inspectable tombstone", async () => {
  const server = new FakeFetchServer(createCatalog());
  const run = createRunSnapshot();
  server.runs = [{ id: run.id, loopId: run.loopId, definitionVersion: 2, status: run.status, createdAtUtc: run.createdAtUtc, updatedAtUtc: run.updatedAtUtc, completedAtUtc: run.completedAtUtc, iteration: 1, nextStepIndex: 1, failureCode: null }];
  server.runDetails.set(run.id, run);
  const liveTrace = createTraceSnapshot(run);
  server.traceDetails.set(run.id, liveTrace);
  server.on("POST", `/api/loop-runs/${run.id}/trace/delete`, ({ body }) => {
    assert.equal(body.expectedTraceHash, liveTrace.persistedArtifactHash);
    assert.equal(typeof body.operationId, "string");
    assert.equal(Object.hasOwn(body, "actor"), false);
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
      outcomeIntegrity: "Complete"
    };
    server.traceDetails.set(run.id, { ...liveTrace, kind: "Tombstone", persistedArtifactUtf8Bytes: 1024, isDeleted: true, tombstone });
    server.runs = [{ id: run.id, loopId: run.loopId, admissionOperationId: run.admissionOperationId, definitionVersion: 2, status: run.status, createdAtUtc: run.createdAtUtc, updatedAtUtc: tombstone.deletedAtUtc, completedAtUtc: run.completedAtUtc, iteration: 0, nextStepIndex: 0, failureCode: null, isDeleted: true }];
    server.runDetails.delete(run.id);
    server.traceQuota = createTraceQuota(0, 1, 1024);
    return { status: 200, body: { status: "Deleted", isCommitted: true, detail: "Deleted.", tombstone } };
  });
  const app = await loadLoopBuilder({ server });
  await selectCustomLoop(app);
  await app.elements.runsTab.click();
  await flushAsyncWork();

  const deleteButton = app.elements.runActions.children.find(child => child.textContent === "Delete sensitive trace");
  assert.ok(deleteButton);
  await deleteButton.click();
  await flushAsyncWork();

  assert.match(app.window.confirmations.at(-1), /Permanently delete the sensitive trace content/);
  assert.match(app.window.confirmations.at(-1), /small audited tombstone will remain/);
  assert.match(app.elements.runTitle.textContent, /Deleted trace run-test/);
  assert.match(app.elements.runNotice.textContent, /Sensitive prompt, context, output, and tool evidence were explicitly deleted/);
  assert.match(app.elements.inspectorContent.textContent, /Audited trace tombstone/);
  assert.match(app.elements.inspectorContent.textContent, /trace-delete-intent-test/);
  assert.match(app.elements.traceQuota.textContent, /0\/250 live/);
  assert.match(app.elements.toast.textContent, /audited tombstone remains/);
  const callsAfterDeletion = server.calls.slice(server.calls.findIndex(call => call.method === "POST" && call.url.endsWith("/trace/delete")) + 1);
  assert.equal(callsAfterDeletion.some(call => call.method === "GET" && call.url === `/api/loop-runs/${run.id}`), false);
});

test("Run confirmation exposes real governed limits and never reintroduces fixed context", async () => {
  const app = await loadLoopBuilder();
  await selectCustomLoop(app);

  await app.elements.invokeButton.click();

  assert.match(app.elements.invokeModal.className, /open/);
  assert.match(app.elements.invokeSummary.textContent, /Research pass v2/);
  assert.match(app.elements.invokeSummary.textContent, /OpenAiCodex · gpt-5-test/);
  assert.match(app.elements.invokeSummary.textContent, /initial user prompt/);
  assert.match(app.elements.invokeLimits.textContent, /65 model attempts/);
  assert.match(app.elements.invokeLimits.textContent, /5 governed tool requests per attempt/);
  assert.match(app.elements.invokeLimits.textContent, /30 per run/);
  assert.match(app.elements.invokeLimits.textContent, /within 30m of accumulated execution time/);
  assert.match(app.elements.invokeLimits.textContent, /canonical model output is capped at 8,000 characters/);
  assert.match(app.elements.invokeLimits.textContent, /24,000 characters across 384 selected messages/);
  assert.match(app.elements.invokeLimits.textContent, /targets are capped at 1,024 characters, arguments at 1,024/);
  assert.match(app.elements.invokeLimits.textContent, /formatted result returned to the model at 64,000 characters/);
  assert.match(app.elements.invokeLimits.textContent, /768 events, including 64 lifecycle\/control events, and 16\.0 MiB/);
  assert.match(app.elements.invokeLimits.textContent, /list, read, search/);
  assert.equal(app.elements.invocationPromptField.hidden, false);
  assert.doesNotMatch(`${app.elements.invokeSummary.textContent}\n${app.elements.invokeLimits.textContent}`, /additional fixed context/i);
});

test("owned run approvals expose resolved governance evidence in the loop builder", async () => {
  const app = await loadLoopBuilder();

  app.context.renderLoopApprovals([{
    requestId: "approval-test",
    command: "workspace",
    operation: "read",
    targetPath: "docs/issue.md",
    resolvedPath: "C:/workspace/docs/issue.md",
    matchedPath: "C:/workspace",
    reason: "The active loop requires governed workspace read access."
  }]);

  assert.equal(app.elements.approvalPanel.hidden, false);
  assert.equal(app.elements.approvalCount.textContent, "1 pending");
  assert.match(app.elements.approvals.textContent, /workspace read/i);
  assert.match(app.elements.approvals.textContent, /target docs\/issue\.md/);
  assert.match(app.elements.approvals.textContent, /resolved C:\/workspace\/docs\/issue\.md/);
  assert.match(app.elements.approvals.textContent, /matched permission C:\/workspace/);
  assert.match(app.elements.approvals.textContent, /governed workspace read access/);
  assert.deepEqual(findByTag(app.elements.approvals, "button").map(button => button.textContent), ["Reject", "Approve"]);

  app.context.renderLoopApprovals([]);
  assert.equal(app.elements.approvalPanel.hidden, true);
  assert.equal(app.elements.approvalCount.textContent, "0 pending");
  assert.equal(app.elements.approvals.children.length, 0);
});

async function loadLoopBuilder(options = {}) {
  const document = new FakeDocument(loopsHtml);
  const server = options.server ?? new FakeFetchServer(options.catalog ?? createCatalog());
  let operation = 0;
  const window = {
    confirmations: [],
    delayedHandlers: [],
    location: { href: "http://127.0.0.1:4378/?view=loops" },
    addEventListener() { },
    confirm(message) { this.confirmations.push(message); return true; },
    setTimeout(handler, delay) {
      if (delay > 100) {
        const scheduled = { handler, delay, cancelled: false };
        this.delayedHandlers.push(scheduled);
        return scheduled;
      }
      return setTimeout(handler, delay);
    },
    clearTimeout(handle) {
      if (handle && typeof handle === "object" && Object.hasOwn(handle, "cancelled")) handle.cancelled = true;
      else clearTimeout(handle);
    }
  };
  const context = {
    console,
    crypto: { randomUUID: () => `00000000-0000-4000-8000-${String(++operation).padStart(12, "0")}` },
    document,
    fetch: server.fetch.bind(server),
    setTimeout,
    clearTimeout,
    structuredClone,
    window
  };
  context.globalThis = context;
  vm.runInNewContext(builderSource, context, { filename: "loop-builder.js" });
  await flushAsyncWork();
  document.elementsObject.approvalPanel = document.elementsObject.loopApprovalPanel;
  document.elementsObject.approvalCount = document.elementsObject.loopApprovalCount;
  document.elementsObject.approvals = document.elementsObject.loopApprovals;
  return { context, document, elements: document.elementsObject, server, window };
}

async function selectCustomLoop(app) {
  const item = app.elements.loopList.children.find(child => child.textContent.includes("Research pass") || child.textContent.includes("Untitled loop") || child.textContent.includes("<script>"));
  assert.ok(item, "expected a custom loop catalog item");
  await item.click();
  await flushAsyncWork();
}

function nodeCard(app, className) {
  const card = findByClass(app.elements.loopCanvas, className).find(element => element.className.split(/\s+/).includes("node-card"));
  assert.ok(card, `expected ${className} node card`);
  return card;
}

function findControlByLabel(root, labelText, tagName) {
  const label = findByTag(root, "label").find(item => item.children.some(child => child.tagName === "SPAN" && child._textContent === labelText));
  assert.ok(label, `expected label containing ${labelText}`);
  const control = findByTag(label, tagName)[0];
  assert.ok(control, `expected ${tagName} control for ${labelText}`);
  return control;
}

function findByTag(root, tagName) {
  return findAll(root, child => child.tagName === tagName.toUpperCase());
}

function findByClass(root, className) {
  return findAll(root, child => child.className.split(/\s+/).includes(className));
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
  const defaults = {
    inference: createContextPolicy({ publishToInvokingConversation: false }),
    exit: createContextPolicy({ includePreviousIterationResult: true, retainForLoopReasoning: false })
  };
  return {
    roleId: "default",
    runtimeModel: { provider: "OpenAiCodex", model: "gpt-5-test" },
    systemDefault: {
      ...createCustomDefinition({ id: "default-conversation", displayName: "Default conversation", definitionVersion: 1 }),
      description: "System-managed conversation loop.",
      contextDefaults: clone(defaults),
      inferenceSteps: [{
        id: "dispatch-inference",
        name: "Respond in role",
        instruction: "System-managed default conversation behavior.",
        contextPolicy: { mode: "custom", customPolicy: createContextPolicy({ publishToInvokingConversation: true }) }
      }]
    },
    customDefinitions: [createCustomDefinition()],
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
      maxRunExecutionMilliseconds: 1800000
    }
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
    triggerPolicy: { promptSource: "invocation", presetPrompt: "", includeInvokingConversation: false },
    contextDefaults: {
      inference: createContextPolicy({ publishToInvokingConversation: false }),
      exit: createContextPolicy({ includePreviousIterationResult: true, retainForLoopReasoning: false })
    },
    inferenceSteps: [{ id: "step-research", name: "Research", instruction: "Inspect the issue and report evidence.", contextPolicy: { mode: "inherit", customPolicy: null } }],
    toolAssignments: ["list", "read", "search"],
    exitPolicy: { maxAdditionalIterations: 0, decisionInstruction: "", contextPolicy: { mode: "inherit", customPolicy: null } },
    lastMutationOperationId: "op-initial",
    ...overrides
  };
}

function createContextPolicy(overrides = {}) {
  return {
    contextIn: {
      includeRoleContext: true,
      includeTriggerPrompt: true,
      includeInvokingConversation: true,
      includeEarlierRetainedOutputs: true,
      includePreviousIterationResult: overrides.includePreviousIterationResult ?? false
    },
    contextOut: {
      retainForLoopReasoning: overrides.retainForLoopReasoning ?? true,
      publishToInvokingConversation: overrides.publishToInvokingConversation ?? false
    }
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
    admissionRequestHash: "a".repeat(64),
    admittedDefinition: definition,
    triggerPrompt: "Inspect this issue.",
    invokingConversation: null,
    context: {
      schemaVersion: 1,
      capturedAtUtc: "2026-07-16T12:00:00Z",
      manifestHash: "manifest-test",
      sourceManifest: [{ order: 1, sourceType: "RoleInstruction", sourceId: "nearest-agents", sourcePath: "C:/workspace/AGENTS.md", provenance: "WorkspaceRoleFile", trustClass: "TrustedInstruction", role: "system", content: "Role context", contentHash: "hash-role", originalCharacterCount: 12, usedCharacterCount: 12, truncated: false, truncationReason: null, omissionReason: null, capturedAtUtc: "2026-07-16T12:00:00Z" }],
      directoryRoleMessages: [{ role: "system", content: "Role context" }],
      invokingConversationMessages: []
    },
    executionClock: { accumulatedRunningMilliseconds: 1800, activeSinceUtc: null },
    checkpoint: { iteration: 1, nextStepIndex: 1, acceptedRepeatCount: 0, pendingExitDecision: false, earlierRetainedOutputs: [], previousIterationResult: null, currentIterationResult: null, toolRequestsUsed: 1, lastCommittedSequence: 4 },
    events: [
      { sequence: 1, eventId: "event-1", timestampUtc: "2026-07-16T12:00:00Z", kind: "Admitted", iteration: null, stepId: null, attempt: null, detail: "Canonical request admitted.", contextBlocks: [], canonicalOutput: null },
      { sequence: 2, eventId: "event-2", timestampUtc: "2026-07-16T12:00:01Z", kind: "NodeAttemptStarted", iteration: 1, stepId: "step-research", attempt: 1, detail: "Node attempt started.", contextBlocks: [{ source: "TriggerPrompt", sourceId: "trigger", role: "user", included: true, omissionReason: null, content: "Inspect this issue.", contentHash: "hash-trigger", characterCount: 19, truncated: false }], canonicalOutput: null, toolAuthority: createToolAuthoritySnapshot() },
      { sequence: 3, eventId: "event-3", timestampUtc: "2026-07-16T12:00:01Z", kind: "ToolOutcomeObserved", iteration: 1, stepId: "step-research", attempt: 1, detail: "Governed tool outcome persisted.", contextBlocks: [], canonicalOutput: null, toolAuthority: createToolAuthoritySnapshot(), toolEvidence: createToolEvidenceSnapshot() },
      { sequence: 4, eventId: "event-4", timestampUtc: "2026-07-16T12:00:02Z", kind: "NodeOutcomeObserved", iteration: 1, stepId: "step-research", attempt: 1, detail: "Outcome persisted.", contextBlocks: [], canonicalOutput: "Exact bounded output", originalOutputCharacterCount: 20, canonicalOutputTruncated: false, retainedForLoopReasoning: false, publishedToInvokingConversation: false, conversationPublicationId: null, provider: "codex", model: "test-model", providerResponseId: "inference-response-1" }
    ],
    finalOutput: "Exact bounded output",
    failureCode: null,
    failureDetail: null
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
    detail: "Current authority was evaluated before this request."
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
      approvalDetail: "No approval was required."
    },
    outcome: "Succeeded",
    canonicalResultReturnedToModel: "Exact governed tool result",
    canonicalResultHash: "tool-result-hash",
    canonicalResultCharacterCount: 28,
    returnedToModel: true,
    reservedUtf8Bytes: 393216
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
    tombstone: null
  };
}

function createTraceQuota(liveTraceCount = 0, tombstoneCount = 0, actualStoredUtf8Bytes = liveTraceCount * 16384) {
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
    isOverLimit: false
  };
}

function authoringResponse(status, definition) {
  return { status, isCommitted: true, definition: definition ? clone(definition) : null, validationErrors: [], conflict: null, detail: null };
}

class FakeFetchServer {
  constructor(catalog) {
    this.catalog = clone(catalog);
    this.runs = [];
    this.runDetails = new Map();
    this.traceDetails = new Map();
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
    const call = { url, method, body, options: { ...options, headers: { ...(options.headers ?? {}) } } };
    this.calls.push(call);
    const custom = this.handlers.get(`${method} ${url}`);
    if (custom) return responseFrom(await custom(call));
    if (method === "GET" && url === "/api/session") return responseFrom({ status: 200, body: { token: "loop-test-token" } });
    if (method === "GET" && url === "/api/status") return responseFrom({ status: 200, body: { workspaceRoot: "C:/workspace", initialized: true } });
    if (method === "GET" && url === "/api/loops") return responseFrom({ status: 200, body: clone(this.catalog) });
    if (method === "GET" && url === "/api/loop-runs?maximumCount=50") return responseFrom({ status: 200, body: clone(this.runs) });
    if (method === "GET" && url === "/api/loop-runs/quota") return responseFrom({ status: 200, body: clone(this.traceQuota ?? createTraceQuota(this.runs.length)) });
    if (method === "GET" && url.endsWith("/trace") && url.startsWith("/api/loop-runs/")) {
      const runId = decodeURIComponent(url.slice("/api/loop-runs/".length, -"/trace".length));
      const trace = this.traceDetails.get(runId) ?? (this.runDetails.has(runId) ? createTraceSnapshot(this.runDetails.get(runId)) : null);
      return trace ? responseFrom({ status: 200, body: clone(trace) }) : responseFrom({ status: 404, body: { detail: "Trace not found." } });
    }
    if (method === "GET" && url.startsWith("/api/loop-runs/")) {
      const run = this.runDetails.get(decodeURIComponent(url.slice("/api/loop-runs/".length)));
      return run ? responseFrom({ status: 200, body: clone(run) }) : responseFrom({ status: 404, body: { detail: "Run not found." } });
    }
    return responseFrom({ status: 500, body: { detail: `Unexpected request: ${method} ${url}` } });
  }
}

function responseFrom({ status, body }) {
  const text = body === null || body === undefined ? "" : JSON.stringify(body);
  return { ok: status >= 200 && status < 300, status, text: async () => text };
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

  getElementById(id) { return this.elements.get(id); }
  createElement(tagName) { return new FakeElement(tagName); }
  createTextNode(text) { return new FakeTextNode(text); }
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
        if (add) values.add(name); else values.delete(name);
        this.className = [...values].join(" ");
      }
    };
  }

  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = []; this._textContent = ""; this.append(...nodes); }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  addEventListener(name, handler) { this.listeners.set(name, handler); }
  async dispatch(name) { return this.listeners.get(name)?.({ target: this, preventDefault() { }, returnValue: undefined }); }
  async click() { if (!this.disabled) return this.dispatch("click"); }
  async change() { return this.dispatch("change"); }
  async input() { return this.dispatch("input"); }

  querySelector(selector) {
    if (selector === '[aria-selected="true"] .loop-list-name') {
      const selected = findAll(this, child => child.attributes?.get("aria-selected") === "true")[0];
      return selected ? findByClass(selected, "loop-list-name")[0] ?? null : null;
    }
    if (selector.startsWith(".")) return findByClass(this, selector.slice(1))[0] ?? null;
    return null;
  }

  set value(value) {
    this._value = String(value ?? "");
    if (this.tagName === "SELECT") {
      for (const child of this.children) child.selected = child.value === this._value;
    }
  }

  get value() {
    if (this.tagName === "SELECT") return this.children.find(child => child.selected)?.value ?? this.children[0]?.value ?? "";
    return this._value;
  }

  set textContent(value) { this._textContent = String(value ?? ""); this.children = []; }
  get textContent() { return this._textContent + this.children.map(child => child.textContent).join(""); }
}

function clone(value) {
  return structuredClone(value);
}

async function flushAsyncWork() {
  await new Promise(resolve => setTimeout(resolve, 35));
}
