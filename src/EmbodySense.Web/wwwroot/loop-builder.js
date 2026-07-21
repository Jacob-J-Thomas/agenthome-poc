let sessionToken = "";
let catalog = null;
let currentDefinition = null;
let draft = null;
let selectedNodeId = "trigger";
let dirty = false;
let currentView = "builder";
let recentRuns = [];
let selectedRunId = null;
let selectedRun = null;
let selectedTrace = null;
let traceQuota = null;
let hub = null;
let invokeReturnFocus = null;
let historicalLoopId = null;
let selectedRunRefreshTimer = null;
let activeRunOperationMonitors = 0;
let mutationInFlight = false;
let pendingCreateOperationId = null;
let pendingTraceDeletion = null;

const signalRRecordSeparator = "\u001e";
const signalRKeepAliveMilliseconds = 10000;

const elements = {
  addStepButton: document.getElementById("addStepButton"),
  approvalCount: document.getElementById("approvalCount"),
  approvalPanel: document.getElementById("approvalPanel"),
  approvals: document.getElementById("approvals"),
  builderTab: document.getElementById("builderTab"),
  builderView: document.getElementById("builderView"),
  cancelInvokeButton: document.getElementById("cancelInvokeButton"),
  canvas: document.getElementById("loopCanvas"),
  closeInvokeButton: document.getElementById("closeInvokeButton"),
  connectionDot: document.getElementById("connectionDot"),
  createLoopButton: document.getElementById("createLoopButton"),
  deleteButton: document.getElementById("deleteButton"),
  description: document.getElementById("loopDescription"),
  inspectorContent: document.getElementById("inspectorContent"),
  inspectorTitle: document.getElementById("inspectorTitle"),
  invocationPrompt: document.getElementById("invocationPrompt"),
  invocationPromptField: document.getElementById("invocationPromptField"),
  invokeButton: document.getElementById("invokeButton"),
  invokeLimits: document.getElementById("invokeLimits"),
  invokeModal: document.getElementById("invokeModal"),
  invokeSummary: document.getElementById("invokeSummary"),
  list: document.getElementById("loopList"),
  loopSettingsButton: document.getElementById("loopSettingsButton"),
  name: document.getElementById("loopName"),
  reloadButton: document.getElementById("reloadButton"),
  roleId: document.getElementById("roleId"),
  runActions: document.getElementById("runActions"),
  runCount: document.getElementById("runCount"),
  runList: document.getElementById("runList"),
  runNotice: document.getElementById("runNotice"),
  runsTab: document.getElementById("runsTab"),
  runsView: document.getElementById("runsView"),
  runSubtitle: document.getElementById("runSubtitle"),
  runTimeline: document.getElementById("runTimeline"),
  runTitle: document.getElementById("runTitle"),
  saveButton: document.getElementById("saveButton"),
  saveState: document.getElementById("saveState"),
  startRunButton: document.getElementById("startRunButton"),
  toast: document.getElementById("toast"),
  traceQuota: document.getElementById("traceQuota"),
  validationBanner: document.getElementById("validationBanner"),
  workspaceRoot: document.getElementById("workspaceRoot"),
  workspaceStatus: document.getElementById("workspaceStatus")
};

async function boot() {
  bindStaticEvents();
  try {
    const session = await requestJson("/api/session");
    sessionToken = session.token;
    const status = await requestJson("/api/status");
    elements.workspaceRoot.textContent = status.workspaceRoot;
    elements.workspaceStatus.textContent = status.initialized ? "Initialized" : "Needs initialization";
    elements.connectionDot.classList.toggle("ready", status.initialized);
    if (!status.initialized) {
      showBanner("Initialize the workspace from Chat before creating loops.", "notice");
      setInteractive(false);
      return;
    }

    await loadCatalog();
    await loadRuns();
  } catch (error) {
    showBanner(`Loop builder unavailable: ${error.message}`);
    setInteractive(false);
  }
}

function bindStaticEvents() {
  elements.createLoopButton.addEventListener("click", createLoop);
  elements.builderTab.addEventListener("click", () => switchView("builder"));
  elements.runsTab.addEventListener("click", () => switchView("runs"));
  elements.invokeButton.addEventListener("click", openInvokeModal);
  elements.closeInvokeButton.addEventListener("click", closeInvokeModal);
  elements.cancelInvokeButton.addEventListener("click", closeInvokeModal);
  elements.startRunButton.addEventListener("click", startRun);
  elements.saveButton.addEventListener("click", saveLoop);
  elements.deleteButton.addEventListener("click", deleteLoop);
  elements.reloadButton.addEventListener("click", reloadCurrent);
  elements.addStepButton.addEventListener("click", addInferenceStep);
  elements.loopSettingsButton.addEventListener("click", () => {
    if (!draft) return;
    selectedNodeId = "loop-settings";
    renderCanvas();
    renderInspector();
  });
  elements.name.addEventListener("input", event => updateDraftValue("displayName", event.target.value));
  elements.description.addEventListener("input", event => updateDraftValue("description", event.target.value));
  window.addEventListener("beforeunload", event => {
    if (dirty) {
      event.preventDefault();
      event.returnValue = "";
    }
  });
  window.addEventListener("keydown", event => {
    if (event.key === "Escape" && elements.invokeModal.className.split(/\s+/).includes("open")) closeInvokeModal();
  });
}

async function requestJson(url, options = {}) {
  const headers = { ...(options.headers ?? {}) };
  if (sessionToken) headers["X-EmbodySense-Session"] = sessionToken;
  if (options.body && !headers["Content-Type"]) headers["Content-Type"] = "application/json";
  const response = await fetch(url, { ...options, headers });
  const text = await response.text();
  let payload = null;
  if (text) {
    try { payload = JSON.parse(text); } catch { payload = text; }
  }
  if (!response.ok) {
    const detail = typeof payload === "string" ? payload : payload?.detail ?? payload?.title ?? `Request failed (${response.status})`;
    const error = new Error(detail);
    error.status = response.status;
    error.payload = payload;
    throw error;
  }
  return payload;
}

async function loadCatalog(preferredLoopId) {
  catalog = await requestJson("/api/loops");
  elements.roleId.textContent = catalog.roleId;
  const definitions = allDefinitions();
  const requested = preferredLoopId ?? currentDefinition?.id;
  const next = definitions.find(definition => definition.id === requested) ?? definitions[0] ?? null;
  applyDefinition(next);
  renderList();
}

function allDefinitions() {
  if (!catalog) return [];
  return [catalog.systemDefault, ...catalog.customDefinitions];
}

function applyDefinition(definition) {
  historicalLoopId = null;
  currentDefinition = definition;
  draft = definition ? clone(definition) : null;
  selectedNodeId = "trigger";
  dirty = false;
  elements.name.value = draft?.displayName ?? "";
  elements.description.value = draft?.description ?? "";
  renderAll();
}

function renderAll() {
  renderList();
  renderTabs();
  if (currentView === "builder") {
    renderCanvas();
    renderInspector();
  } else {
    renderRuns();
    renderRunEvidence();
  }
  renderToolbar();
  renderValidation();
  scheduleSelectedRunRefresh();
}

function renderTabs() {
  const builderActive = currentView === "builder" && !historicalLoopId;
  elements.builderTab.disabled = mutationInFlight || Boolean(historicalLoopId);
  elements.runsTab.disabled = mutationInFlight;
  elements.builderTab.classList.toggle("active", builderActive);
  elements.runsTab.classList.toggle("active", !builderActive);
  elements.builderTab.setAttribute("aria-selected", String(builderActive));
  elements.runsTab.setAttribute("aria-selected", String(!builderActive));
  elements.builderView.hidden = !builderActive;
  elements.runsView.hidden = builderActive;
  elements.runCount.textContent = String(runsForCurrentLoop().length);
}

async function switchView(view) {
  if (mutationInFlight) return;
  if (view !== "builder" && view !== "runs") return;
  if (view === "builder" && historicalLoopId) return;
  currentView = view;
  if (view === "runs") {
    renderAll();
    await loadRuns();
    return;
  }
  renderAll();
}

function runsForCurrentLoop() {
  const loopId = selectedLoopId();
  return loopId ? recentRuns.filter(run => run.loopId === loopId) : [];
}

function selectedLoopId() {
  return draft?.id ?? historicalLoopId;
}

async function loadRuns({ silent = false, preferredRunId = null, preferredAdmissionOperationId = null, preserveEmptySelection = false } = {}) {
  if (!catalog) return;
  try {
    const [payload, quotaPayload] = await Promise.all([
      requestJson("/api/loop-runs?maximumCount=50"),
      requestJson("/api/loop-runs/quota")
    ]);
    recentRuns = Array.isArray(payload) ? payload : payload?.items ?? [];
    traceQuota = quotaPayload;
    const visible = runsForCurrentLoop();
    const preferred = visible.find(run => run.id === preferredRunId || run.admissionOperationId === preferredAdmissionOperationId);
    if (preferred) selectedRunId = preferred.id;
    if (!visible.some(run => run.id === selectedRunId)) selectedRunId = preserveEmptySelection ? null : visible[0]?.id ?? null;
    if (selectedRunId) {
      const requestedRunId = selectedRunId;
      const summary = visible.find(run => run.id === requestedRunId);
      let nextRun;
      let nextTrace;
      if (summary?.isDeleted) {
        nextRun = null;
        nextTrace = await requestJson(`/api/loop-runs/${encodeURIComponent(requestedRunId)}/trace`);
      } else {
        [nextRun, nextTrace] = await Promise.all([
          requestJson(`/api/loop-runs/${encodeURIComponent(requestedRunId)}`),
          requestJson(`/api/loop-runs/${encodeURIComponent(requestedRunId)}/trace`)
        ]);
      }
      if (selectedRunId !== requestedRunId) return false;
      selectedRun = nextRun;
      selectedTrace = nextTrace;
    } else {
      selectedRun = null;
      selectedTrace = null;
    }
    if (currentView === "runs") {
      renderRuns();
      renderRunEvidence();
    }
    renderList();
    renderTabs();
    scheduleSelectedRunRefresh();
    return true;
  } catch (error) {
    if (!silent) showBanner(`Run evidence unavailable: ${error.message}`);
    return false;
  }
}

function renderRuns() {
  renderTraceQuota();
  elements.runList.replaceChildren();
  const runs = runsForCurrentLoop();
  if (runs.length === 0) {
    elements.runList.append(node("p", "empty-state", isSystemLoop() ? "Custom-loop runs do not apply to the system loop." : "No runs for this loop yet."));
  } else {
    for (const run of runs) {
      const button = node("button", `run-item${run.id === selectedRunId ? " selected" : ""}`);
      button.type = "button";
      const top = node("span", "run-item-top");
      top.append(node("span", "run-id", run.id), node("span", `run-status-dot ${statusClass(run.status)}`));
      button.append(top, node("span", "run-meta", `v${run.definitionVersion} · ${formatStatus(run.status)}${run.isDeleted ? " · trace deleted" : ""}`), node("span", "run-meta", formatTimestamp(run.updatedAtUtc)));
      button.addEventListener("click", () => selectRun(run.id));
      elements.runList.append(button);
    }
  }

  elements.runTimeline.replaceChildren();
  elements.runActions.replaceChildren();
  elements.runNotice.textContent = "";
  elements.runNotice.className = "run-notice";
  if ((!selectedRun || selectedRun.loopId !== selectedLoopId()) && !(selectedTrace?.isDeleted && selectedTrace.loopId === selectedLoopId())) {
    elements.runTitle.textContent = "No run selected";
    elements.runSubtitle.textContent = "Start a saved loop to inspect its durable evidence.";
    elements.runTimeline.append(node("p", "empty-state", "The ordered timeline will appear here."));
    return;
  }

  if (!selectedRun && selectedTrace?.isDeleted) {
    elements.runTitle.textContent = `Deleted trace ${selectedTrace.runId}`;
    elements.runSubtitle.textContent = `${selectedTrace.tombstone?.terminalStatus ?? selectedTrace.status} run content replaced by an audited tombstone`;
    elements.runNotice.textContent = `Sensitive prompt, context, output, and tool evidence were explicitly deleted. The metadata-only tombstone remains inspectable; outcome integrity: ${formatStatus(selectedTrace.tombstone?.outcomeIntegrity)}.`;
    elements.runNotice.className = "run-notice visible";
    const tombstoneEvent = node("div", "timeline");
    const event = node("div", "timeline-event");
    event.append(node("div", "event-dot completed", "✓"));
    const card = node("div", "event-card");
    card.append(node("div", "event-title", "Trace content deleted"), node("div", "event-detail", `Deleted ${formatTimestamp(selectedTrace.tombstone?.deletedAtUtc)}\nOperation ${selectedTrace.tombstone?.deletionOperationId ?? "unknown"}`));
    event.append(card); tombstoneEvent.append(event); elements.runTimeline.append(tombstoneEvent);
    if (draft && !isSystemLoop()) elements.runActions.append(actionButton("New run", openInvokeModal, dirty, "secondary-button"));
    return;
  }

  elements.runTitle.textContent = `Run ${selectedRun.id}`;
  elements.runSubtitle.textContent = `${selectedRun.admittedDefinition.displayName} v${selectedRun.admittedDefinition.definitionVersion} · ${formatStatus(selectedRun.status)}`;
  renderRunActions(selectedRun);
  if (selectedRun.status === "PauseRequested" || selectedRun.status === "CancelRequested") {
    elements.runNotice.textContent = selectedRun.status === "PauseRequested"
      ? "Pause requested. The current operation may finish; no later model boundary will start after the next proved checkpoint."
      : "Cancellation requested. The runtime will report Cancelled only when the last outcome is proved; uncertainty becomes Needs review.";
    elements.runNotice.className = "run-notice visible";
  }

  const timeline = node("div", "timeline");
  for (const event of selectedRun.events ?? []) timeline.append(renderRunEvent(event));
  if ((selectedRun.events ?? []).length === 0) timeline.append(node("p", "empty-state", "No persisted events were returned."));
  elements.runTimeline.append(timeline);
}

async function selectRun(runId) {
  selectedRunId = runId;
  selectedRun = null;
  selectedTrace = null;
  renderRuns();
  renderRunEvidence();
  try {
    const summary = runsForCurrentLoop().find(run => run.id === runId);
    let nextRun;
    let nextTrace;
    if (summary?.isDeleted) {
      nextRun = null;
      nextTrace = await requestJson(`/api/loop-runs/${encodeURIComponent(runId)}/trace`);
    } else {
      [nextRun, nextTrace] = await Promise.all([
        requestJson(`/api/loop-runs/${encodeURIComponent(runId)}`),
        requestJson(`/api/loop-runs/${encodeURIComponent(runId)}/trace`)
      ]);
    }
    if (selectedRunId !== runId) return;
    selectedRun = nextRun;
    selectedTrace = nextTrace;
    renderRuns();
    renderRunEvidence();
    scheduleSelectedRunRefresh();
  } catch (error) {
    if (selectedRunId !== runId) return;
    showBanner(`Run detail unavailable: ${error.message}`);
  }
}

function renderRunActions(run) {
  if (run.status === "Running") elements.runActions.append(actionButton("Pause at boundary", () => controlRun("pause"), false));
  if (run.status === "Paused") elements.runActions.append(actionButton("Resume", resumeRun, false, "primary-button"));
  if (["Admitted", "Running", "PauseRequested", "Paused"].includes(run.status)) elements.runActions.append(actionButton("Cancel", () => controlRun("cancel"), false, "danger-button"));
  if (["Completed", "Failed", "Cancelled", "NeedsReview"].includes(run.status) && selectedTrace && !selectedTrace.isDeleted) elements.runActions.append(actionButton("Delete sensitive trace", deleteSelectedTrace, false, "danger-button"));
  if (draft && !isSystemLoop()) elements.runActions.append(actionButton("New run", openInvokeModal, dirty, "secondary-button"));
}

function renderTraceQuota() {
  if (!elements.traceQuota) return;
  if (!traceQuota) {
    elements.traceQuota.textContent = "Trace quota unavailable";
    return;
  }

  elements.traceQuota.textContent = `${traceQuota.liveTraceCount}/${traceQuota.maximumLiveTraceCount} live · ${formatBytes(traceQuota.actualStoredUtf8Bytes)} stored · ${formatBytes(traceQuota.reservedCapacityUtf8Bytes)} reserved · ${formatBytes(traceQuota.availableAccountedUtf8Bytes)} available`;
}

function renderRunEvent(event) {
  const container = node("div", "timeline-event");
  const symbol = event.kind?.includes("Failed") ? "!" : event.kind?.includes("Completed") ? "✓" : event.kind?.includes("Exit") ? "E" : event.kind?.includes("Node") ? "N" : "·";
  container.append(node("div", `event-dot ${statusClass(event.kind)}`, symbol));
  const card = node("div", "event-card");
  const top = node("div", "event-card-top");
  const location = [event.iteration ? `iteration ${event.iteration}` : "", event.stepId ?? "", event.attempt ? `attempt ${event.attempt}` : ""].filter(Boolean).join(" · ");
  top.append(node("span", "event-title", splitWords(event.kind)), node("span", "event-time", formatTimestamp(event.timestampUtc)));
  card.append(top, node("div", "event-detail", `${location}${location && event.detail ? "\n" : ""}${event.detail ?? ""}`));
  if (event.canonicalOutput) card.append(node("div", "event-output", event.canonicalOutput));
  const attemptEvidence = [
    event.provider ? `provider ${event.provider}${event.model ? ` · model ${event.model}` : ""}` : null,
    event.providerResponseId ? `provider response ${event.providerResponseId}` : null,
    event.originalOutputCharacterCount != null ? `canonical output ${event.canonicalOutput?.length ?? 0}/${event.originalOutputCharacterCount} chars · ${event.canonicalOutputTruncated ? "truncated" : "complete"}` : null,
    event.retainedForLoopReasoning != null ? `loop reasoning ${event.retainedForLoopReasoning ? "retained" : "evidence only"}` : null,
    event.publishedToInvokingConversation != null ? `conversation ${event.publishedToInvokingConversation ? "published" : "not published"}${event.conversationPublicationId ? ` · ${event.conversationPublicationId}` : ""}` : null,
    event.exitDecision ? `Exit decision ${formatStatus(event.exitDecision)}` : null
  ].filter(Boolean);
  if (attemptEvidence.length) card.append(node("div", "evidence-code", attemptEvidence.join("\n")));
  if (event.toolEvidence) card.append(renderToolEvidence(event.toolEvidence, false));
  else if (event.toolAuthority) card.append(renderToolAuthority(event.toolAuthority));
  container.append(card);
  return container;
}

function renderToolEvidence(evidence, includePayload = true) {
  const details = node("details", "context-block tool-evidence");
  const outcome = evidence.outcome ? ` · ${formatStatus(evidence.outcome)}` : "";
  details.append(node("summary", "", `Tool request ${evidence.requestOrdinal} · ${formatStatus(evidence.command)} · ${formatStatus(evidence.phase)}${outcome}`));
  const governance = evidence.governance;
  const lines = [
    `request ${evidence.requestCorrelationId}`,
    evidence.brokerRequestId ? `broker ${evidence.brokerRequestId}` : null,
    `target ${evidence.targetPath}`,
    evidence.resolvedTarget ? `resolved ${evidence.resolvedTarget}` : null,
    `returned to model ${evidence.returnedToModel ? "yes" : "no"}`,
    evidence.canonicalResultCharacterCount != null ? `canonical result ${evidence.canonicalResultCharacterCount} chars · ${evidence.canonicalResultHash ?? "hash unavailable"}` : null,
    governance ? `authority ${formatStatus(governance.authorityDecision)} · permission ${governance.permissionDecision ? formatStatus(governance.permissionDecision) : "not evaluated"} · approval ${formatStatus(governance.approvalDecision)}` : "governance decision not yet recorded",
    governance?.authorityDetail ? `authority detail ${governance.authorityDetail}` : null,
    governance?.permissionMatchedPath ? `permission rule ${governance.permissionMatchedPath}` : null,
    governance?.permissionDetail ? `permission detail ${governance.permissionDetail}` : null,
    governance?.permissionPolicyHash ? `permission policy ${governance.permissionPolicyHash}` : null,
    governance?.approvalDecisionBy ? `approval decision by ${governance.approvalDecisionBy}` : null,
    governance?.approvalDetail ? `approval detail ${governance.approvalDetail}` : null,
    ...toolAuthorityLines(evidence.authority)
  ].filter(Boolean);
  details.append(node("div", "evidence-code", lines.join("\n")));
  if (includePayload) {
    const argumentsText = [evidence.content == null ? null : `content\n${evidence.content}`, evidence.pattern == null ? null : `pattern\n${evidence.pattern}`].filter(Boolean).join("\n\n");
    if (argumentsText) details.append(node("pre", "", argumentsText));
    if (evidence.canonicalResultReturnedToModel != null) details.append(node("pre", "", evidence.canonicalResultReturnedToModel));
  }
  return details;
}

function renderToolAuthority(authority) {
  const details = node("details", "context-block tool-authority");
  details.append(node("summary", "", `Tool authority · ${authority.isValid ? "valid" : "invalid"} · ${authority.effectiveAssignments?.length ?? 0} effective`));
  details.append(node("div", "evidence-code", toolAuthorityLines(authority).join("\n")));
  return details;
}

function toolAuthorityLines(authority) {
  if (!authority) return ["authority snapshot unavailable"];
  return [
    `role ${authority.roleId}`,
    `admitted maximum ${(authority.admittedMaximum ?? []).join(", ") || "none"}`,
    `current role ceiling ${(authority.currentRoleCeiling ?? []).join(", ") || "none"}`,
    `implemented catalog ${(authority.implementedCatalog ?? []).join(", ") || "none"}`,
    `effective assignments ${(authority.effectiveAssignments ?? []).join(", ") || "none"}`,
    `role ceiling hash ${authority.roleCeilingHash}`,
    `catalog hash ${authority.catalogHash}`,
    `evaluated ${formatTimestamp(authority.evaluatedAtUtc)}`,
    authority.detail
  ];
}

function renderRunEvidence() {
  elements.inspectorContent.replaceChildren();
  elements.inspectorTitle.textContent = "Run evidence";
  appendQuotaEvidence();
  if (!selectedRun && selectedTrace?.isDeleted && selectedTrace.loopId === selectedLoopId()) {
    elements.inspectorContent.append(node("h3", "evidence-title", "Audited trace tombstone"), node("p", "evidence-subtitle", "Sensitive run content is gone; bounded identity and deletion-integrity metadata remain."));
    appendEvidenceSection("Deleted run", selectedTrace.runId, `${selectedTrace.tombstone?.terminalStatus ?? selectedTrace.status} · loop ${selectedTrace.loopId}\nDefinition ${selectedTrace.definitionHash}`);
    appendEvidenceSection("Original trace", formatBytes(selectedTrace.originalTraceUtf8Bytes), selectedTrace.originalTraceHash);
    appendEvidenceSection("Deletion", `${formatTimestamp(selectedTrace.tombstone?.deletedAtUtc)} · ${formatStatus(selectedTrace.tombstone?.outcomeIntegrity)}`, `Operation ${selectedTrace.tombstone?.deletionOperationId ?? "unknown"}\nIntent audit ${selectedTrace.tombstone?.intentAuditCorrelationId ?? "unknown"}\nOutcome audit ${selectedTrace.tombstone?.outcomeAuditCorrelationId ?? "unknown"}`);
    return;
  }
  if (!selectedRun || selectedRun.loopId !== selectedLoopId()) {
    elements.inspectorContent.append(node("p", "empty-state", "Select a run to inspect its admitted definition, context, outputs, and recovery state."));
    return;
  }

  elements.inspectorContent.append(node("h3", "evidence-title", "What ran, why, and with what authority"), node("p", "evidence-subtitle", "Durable logical evidence; provider-private reasoning is not exposed."));
  const definition = selectedRun.admittedDefinition;
  appendEvidenceSection("Admitted definition", `${definition.displayName} v${definition.definitionVersion}`, `${definition.contentHash}\n${definition.inferenceSteps.length} inference step${definition.inferenceSteps.length === 1 ? "" : "s"} · ${definition.exitPolicy.maxAdditionalIterations > 0 ? `LLM-gated continuation, ceiling ${definition.exitPolicy.maxAdditionalIterations} additional` : "one deterministic iteration"}`);
  appendEvidenceSection("Invocation", selectedRun.triggerPrompt || "No prompt admitted", `${selectedRun.surface} surface · conversation ${selectedRun.invokingConversation ? "bound" : "not bound"}`);
  appendRunProgressEvidence(selectedRun, definition);
  appendEvidenceSection("Provider and model", `${selectedRun.model.provider} · ${selectedRun.model.model || "provider default"}`, (selectedRun.events ?? []).filter(event => event.provider || event.providerResponseId).map(event => `event ${event.sequence}: ${event.provider ?? selectedRun.model.provider} · ${event.model ?? selectedRun.model.model ?? "provider default"}${event.providerResponseId ? ` · response ${event.providerResponseId}` : ""}`).join("\n") || "No provider attempt has been persisted yet.");
  appendEvidenceSection("Provider usage and cost", "Unavailable", "The current provider response does not report token usage or cost; no estimate is fabricated.");
  appendEvidenceSection("Role and authority", definition.roleId, definition.toolAssignments.length ? definition.toolAssignments.join(" · ") : "No model-facing tools assigned");
  appendEvidenceSection("Context snapshot", `Captured ${formatTimestamp(selectedRun.context.capturedAtUtc)}`, `${selectedRun.context.manifestHash}\n${selectedRun.context.directoryRoleMessages.length} role messages · ${selectedRun.context.invokingConversationMessages.length} conversation messages`);
  if (selectedTrace) appendEvidenceSection("Sensitive trace storage", formatBytes(selectedTrace.persistedArtifactUtf8Bytes), `${selectedTrace.persistedArtifactHash}\nExplicit deletion is irreversible. No trace is pruned automatically.`);

  const manifest = selectedRun.context.sourceManifest ?? [];
  if (manifest.length > 0) {
    const manifestSection = evidenceSection("Admitted context source manifest");
    for (const source of manifest) {
      const details = node("details", "context-block");
      const disposition = source.omissionReason ? `omitted: ${source.omissionReason}` : `${source.usedCharacterCount}/${source.originalCharacterCount} chars${source.truncated ? " · truncated" : ""}`;
      details.append(node("summary", "", `${source.order}. ${source.sourceType} · ${source.sourceId} · ${source.trustClass} · ${disposition}`));
      details.append(node("div", "evidence-code", `${source.sourcePath}\n${source.provenance} · ${source.role} · captured ${formatTimestamp(source.capturedAtUtc)}\n${source.contentHash}${source.truncationReason ? `\n${source.truncationReason}` : ""}`));
      details.append(node("pre", "", source.omissionReason ? "Content was not admitted." : source.content));
      manifestSection.append(details);
    }
    elements.inspectorContent.append(manifestSection);
  }

  const contextEvents = (selectedRun.events ?? []).filter(event => (event.contextBlocks ?? []).length > 0);
  if (contextEvents.length > 0) {
    const contextSection = evidenceSection("Resolved model context");
    for (const event of contextEvents) {
      const attempt = node("article", "context-attempt");
      const location = [`event ${event.sequence}`, event.iteration ? `iteration ${event.iteration}` : null, event.stepId ? `node ${event.stepId}` : null, event.attempt ? `attempt ${event.attempt}` : null].filter(Boolean).join(" · ");
      attempt.append(node("h4", "section-heading", location));
      const policy = resolvedEventPolicy(selectedRun.admittedDefinition, event);
      if (policy) attempt.append(node("div", "evidence-code", contextPolicyLines(policy).join("\n")));
      for (const block of event.contextBlocks) {
        const details = node("details", "context-block");
        details.append(node("summary", "", `${block.source} · ${block.included ? "included" : `omitted: ${block.omissionReason ?? "policy"}`} · ${block.characterCount} chars${block.truncated ? " · truncated" : ""}${block.sourceVersion ? ` · source ${block.sourceVersion}` : ""}`));
        details.append(node("div", "evidence-code", `source id ${block.sourceId}\nrole ${block.role}\nhash ${block.contentHash}\nsource version ${block.sourceVersion ?? "not versioned"}\ndisposition ${block.included ? "included" : `omitted: ${block.omissionReason ?? "policy"}`} · ${block.characterCount} chars · ${block.truncated ? "truncated" : "complete"}`));
        details.append(node("pre", "", block.included ? block.content : "Content omitted by the recorded policy."));
        attempt.append(details);
      }
      contextSection.append(attempt);
    }
    elements.inspectorContent.append(contextSection);
  }

  const toolEvents = (selectedRun.events ?? []).filter(event => event.toolEvidence);
  if (toolEvents.length > 0) {
    const toolSection = evidenceSection("Tool requests, governance, and model-visible results");
    for (const event of toolEvents) toolSection.append(renderToolEvidence(event.toolEvidence));
    elements.inspectorContent.append(toolSection);
  } else {
    const authorityEvent = (selectedRun.events ?? []).find(event => event.toolAuthority);
    if (authorityEvent) {
      const authoritySection = evidenceSection("Attempt authority");
      authoritySection.append(renderToolAuthority(authorityEvent.toolAuthority));
      elements.inspectorContent.append(authoritySection);
    }
  }

  const publicationEvents = (selectedRun.events ?? []).filter(event => event.conversationPublicationId);
  appendEvidenceSection("Output disposition", selectedRun.finalOutput ?? "No terminal output", publicationEvents.length ? publicationEvents.map(event => `${event.conversationPublicationId}: ${event.publishedToInvokingConversation ? "published" : "not published"}`).join("\n") : "Evidence retained; no conversation publication correlation recorded.");
  if (selectedRun.failureCode || selectedRun.failureDetail) appendEvidenceSection("Failure or recovery", selectedRun.failureCode ?? formatStatus(selectedRun.status), selectedRun.failureDetail ?? "Inspect the ordered timeline for the persisted boundary.");
}

function appendRunProgressEvidence(run, definition) {
  const checkpoint = run.checkpoint;
  const events = run.events ?? [];
  const latest = events.at(-1);
  const accumulated = Number(run.executionClock?.accumulatedRunningMilliseconds ?? 0);
  const activeSince = run.executionClock?.activeSinceUtc ? new Date(run.executionClock.activeSinceUtc).valueOf() : null;
  const activeElapsed = Number.isFinite(activeSince) ? Math.max(0, Date.now() - activeSince) : 0;
  const elapsed = Math.max(0, accumulated + activeElapsed);
  const deadline = Number(catalog?.limits?.maxRunExecutionMilliseconds);
  const remaining = Number.isFinite(deadline) ? Math.max(0, deadline - elapsed) : null;
  const terminal = ["Completed", "Failed", "Cancelled", "NeedsReview"].includes(run.status);
  const nextStep = terminal
    ? "Terminal checkpoint"
    : checkpoint?.pendingExitDecision || (checkpoint?.nextStepIndex >= definition.inferenceSteps.length && definition.exitPolicy.maxAdditionalIterations > 0)
      ? "Exit decision"
      : checkpoint?.nextStepIndex >= definition.inferenceSteps.length
        ? "Deterministic completion boundary"
        : definition.inferenceSteps[checkpoint?.nextStepIndex ?? 0]?.name ?? "Unknown boundary";
  const current = [
    latest?.iteration ? `iteration ${latest.iteration}` : checkpoint?.iteration ? `iteration ${checkpoint.iteration}` : null,
    latest?.stepId,
    latest?.attempt ? `attempt ${latest.attempt}` : null
  ].filter(Boolean).join(" · ") || "No model attempt dispatched";
  const deadlineText = Number.isFinite(deadline) ? `${formatDuration(elapsed)} elapsed · ${formatDuration(remaining)} remaining of ${formatDuration(deadline)}` : `${formatDuration(elapsed)} elapsed · deadline unavailable`;
  appendEvidenceSection("Status and checkpoint", `${formatStatus(run.status)} · ${current}`, `run ${run.id}\nloop ${run.loopId} · role ${definition.roleId} · ${run.surface} surface\nlifecycle version ${run.lifecycleVersion}\nexecution ${deadlineText}\nnext proved boundary ${nextStep}\niteration ${checkpoint?.iteration ?? "unknown"} · accepted repeats ${checkpoint?.acceptedRepeatCount ?? "unknown"} · tool requests ${checkpoint?.toolRequestsUsed ?? "unknown"}\nlast committed sequence ${checkpoint?.lastCommittedSequence ?? "none"} · latest event ${latest?.sequence ?? "none"} ${latest?.kind ? formatStatus(latest.kind) : ""}\npending approvals visible to this connection ${elements.approvals.children.length}`);
}

function resolvedEventPolicy(definition, event) {
  const isExit = event.stepId === "exit" || String(event.kind ?? "").startsWith("Exit");
  const kind = isExit ? "exit" : "inference";
  const owner = isExit ? definition.exitPolicy : definition.inferenceSteps.find(step => step.id === event.stepId);
  if (!owner?.contextPolicy) return null;
  return owner.contextPolicy.mode === "custom" ? owner.contextPolicy.customPolicy : definition.contextDefaults[kind];
}

function contextPolicyLines(policy) {
  return [
    `resolved context in · role ${yesNo(policy.contextIn.includeRoleContext)} · trigger ${yesNo(policy.contextIn.includeTriggerPrompt)} · conversation ${yesNo(policy.contextIn.includeInvokingConversation)} · retained outputs ${yesNo(policy.contextIn.includeEarlierRetainedOutputs)} · previous iteration ${yesNo(policy.contextIn.includePreviousIterationResult)}`,
    `resolved context out · loop reasoning ${yesNo(policy.contextOut.retainForLoopReasoning)} · invoking conversation ${yesNo(policy.contextOut.publishToInvokingConversation)}`
  ];
}

function yesNo(value) {
  return value ? "included" : "excluded";
}

function appendQuotaEvidence() {
  if (!traceQuota) return;
  appendEvidenceSection("Workspace trace quota", `${traceQuota.liveTraceCount}/${traceQuota.maximumLiveTraceCount} live · ${traceQuota.tombstoneCount}/${traceQuota.maximumTombstoneCount} tombstones`, `${formatBytes(traceQuota.actualStoredUtf8Bytes)} physically stored\n${formatBytes(traceQuota.reservedCapacityUtf8Bytes)} reserved across ${traceQuota.activeReservationCount} trace reservation${traceQuota.activeReservationCount === 1 ? "" : "s"}\n${formatBytes(traceQuota.accountedUtf8Bytes)} accounted of ${formatBytes(traceQuota.maximumWorkspaceUtf8Bytes)} · no automatic pruning`);
}

function appendEvidenceSection(label, value, code) {
  const container = evidenceSection(label);
  container.append(node("div", "evidence-value", value));
  if (code) container.append(node("div", "evidence-code", code));
  elements.inspectorContent.append(container);
}

function evidenceSection(label) {
  const container = node("section", "evidence-section");
  container.append(node("div", "evidence-label", label));
  return container;
}

function renderList() {
  elements.list.replaceChildren();
  if (!catalog) return;
  for (const definition of allDefinitions()) {
    const button = node("button", "loop-list-item");
    button.type = "button";
    button.disabled = mutationInFlight;
    button.setAttribute("role", "option");
    button.setAttribute("aria-selected", definition.id === currentDefinition?.id ? "true" : "false");
    button.classList.toggle("selected", definition.id === currentDefinition?.id);
    button.append(node("span", "loop-list-name", definition.displayName));
    const meta = node("span", "loop-list-meta");
    meta.append(node("span", definition.id === "default-conversation" ? "system-chip" : "version-chip", definition.id === "default-conversation" ? "System" : `v${definition.definitionVersion}`));
    meta.append(node("span", "", definition.inferenceSteps.length === 1 ? "1 step" : `${definition.inferenceSteps.length} steps`));
    button.append(meta);
    button.addEventListener("click", () => selectDefinition(definition));
    elements.list.append(button);
  }
  const knownLoopIds = new Set(allDefinitions().map(definition => definition.id));
  const archivedGroups = new Map();
  for (const run of recentRuns) {
    if (!knownLoopIds.has(run.loopId)) archivedGroups.set(run.loopId, (archivedGroups.get(run.loopId) ?? 0) + 1);
  }
  for (const [loopId, runCount] of archivedGroups) {
    const button = node("button", "loop-list-item");
    button.type = "button";
    button.disabled = mutationInFlight;
    button.setAttribute("role", "option");
    button.setAttribute("aria-selected", loopId === historicalLoopId ? "true" : "false");
    button.classList.toggle("selected", loopId === historicalLoopId);
    button.append(node("span", "loop-list-name", `Deleted loop · ${loopId}`));
    const meta = node("span", "loop-list-meta");
    meta.append(node("span", "system-chip", "Archived evidence"), node("span", "", `${runCount} run${runCount === 1 ? "" : "s"}`));
    button.append(meta);
    button.addEventListener("click", () => selectHistoricalLoop(loopId));
    elements.list.append(button);
  }
}

function selectDefinition(definition) {
  if (mutationInFlight) return;
  if (definition.id === currentDefinition?.id && !historicalLoopId) return;
  if (dirty && !window.confirm("Discard unsaved loop edits?")) return;
  applyDefinition(definition);
}

async function selectHistoricalLoop(loopId) {
  if (mutationInFlight) return;
  if (dirty && !window.confirm("Discard unsaved loop edits?")) return;
  historicalLoopId = loopId;
  currentDefinition = null;
  draft = null;
  dirty = false;
  currentView = "runs";
  selectedRunId = recentRuns.find(run => run.loopId === loopId)?.id ?? null;
  selectedRun = null;
  selectedTrace = null;
  elements.name.value = "";
  elements.description.value = "";
  renderAll();
  await loadRuns({ silent: false, preferredRunId: selectedRunId });
}

function renderCanvas() {
  elements.canvas.replaceChildren();
  if (!draft) {
    elements.canvas.append(node("p", "empty-state", "Create a loop to begin."));
    return;
  }

  elements.canvas.append(createNodeCard("trigger", "Trigger", "Manual trigger", triggerSummary(), "T", "system"));
  appendConnector();
  draft.inferenceSteps.forEach((step, index) => {
    elements.canvas.append(createNodeCard(step.id ?? `local-${index}`, "Inference", step.name || `Step ${index + 1}`, step.instruction || "Instruction required", String(index + 1), "inference", step.contextPolicy?.mode));
    appendConnector();
  });
  const exitPolicy = draft.exitPolicy;
  elements.canvas.append(createNodeCard("exit", "Exit", "Exit", exitPolicy.maxAdditionalIterations > 0 ? `Model-gated · up to ${exitPolicy.maxAdditionalIterations} additional` : "Complete after one iteration", "E", "exit", exitPolicy.contextPolicy?.mode));
  if (exitPolicy.maxAdditionalIterations > 0) {
    const rail = node("div", "repeat-rail");
    rail.append(node("span", "", `Repeat may return to Step 1 · ceiling ${exitPolicy.maxAdditionalIterations}`));
    elements.canvas.append(rail);
  }
}

function createNodeCard(id, kind, name, summary, icon, className, policyMode) {
  const button = node("button", `node-card ${className}`);
  button.type = "button";
  button.classList.toggle("selected", selectedNodeId === id);
  button.setAttribute("aria-pressed", selectedNodeId === id ? "true" : "false");
  button.append(node("span", "node-icon", icon));
  const copy = node("span", "node-copy");
  copy.append(node("span", "node-kind", kind), node("span", "node-name", name), node("span", "node-summary", summary));
  button.append(copy);
  button.append(node("span", "node-policy", policyMode === "custom" ? "custom context" : kind === "Trigger" ? "admission" : "inherits context"));
  button.addEventListener("click", () => {
    selectedNodeId = id;
    renderCanvas();
    renderInspector();
  });
  return button;
}

function appendConnector() {
  elements.canvas.append(node("span", "connector"));
}

function triggerSummary() {
  const trigger = draft.triggerPolicy;
  const source = trigger.promptSource === "preset" ? "Preset prompt" : trigger.promptSource === "none" ? "No prompt" : "Invoking user prompt";
  return `${source} · conversation ${trigger.includeInvokingConversation ? "included" : "excluded"}`;
}

function renderInspector() {
  elements.inspectorContent.replaceChildren();
  if (!draft) {
    elements.inspectorTitle.textContent = "Loop settings";
    elements.inspectorContent.append(node("p", "empty-state", "No loop selected."));
    return;
  }

  if (selectedNodeId === "trigger") {
    renderTriggerInspector();
    return;
  }
  if (selectedNodeId === "exit") {
    renderExitInspector();
    return;
  }
  const step = draft.inferenceSteps.find(item => item.id === selectedNodeId) ?? draft.inferenceSteps.find((_, index) => `local-${index}` === selectedNodeId);
  if (step) renderInferenceInspector(step);
  else renderLoopInspector();
}

function renderLoopInspector() {
  elements.inspectorTitle.textContent = "Loop settings";
  const model = section("Inherited provider and model");
  model.append(node("div", "context-note", `${catalog.runtimeModel?.provider ?? "Provider unavailable"} · ${catalog.runtimeModel?.model || "provider default model"}. Provider and model cannot be overridden per loop in wave one.`));
  const authority = section("Workspace tools");
  authority.append(node("p", "field-hint", "No tools are assigned by default. Exit decisions are always tool-less."));
  for (const assignment of catalog.tools?.customAssignable ?? []) {
    authority.append(checkboxRow(capitalize(assignment), `Allow inference nodes to request the governed ${assignment} command.`, draft.toolAssignments.includes(assignment), checked => {
      draft.toolAssignments = checked ? [...draft.toolAssignments, assignment] : draft.toolAssignments.filter(value => value !== assignment);
      markDirty();
    }, isSystemLoop()));
  }
  const defaults = section("Context defaults");
  defaults.append(node("p", "field-hint", "Versioned server defaults are inspectable here. Context is customized at each Inference or Exit node."));
  defaults.append(contextSummary("Inference", draft.contextDefaults.inference), contextSummary("Exit", draft.contextDefaults.exit));
  defaults.append(evidenceNote());
  elements.inspectorContent.append(model, authority, defaults);
}

function renderTriggerInspector() {
  elements.inspectorTitle.textContent = "Manual trigger";
  const trigger = draft.triggerPolicy;
  const purpose = section("Context admitted to the run");
  const source = document.createElement("select");
  for (const [value, label] of [["invocation", "Invoking user prompt"], ["preset", "Preset prompt"], ["none", "No prompt"]]) {
    const option = document.createElement("option");
    option.value = value; option.textContent = label; option.selected = trigger.promptSource === value; source.append(option);
  }
  source.disabled = isSystemLoop();
  source.addEventListener("change", event => {
    trigger.promptSource = event.target.value;
    if (trigger.promptSource !== "preset") trigger.presetPrompt = "";
    markDirty(); renderInspector(); renderCanvas();
  });
  purpose.append(field("Prompt source", source, "Trigger admits exactly one typed prompt source; sources are never silently combined."));
  if (trigger.promptSource === "preset") {
    const preset = document.createElement("textarea");
    preset.maxLength = catalog.limits.maxTriggerPromptCharacters;
    preset.value = trigger.presetPrompt;
    preset.disabled = isSystemLoop();
    preset.addEventListener("input", event => { trigger.presetPrompt = event.target.value; markDirty(); });
    purpose.append(field("Preset prompt", preset, "Saved prompt supplied whenever this loop is invoked."));
  }
  purpose.append(checkboxRow("Include invoking conversation history", "Admit a bounded snapshot of the logical user session when one exists. Provider-thread history is never used.", trigger.includeInvokingConversation, checked => {
    trigger.includeInvokingConversation = checked; markDirty(); renderCanvas();
  }, isSystemLoop()));
  purpose.append(node("div", "context-note", "The invoking prompt enters once. Trigger admission does not append it again or write durable memory."));
  elements.inspectorContent.append(purpose);
}

function renderInferenceInspector(step) {
  const index = draft.inferenceSteps.indexOf(step);
  elements.inspectorTitle.textContent = `Inference · Step ${index + 1}`;
  const instruction = section("Inference");
  const name = document.createElement("input");
  name.maxLength = catalog.limits.maxNameCharacters; name.value = step.name; name.disabled = isSystemLoop();
  name.addEventListener("input", event => { step.name = event.target.value; markDirty(); renderCanvas(); });
  const prompt = document.createElement("textarea");
  prompt.maxLength = catalog.limits.maxInstructionCharacters; prompt.value = step.instruction; prompt.disabled = isSystemLoop();
  prompt.addEventListener("input", event => { step.instruction = event.target.value; markDirty(); renderCanvas(); });
  instruction.append(field("Node name", name), field("Prompt-visible instruction", prompt, "Write the local objective. Trigger material and earlier output are supplied separately as governed context."));
  const actions = node("div", "inline-actions");
  actions.append(actionButton("↑ Move earlier", () => moveStep(index, -1), index === 0 || isSystemLoop()), actionButton("↓ Move later", () => moveStep(index, 1), index === draft.inferenceSteps.length - 1 || isSystemLoop()), actionButton("Remove", () => removeStep(index), draft.inferenceSteps.length === 1 || isSystemLoop(), "danger-button"));
  instruction.append(actions);
  elements.inspectorContent.append(instruction, contextEditor(step, "inference"));
}

function renderExitInspector() {
  elements.inspectorTitle.textContent = "Exit";
  const exit = draft.exitPolicy;
  const continuation = section("Conditional continuation");
  continuation.append(checkboxRow("Allow continuation requests", "Exit may ask to return to Step 1. The ceiling never causes a repeat by itself.", exit.maxAdditionalIterations > 0, checked => {
    exit.maxAdditionalIterations = checked ? 1 : 0;
    markDirty(); renderInspector(); renderCanvas();
  }, isSystemLoop()));
  if (exit.maxAdditionalIterations > 0) {
    const decision = document.createElement("textarea");
    decision.maxLength = catalog.limits.maxInstructionCharacters; decision.value = exit.decisionInstruction; decision.disabled = isSystemLoop();
    decision.addEventListener("input", event => { exit.decisionInstruction = event.target.value; markDirty(); });
    const ceiling = document.createElement("input");
    ceiling.type = "number"; ceiling.min = "1"; ceiling.max = String(catalog.limits.maxAdditionalIterations); ceiling.value = String(exit.maxAdditionalIterations); ceiling.disabled = isSystemLoop();
    ceiling.addEventListener("change", event => {
      const value = Math.max(1, Math.min(catalog.limits.maxAdditionalIterations, Number.parseInt(event.target.value, 10) || 1));
      exit.maxAdditionalIterations = value; event.target.value = String(value); markDirty(); renderCanvas();
    });
    continuation.append(field("Decision instruction", decision, "The trimmed response must be exactly one Complete or Repeat token (case-insensitive). Invalid or uncertain decisions never repeat."), field("Maximum additional iterations", ceiling, "A hard ceiling, not a target. No Exit call is made once the ceiling is exhausted."));
  }
  continuation.append(node("div", "context-note", "Exit is tool-less. With continuation off, the run completes after one iteration without an Exit model call."));
  elements.inspectorContent.append(continuation, contextEditor(exit, "exit"));
}

function contextEditor(owner, kind) {
  const container = section(`${capitalize(kind)} context`);
  const select = document.createElement("select");
  for (const [value, label] of [["inherit", "Inherit loop defaults"], ["custom", "Customize this node"]]) {
    const option = document.createElement("option"); option.value = value; option.textContent = label; option.selected = owner.contextPolicy.mode === value; select.append(option);
  }
  select.disabled = isSystemLoop();
  select.addEventListener("change", event => {
    owner.contextPolicy = event.target.value === "custom"
      ? { mode: "custom", customPolicy: clone(draft.contextDefaults[kind]) }
      : { mode: "inherit", customPolicy: null };
    markDirty(); renderInspector(); renderCanvas();
  });
  container.append(field("Policy source", select));
  const policy = owner.contextPolicy.mode === "custom" ? owner.contextPolicy.customPolicy : draft.contextDefaults[kind];
  const disabled = isSystemLoop() || owner.contextPolicy.mode !== "custom";
  container.append(node("h3", "section-heading", "Context in"));
  const inputOptions = [
    ["includeRoleContext", "Directory role and startup context", "Role files and bounded workspace memory/context. Harness governance remains even when this is off."],
    ["includeTriggerPrompt", "Trigger prompt", "The invocation or preset prompt admitted by Trigger."],
    ["includeInvokingConversation", "Invoking conversation history", "Logical session history admitted by Trigger, never provider-thread history."],
    ["includeEarlierRetainedOutputs", "Earlier retained outputs", "Only outputs whose producer retained them for loop reasoning."],
    ["includePreviousIterationResult", "Previous iteration result", "The prior result after an accepted Exit Repeat decision."]
  ];
  const inputGrid = node("div", "context-grid");
  for (const [key, label, hint] of inputOptions) inputGrid.append(checkboxRow(label, hint, policy.contextIn[key], checked => { policy.contextIn[key] = checked; markDirty(); }, disabled));
  container.append(inputGrid, node("h3", "section-heading", "Context out"));
  const outputGrid = node("div", "context-grid");
  outputGrid.append(checkboxRow("Retain for later loop reasoning", "Makes this canonical output selectable at later model boundaries.", policy.contextOut.retainForLoopReasoning, checked => { policy.contextOut.retainForLoopReasoning = checked; markDirty(); }, disabled));
  outputGrid.append(checkboxRow("Publish to the invoking conversation", "Appends idempotently only to the server-bound invoking conversation when one exists.", policy.contextOut.publishToInvokingConversation, checked => { policy.contextOut.publishToInvokingConversation = checked; markDirty(); }, disabled));
  container.append(outputGrid, evidenceNote());
  return container;
}

function contextSummary(label, policy) {
  const enabledIn = Object.values(policy.contextIn).filter(Boolean).length;
  const enabledOut = Object.values(policy.contextOut).filter(Boolean).length;
  return node("div", "context-note", `${label}: ${enabledIn} context-in sources · ${enabledOut} context-out destinations`);
}

function evidenceNote() {
  const note = node("div", "context-note");
  const strong = node("strong", "", "Evidence is independent of context. ");
  note.append(strong, document.createTextNode("Even when both destinations are off, bounded output remains inspectable in the run trace. Durable memory writeback is a separate governed action and is not automatic."));
  return note;
}

function renderToolbar() {
  const editable = Boolean(draft) && !isSystemLoop() && !mutationInFlight;
  elements.name.disabled = !editable;
  elements.description.disabled = !editable;
  elements.saveButton.disabled = !editable || !dirty || validateDraft().length > 0;
  elements.reloadButton.disabled = mutationInFlight || !draft || !dirty;
  elements.deleteButton.disabled = !editable;
  elements.invokeButton.disabled = !editable || dirty;
  elements.addStepButton.disabled = !editable || draft.inferenceSteps.length >= catalog.limits.maxInferenceSteps;
  elements.loopSettingsButton.disabled = mutationInFlight || !draft;
  elements.createLoopButton.disabled = mutationInFlight || !catalog || catalog.customDefinitions.length >= catalog.limits.maxDefinitionsPerWorkspace;
  elements.saveState.textContent = historicalLoopId ? "Archived evidence" : !draft ? "No loop" : isSystemLoop() ? "System managed" : dirty ? "Unsaved changes" : `Saved · v${draft.definitionVersion}`;
}

function renderValidation() {
  const errors = validateDraft();
  if (errors.length === 0) {
    elements.validationBanner.textContent = "";
    elements.validationBanner.className = "validation-banner";
    return;
  }
  elements.validationBanner.textContent = errors[0];
  elements.validationBanner.className = "validation-banner visible";
}

function validateDraft() {
  if (!draft || isSystemLoop()) return [];
  const errors = [];
  if (!draft.displayName.trim()) errors.push("Loop name is required.");
  if (draft.inferenceSteps.length < catalog.limits.minInferenceSteps || draft.inferenceSteps.length > catalog.limits.maxInferenceSteps) errors.push(`Use ${catalog.limits.minInferenceSteps}–${catalog.limits.maxInferenceSteps} inference steps.`);
  if (draft.inferenceSteps.some(step => !step.name.trim() || !step.instruction.trim())) errors.push("Every inference step needs a name and instruction.");
  if (draft.triggerPolicy.promptSource === "preset" && !draft.triggerPolicy.presetPrompt.trim()) errors.push("Preset trigger prompt is required.");
  if (draft.triggerPolicy.promptSource !== "preset" && draft.triggerPolicy.presetPrompt) errors.push("Unused preset prompt must be empty.");
  if (draft.exitPolicy.maxAdditionalIterations > 0 && !draft.exitPolicy.decisionInstruction.trim()) errors.push("Exit decision instruction is required when continuation is enabled.");
  return errors;
}

function markDirty() {
  if (isSystemLoop()) return;
  dirty = true;
  renderToolbar();
  renderValidation();
}

function updateDraftValue(fieldName, value) {
  if (mutationInFlight || !draft || isSystemLoop()) return;
  draft[fieldName] = value;
  markDirty();
  renderListDraftName();
}

function renderListDraftName() {
  const selected = elements.list.querySelector('[aria-selected="true"] .loop-list-name');
  if (selected) selected.textContent = draft.displayName || "Untitled loop";
}

async function createLoop() {
  if (mutationInFlight) return;
  if (dirty && !window.confirm("Discard unsaved loop edits and create a new loop?")) return;
  pendingCreateOperationId ??= newOperationId();
  setBusy(true, "Creating");
  try {
    const response = await requestJson("/api/loops", { method: "POST", body: JSON.stringify({ operationId: pendingCreateOperationId }) });
    await loadCatalog(response.definition.id);
    pendingCreateOperationId = null;
    showToast("Loop created. Add instructions, review context, then Save.");
  } catch (error) {
    showResponseError(error);
  } finally {
    setBusy(false);
  }
}

async function saveLoop() {
  if (mutationInFlight) return;
  const errors = validateDraft();
  if (errors.length > 0) { showBanner(errors[0]); return; }
  setBusy(true, "Saving");
  try {
    const definition = {
      displayName: draft.displayName,
      description: draft.description,
      triggerPolicy: clone(draft.triggerPolicy),
      inferenceSteps: draft.inferenceSteps.map(step => ({ id: step.id?.startsWith("local-") ? null : step.id, name: step.name, instruction: step.instruction, contextPolicy: clone(step.contextPolicy) })),
      toolAssignments: [...draft.toolAssignments],
      exitPolicy: clone(draft.exitPolicy)
    };
    const response = await requestJson(`/api/loops/${encodeURIComponent(draft.id)}`, { method: "PUT", body: JSON.stringify({ expectedDefinitionVersion: currentDefinition.definitionVersion, operationId: newOperationId(), definition }) });
    await loadCatalog(response.definition.id);
    showToast(response.status === "CommittedWithAuditWarning" ? response.detail : "Loop saved.");
  } catch (error) {
    showResponseError(error);
  } finally {
    setBusy(false);
  }
}

async function deleteLoop() {
  if (mutationInFlight) return;
  if (!draft || isSystemLoop() || !window.confirm(`Delete “${draft.displayName}”? Historical run evidence will remain available.`)) return;
  setBusy(true, "Deleting");
  try {
    const response = await requestJson(`/api/loops/${encodeURIComponent(draft.id)}`, { method: "DELETE", body: JSON.stringify({ expectedDefinitionVersion: currentDefinition.definitionVersion, operationId: newOperationId() }) });
    currentDefinition = null;
    await loadCatalog("default-conversation");
    showToast(response.status === "CommittedWithAuditWarning" ? response.detail ?? "Loop deleted, but its outcome audit has an integrity warning. Historical run evidence was preserved." : "Loop deleted. Historical run evidence was preserved.");
  } catch (error) {
    showResponseError(error);
  } finally {
    setBusy(false);
  }
}

function openInvokeModal() {
  if (!draft || isSystemLoop() || dirty) return;
  invokeReturnFocus = document.activeElement ?? elements.invokeButton;
  const trigger = draft.triggerPolicy;
  const promptRequired = trigger.promptSource === "invocation";
  elements.invocationPrompt.value = "";
  elements.invocationPrompt.maxLength = catalog.limits.maxTriggerPromptCharacters;
  elements.invocationPromptField.hidden = !promptRequired;
  elements.invokeSummary.textContent = `${draft.displayName} v${draft.definitionVersion} will run the saved definition with ${draft.inferenceSteps.length} ordered inference step${draft.inferenceSteps.length === 1 ? "" : "s"} using ${catalog.runtimeModel?.provider ?? "the configured provider"} · ${catalog.runtimeModel?.model || "provider default model"}. Trigger source: ${promptSourceLabel(trigger.promptSource)}. Invoking conversation: ${trigger.includeInvokingConversation ? "admitted as a bounded snapshot" : "excluded from model context"}.`;
  const destinations = [...draft.inferenceSteps.map(step => resolvedPolicy(step, "inference")), resolvedPolicy(draft.exitPolicy, "exit")].filter(policy => policy.contextOut.publishToInvokingConversation).length;
  elements.invokeLimits.textContent = `Hard bounds: ${catalog.limits.maxModelAttemptsPerRun} model attempts per run, ${catalog.limits.maxGovernedToolRequestsPerAttempt} governed tool requests per attempt, and ${catalog.limits.maxGovernedToolRequestsPerRun} per run, within ${formatDuration(catalog.limits.maxRunExecutionMilliseconds)} of accumulated execution time. Each canonical model output is capped at ${catalog.limits.maxCanonicalModelOutputCharacters.toLocaleString()} characters. Conversation snapshots retain at most ${catalog.limits.maxInvokingConversationCharacters.toLocaleString()} characters across ${catalog.limits.maxInvokingConversationEntries} selected messages; older omissions are aggregated. Tool targets are capped at ${catalog.limits.maxGovernedToolTargetCharacters.toLocaleString()} characters, arguments at ${catalog.limits.maxGovernedToolArgumentCharacters.toLocaleString()}, and the exact formatted result returned to the model at ${catalog.limits.maxCanonicalToolResultCharacters.toLocaleString()} characters. Run evidence is capped at ${catalog.limits.maxTraceEventsPerRun} events, including ${catalog.limits.maxLifecycleControlEventsPerRun} lifecycle/control events, and ${formatBytes(catalog.limits.maxRunTraceUtf8Bytes)}. Assigned tools: ${draft.toolAssignments.length ? draft.toolAssignments.join(", ") : "none"}. ${destinations} node output destination${destinations === 1 ? "" : "s"} may publish to the bound invoking conversation.`;
  elements.startRunButton.disabled = false;
  elements.invokeModal.classList.toggle("open", true);
  elements.invokeModal.setAttribute("aria-hidden", "false");
  window.setTimeout(() => (promptRequired ? elements.invocationPrompt : elements.startRunButton).focus?.(), 0);
}

function closeInvokeModal() {
  elements.invokeModal.classList.toggle("open", false);
  elements.invokeModal.setAttribute("aria-hidden", "true");
  invokeReturnFocus?.focus?.();
  invokeReturnFocus = null;
}

async function startRun() {
  if (!draft || dirty || isSystemLoop()) return;
  const invocationPrompt = draft.triggerPolicy.promptSource === "invocation" ? elements.invocationPrompt.value : null;
  if (draft.triggerPolicy.promptSource === "invocation" && !invocationPrompt.trim()) {
    showBanner("This loop requires an initial user prompt.");
    return;
  }

  elements.startRunButton.disabled = true;
  elements.startRunButton.textContent = "Running";
  let operationId = null;
  try {
    const connection = await getHub();
    operationId = newOperationId();
    const invocation = connection.invoke("InvokeLoop", {
      loopId: draft.id,
      expectedDefinitionVersion: draft.definitionVersion,
      expectedDefinitionHash: draft.contentHash,
      operationId,
      invocationPrompt
    });
    closeInvokeModal();
    currentView = "runs";
    selectedRunId = null;
    selectedRun = null;
    selectedTrace = null;
    renderAll();
    const response = await waitForRunOperation(invocation, { preferredAdmissionOperationId: operationId, preserveEmptySelection: true });
    if (response?.admissionStatus !== "Admitted" || !response?.run) {
      await loadRuns({ silent: true, preserveEmptySelection: true });
      renderAll();
      showBanner(`Run was not admitted: ${response?.detail ?? "The runtime rejected the invocation."}`);
      return;
    }
    selectedRunId = response.run.id;
    selectedRun = response.run;
    await loadRuns({ silent: true, preferredRunId: response.run.id });
    renderAll();
    showToast(response?.detail ?? "Run finished. Durable evidence is available in Runs.");
  } catch (error) {
    const reconciled = operationId
      ? await loadRuns({ silent: true, preferredAdmissionOperationId: operationId, preserveEmptySelection: true })
      : false;
    if (reconciled && selectedRun?.admissionOperationId === operationId) {
      renderAll();
      showBanner("The live connection was lost after admission. Durable run evidence was recovered; monitoring continues while the run remains active.");
    } else {
      showBanner(`Run could not start: ${error.message}`);
    }
  } finally {
    elements.startRunButton.disabled = false;
    elements.startRunButton.textContent = "Start run";
  }
}

async function controlRun(action) {
  if (!selectedRun) return;
  try {
    const response = await requestJson(`/api/loop-runs/${encodeURIComponent(selectedRun.id)}/${action}`, {
      method: "POST",
      body: JSON.stringify({ expectedLifecycleVersion: selectedRun.lifecycleVersion, operationId: newOperationId() })
    });
    selectedRun = response.run ?? response;
    await loadRuns({ silent: true });
    showToast(response.detail ?? `${capitalize(action)} request recorded.`);
  } catch (error) {
    showBanner(`${capitalize(action)} failed: ${error.message}`);
  }
}

async function resumeRun() {
  if (!selectedRun || selectedRun.status !== "Paused") return;
  try {
    const connection = await getHub();
    const operationId = newOperationId();
    const invocation = connection.invoke("ResumeLoop", {
      runId: selectedRun.id,
      expectedLifecycleVersion: selectedRun.lifecycleVersion,
      operationId
    });
    const response = await waitForRunOperation(invocation, { preferredRunId: selectedRun.id });
    if (!["Resumed", "Completed", "Cancelled", "Paused", "NeedsReview"].includes(response?.status)) {
      await loadRuns({ silent: true, preferredRunId: selectedRun.id });
      renderAll();
      showBanner(`Resume failed: ${response?.detail ?? "The runtime rejected the Resume operation."}`);
      return;
    }
    selectedRun = response?.run ?? selectedRun;
    await loadRuns({ silent: true });
    showToast(response?.detail ?? "Resume completed.");
  } catch (error) {
    showBanner(`Resume failed: ${error.message}`);
  }
}

async function waitForRunOperation(invocation, preferredSelection) {
  activeRunOperationMonitors++;
  let settled = false;
  invocation.then(() => { settled = true; }, () => { settled = true; });
  try {
    while (!settled) {
      await loadRuns({ silent: true, ...preferredSelection });
      if (!settled) await new Promise(resolve => setTimeout(resolve, 500));
    }
    return await invocation;
  } finally {
    activeRunOperationMonitors--;
    scheduleSelectedRunRefresh();
  }
}

function scheduleSelectedRunRefresh() {
  if (selectedRunRefreshTimer != null) {
    window.clearTimeout(selectedRunRefreshTimer);
    selectedRunRefreshTimer = null;
  }
  if (activeRunOperationMonitors > 0 || currentView !== "runs" || !selectedRun || !isNonterminalRun(selectedRun)) return;
  const runId = selectedRun.id;
  selectedRunRefreshTimer = window.setTimeout(async () => {
    selectedRunRefreshTimer = null;
    if (currentView !== "runs" || selectedRun?.id !== runId) return;
    try {
      await loadRuns({ silent: true, preferredRunId: runId });
    } finally {
      if (currentView === "runs" && selectedRun?.id === runId && isNonterminalRun(selectedRun)) scheduleSelectedRunRefresh();
    }
  }, 1000);
}

function isNonterminalRun(run) {
  return ["Admitted", "Running", "PauseRequested", "Paused", "CancelRequested"].includes(run?.status);
}

async function deleteSelectedTrace() {
  if (!selectedRun || !selectedTrace || selectedTrace.isDeleted || !["Completed", "Failed", "Cancelled", "NeedsReview"].includes(selectedRun.status)) return;
  const confirmed = window.confirm(`Permanently delete the sensitive trace content for ${selectedRun.id}?\n\nPrompts, captured context, outputs, and tool evidence will be removed. A small audited tombstone will remain. This cannot be undone.`);
  if (!confirmed) return;

  const runId = selectedRun.id;
  const expectedTraceHash = selectedTrace.persistedArtifactHash;
  if (pendingTraceDeletion?.runId !== runId || pendingTraceDeletion.expectedTraceHash !== expectedTraceHash) {
    pendingTraceDeletion = { runId, expectedTraceHash, operationId: newOperationId() };
  }
  let deletion;
  try {
    deletion = await requestJson(`/api/loop-runs/${encodeURIComponent(runId)}/trace/delete`, {
      method: "POST",
      body: JSON.stringify({ expectedTraceHash, operationId: pendingTraceDeletion.operationId })
    });
  } catch (error) {
    showBanner(`Trace deletion failed: ${error.message}`);
    return;
  }

  pendingTraceDeletion = null;

  selectedRun = null;
  selectedTrace = null;
  recentRuns = recentRuns.filter(run => run.id !== runId);
  renderAll();
  const refreshed = await loadRuns({ silent: true, preferredRunId: runId });
  const warning = deletion.status === "CommittedWithAuditWarning" ? " The deletion committed, but its outcome audit has an integrity warning." : "";
  showToast(`Sensitive trace content deleted; the audited tombstone remains.${warning}`);
  if (!refreshed) showBanner("Trace deletion committed, but refreshed tombstone and quota evidence could not be loaded. Reload Runs to inspect the durable outcome.");
}

async function getHub() {
  if (hub?.connected) return hub;
  hub = new JsonSignalRConnection(createHubUrl());
  hub.on("ApprovalsChanged", renderLoopApprovals);
  hub.onclose = () => { renderLoopApprovals([]); hub = null; };
  await hub.start();
  return hub;
}

function renderLoopApprovals(approvals) {
  const pending = Array.isArray(approvals) ? approvals : [];
  elements.approvalCount.textContent = `${pending.length} pending`;
  elements.approvalPanel.hidden = pending.length === 0;
  elements.approvals.replaceChildren(...pending.map(renderLoopApproval));
  if (currentView === "runs" && selectedRun) renderRunEvidence();
}

function renderLoopApproval(approval) {
  const item = node("article", "approval-item");
  item.append(node("strong", "", `${formatStatus(approval.command)} ${approval.operation}`));
  item.append(node("div", "evidence-code", [
    `target ${approval.targetPath}`,
    `resolved ${approval.resolvedPath}`,
    `matched permission ${approval.matchedPath}`,
    approval.reason
  ].filter(Boolean).join("\n")));
  const actions = node("div", "approval-actions");
  const reject = actionButton("Reject", () => decideLoopApproval(approval.requestId, false, reject), false, "danger-button");
  const approve = actionButton("Approve", () => decideLoopApproval(approval.requestId, true, approve), false, "primary-button");
  actions.append(reject, approve);
  item.append(actions);
  return item;
}

async function decideLoopApproval(requestId, approved, button) {
  button.disabled = true;
  try {
    const connection = await getHub();
    const result = await connection.invoke("DecideApproval", requestId, { approved });
    if (!result?.accepted) showBanner(result?.message ?? "The approval decision was not accepted.");
  } catch (error) {
    showBanner(`Approval decision failed: ${error.message}`);
  } finally {
    button.disabled = false;
  }
}

function createHubUrl() {
  const url = new URL("/hubs/session", window.location.href);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  url.searchParams.set("access_token", sessionToken);
  return url.toString();
}

function resolvedPolicy(owner, kind) {
  return owner.contextPolicy.mode === "custom" ? owner.contextPolicy.customPolicy : draft.contextDefaults[kind];
}

function promptSourceLabel(value) {
  return value === "invocation" ? "initial user prompt" : value === "preset" ? "saved preset prompt" : "no prompt";
}

function reloadCurrent() {
  if (mutationInFlight || !currentDefinition) return;
  if (dirty && !window.confirm("Discard unsaved loop edits?")) return;
  applyDefinition(currentDefinition);
}

function addInferenceStep() {
  if (!draft || isSystemLoop() || draft.inferenceSteps.length >= catalog.limits.maxInferenceSteps) return;
  const id = `local-${newOperationId()}`;
  draft.inferenceSteps.push({ id, name: `Step ${draft.inferenceSteps.length + 1}`, instruction: "", contextPolicy: { mode: "inherit", customPolicy: null } });
  selectedNodeId = id;
  markDirty();
  renderCanvas(); renderInspector(); renderToolbar();
}

function moveStep(index, delta) {
  const next = index + delta;
  if (next < 0 || next >= draft.inferenceSteps.length) return;
  const [step] = draft.inferenceSteps.splice(index, 1);
  draft.inferenceSteps.splice(next, 0, step);
  markDirty(); renderCanvas(); renderInspector();
}

function removeStep(index) {
  if (draft.inferenceSteps.length <= 1) return;
  draft.inferenceSteps.splice(index, 1);
  selectedNodeId = draft.inferenceSteps[Math.min(index, draft.inferenceSteps.length - 1)].id;
  markDirty(); renderCanvas(); renderInspector();
}

function setBusy(busy, label) {
  mutationInFlight = busy;
  for (const region of [elements.list, elements.builderView, elements.runsView]) {
    region.inert = busy;
    region.setAttribute("aria-busy", String(busy));
  }
  if (busy) {
    renderAll();
    elements.saveState.textContent = label;
  } else {
    renderList();
    renderTabs();
    renderToolbar();
  }
}

function setInteractive(enabled) {
  for (const button of [elements.createLoopButton, elements.saveButton, elements.deleteButton, elements.reloadButton, elements.addStepButton, elements.invokeButton, elements.builderTab, elements.runsTab]) button.disabled = !enabled;
  elements.name.disabled = !enabled;
  elements.description.disabled = !enabled;
}

function showResponseError(error) {
  const validation = error.payload?.validationErrors;
  const conflict = error.payload?.conflict;
  if (Array.isArray(validation) && validation.length > 0) {
    showBanner(`${validation[0].field}: ${validation[0].message}`);
  } else if (error.status === 409 && conflict?.actualDefinitionVersion) {
    showBanner(`${error.message} Server version ${conflict.actualDefinitionVersion}. Reload before applying the edit again.`);
  } else {
    showBanner(error.message);
  }
}

function showBanner(message, style) {
  elements.validationBanner.textContent = message;
  elements.validationBanner.className = `validation-banner visible${style ? ` ${style}` : ""}`;
}

function showToast(message) {
  elements.toast.textContent = message;
  elements.toast.hidden = false;
  window.clearTimeout(showToast.timer);
  showToast.timer = window.setTimeout(() => { elements.toast.hidden = true; }, 4200);
}

function section(title) {
  const container = node("section", "form-section");
  container.append(node("h3", "section-heading", title));
  return container;
}

function field(labelText, control, hint) {
  const label = document.createElement("label");
  label.append(node("span", "", labelText), control);
  if (hint) label.append(node("span", "field-hint", hint));
  return label;
}

function checkboxRow(labelText, hint, checked, handler, disabled) {
  const label = node("label", "checkbox-row");
  const input = document.createElement("input");
  input.type = "checkbox"; input.checked = Boolean(checked); input.disabled = Boolean(disabled);
  input.addEventListener("change", event => handler(event.target.checked));
  const copy = node("span", "", labelText);
  if (hint) copy.append(node("small", "", hint));
  label.append(input, copy);
  return label;
}

function actionButton(label, handler, disabled, className = "secondary-button") {
  const button = node("button", className, label);
  button.type = "button"; button.disabled = disabled; button.addEventListener("click", handler);
  return button;
}

function node(tagName, className, text) {
  const element = document.createElement(tagName);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
}

function clone(value) {
  return typeof structuredClone === "function" ? structuredClone(value) : JSON.parse(JSON.stringify(value));
}

function newOperationId() {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID().toLowerCase();
  return `op-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
}

function capitalize(value) {
  return value ? value[0].toUpperCase() + value.slice(1) : value;
}

function isSystemLoop() {
  return draft?.id === "default-conversation";
}

function statusClass(value) {
  return String(value ?? "unknown").replace(/[^a-z0-9]/gi, "").toLowerCase();
}

function formatStatus(value) {
  return splitWords(value || "Unknown");
}

function splitWords(value) {
  return String(value ?? "").replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[_-]+/g, " ");
}

function formatTimestamp(value) {
  if (!value) return "Unknown time";
  const timestamp = new Date(value);
  return Number.isNaN(timestamp.valueOf()) ? String(value) : timestamp.toLocaleString([], { dateStyle: "medium", timeStyle: "medium" });
}

function formatBytes(value) {
  const bytes = Number(value);
  if (!Number.isFinite(bytes) || bytes < 0) return "Unknown size";
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KiB", "MiB", "GiB"];
  let size = bytes / 1024;
  let index = 0;
  while (size >= 1024 && index < units.length - 1) { size /= 1024; index++; }
  return `${size >= 10 ? size.toFixed(1) : size.toFixed(2)} ${units[index]}`;
}

function formatDuration(value) {
  const milliseconds = Number(value);
  if (!Number.isFinite(milliseconds) || milliseconds < 0) return "unknown duration";
  const totalSeconds = Math.ceil(milliseconds / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return [hours ? `${hours}h` : null, minutes ? `${minutes}m` : null, seconds || (!hours && !minutes) ? `${seconds}s` : null].filter(Boolean).join(" ");
}

class JsonSignalRConnection {
  constructor(url) {
    this.url = url;
    this.socket = null;
    this.handlers = new Map();
    this.invocations = new Map();
    this.nextInvocationId = 0;
    this.buffer = "";
    this.connected = false;
    this.closedByClient = false;
    this.isClosed = true;
    this.handshakeReject = null;
    this.handshakeResolve = null;
    this.keepAliveTimer = null;
    this.onclose = null;
  }

  on(target, handler) {
    this.handlers.set(target, handler);
  }

  async start() {
    this.closedByClient = false;
    this.isClosed = false;
    this.socket = new WebSocket(this.url);
    this.socket.onmessage = event => this.receive(event.data);
    this.socket.onclose = () => this.handleClose();
    await new Promise((resolve, reject) => {
      this.socket.onopen = resolve;
      this.socket.onerror = () => reject(new Error("SignalR connection failed."));
    });
    const handshake = new Promise((resolve, reject) => {
      this.handshakeResolve = resolve;
      this.handshakeReject = reject;
      window.setTimeout(() => this.handshakeReject?.(new Error("SignalR handshake timed out.")), 5000);
    });
    this.socket.onerror = () => this.handleClose();
    this.sendRaw({ protocol: "json", version: 1 });
    await handshake;
    this.connected = true;
    this.startKeepAlive();
  }

  async invoke(target, ...args) {
    if (!this.connected || !this.socket || this.socket.readyState !== WebSocket.OPEN) throw new Error("SignalR connection is not available.");
    const invocationId = String(this.nextInvocationId++);
    const completion = new Promise((resolve, reject) => this.invocations.set(invocationId, { resolve, reject }));
    this.sendRaw({ type: 1, invocationId, target, arguments: args });
    return await completion;
  }

  sendRaw(message) {
    this.socket.send(`${JSON.stringify(message)}${signalRRecordSeparator}`);
  }

  startKeepAlive() {
    this.stopKeepAlive();
    this.keepAliveTimer = window.setInterval(() => {
      if (this.connected && this.socket?.readyState === WebSocket.OPEN) this.sendRaw({ type: 6 });
    }, signalRKeepAliveMilliseconds);
  }

  stopKeepAlive() {
    if (this.keepAliveTimer == null) return;
    window.clearInterval(this.keepAliveTimer);
    this.keepAliveTimer = null;
  }

  async receive(data) {
    const text = typeof data === "string" ? data : await data.text();
    this.buffer += text;
    const messages = this.buffer.split(signalRRecordSeparator);
    this.buffer = messages.pop() ?? "";
    for (const messageText of messages) {
      if (!messageText) continue;
      const message = JSON.parse(messageText);
      if (!message.type) {
        if (message.error) this.handshakeReject?.(new Error(message.error));
        else this.handshakeResolve?.();
        continue;
      }
      this.handleMessage(message);
    }
  }

  handleMessage(message) {
    if (message.type === 1) {
      this.handlers.get(message.target)?.(...(message.arguments ?? []));
      return;
    }
    if (message.type === 3) {
      const invocation = this.invocations.get(message.invocationId);
      if (!invocation) return;
      this.invocations.delete(message.invocationId);
      if (message.error) invocation.reject(new Error(message.error));
      else invocation.resolve(message.result);
      return;
    }
    if (message.type === 7) this.handleClose();
  }

  handleClose() {
    if (this.isClosed) return;
    this.isClosed = true;
    this.connected = false;
    this.stopKeepAlive();
    this.handshakeReject?.(new Error("SignalR connection closed."));
    for (const invocation of this.invocations.values()) invocation.reject(new Error("SignalR connection closed."));
    this.invocations.clear();
    this.socket = null;
    if (!this.closedByClient) this.onclose?.();
  }
}

boot();
