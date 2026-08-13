let catalog = null;
let currentDefinition = null;
let draft = null;
let selectedNodeId = "trigger";
let lastSelectedNodeId = "trigger";
let loopSearchQuery = "";
let canvasZoom = 1;
let loopBuilderActivated = false;
let loopBuilderEventsBound = false;
let loopBuilderSurfaceActive = false;
let loopBuilderRefresh = null;
let loopBuilderRefreshAbortController = null;
let loopBuilderRefreshQueued = false;
let loopBuilderRecoveryQueued = false;
let loopBuilderSessionAvailable =
  window.embodySenseSession?.getState?.().connected ?? true;
let requestedLoopDeepLink = readRequestedLoopDeepLink();
let loopBuilderSessionAbortController = new AbortController();
let workspaceStatusSnapshot = null;
let workspaceInitializationInFlight = false;
let workspaceInitializationGeneration = 0;
let workspaceInitializationPhase = "idle";
let workspaceInitializationMessage = "";
let workspaceAuthoringHydrated = false;
let dirty = false;
let currentView = "builder";
let retentionPosture = null;
let retentionPostureFailure = null;
let retentionCleanupOutcome = null;
let retentionCleanupInFlight = false;
let retentionCleanupRegistryFailure = null;
let retentionPostureRequestGeneration = 0;
let retentionRecoveryRenderTimer = null;
const retentionCleanupOperationIds = new Map();
let recentRuns = [];
let runContinuationCursor = null;
let runPaginationLoopId = null;
let runPaginationExtended = false;
let workspaceRunContinuationCursor = null;
let workspaceRunPaginationExtended = false;
let loadingMoreRuns = false;
let loadingMoreRunsLoopId = null;
let runEvidenceRequestGeneration = 0;
let runSelectionGeneration = 0;
let selectedRunId = null;
let selectedRun = null;
let selectedTrace = null;
let selectedRunMonitorId = null;
let selectedRunMonitorEtag = null;
let selectedRunMonitorMissCount = 0;
let selectedRunMonitorFailureKind = null;
let selectedRunMonitorFallbackFailureCount = 0;
let selectedRunMonitorNextFallbackAt = 0;
let traceQuota = null;
let hub = null;
let invokeReturnFocus = null;
let historicalLoopId = null;
let selectedRunRefreshTimer = null;
let selectedRunRefreshInFlight = false;
let activeRunOperationMonitors = 0;
let mutationInFlight = false;
let newLoopDraftOperationId = null;
let newLoopDraftCommitState = null;
let newLoopDraftFailureDetail = null;
let pendingCreateRequest = null;
const newLoopDraftStorageKeyPrefix = "embodysense.unsaved-loop-draft.v1";
const supportedCustomToolAssignments = Object.freeze([
  "list",
  "read",
  "search",
]);
let newLoopDraftStorageKey = null;
let pendingUpdateRequest = null;
let pendingDeleteRequest = null;
let pendingTraceDeletion = null;
let invocationInFlight = false;
let activeInvocationAttempt = null;
const pendingLifecycleStorageKeyPrefix =
  "embodysense.pending-loop-lifecycle.v1";
const pendingLifecycleRegistryLockNamePrefix =
  "embodysense.pending-loop-lifecycle";
let pendingLifecycleStorageKey = null;
let pendingLifecycleRegistryLockName = null;
let reconciledPendingLifecycleStorageKey = null;
const maximumPendingLifecycleRequests = 100;
const maximumConcurrentLifecycleReceiptReads = 8;
const pendingLifecycleReconciliationDeadlineMilliseconds = 2000;
const pendingLifecycleRequests = new Map();
const retentionCleanupStorageKeyPrefix =
  "embodysense.pending-receipt-cleanup.v1";
const retentionCleanupRegistryLockNamePrefix =
  "embodysense.pending-receipt-cleanup";
let retentionCleanupStorageKey = null;
let retentionCleanupRegistryLockName = null;
const pendingInvocationStorageKeyPrefix =
  "embodysense.pending-loop-invocations.v1";
const pendingInvocationRegistryLockNamePrefix =
  "embodysense.pending-loop-invocations";
let pendingInvocationStorageKey = null;
let pendingInvocationRegistryLockName = null;
const maximumPendingInvocationRequests = 100;
const pendingInvocationRequests = new Map();
const workspaceInitializationLockNamePrefix =
  "embodysense.workspace-initialization.v1";

const signalRRecordSeparator = "\u001e";
const signalRKeepAliveMilliseconds = 10000;
const selectedRunMonitorFallbackBaseDelayMilliseconds = 5000;
const selectedRunMonitorFallbackMaximumDelayMilliseconds = 30000;
const invocationReconciliationMaximumAttempts = 20;
const invocationReconciliationDelayMilliseconds = 500;
const invocationReconciliationDeadlineMilliseconds =
  invocationReconciliationMaximumAttempts *
  invocationReconciliationDelayMilliseconds;
const pendingInvocationRegistryReconciliationDeadlineMilliseconds = 2000;

const elements = {
  addStepButton: document.getElementById("addStepButton"),
  appShell: document.getElementById("appShell"),
  approvalCount: document.getElementById("loopApprovalCount"),
  approvalPanel: document.getElementById("loopApprovalPanel"),
  approvals: document.getElementById("loopApprovals"),
  builderTab: document.getElementById("builderTab"),
  builderLayout: document.getElementById("builderLayout"),
  builderView: document.getElementById("builderView"),
  cancelInvokeButton: document.getElementById("cancelInvokeButton"),
  canvas: document.getElementById("loopCanvas"),
  canvasAuthority: document.getElementById("canvasAuthority"),
  canvasStepCount: document.getElementById("canvasStepCount"),
  closeInvokeButton: document.getElementById("closeInvokeButton"),
  createLoopButton: document.getElementById("createLoopButton"),
  deleteButton: document.getElementById("deleteButton"),
  description: document.getElementById("loopDescription"),
  inspectorContent: document.getElementById("inspectorContent"),
  inspectorTabs: document.getElementById("inspectorTabs"),
  inspectorTitle: document.getElementById("inspectorTitle"),
  invocationPrompt: document.getElementById("invocationPrompt"),
  invocationPromptField: document.getElementById("invocationPromptField"),
  initializeWorkspaceButton: document.getElementById(
    "initializeLoopsWorkspaceButton",
  ),
  invokeButton: document.getElementById("invokeButton"),
  invokeError: document.getElementById("invokeError"),
  invokeLimits: document.getElementById("invokeLimits"),
  invokeModal: document.getElementById("invokeModal"),
  invokeSummary: document.getElementById("invokeSummary"),
  list: document.getElementById("loopList"),
  loopHeaderMeta: document.getElementById("loopHeaderMeta"),
  loopsView: document.getElementById("loopsView"),
  loopSearch: document.getElementById("loopSearch"),
  loopSettingsButton: document.getElementById("loopSettingsButton"),
  initializationAnnouncement: document.getElementById(
    "loopInitializationAnnouncement",
  ),
  initializationPanel: document.getElementById("loopInitializationPanel"),
  initializationRoot: document.getElementById("loopInitializationRoot"),
  initializationStatus: document.getElementById("loopInitializationStatus"),
  declineInitializationButton: document.getElementById(
    "declineLoopsInitializationButton",
  ),
  name: document.getElementById("loopName"),
  reloadButton: document.getElementById("reloadButton"),
  refreshRetentionButton: document.getElementById("refreshRetentionButton"),
  retentionContent: document.getElementById("retentionContent"),
  retentionNotice: document.getElementById("retentionNotice"),
  retentionTab: document.getElementById("retentionTab"),
  retentionView: document.getElementById("retentionView"),
  roleId: document.getElementById("roleId"),
  rolePath: document.getElementById("rolePath"),
  loadMoreRunsButton: document.getElementById("loadMoreRunsButton"),
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
  selectedNodeButton: document.getElementById("selectedNodeButton"),
  startRunButton: document.getElementById("startRunButton"),
  toast: document.getElementById("toast"),
  traceQuota: document.getElementById("traceQuota"),
  validationBanner: document.getElementById("validationBanner"),
  workspaceRoot: document.getElementById("workspaceRoot"),
  workspaceStatus: document.getElementById("workspaceStatus"),
  zoomFitButton: document.getElementById("zoomFitButton"),
  zoomInButton: document.getElementById("zoomInButton"),
  zoomLevel: document.getElementById("zoomLevel"),
  zoomOutButton: document.getElementById("zoomOutButton"),
};

function activate() {
  loopBuilderSurfaceActive = true;
  if (!loopBuilderSessionAvailable) return Promise.resolve(false);
  if (loopBuilderRefresh) return loopBuilderRefresh;
  if (loopBuilderActivated) {
    scheduleSelectedRunRefresh();
    return Promise.resolve();
  }
  if (!loopBuilderEventsBound) {
    bindStaticEvents();
    loopBuilderEventsBound = true;
  }
  loopBuilderActivated = true;
  return beginLoopBuilderRefresh(startLoopBuilder);
}

function deactivate() {
  loopBuilderSurfaceActive = false;
  scheduleSelectedRunRefresh();
}

function beginLoopBuilderRefresh(operation, externalSignal = null) {
  const abortController = new AbortController();
  loopBuilderRefreshAbortController = abortController;
  const relayAbort = () => abortController.abort(externalSignal.reason);
  if (externalSignal?.aborted) relayAbort();
  else externalSignal?.addEventListener("abort", relayAbort, { once: true });
  const refresh = drainLoopBuilderRefresh(
    operation,
    abortController.signal,
  ).finally(() => {
    externalSignal?.removeEventListener("abort", relayAbort);
    if (loopBuilderRefreshAbortController === abortController)
      loopBuilderRefreshAbortController = null;
    if (loopBuilderRefresh === refresh) loopBuilderRefresh = null;
  });
  loopBuilderRefresh = refresh;
  return refresh;
}

async function drainLoopBuilderRefresh(operation, signal) {
  let refreshed = await operation(signal);
  applyLoopBuilderRefreshOutcome(refreshed);
  while (loopBuilderRefreshQueued) {
    const recoveryRefresh = loopBuilderRecoveryQueued;
    loopBuilderRefreshQueued = false;
    loopBuilderRecoveryQueued = false;
    refreshed = await refreshWorkspaceCore(
      recoveryRefresh ? false : Boolean(catalog),
      recoveryRefresh,
      { signal, suppressRecovery: recoveryRefresh },
    );
    applyLoopBuilderRefreshOutcome(refreshed);
  }
  return refreshed;
}

function applyLoopBuilderRefreshOutcome(refreshed) {
  loopBuilderActivated = refreshed !== false;
  if (!loopBuilderActivated) appendActivationRetry();
}

async function startLoopBuilder(signal) {
  try {
    if (window.embodySenseSession)
      await waitForLoopBuilderOperation(
        window.embodySenseSession.getHub(),
        signal,
      );
    else await requestJson("/api/session", { signal });
    if (signal.aborted) return false;
    return await refreshWorkspaceCore(Boolean(catalog), false, { signal });
  } catch (error) {
    showBanner(`Loop builder unavailable: ${error.message}`);
    setInteractive(false);
    return false;
  }
}

function refreshWorkspace() {
  if (loopBuilderRefresh) {
    loopBuilderRefreshQueued = true;
    return loopBuilderRefresh;
  }
  if (!loopBuilderActivated)
    return loopBuilderSurfaceActive ? activate() : Promise.resolve();
  return beginLoopBuilderRefresh((signal) =>
    refreshWorkspaceCore(false, false, { signal }),
  );
}

async function rehydrateSession({
  approvals = [],
  signal = null,
  workspaceRoot = null,
} = {}) {
  renderLoopApprovals(approvals);
  if (!loopBuilderEventsBound) return { refreshed: false, skipped: true };
  if (loopBuilderRefresh) {
    await loopBuilderRefresh;
    if (signal?.aborted) return { refreshed: false };
    return await rehydrateSession({ approvals, signal, workspaceRoot });
  }
  if (
    workspaceRoot &&
    elements.workspaceRoot.textContent &&
    elements.workspaceRoot.textContent !== "Workspace loading" &&
    elements.workspaceRoot.textContent !== workspaceRoot &&
    dirty
  ) {
    showBanner(
      "The host workspace changed. This unsaved loop draft remains loaded and was not applied to the new workspace.",
    );
    setInteractive(false);
    return { requiresManualAction: true };
  }
  const refreshed = await beginLoopBuilderRefresh(
    (refreshSignal) =>
      refreshWorkspaceCore(false, true, {
        propagateFailure: true,
        signal: refreshSignal,
        suppressRecovery: true,
      }),
    signal,
  );
  return { refreshed };
}

async function refreshWorkspaceCore(
  reuseCatalog = false,
  preserveUnsavedDraft = false,
  { propagateFailure = false, signal = null, suppressRecovery = false } = {},
) {
  try {
    const requestOptions = { signal, suppressRecovery };
    const status = await requestJson("/api/status", requestOptions);
    if (signal?.aborted) return false;
    workspaceStatusSnapshot = status;
    renderWorkspaceInitialization();
    try {
      await configurePendingInvocationRegistry(status.workspaceRoot);
    } catch {
      pendingInvocationStorageKey = null;
      pendingInvocationRegistryLockName = null;
      pendingInvocationRequests.clear();
    }
    try {
      await configurePendingLifecycleRegistry(
        status.workspaceRoot,
        status.initialized,
      );
    } catch {
      pendingLifecycleStorageKey = null;
      pendingLifecycleRegistryLockName = null;
      reconciledPendingLifecycleStorageKey = null;
      pendingLifecycleRequests.clear();
    }
    try {
      configureRetentionCleanupRegistry(status.workspaceRoot);
      retentionCleanupRegistryFailure = null;
    } catch {
      retentionCleanupStorageKey = null;
      retentionCleanupRegistryLockName = null;
      retentionCleanupOperationIds.clear();
      retentionCleanupRegistryFailure =
        "Receipt cleanup is unavailable because this browser cannot durably coordinate its workspace cleanup identity.";
    }
    configureNewLoopDraftStorage(status.workspaceRoot);
    elements.workspaceRoot.textContent = status.workspaceRoot;
    elements.rolePath.textContent = status.workspaceRoot;
    elements.workspaceStatus.textContent = status.initialized
      ? "Initialized"
      : "Needs initialization";
    if (!status.initialized) {
      workspaceAuthoringHydrated = false;
      showBanner(
        "Complete workspace initialization before creating loops.",
        "notice",
      );
      setInteractive(false);
      return true;
    }

    if (!reuseCatalog || !catalog) {
      await loadCatalog(
        requestedLoopDeepLink ?? undefined,
        preserveUnsavedDraft,
        requestOptions,
      );
      requestedLoopDeepLink = null;
    }
    if (signal?.aborted) return false;
    const runsLoaded = await loadRuns({ propagateFailure, requestOptions });
    if (runsLoaded === false) return false;
    renderAll();
    workspaceAuthoringHydrated = true;
    renderWorkspaceInitialization();
    return true;
  } catch (error) {
    showBanner(`Loop builder unavailable: ${error.message}`);
    setInteractive(false);
    if (propagateFailure) throw error;
    return false;
  }
}

function initializationState(status = workspaceStatusSnapshot) {
  if (status?.initialized) return "initialized";
  return status?.initializationState === "partial"
    ? "partial"
    : "uninitialized";
}

function readRequestedLoopDeepLink() {
  try {
    const loopId = new URL(window.location.href).searchParams.get("loopId");
    return loopId && loopId.length <= 200 ? loopId.normalize("NFC") : null;
  } catch {
    return null;
  }
}

function initializationRequiresCleanup(status = workspaceStatusSnapshot) {
  return status?.initializationRequiresCleanup === true;
}

function renderWorkspaceInitialization() {
  const state = initializationState();
  const requiresCleanup = initializationRequiresCleanup();
  const hydrated =
    state === "initialized" &&
    workspaceAuthoringHydrated &&
    !workspaceInitializationInFlight;
  elements.initializationRoot.textContent =
    workspaceStatusSnapshot?.workspaceRoot ?? "the configured workspace";
  elements.initializationPanel.hidden = hydrated;
  elements.initializationPanel.setAttribute(
    "aria-busy",
    workspaceInitializationInFlight ? "true" : "false",
  );

  if (hydrated) return;

  if (workspaceInitializationPhase === "running") {
    elements.initializationStatus.textContent =
      "Initialization is in progress. Authoring remains locked until authoritative workspace, role, catalog, and run state are loaded.";
  } else if (workspaceInitializationMessage) {
    elements.initializationStatus.textContent = workspaceInitializationMessage;
  } else if (state === "partial") {
    elements.initializationStatus.textContent = requiresCleanup
      ? "This workspace has an unusable protected .agent/ROLE.md or .agent/permissions.json document, or a nonreplaceable .agent/workspace-initialized.json path. Back up any intentional content, remove the invalid file or directory, then initialize again. Retrying without cleanup cannot replace it."
      : "This workspace has an incomplete .agent scaffold. Retry initialization to create the missing required files; existing protected seed documents will remain unchanged.";
  } else if (state === "initialized") {
    elements.initializationStatus.textContent =
      "The workspace is initialized, but Loops has not finished loading authoritative role and catalog state. Retry hydration.";
  } else if (!loopBuilderSessionAvailable) {
    elements.initializationStatus.textContent =
      "The browser session is disconnected. Reconnect before initializing; no completion is assumed.";
  } else {
    elements.initializationStatus.textContent =
      "Review the effects above, then initialize when you are ready.";
  }

  elements.initializationStatus.classList.toggle(
    "error",
    ["failed", "partial", "disconnected"].includes(
      workspaceInitializationPhase,
    ) || state === "partial",
  );
  elements.initializeWorkspaceButton.textContent =
    state === "partial"
      ? requiresCleanup
        ? "Check after cleanup"
        : "Retry initialization"
      : state === "initialized"
        ? "Retry Loops hydration"
        : "Initialize workspace";
  elements.initializeWorkspaceButton.disabled =
    workspaceInitializationInFlight || !loopBuilderSessionAvailable;
  elements.declineInitializationButton.disabled =
    workspaceInitializationInFlight || !loopBuilderSessionAvailable;
}

function setWorkspaceInitializationOutcome(phase, message) {
  workspaceInitializationPhase = phase;
  workspaceInitializationMessage = message;
  elements.initializationAnnouncement.textContent = message;
  renderWorkspaceInitialization();
}

function declineLoopsInitialization() {
  setWorkspaceInitializationOutcome(
    "declined",
    "Initialization declined. Nothing was changed, and no loop ran. You can initialize this workspace later.",
  );
}

async function initializeLoopsWorkspace() {
  if (workspaceInitializationInFlight || !loopBuilderSessionAvailable) return;
  const generation = ++workspaceInitializationGeneration;
  workspaceInitializationInFlight = true;
  workspaceInitializationPhase = "running";
  workspaceInitializationMessage = "";
  renderWorkspaceInitialization();

  const initializeUnderLock = async () => {
    const currentStatus = await requestJson("/api/status");
    if (generation !== workspaceInitializationGeneration) return;
    workspaceStatusSnapshot = currentStatus;
    if (initializationRequiresCleanup(currentStatus)) {
      setWorkspaceInitializationOutcome(
        "partial",
        "Initialization still requires cleanup. Back up any intentional content, remove the unusable protected .agent/ROLE.md or .agent/permissions.json document or the nonreplaceable .agent/workspace-initialized.json path, then check again. No loop ran, and no protected file was replaced.",
      );
      return;
    }

    if (currentStatus.initialized) {
      await completeWorkspaceInitialization(generation, "already-initialized");
      return;
    }

    const result = await requestJson("/api/workspace/init", {
      method: "POST",
      body: "{}",
    });
    if (generation !== workspaceInitializationGeneration) return;
    workspaceStatusSnapshot = result;
    if (!result.initialized) {
      const partial = initializationState(result) === "partial";
      const requiresCleanup = initializationRequiresCleanup(result);
      setWorkspaceInitializationOutcome(
        partial ? "partial" : "failed",
        partial
          ? requiresCleanup
            ? "Initialization stopped with an unusable protected .agent/ROLE.md or .agent/permissions.json document or a nonreplaceable .agent/workspace-initialized.json path. No loop ran. Back up any intentional content, remove the invalid file or directory, then initialize again; retrying without cleanup cannot replace it."
            : "Initialization stopped after creating part of the workspace scaffold. No loop ran. Retry to create the missing required files."
          : "Initialization did not produce a complete workspace. Nothing is unlocked, and no loop ran. Retry when ready.",
      );
      return;
    }

    await completeWorkspaceInitialization(
      generation,
      result.initializationOutcome ?? "initialized",
    );
  };

  try {
    const lockName = `${workspaceInitializationLockNamePrefix}:${workspaceStatusSnapshot?.workspaceRoot ?? "configured-workspace"}`;
    if (navigator.locks?.request)
      await navigator.locks.request(
        lockName,
        { mode: "exclusive" },
        initializeUnderLock,
      );
    else await initializeUnderLock();
  } catch (error) {
    if (generation !== workspaceInitializationGeneration) return;
    if (!loopBuilderSessionAvailable || error?.name === "AbortError") {
      setWorkspaceInitializationOutcome(
        "disconnected",
        "The browser disconnected during initialization. No completion is assumed. Reconnect to load authoritative workspace state before retrying.",
      );
    } else {
      await reconcileWorkspaceInitializationFailure(generation, error);
    }
  } finally {
    if (generation === workspaceInitializationGeneration) {
      workspaceInitializationInFlight = false;
      renderWorkspaceInitialization();
    }
  }
}

async function completeWorkspaceInitialization(generation, outcome) {
  const refreshed = await refreshWorkspace();
  if (generation !== workspaceInitializationGeneration) return;
  if (
    refreshed === false ||
    !workspaceStatusSnapshot?.initialized ||
    !workspaceAuthoringHydrated
  ) {
    setWorkspaceInitializationOutcome(
      "failed",
      "The workspace reports initialized, but Loops could not hydrate authoritative role, catalog, and run state. Authoring remains locked; retry hydration.",
    );
    return;
  }

  const message =
    outcome === "already-initialized"
      ? "This workspace was already initialized. Authoritative role, catalog, and run state are now loaded; no loop ran."
      : "Workspace initialization completed. Authoritative role, catalog, and run state are loaded; no loop ran.";
  setWorkspaceInitializationOutcome("succeeded", message);
  elements.createLoopButton.focus?.();
}

async function reconcileWorkspaceInitializationFailure(generation, error) {
  try {
    const status = await requestJson("/api/status", {
      suppressRecovery: true,
    });
    if (generation !== workspaceInitializationGeneration) return;
    workspaceStatusSnapshot = status;
    if (status.initialized) {
      await completeWorkspaceInitialization(generation, "already-initialized");
      return;
    }

    const partial = initializationState(status) === "partial";
    const requiresCleanup = initializationRequiresCleanup(status);
    setWorkspaceInitializationOutcome(
      partial ? "partial" : "failed",
      partial
        ? requiresCleanup
          ? "Initialization failed with an unusable protected .agent/ROLE.md or .agent/permissions.json document or a nonreplaceable .agent/workspace-initialized.json path. No loop ran. Back up any intentional content, remove the invalid file or directory, then initialize again; retrying without cleanup cannot replace it."
          : "Initialization failed after creating part of the .agent scaffold. No loop ran. Retry to create the missing required files."
        : `Initialization failed before the workspace became ready. Nothing is unlocked, and no loop ran. ${error.message}`,
    );
  } catch {
    setWorkspaceInitializationOutcome(
      "disconnected",
      "Initialization could not be verified because the browser session is unavailable. No completion is assumed. Reconnect to load authoritative workspace state.",
    );
  }
}

function bindStaticEvents() {
  elements.initializeWorkspaceButton.addEventListener(
    "click",
    initializeLoopsWorkspace,
  );
  elements.declineInitializationButton.addEventListener(
    "click",
    declineLoopsInitialization,
  );
  elements.createLoopButton.addEventListener("click", createLoop);
  elements.builderTab.addEventListener("click", () => switchView("builder"));
  elements.runsTab.addEventListener("click", () => switchView("runs"));
  elements.retentionTab.addEventListener("click", () =>
    switchView("retention"),
  );
  elements.refreshRetentionButton.addEventListener("click", () =>
    loadRetentionPosture(),
  );
  elements.invokeButton.addEventListener("click", openInvokeModal);
  elements.closeInvokeButton.addEventListener("click", cancelInvokeModal);
  elements.cancelInvokeButton.addEventListener("click", cancelInvokeModal);
  elements.startRunButton.addEventListener("click", startRun);
  elements.saveButton.addEventListener("click", saveLoop);
  elements.deleteButton.addEventListener("click", deleteLoop);
  elements.reloadButton.addEventListener("click", reloadCurrent);
  elements.addStepButton.addEventListener("click", addInferenceStep);
  elements.selectedNodeButton.addEventListener("click", () => {
    if (!draft) return;
    selectedNodeId = lastSelectedNodeId;
    renderCanvas();
    renderInspector();
    renderToolbar();
  });
  elements.loopSettingsButton.addEventListener("click", () => {
    if (!draft) return;
    selectedNodeId = "loop-settings";
    renderCanvas();
    renderInspector();
    renderToolbar();
  });
  bindTabKeyboard(elements.builderTab, [
    elements.builderTab,
    elements.runsTab,
    elements.retentionTab,
  ]);
  bindTabKeyboard(elements.runsTab, [
    elements.builderTab,
    elements.runsTab,
    elements.retentionTab,
  ]);
  bindTabKeyboard(elements.retentionTab, [
    elements.builderTab,
    elements.runsTab,
    elements.retentionTab,
  ]);
  bindTabKeyboard(elements.selectedNodeButton, [
    elements.selectedNodeButton,
    elements.loopSettingsButton,
  ]);
  bindTabKeyboard(elements.loopSettingsButton, [
    elements.selectedNodeButton,
    elements.loopSettingsButton,
  ]);
  elements.loopSearch.addEventListener("input", (event) => {
    loopSearchQuery = event.target.value.trim().toLocaleLowerCase();
    renderList();
  });
  elements.zoomOutButton.addEventListener("click", () =>
    setCanvasZoom(canvasZoom - 0.1),
  );
  elements.zoomInButton.addEventListener("click", () =>
    setCanvasZoom(canvasZoom + 0.1),
  );
  elements.zoomFitButton.addEventListener("click", fitCanvas);
  elements.loadMoreRunsButton.addEventListener("click", loadMoreRuns);
  elements.name.addEventListener("input", (event) =>
    updateDraftValue("displayName", event.target.value),
  );
  elements.description.addEventListener("input", (event) =>
    updateDraftValue("description", event.target.value),
  );
  window.addEventListener("beforeunload", (event) => {
    if (dirty) {
      event.preventDefault();
      event.returnValue = "";
    }
  });
  window.addEventListener("keydown", (event) => {
    if (!elements.invokeModal.className.split(/\s+/).includes("open")) return;
    if (event.key === "Escape") cancelInvokeModal();
    else if (event.key === "Tab") trapInvokeModalFocus(event);
  });
  window.addEventListener("storage", (event) => {
    if (
      pendingInvocationStorageKey &&
      event.key === pendingInvocationStorageKey
    ) {
      try {
        synchronizePendingInvocationRequestsFromStorage();
      } catch {
        // Retain the last verified in-memory view and fail closed on the next reservation attempt.
      }
    }
    if (
      pendingLifecycleStorageKey &&
      event.key === pendingLifecycleStorageKey
    ) {
      try {
        synchronizePendingLifecycleRequestsFromStorage();
      } catch {
        // Retain the last verified in-memory view and fail closed on the next lifecycle request.
      }
    }
    if (
      retentionCleanupStorageKey &&
      event.key === retentionCleanupStorageKey
    ) {
      try {
        synchronizeRetentionCleanupOperationIdsFromStorage();
        retentionCleanupRegistryFailure = null;
        if (currentView === "retention") renderRetention();
      } catch {
        retentionCleanupRegistryFailure =
          "Receipt cleanup is unavailable because the shared workspace cleanup registry could not be validated.";
        if (currentView === "retention") renderRetention();
      }
    }
  });
}

function bindTabKeyboard(tab, tabs) {
  tab.addEventListener("keydown", (event) => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    const enabledTabs = tabs.filter((item) => !item.disabled && !item.hidden);
    if (enabledTabs.length === 0) return;
    event.preventDefault();
    const currentIndex = enabledTabs.indexOf(tab);
    const nextIndex =
      event.key === "Home"
        ? 0
        : event.key === "End"
          ? enabledTabs.length - 1
          : (currentIndex +
              (event.key === "ArrowLeft" ? -1 : 1) +
              enabledTabs.length) %
            enabledTabs.length;
    const nextTab = enabledTabs[nextIndex];
    nextTab.focus();
    nextTab.click();
  });
}

function moveLoopOptionFocus(event, currentOption) {
  if (!["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) return;
  const options = Array.from(
    elements.list.querySelectorAll('[role="option"]'),
  ).filter((option) => !option.disabled);
  if (options.length === 0) return;
  event.preventDefault();
  const currentIndex = options.indexOf(currentOption);
  const nextIndex =
    event.key === "Home"
      ? 0
      : event.key === "End"
        ? options.length - 1
        : (currentIndex + (event.key === "ArrowUp" ? -1 : 1) + options.length) %
          options.length;
  const nextOption = options[nextIndex];
  const nextOptionKey = nextOption.dataset.loopOptionKey;
  nextOption.focus();
  nextOption.click();
  const replacement = Array.from(
    elements.list.querySelectorAll('[role="option"]'),
  ).find((option) => option.dataset.loopOptionKey === nextOptionKey);
  replacement?.focus();
}

async function requestJson(url, options = {}) {
  const { suppressRecovery = false, ...fetchOptions } = options;
  const signal =
    fetchOptions.signal ?? loopBuilderSessionAbortController.signal;
  const headers = { ...(fetchOptions.headers ?? {}) };
  if (fetchOptions.body && !headers["Content-Type"])
    headers["Content-Type"] = "application/json";
  const response = await waitForLoopBuilderOperation(
    fetch(url, {
      ...fetchOptions,
      credentials: "same-origin",
      headers,
      signal,
    }),
    signal,
  );
  const text = await response.text();
  let payload = null;
  if (text) {
    try {
      payload = JSON.parse(text);
    } catch {
      payload = text;
    }
  }
  if (!response.ok) {
    const detail =
      typeof payload === "string"
        ? payload
        : (payload?.detail ??
          payload?.title ??
          `Request failed (${response.status})`);
    const error = new Error(
      response.status === 401 && url !== "/api/session"
        ? "The local session changed. Recovery started, and the prior request was not replayed."
        : detail,
    );
    error.status = response.status;
    error.payload = payload;
    if (response.status === 401 && url !== "/api/session" && !suppressRecovery)
      beginSessionRecovery();
    throw error;
  }
  return payload;
}

async function requestRunMonitor(runId) {
  const headers = {};
  if (selectedRunMonitorId === runId && selectedRunMonitorEtag)
    headers["If-None-Match"] = selectedRunMonitorEtag;
  const signal = loopBuilderSessionAbortController.signal;
  const response = await waitForLoopBuilderOperation(
    fetch(`/api/loop-runs/${encodeURIComponent(runId)}/monitor`, {
      credentials: "same-origin",
      headers,
      signal,
    }),
    signal,
  );
  const etag = response.headers?.get?.("ETag") ?? selectedRunMonitorEtag;
  if (response.status === 304)
    return { notModified: true, summary: null, etag };
  const text = await response.text();
  let payload = null;
  if (text) {
    try {
      payload = JSON.parse(text);
    } catch {
      payload = text;
    }
  }
  if (!response.ok) {
    const detail =
      typeof payload === "string"
        ? payload
        : (payload?.detail ??
          payload?.title ??
          `Request failed (${response.status})`);
    const error = new Error(detail);
    error.status = response.status;
    error.payload = payload;
    if (response.status === 401) beginSessionRecovery();
    throw error;
  }
  return { notModified: false, summary: payload, etag };
}

async function loadCatalog(
  preferredLoopId,
  preserveUnsavedDraft = false,
  requestOptions = {},
) {
  const nextCatalog = await requestJson("/api/loops", requestOptions);
  const shouldPreserveDraft = preserveUnsavedDraft && dirty && draft;
  catalog = nextCatalog;
  elements.roleId.textContent = catalog.roleId;
  if (isNewLoopDraft()) {
    const committed = reconcileNewLoopDraftFromCatalog();
    if (committed) {
      applyDefinition(committed);
      return;
    }

    renderList();
    return;
  }
  if (shouldPreserveDraft) {
    renderList();
    return;
  }
  if (restoreNewLoopDraft()) {
    const committed = reconcileNewLoopDraftFromCatalog();
    if (committed) applyDefinition(committed);
    else renderAll();
    return;
  }
  const definitions = allDefinitions();
  const requested = preferredLoopId ?? currentDefinition?.id;
  const next =
    definitions.find((definition) => definition.id === requested) ??
    definitions[0] ??
    null;
  applyDefinition(next);
  renderList();
}

async function loadRetentionPosture(preserveOutcome = false) {
  if (retentionCleanupInFlight) return;
  const requestGeneration = ++retentionPostureRequestGeneration;
  elements.refreshRetentionButton.disabled = true;
  try {
    const posture = await requestJson("/api/loops/receipt-retention");
    if (requestGeneration !== retentionPostureRequestGeneration) return;
    retentionPosture = posture;
    retentionPostureFailure = null;
    if (!preserveOutcome) retentionCleanupOutcome = null;
    if (currentView === "retention") renderRetention();
  } catch (error) {
    if (requestGeneration !== retentionPostureRequestGeneration) return;
    if (currentView === "retention") {
      retentionPosture = null;
      retentionPostureFailure = `Retention posture is unavailable: ${error.message}`;
      renderRetention();
    }
  } finally {
    if (requestGeneration === retentionPostureRequestGeneration)
      elements.refreshRetentionButton.disabled = false;
  }
}

async function cleanupRetention(artifactClass) {
  if (retentionCleanupInFlight) return;
  const label = formatStatus(artifactClass);
  if (
    !window.confirm(
      `Clean up eligible expired ${label} evidence? This explicit request can compact at most 64 artifacts and 4 MiB, with a durable audit trail.`,
    )
  )
    return;

  retentionCleanupInFlight = true;
  let operationId = null;
  renderTabs();
  renderRetention();
  try {
    operationId = await getOrCreateRetentionCleanupOperationId(artifactClass);
    const response = await requestJson("/api/loops/receipt-retention/cleanup", {
      method: "POST",
      body: JSON.stringify({
        artifactClass,
        operationId,
        maximumArtifactCount: 64,
        maximumArtifactUtf8Bytes: 4 * 1024 * 1024,
      }),
    });
    retentionCleanupOutcome = response;
    try {
      await forgetRetentionCleanupOperationId(artifactClass, operationId);
    } catch {
      retentionCleanupRegistryFailure =
        "The completed cleanup operation identity remains reserved because it could not be retired safely.";
      retentionCleanupOutcome = {
        ...response,
        detail: `${response.detail} ${retentionCleanupRegistryFailure}`,
      };
    }
  } catch (error) {
    if (!operationId && !error.payload) {
      retentionCleanupRegistryFailure =
        "Receipt cleanup is unavailable because this browser could not durably reserve a shared operation identity.";
    }
    retentionCleanupOutcome = error.payload ?? {
      status: "Unavailable",
      health: "Degraded",
      isCommitted: false,
      exhaustionReason: "None",
      cleanupBlockReason: "None",
      compactedArtifactCount: 0,
      compactedArtifactUtf8Bytes: 0,
      detail:
        retentionCleanupRegistryFailure ??
        "Receipt cleanup is unavailable before a safe server outcome could be obtained.",
    };
    if (
      error.payload &&
      error.payload.status !== "OperationInProgress" &&
      error.payload.status !== "AuditUnavailable"
    ) {
      try {
        await forgetRetentionCleanupOperationId(artifactClass, operationId);
      } catch {
        retentionCleanupRegistryFailure =
          "The terminal cleanup operation identity remains reserved because it could not be retired safely.";
        retentionCleanupOutcome = {
          ...retentionCleanupOutcome,
          detail: `${retentionCleanupOutcome.detail} ${retentionCleanupRegistryFailure}`,
        };
      }
    }
  } finally {
    retentionCleanupInFlight = false;
    await loadRetentionPosture(true);
    renderTabs();
    renderRetention();
  }
}

function renderRetention() {
  clearRetentionRecoveryRenderTimer();
  elements.retentionContent.replaceChildren();
  elements.retentionNotice.textContent = retentionCleanupOutcome
    ? `${formatStatus(retentionCleanupOutcome.status)}: ${retentionCleanupOutcome.detail}`
    : (retentionCleanupRegistryFailure ??
      retentionPostureFailure ??
      retentionPosture?.detail ??
      "Read the current bounded retention posture before requesting cleanup.");
  if (!retentionPosture) {
    elements.retentionContent.append(
      node(
        "p",
        "empty-state",
        "Retention posture has not been loaded. Select Refresh posture to retry.",
      ),
    );
    return;
  }

  const workspace = node("section", "retention-workspace");
  const workspaceTitle = node("div", "retention-workspace-title");
  workspaceTitle.append(
    node(
      "strong",
      `retention-health ${statusClass(retentionPosture.health)}`,
      formatStatus(retentionPosture.health),
    ),
    node(
      "span",
      "",
      `${formatBytes(retentionPosture.accountedWorkspaceUtf8Bytes)} of ${formatBytes(retentionPosture.maximumWorkspaceUtf8Bytes)} accounted`,
    ),
  );
  workspace.append(
    workspaceTitle,
    retentionMetric(
      "Available workspace",
      formatBytes(retentionPosture.availableWorkspaceUtf8Bytes),
    ),
    retentionMetric(
      "Active cleanup journals",
      formatBytes(retentionPosture.activeCleanupJournalUtf8Bytes),
    ),
    retentionMetric(
      "Workspace block",
      formatStatus(retentionPosture.cleanupBlockReason),
    ),
  );
  elements.retentionContent.append(workspace);

  const classList = node("div", "retention-class-list");
  for (const posture of retentionPosture.classes ?? []) {
    const card = node("section", "retention-class-card");
    const heading = node("div", "retention-class-heading");
    heading.append(
      node("h3", "", formatStatus(posture.artifactClass)),
      node(
        "span",
        `retention-health ${statusClass(posture.health)}`,
        formatStatus(posture.health),
      ),
    );
    card.append(heading, node("p", "retention-detail", posture.detail));

    const metrics = node("dl", "retention-metrics");
    metrics.append(
      retentionMetric(
        "Raw evidence",
        `${posture.artifactCount} / ${posture.maximumArtifactCount} · ${formatBytes(posture.artifactUtf8Bytes)} / ${formatBytes(posture.maximumArtifactUtf8Bytes)}`,
      ),
      retentionMetric(
        "Reserved completion",
        `${posture.reservedArtifactCount} slots · ${formatBytes(posture.reservedArtifactUtf8Bytes)}`,
      ),
      retentionMetric(
        "Compact proof",
        `${posture.proofCount} / ${posture.maximumProofCount} · ${formatBytes(posture.proofUtf8Bytes)} / ${formatBytes(posture.maximumProofUtf8Bytes)}`,
      ),
      retentionMetric(
        "Active cleanup journal",
        formatBytes(posture.activeCleanupJournalUtf8Bytes),
      ),
      retentionMetric(
        "Recovery available",
        formatTimestamp(posture.cleanupRecoveryAvailableAtUtc),
      ),
      retentionMetric(
        "Cleanup history",
        `${posture.completedCleanupOperationCount} operations · ${formatBytes(posture.completedCleanupHistoryUtf8Bytes)}`,
      ),
      retentionMetric(
        "Exact replay horizon",
        `${formatTimestamp(posture.oldestExactReplayExpiresAtUtc)} to ${formatTimestamp(posture.newestExactReplayExpiresAtUtc)}`,
      ),
      retentionMetric(
        "Block / exhaustion",
        `${formatStatus(posture.cleanupBlockReason)} / ${formatStatus(posture.exhaustionReason)}`,
      ),
    );
    card.append(metrics);

    const categories = node("ul", "retention-categories");
    for (const category of posture.categories ?? []) {
      categories.append(
        node(
          "li",
          "",
          `${formatStatus(category.category)}: ${category.artifactCount} · ${formatBytes(category.utf8Bytes)}`,
        ),
      );
    }
    card.append(categories);
    const cleanupHealth = statusClass(posture.health);
    const recoveryAvailableAt = Date.parse(
      posture.cleanupRecoveryAvailableAtUtc,
    );
    const recoveryPending =
      cleanupHealth === "recoverypending" &&
      posture.cleanupBlockReason === "OwnershipUnresolved";
    const recoveryReady =
      recoveryPending &&
      Number.isFinite(recoveryAvailableAt) &&
      recoveryAvailableAt <= Date.now();
    const normalCleanupAllowed =
      posture.cleanupBlockReason === "None" &&
      (cleanupHealth === "healthy" || cleanupHealth === "exhausted");
    const cleanup = actionButton(
      retentionCleanupInFlight
        ? "Cleanup in progress"
        : recoveryPending
          ? recoveryReady
            ? "Retry cleanup recovery"
            : `Recovery available ${formatTimestamp(posture.cleanupRecoveryAvailableAtUtc)}`
          : "Clean eligible expired evidence",
      () => cleanupRetention(posture.artifactClass),
      retentionCleanupInFlight ||
        Boolean(retentionCleanupRegistryFailure) ||
        (!normalCleanupAllowed && !recoveryReady),
      "secondary-button retention-cleanup-button",
    );
    cleanup.setAttribute(
      "aria-label",
      `Clean eligible expired ${formatStatus(posture.artifactClass)} evidence`,
    );
    card.append(cleanup);
    classList.append(card);
  }
  elements.retentionContent.append(classList);
  scheduleRetentionRecoveryRender();
}

function clearRetentionRecoveryRenderTimer() {
  if (retentionRecoveryRenderTimer === null) return;
  window.clearTimeout(retentionRecoveryRenderTimer);
  retentionRecoveryRenderTimer = null;
}

function scheduleRetentionRecoveryRender() {
  if (currentView !== "retention" || !retentionPosture) return;
  const now = Date.now();
  const nextRecoveryAt = (retentionPosture.classes ?? [])
    .filter(
      (posture) =>
        statusClass(posture.health) === "recoverypending" &&
        posture.cleanupBlockReason === "OwnershipUnresolved",
    )
    .map((posture) => Date.parse(posture.cleanupRecoveryAvailableAtUtc))
    .filter((value) => Number.isFinite(value) && value > now)
    .sort((left, right) => left - right)[0];
  if (!nextRecoveryAt) return;
  retentionRecoveryRenderTimer = window.setTimeout(
    () => {
      retentionRecoveryRenderTimer = null;
      if (currentView === "retention") renderRetention();
    },
    Math.min(nextRecoveryAt - now + 25, 2_147_483_647),
  );
}

function retentionMetric(label, value) {
  const metric = node("div", "retention-metric");
  metric.append(node("dt", "", label), node("dd", "", value));
  return metric;
}

function allDefinitions() {
  if (!catalog) return [];
  return [catalog.systemDefault, ...catalog.customDefinitions];
}

function applyDefinition(definition) {
  runEvidenceRequestGeneration++;
  historicalLoopId = null;
  currentDefinition = definition;
  draft = definition ? clone(definition) : null;
  if (draft?.id === "default-conversation") {
    const pinnedRoleId = draft.owningRole?.identity?.roleId;
    if (typeof pinnedRoleId !== "string" || !pinnedRoleId)
      throw new Error(
        "The system loop is missing its exact owning-role revision pin.",
      );
    draft.roleId = pinnedRoleId;
  }
  const initialNodeId = definition?.graph?.entryNodeId ?? "trigger";
  selectedNodeId = initialNodeId;
  lastSelectedNodeId = initialNodeId;
  dirty = false;
  elements.name.value = draft?.displayName ?? "";
  elements.description.value = draft?.description ?? "";
  renderAll();
}

function configureNewLoopDraftStorage(workspaceRoot) {
  if (typeof workspaceRoot !== "string" || !workspaceRoot)
    throw new Error("The workspace identity is unavailable.");
  const scope = encodeURIComponent(workspaceRoot.normalize("NFC"));
  const nextKey = `${newLoopDraftStorageKeyPrefix}.${scope}`;
  if (newLoopDraftStorageKey && newLoopDraftStorageKey !== nextKey) {
    currentDefinition = null;
    draft = null;
    resetNewLoopDraftState(false);
  }
  newLoopDraftStorageKey = nextKey;
}

function startNewLoopDraft() {
  const template = catalog?.draftTemplate;
  if (!template)
    throw new Error("The server did not provide a draft template.");
  runEvidenceRequestGeneration++;
  historicalLoopId = null;
  currentView = "builder";
  currentDefinition = null;
  draft = {
    schemaVersion: template.schemaVersion,
    id: null,
    definitionVersion: null,
    contentHash: null,
    createdAtUtc: null,
    updatedAtUtc: null,
    displayName: template.definition.displayName,
    description: template.definition.description,
    roleId: template.roleId,
    triggerPolicy: clone(template.definition.triggerPolicy),
    contextDefaults: clone(template.contextDefaults),
    inferenceSteps: template.definition.inferenceSteps.map((step, index) => ({
      ...clone(step),
      id: `local-draft-${index + 1}-${newOperationId()}`,
    })),
    toolAssignments: [...template.definition.toolAssignments],
    exitPolicy: clone(template.definition.exitPolicy),
    lastMutationOperationId: null,
  };
  newLoopDraftOperationId = newOperationId();
  newLoopDraftCommitState = "editing";
  newLoopDraftFailureDetail = null;
  pendingCreateRequest = null;
  selectedNodeId = "trigger";
  lastSelectedNodeId = "trigger";
  dirty = true;
  elements.name.value = draft.displayName;
  elements.description.value = draft.description;
  tryPersistNewLoopDraft();
  renderAll();
}

function isNewLoopDraft() {
  return (
    currentDefinition === null &&
    !historicalLoopId &&
    draft?.id === null &&
    typeof newLoopDraftOperationId === "string"
  );
}

function isUncertainNewLoopDraft() {
  return isNewLoopDraft() && newLoopDraftCommitState === "uncertain";
}

function canMutateDraft() {
  return (
    Boolean(draft) &&
    !isSystemLoop() &&
    !mutationInFlight &&
    !isUncertainNewLoopDraft()
  );
}

function persistNewLoopDraft() {
  if (!isNewLoopDraft()) return;
  if (!newLoopDraftStorageKey || !window.sessionStorage)
    throw new Error("Tab-scoped draft storage is unavailable.");
  window.sessionStorage.setItem(
    newLoopDraftStorageKey,
    JSON.stringify({
      schemaVersion: 1,
      roleId: draft.roleId,
      operationId: newLoopDraftOperationId,
      commitState:
        newLoopDraftCommitState === "saving"
          ? "uncertain"
          : newLoopDraftCommitState,
      failureDetail: newLoopDraftFailureDetail,
      pendingCreateRequest,
      draft,
    }),
  );
}

function restoreNewLoopDraft() {
  if (!newLoopDraftStorageKey || !window.sessionStorage) return false;
  const stored = window.sessionStorage.getItem(newLoopDraftStorageKey);
  if (!stored) return false;
  let payload;
  try {
    payload = JSON.parse(stored);
  } catch {
    window.sessionStorage.removeItem(newLoopDraftStorageKey);
    return false;
  }
  if (!isStoredNewLoopDraft(payload)) {
    window.sessionStorage.removeItem(newLoopDraftStorageKey);
    return false;
  }

  currentDefinition = null;
  historicalLoopId = null;
  draft = clone(payload.draft);
  newLoopDraftOperationId = payload.operationId;
  newLoopDraftCommitState = payload.commitState;
  newLoopDraftFailureDetail = payload.failureDetail;
  pendingCreateRequest = payload.pendingCreateRequest
    ? clone(payload.pendingCreateRequest)
    : null;
  selectedNodeId = "trigger";
  lastSelectedNodeId = "trigger";
  dirty = true;
  elements.name.value = draft.displayName;
  elements.description.value = draft.description;
  return true;
}

function isStoredNewLoopDraft(payload) {
  return (
    payload?.schemaVersion === 1 &&
    payload.roleId === catalog?.roleId &&
    typeof payload.operationId === "string" &&
    /^[a-z0-9-]{8,128}$/.test(payload.operationId) &&
    ["editing", "failed", "conflict", "uncertain"].includes(
      payload.commitState,
    ) &&
    (payload.failureDetail === null ||
      (typeof payload.failureDetail === "string" &&
        payload.failureDetail.length <= 4000)) &&
    isStoredDraftShape(payload.draft, payload.roleId) &&
    (payload.commitState !== "uncertain" ||
      payload.pendingCreateRequest !== null) &&
    isStoredPendingCreateRequest(
      payload.pendingCreateRequest,
      payload.operationId,
    )
  );
}

function isStoredDraftShape(candidate, roleId) {
  return (
    candidate?.schemaVersion === catalog?.draftTemplate?.schemaVersion &&
    candidate.id === null &&
    candidate.definitionVersion === null &&
    candidate.roleId === roleId &&
    typeof candidate.displayName === "string" &&
    typeof candidate.description === "string" &&
    isStoredTriggerPolicy(candidate.triggerPolicy) &&
    isStoredContextDefaults(candidate.contextDefaults) &&
    Array.isArray(candidate.inferenceSteps) &&
    candidate.inferenceSteps.length >= 1 &&
    candidate.inferenceSteps.length <= catalog.limits.maxInferenceSteps &&
    candidate.inferenceSteps.every(isStoredInferenceStep) &&
    isStoredToolAssignments(candidate.toolAssignments) &&
    isStoredExitPolicy(candidate.exitPolicy)
  );
}

function isStoredPendingCreateRequest(candidate, operationId) {
  if (candidate === null) return true;
  return (
    candidate &&
    typeof candidate.key === "string" &&
    candidate.body?.operationId === operationId &&
    candidate.key === JSON.stringify(candidate.body.definition) &&
    isStoredDefinitionInput(candidate.body.definition)
  );
}

function isStoredDefinitionInput(candidate) {
  return (
    candidate &&
    typeof candidate.displayName === "string" &&
    typeof candidate.description === "string" &&
    isStoredTriggerPolicy(candidate.triggerPolicy) &&
    Array.isArray(candidate.inferenceSteps) &&
    candidate.inferenceSteps.length >= 1 &&
    candidate.inferenceSteps.length <= catalog.limits.maxInferenceSteps &&
    candidate.inferenceSteps.every(isStoredInferenceStep) &&
    isStoredToolAssignments(candidate.toolAssignments) &&
    isStoredExitPolicy(candidate.exitPolicy)
  );
}

function isStoredTriggerPolicy(candidate) {
  return (
    candidate &&
    ["invocation", "preset", "none"].includes(candidate.promptSource) &&
    typeof candidate.presetPrompt === "string" &&
    typeof candidate.includeInvokingConversation === "boolean"
  );
}

function isStoredContextDefaults(candidate) {
  return (
    candidate &&
    isStoredContextPolicy(candidate.inference) &&
    isStoredContextPolicy(candidate.exit)
  );
}

function isStoredContextPolicy(candidate) {
  return (
    candidate &&
    candidate.contextIn &&
    [
      "includeRoleContext",
      "includeTriggerPrompt",
      "includeInvokingConversation",
      "includeEarlierRetainedOutputs",
      "includePreviousIterationResult",
    ].every((key) => typeof candidate.contextIn[key] === "boolean") &&
    candidate.contextOut &&
    ["retainForLoopReasoning", "publishToInvokingConversation"].every(
      (key) => typeof candidate.contextOut[key] === "boolean",
    )
  );
}

function isStoredNodeContextPolicy(candidate) {
  return (
    candidate &&
    ["inherit", "custom"].includes(candidate.mode) &&
    (candidate.mode === "inherit"
      ? candidate.customPolicy === null
      : isStoredContextPolicy(candidate.customPolicy))
  );
}

function isStoredInferenceStep(candidate) {
  return (
    candidate &&
    (candidate.id === null || typeof candidate.id === "string") &&
    typeof candidate.name === "string" &&
    typeof candidate.instruction === "string" &&
    isStoredNodeContextPolicy(candidate.contextPolicy)
  );
}

function isStoredExitPolicy(candidate) {
  return (
    candidate &&
    Number.isInteger(candidate.maxAdditionalIterations) &&
    typeof candidate.decisionInstruction === "string" &&
    isStoredNodeContextPolicy(candidate.contextPolicy)
  );
}

function isStoredToolAssignments(candidate) {
  return (
    Array.isArray(candidate) &&
    candidate.length <= supportedCustomToolAssignments.length &&
    candidate.every((value) =>
      supportedCustomToolAssignments.includes(value),
    ) &&
    new Set(candidate).size === candidate.length
  );
}

function reconcileNewLoopDraftFromCatalog() {
  if (!isNewLoopDraft() || !pendingCreateRequest) return null;
  const committed = catalog.customDefinitions.find(
    (definition) =>
      definition.lastMutationOperationId ===
      pendingCreateRequest.body.operationId,
  );
  if (!committed) return null;
  resetNewLoopDraftState(true);
  showToast("The first save was already committed and has been restored.");
  return committed;
}

function resetNewLoopDraftState(removeStored) {
  if (removeStored && newLoopDraftStorageKey && window.sessionStorage) {
    try {
      window.sessionStorage.removeItem(newLoopDraftStorageKey);
    } catch {
      // Clearing in-memory ownership remains safe; tab-scoped storage is discarded when the tab closes.
    }
  }
  newLoopDraftOperationId = null;
  newLoopDraftCommitState = null;
  newLoopDraftFailureDetail = null;
  pendingCreateRequest = null;
}

function discardNewLoopDraft() {
  if (!isNewLoopDraft() || newLoopDraftCommitState === "uncertain") return;
  resetNewLoopDraftState(true);
  applyDefinition(
    catalog.systemDefault ?? catalog.customDefinitions[0] ?? null,
  );
  showToast("Unsaved draft discarded. No durable loop was deleted.");
}

function tryPersistNewLoopDraft() {
  try {
    persistNewLoopDraft();
    return true;
  } catch (error) {
    const outcomeMayBeUncertain = newLoopDraftCommitState === "uncertain";
    newLoopDraftCommitState = outcomeMayBeUncertain ? "uncertain" : "failed";
    const storageFailure = `This tab could not preserve the draft for reload: ${error.message}`;
    newLoopDraftFailureDetail = newLoopDraftFailureDetail
      ? `${newLoopDraftFailureDetail} ${storageFailure}`
      : `The draft remains in memory, but ${storageFailure}`;
    return false;
  }
}

function renderAll() {
  renderList();
  renderTabs();
  if (currentView === "builder") {
    renderCanvas();
    renderInspector();
  } else if (currentView === "runs") {
    renderRuns();
    renderRunEvidence();
  } else {
    renderRetention();
  }
  renderToolbar();
  renderValidation();
  scheduleSelectedRunRefresh();
}

function renderTabs() {
  const builderActive = currentView === "builder" && !historicalLoopId;
  const runsActive =
    currentView === "runs" ||
    (currentView === "builder" && Boolean(historicalLoopId));
  const retentionActive = currentView === "retention";
  elements.builderTab.disabled = mutationInFlight || Boolean(historicalLoopId);
  elements.runsTab.disabled = mutationInFlight || isNewLoopDraft();
  elements.retentionTab.disabled = mutationInFlight || retentionCleanupInFlight;
  elements.builderTab.classList.toggle("active", builderActive);
  elements.runsTab.classList.toggle("active", runsActive);
  elements.retentionTab.classList.toggle("active", retentionActive);
  elements.builderTab.setAttribute("aria-selected", String(builderActive));
  elements.runsTab.setAttribute("aria-selected", String(runsActive));
  elements.retentionTab.setAttribute("aria-selected", String(retentionActive));
  elements.builderTab.tabIndex = builderActive ? 0 : -1;
  elements.runsTab.tabIndex = runsActive ? 0 : -1;
  elements.retentionTab.tabIndex = retentionActive ? 0 : -1;
  elements.builderView.hidden = !builderActive;
  elements.runsView.hidden = !runsActive;
  elements.retentionView.hidden = !retentionActive;
  elements.inspectorTabs.hidden = !builderActive;
  elements.builderLayout.classList.toggle("runs-active", !builderActive);
  elements.inspectorContent.setAttribute(
    "role",
    builderActive ? "tabpanel" : "region",
  );
  elements.inspectorContent.setAttribute(
    "aria-labelledby",
    builderActive
      ? selectedNodeId === "loop-settings"
        ? "loopSettingsButton"
        : "selectedNodeButton"
      : "inspectorTitle",
  );
  elements.runCount.textContent = String(runsForCurrentLoop().length);
}

async function switchView(view) {
  if (mutationInFlight) return;
  if (view !== "builder" && view !== "runs" && view !== "retention") return;
  if (view === "builder" && historicalLoopId) return;
  if (view === "runs" && isNewLoopDraft()) return;
  currentView = view;
  if (view === "runs") {
    renderAll();
    await loadRuns();
    return;
  }
  if (view === "retention") {
    renderAll();
    await loadRetentionPosture();
    return;
  }
  renderAll();
}

function runsForCurrentLoop() {
  const loopId = selectedLoopId();
  return loopId ? recentRuns.filter((run) => run.loopId === loopId) : [];
}

function selectedLoopId() {
  return draft?.id ?? historicalLoopId;
}

async function loadRuns({
  silent = false,
  preferredRunId = null,
  preferredAdmissionOperationId = null,
  preserveEmptySelection = false,
  propagateFailure = false,
  requestOptions = {},
} = {}) {
  if (!catalog) return;
  const requestGeneration = ++runEvidenceRequestGeneration;
  const loopId = selectedLoopId();
  try {
    const filteredPageRequest = loopId
      ? requestJson(
          `/api/loop-runs?maximumCount=50&loopId=${encodeURIComponent(loopId)}`,
          requestOptions,
        )
      : Promise.resolve(null);
    const [payload, filteredPayload, quotaPayload] = await Promise.all([
      requestJson("/api/loop-runs?maximumCount=50", requestOptions),
      filteredPageRequest,
      requestJson("/api/loop-runs/quota", requestOptions),
    ]);
    if (
      requestGeneration !== runEvidenceRequestGeneration ||
      selectedLoopId() !== loopId
    )
      return null;
    if (runPaginationLoopId !== loopId) {
      runPaginationLoopId = loopId;
      runContinuationCursor = null;
      runPaginationExtended = false;
    }
    const workspaceRuns = Array.isArray(payload)
      ? payload
      : (payload?.items ?? []);
    const filteredRuns = Array.isArray(filteredPayload)
      ? filteredPayload
      : (filteredPayload?.items ?? []);
    const incomingRuns = mergeRunSummaries(filteredRuns, workspaceRuns);
    recentRuns = mergeRunSummaries(incomingRuns, recentRuns);
    if (!workspaceRunPaginationExtended && !loadingMoreRuns) {
      workspaceRunContinuationCursor = Array.isArray(payload)
        ? null
        : (payload?.continuationCursor ?? null);
    }
    if (
      !runPaginationExtended &&
      (!loadingMoreRuns || loadingMoreRunsLoopId !== loopId)
    ) {
      const pagePayload = filteredPayload ?? payload;
      runContinuationCursor = Array.isArray(pagePayload)
        ? null
        : (pagePayload?.continuationCursor ?? null);
    }
    traceQuota = quotaPayload;
    const visible = runsForCurrentLoop();
    const preferred = visible.find(
      (run) =>
        run.id === preferredRunId ||
        run.admissionOperationId === preferredAdmissionOperationId,
    );
    if (preferred) selectedRunId = preferred.id;
    if (!visible.some((run) => run.id === selectedRunId))
      selectedRunId = preserveEmptySelection ? null : (visible[0]?.id ?? null);
    if (selectedRunId) {
      const requestedRunId = selectedRunId;
      const summary = visible.find((run) => run.id === requestedRunId);
      const evidence = await loadSelectedRunEvidence(
        requestedRunId,
        summary,
        requestOptions,
      );
      if (evidence.trace?.isDeleted) {
        recentRuns = mergeRunSummaries(
          [tombstoneRunSummary(evidence.trace)],
          recentRuns.filter((run) => run.id !== requestedRunId),
        );
      }
      if (
        requestGeneration !== runEvidenceRequestGeneration ||
        selectedLoopId() !== loopId ||
        selectedRunId !== requestedRunId
      )
        return null;
      selectedRun = evidence.run;
      selectedTrace = evidence.trace;
      bindSelectedRunMonitor(selectedRun?.id ?? null);
      selectedRunMonitorMissCount = 0;
    } else {
      selectedRun = null;
      selectedTrace = null;
      bindSelectedRunMonitor(null);
      selectedRunMonitorMissCount = 0;
    }
    if (currentView === "runs") {
      renderRuns();
      renderRunEvidence();
    }
    renderList();
    renderTabs();
    if (
      elements.validationBanner.textContent.startsWith(
        "Run evidence unavailable:",
      )
    )
      renderValidation();
    scheduleSelectedRunRefresh();
    return true;
  } catch (error) {
    if (
      requestGeneration === runEvidenceRequestGeneration &&
      selectedLoopId() === loopId &&
      !silent
    )
      showBanner(`Run evidence unavailable: ${error.message}`);
    if (propagateFailure) throw error;
    return false;
  }
}

async function loadMoreRuns() {
  const loopId = runPaginationLoopId;
  if (!runContinuationCursor && !workspaceRunContinuationCursor) return;
  if (!loopId || selectedLoopId() !== loopId || loadingMoreRuns) return;
  const requestGeneration = ++runEvidenceRequestGeneration;
  const loopCursor = runContinuationCursor;
  const workspaceCursor = workspaceRunContinuationCursor;
  loadingMoreRuns = true;
  loadingMoreRunsLoopId = loopId;
  renderRunPagination();
  try {
    const [workspacePayload, loopPayload] = await Promise.all([
      workspaceCursor
        ? requestJson(
            `/api/loop-runs?maximumCount=50&cursor=${encodeURIComponent(workspaceCursor)}`,
          )
        : Promise.resolve(null),
      loopCursor
        ? requestJson(
            `/api/loop-runs?maximumCount=50&loopId=${encodeURIComponent(loopId)}&cursor=${encodeURIComponent(loopCursor)}`,
          )
        : Promise.resolve(null),
    ]);
    if (
      requestGeneration !== runEvidenceRequestGeneration ||
      selectedLoopId() !== loopId ||
      runPaginationLoopId !== loopId
    )
      return;
    recentRuns = mergeRunSummaries(
      loopPayload?.items ?? [],
      mergeRunSummaries(workspacePayload?.items ?? [], recentRuns),
    );
    if (workspacePayload) {
      workspaceRunContinuationCursor =
        workspacePayload.continuationCursor ?? null;
      workspaceRunPaginationExtended = true;
    }
    if (loopPayload) {
      runContinuationCursor = loopPayload.continuationCursor ?? null;
      runPaginationExtended = true;
    }
    renderList();
    renderTabs();
    if (currentView === "runs") renderRuns();
  } catch (error) {
    if (
      requestGeneration === runEvidenceRequestGeneration &&
      selectedLoopId() === loopId
    )
      showBanner(`More run evidence unavailable: ${error.message}`);
  } finally {
    if (loadingMoreRunsLoopId === loopId) {
      loadingMoreRuns = false;
      loadingMoreRunsLoopId = null;
    }
    renderRunPagination();
  }
}

async function loadSelectedRunEvidence(runId, summary, requestOptions = {}) {
  const traceRequest = requestJson(
    `/api/loop-runs/${encodeURIComponent(runId)}/trace`,
    requestOptions,
  );
  if (summary?.isDeleted) {
    return { run: null, trace: await traceRequest };
  }

  const [runResult, traceResult] = await Promise.allSettled([
    requestJson(`/api/loop-runs/${encodeURIComponent(runId)}`, requestOptions),
    traceRequest,
  ]);
  if (traceResult.status === "rejected") throw traceResult.reason;
  if (runResult.status === "fulfilled")
    return { run: runResult.value, trace: traceResult.value };
  if (
    runResult.reason?.status === 404 &&
    traceResult.value?.isDeleted &&
    traceResult.value.runId === runId
  ) {
    return { run: null, trace: traceResult.value };
  }
  throw runResult.reason;
}

function mergeRunSummaries(primary, secondary) {
  const byId = new Map();
  for (const run of [...primary, ...secondary]) {
    if (!byId.has(run.id)) byId.set(run.id, run);
  }
  return [...byId.values()].sort((left, right) => {
    const updated = String(right.updatedAtUtc).localeCompare(
      String(left.updatedAtUtc),
    );
    if (updated !== 0) return updated;
    const created = String(right.createdAtUtc).localeCompare(
      String(left.createdAtUtc),
    );
    return created !== 0
      ? created
      : String(left.id).localeCompare(String(right.id));
  });
}

function tombstoneRunSummary(trace) {
  const tombstone = trace.tombstone;
  return {
    id: tombstone.runId,
    loopId: tombstone.loopId,
    admissionOperationId: tombstone.admissionOperationId,
    definitionVersion: tombstone.definitionVersion,
    lifecycleVersion: 0,
    status: tombstone.terminalStatus,
    createdAtUtc: tombstone.createdAtUtc,
    updatedAtUtc: tombstone.deletedAtUtc,
    completedAtUtc: tombstone.completedAtUtc,
    iteration: 0,
    nextStepIndex: 0,
    failureCode: null,
    isDeleted: true,
  };
}

function liveRunSummary(run) {
  return {
    id: run.id,
    loopId: run.loopId,
    admissionOperationId: run.admissionOperationId,
    definitionVersion: run.admittedDefinition?.definitionVersion,
    lifecycleVersion: run.lifecycleVersion,
    status: run.status,
    createdAtUtc: run.createdAtUtc,
    updatedAtUtc: run.updatedAtUtc,
    completedAtUtc: run.completedAtUtc,
    iteration: run.checkpoint?.iteration ?? 0,
    nextStepIndex: run.checkpoint?.nextStepIndex ?? 0,
    failureCode: run.failureCode,
    isDeleted: false,
  };
}

function bindSelectedRunMonitor(runId) {
  if (selectedRunMonitorId === runId) return;
  selectedRunMonitorId = runId;
  selectedRunMonitorEtag = null;
  selectedRunMonitorMissCount = 0;
  selectedRunMonitorFailureKind = null;
  resetSelectedRunMonitorFallback();
}

async function refreshSelectedRunFromMonitor(runId) {
  if (selectedRun?.id !== runId) return false;
  bindSelectedRunMonitor(runId);
  const previousEtag = selectedRunMonitorEtag;
  try {
    const monitor = await requestRunMonitor(runId);
    if (selectedRun?.id !== runId) return false;
    if (monitor.notModified) {
      selectedRunMonitorMissCount = 0;
      selectedRunMonitorFailureKind = null;
      resetSelectedRunMonitorFallback();
      return true;
    }

    const refreshed = await loadRuns({ silent: true, preferredRunId: runId });
    if (refreshed && selectedRun?.id === runId) {
      selectedRunMonitorMissCount = 0;
      selectedRunMonitorFailureKind = null;
      selectedRunMonitorEtag = monitor.etag ?? previousEtag;
      resetSelectedRunMonitorFallback();
      return true;
    }

    selectedRunMonitorFailureKind = "full-refresh";
    selectedRunMonitorEtag = previousEtag;
    return false;
  } catch (error) {
    selectedRunMonitorEtag = previousEtag;
    if (error.status !== 404 || selectedRun?.id !== runId) {
      selectedRunMonitorMissCount = 0;
      selectedRunMonitorFailureKind = "endpoint";
      return false;
    }
    selectedRunMonitorFailureKind = "missing";
    selectedRunMonitorMissCount++;
    if (selectedRunMonitorMissCount < 2) return false;
    recentRuns = recentRuns.filter((run) => run.id !== runId);
    selectedRunId = null;
    selectedRun = null;
    selectedTrace = null;
    bindSelectedRunMonitor(null);
    await loadRuns({ silent: true, preserveEmptySelection: true });
    if (currentView === "runs") {
      renderRuns();
      renderRunEvidence();
    }
    showBanner(
      `Run evidence unavailable: ${error.message || "The selected run no longer exists."}`,
    );
    return false;
  }
}

async function fallbackSelectedRunAfterMonitorFailure(runId) {
  if (selectedRunMonitorFailureKind !== "endpoint" || selectedRun?.id !== runId)
    return false;
  const now = performance.now();
  if (now < selectedRunMonitorNextFallbackAt) return false;
  const refreshed = await loadRuns({ silent: true, preferredRunId: runId });
  selectedRunMonitorFallbackFailureCount++;
  const delay = Math.min(
    selectedRunMonitorFallbackMaximumDelayMilliseconds,
    selectedRunMonitorFallbackBaseDelayMilliseconds *
      2 ** (selectedRunMonitorFallbackFailureCount - 1),
  );
  selectedRunMonitorNextFallbackAt = now + delay;
  return refreshed;
}

function resetSelectedRunMonitorFallback() {
  selectedRunMonitorFallbackFailureCount = 0;
  selectedRunMonitorNextFallbackAt = 0;
}

function renderRuns() {
  renderTraceQuota();
  renderRunPagination();
  elements.runList.replaceChildren();
  const runs = runsForCurrentLoop();
  if (runs.length === 0) {
    elements.runList.append(
      node(
        "p",
        "empty-state",
        isSystemLoop()
          ? "Custom-loop runs do not apply to the system loop."
          : "No runs for this loop yet.",
      ),
    );
  } else {
    for (const run of runs) {
      const button = node(
        "button",
        `run-item${run.id === selectedRunId ? " selected" : ""}`,
      );
      button.type = "button";
      const top = node("span", "run-item-top");
      top.append(
        node("span", "run-id", run.id),
        node("span", `run-status-dot ${statusClass(run.status)}`),
      );
      button.append(
        top,
        node(
          "span",
          "run-meta",
          `v${run.definitionVersion} · ${formatStatus(run.status)}${run.isDeleted ? " · trace deleted" : ""}`,
        ),
        node("span", "run-meta", formatTimestamp(run.updatedAtUtc)),
      );
      button.addEventListener("click", () => selectRun(run.id));
      elements.runList.append(button);
    }
  }

  elements.runTimeline.replaceChildren();
  elements.runActions.replaceChildren();
  elements.runNotice.textContent = "";
  elements.runNotice.className = "run-notice";
  if (
    (!selectedRun || selectedRun.loopId !== selectedLoopId()) &&
    !(selectedTrace?.isDeleted && selectedTrace.loopId === selectedLoopId())
  ) {
    elements.runTitle.textContent = "No run selected";
    elements.runSubtitle.textContent =
      "Start a saved loop to inspect its durable evidence.";
    elements.runTimeline.append(
      node("p", "empty-state", "The ordered timeline will appear here."),
    );
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
    card.append(
      node("div", "event-title", "Trace content deleted"),
      node(
        "div",
        "event-detail",
        `Deleted ${formatTimestamp(selectedTrace.tombstone?.deletedAtUtc)}\nOperation ${selectedTrace.tombstone?.deletionOperationId ?? "unknown"}`,
      ),
    );
    event.append(card);
    tombstoneEvent.append(event);
    elements.runTimeline.append(tombstoneEvent);
    if (draft && !isSystemLoop())
      elements.runActions.append(
        actionButton("New run", openInvokeModal, dirty, "secondary-button"),
      );
    return;
  }

  elements.runTitle.textContent = `Run ${selectedRun.id}`;
  elements.runSubtitle.textContent = `${selectedRun.admittedDefinition.displayName} v${selectedRun.admittedDefinition.definitionVersion} · ${formatStatus(selectedRun.status)}`;
  renderRunActions(selectedRun);
  if (
    selectedRun.status === "PauseRequested" ||
    selectedRun.status === "CancelRequested"
  ) {
    elements.runNotice.textContent =
      selectedRun.status === "PauseRequested"
        ? "Pause requested. The current operation may finish; no later model boundary will start after the next proved checkpoint."
        : "Cancellation requested. The runtime will report Cancelled only when the last outcome is proved; uncertainty becomes Needs review.";
    elements.runNotice.className = "run-notice visible";
  }

  const timeline = node("div", "timeline");
  for (const event of selectedRun.events ?? [])
    timeline.append(renderRunEvent(event));
  if ((selectedRun.events ?? []).length === 0)
    timeline.append(
      node("p", "empty-state", "No persisted events were returned."),
    );
  elements.runTimeline.append(timeline);
}

function renderRunPagination() {
  const loadingCurrentLoop =
    loadingMoreRuns && loadingMoreRunsLoopId === runPaginationLoopId;
  elements.loadMoreRunsButton.hidden =
    !runContinuationCursor && !workspaceRunContinuationCursor;
  elements.loadMoreRunsButton.disabled = loadingCurrentLoop || mutationInFlight;
  elements.loadMoreRunsButton.textContent = loadingCurrentLoop
    ? "Loading more…"
    : "Load older evidence";
}

async function selectRun(runId) {
  runSelectionGeneration++;
  const requestGeneration = ++runEvidenceRequestGeneration;
  selectedRunId = runId;
  selectedRun = null;
  selectedTrace = null;
  renderRuns();
  renderRunEvidence();
  try {
    const summary = runsForCurrentLoop().find((run) => run.id === runId);
    const evidence = await loadSelectedRunEvidence(runId, summary);
    if (evidence.trace?.isDeleted) {
      recentRuns = mergeRunSummaries(
        [tombstoneRunSummary(evidence.trace)],
        recentRuns.filter((run) => run.id !== runId),
      );
    }
    if (
      requestGeneration !== runEvidenceRequestGeneration ||
      selectedRunId !== runId
    )
      return;
    selectedRun = evidence.run;
    selectedTrace = evidence.trace;
    renderRuns();
    renderRunEvidence();
    scheduleSelectedRunRefresh();
  } catch (error) {
    if (
      requestGeneration !== runEvidenceRequestGeneration ||
      selectedRunId !== runId
    )
      return;
    showBanner(`Run detail unavailable: ${error.message}`);
  }
}

function renderRunActions(run) {
  if (run.status === "Running")
    elements.runActions.append(
      actionButton("Pause at boundary", () => controlRun("pause"), false),
    );
  if (run.status === "Paused")
    elements.runActions.append(
      actionButton("Resume", resumeRun, false, "primary-button"),
    );
  if (["Admitted", "Running", "PauseRequested", "Paused"].includes(run.status))
    elements.runActions.append(
      actionButton(
        "Cancel",
        () => controlRun("cancel"),
        false,
        "danger-button",
      ),
    );
  if (
    ["Completed", "Failed", "Cancelled", "NeedsReview"].includes(run.status) &&
    selectedTrace &&
    !selectedTrace.isDeleted
  )
    elements.runActions.append(
      actionButton(
        "Delete sensitive trace",
        deleteSelectedTrace,
        false,
        "danger-button",
      ),
    );
  if (draft && !isSystemLoop())
    elements.runActions.append(
      actionButton("New run", openInvokeModal, dirty, "secondary-button"),
    );
}

function renderTraceQuota() {
  if (!elements.traceQuota) return;
  if (!traceQuota) {
    elements.traceQuota.textContent = "Trace quota unavailable";
    return;
  }

  elements.traceQuota.textContent = `${traceQuota.liveTraceCount}/${traceQuota.maximumLiveTraceCount} live · ${traceQuota.deletionOperationCount}/${traceQuota.maximumDeletionOperationCount} deletion receipts · ${formatBytes(traceQuota.actualStoredUtf8Bytes)} stored · ${formatBytes(traceQuota.reservedCapacityUtf8Bytes)} reserved · ${formatBytes(traceQuota.availableAccountedUtf8Bytes)} available`;
}

function renderRunEvent(event) {
  const container = node("div", "timeline-event");
  const symbol = event.kind?.includes("Failed")
    ? "!"
    : event.kind?.includes("Completed")
      ? "✓"
      : event.kind?.includes("Exit")
        ? "E"
        : event.kind?.includes("Node")
          ? "N"
          : "·";
  container.append(node("div", `event-dot ${statusClass(event.kind)}`, symbol));
  const card = node("div", "event-card");
  const top = node("div", "event-card-top");
  const location = [
    event.iteration ? `iteration ${event.iteration}` : "",
    event.stepId ?? "",
    event.attempt ? `attempt ${event.attempt}` : "",
  ]
    .filter(Boolean)
    .join(" · ");
  top.append(
    node("span", "event-title", splitWords(event.kind)),
    node("span", "event-time", formatTimestamp(event.timestampUtc)),
  );
  card.append(
    top,
    node(
      "div",
      "event-detail",
      `${location}${location && event.detail ? "\n" : ""}${event.detail ?? ""}`,
    ),
  );
  if (event.canonicalOutput)
    card.append(node("div", "event-output", event.canonicalOutput));
  const attemptEvidence = [
    event.provider
      ? `provider ${event.provider}${event.model ? ` · model ${event.model}` : ""}`
      : null,
    event.providerResponseId
      ? `provider response ${event.providerResponseId}`
      : null,
    event.originalOutputCharacterCount != null
      ? `canonical output ${event.canonicalOutput?.length ?? 0}/${event.originalOutputCharacterCount} chars · ${event.canonicalOutputTruncated ? "truncated" : "complete"}`
      : null,
    event.retainedForLoopReasoning != null
      ? `loop reasoning ${event.retainedForLoopReasoning ? "retained" : "evidence only"}`
      : null,
    publicationTimelineEvidence(event),
    event.exitDecision
      ? `Exit decision ${formatStatus(event.exitDecision)}`
      : null,
  ].filter(Boolean);
  if (attemptEvidence.length)
    card.append(node("div", "evidence-code", attemptEvidence.join("\n")));
  if (event.toolEvidence)
    card.append(renderToolEvidence(event.toolEvidence, false));
  else if (event.toolAuthority)
    card.append(renderToolAuthority(event.toolAuthority));
  container.append(card);
  return container;
}

function renderToolEvidence(evidence, includePayload = true) {
  const details = node("details", "context-block tool-evidence");
  const outcome = evidence.outcome
    ? ` · ${formatStatus(evidence.outcome)}`
    : "";
  details.append(
    node(
      "summary",
      "",
      `Tool request ${evidence.requestOrdinal} · ${formatStatus(evidence.command)} · ${formatStatus(evidence.phase)}${outcome}`,
    ),
  );
  const governance = evidence.governance;
  const governanceDisposition = governance
    ? `authority ${formatStatus(governance.authorityDecision)} · permission ${governance.permissionDecision ? formatStatus(governance.permissionDecision) : "not evaluated"} · approval ${formatStatus(governance.approvalDecision)}`
    : evidence.phase === "IntegrityFailed"
      ? "governance not evaluated · non-actuating integrity failure"
      : evidence.phase === "RequestReserved"
        ? "governance pending after durable request reservation"
        : "governance evidence unavailable";
  const lines = [
    `request ${evidence.requestCorrelationId}`,
    evidence.brokerRequestId ? `broker ${evidence.brokerRequestId}` : null,
    `target ${evidence.targetPath}`,
    evidence.resolvedTarget ? `resolved ${evidence.resolvedTarget}` : null,
    `returned to model ${evidence.returnedToModel ? "yes" : "no"}`,
    evidence.canonicalResultCharacterCount != null
      ? `canonical result ${evidence.canonicalResultCharacterCount} chars · ${evidence.canonicalResultHash ?? "hash unavailable"}`
      : null,
    governanceDisposition,
    governance?.authorityDetail
      ? `authority detail ${governance.authorityDetail}`
      : null,
    governance?.permissionMatchedPath
      ? `permission rule ${governance.permissionMatchedPath}`
      : null,
    governance?.permissionDetail
      ? `permission detail ${governance.permissionDetail}`
      : null,
    governance?.permissionPolicyHash
      ? `permission policy ${governance.permissionPolicyHash}`
      : null,
    governance?.approvalDecisionBy
      ? `approval decision by ${governance.approvalDecisionBy}`
      : null,
    governance?.approvalDetail
      ? `approval detail ${governance.approvalDetail}`
      : null,
    ...toolAuthorityLines(evidence.authority),
  ].filter(Boolean);
  details.append(node("div", "evidence-code", lines.join("\n")));
  if (includePayload) {
    const argumentsText = [
      evidence.content == null ? null : `content\n${evidence.content}`,
      evidence.pattern == null ? null : `pattern\n${evidence.pattern}`,
    ]
      .filter(Boolean)
      .join("\n\n");
    if (argumentsText) details.append(node("pre", "", argumentsText));
    if (evidence.canonicalResultReturnedToModel != null)
      details.append(node("pre", "", evidence.canonicalResultReturnedToModel));
  }
  return details;
}

function renderToolAuthority(authority) {
  const details = node("details", "context-block tool-authority");
  details.append(
    node(
      "summary",
      "",
      `Tool authority · ${authority.isValid ? "valid" : "invalid"} · ${authority.effectiveAssignments?.length ?? 0} effective`,
    ),
  );
  details.append(
    node("div", "evidence-code", toolAuthorityLines(authority).join("\n")),
  );
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
    authority.detail,
  ];
}

function renderRunEvidence() {
  elements.inspectorContent.replaceChildren();
  elements.inspectorTitle.textContent = "Run evidence";
  appendQuotaEvidence();
  if (
    !selectedRun &&
    selectedTrace?.isDeleted &&
    selectedTrace.loopId === selectedLoopId()
  ) {
    elements.inspectorContent.append(
      node("h3", "evidence-title", "Audited trace tombstone"),
      node(
        "p",
        "evidence-subtitle",
        "Sensitive run content is gone; bounded identity and deletion-integrity metadata remain.",
      ),
    );
    appendEvidenceSection(
      "Deleted run",
      selectedTrace.runId,
      `${selectedTrace.tombstone?.terminalStatus ?? selectedTrace.status} · loop ${selectedTrace.loopId}\nDefinition ${selectedTrace.definitionHash}`,
    );
    appendEvidenceSection(
      "Original trace",
      formatBytes(selectedTrace.originalTraceUtf8Bytes),
      selectedTrace.originalTraceHash,
    );
    appendEvidenceSection(
      "Deletion",
      `${formatTimestamp(selectedTrace.tombstone?.deletedAtUtc)} · ${formatStatus(selectedTrace.tombstone?.outcomeIntegrity)}`,
      `Operation ${selectedTrace.tombstone?.deletionOperationId ?? "unknown"}\nIntent audit ${selectedTrace.tombstone?.intentAuditCorrelationId ?? "unknown"}\nOutcome audit ${selectedTrace.tombstone?.outcomeAuditCorrelationId ?? "unknown"}`,
    );
    return;
  }
  if (!selectedRun || selectedRun.loopId !== selectedLoopId()) {
    elements.inspectorContent.append(
      node(
        "p",
        "empty-state",
        "Select a run to inspect its admitted definition, context, outputs, and recovery state.",
      ),
    );
    return;
  }

  elements.inspectorContent.append(
    node("h3", "evidence-title", "What ran, why, and with what authority"),
    node(
      "p",
      "evidence-subtitle",
      "Durable logical evidence; provider-private reasoning is not exposed.",
    ),
  );
  const definition = selectedRun.admittedDefinition;
  appendEvidenceSection(
    "Admitted definition",
    `${definition.displayName} v${definition.definitionVersion}`,
    `${definition.contentHash}\n${definition.inferenceSteps.length} inference step${definition.inferenceSteps.length === 1 ? "" : "s"} · ${definition.exitPolicy.maxAdditionalIterations > 0 ? `LLM-gated continuation, ceiling ${definition.exitPolicy.maxAdditionalIterations} additional` : "one deterministic iteration"}`,
  );
  appendEvidenceSection(
    "Invocation",
    selectedRun.triggerPrompt || "No prompt admitted",
    `${selectedRun.surface} surface · conversation ${selectedRun.invokingConversation ? "bound" : "not bound"}`,
  );
  appendEvidenceSection(
    "Admission identity",
    selectedRun.admissionActor,
    `Operation ${selectedRun.admissionOperationId}\nRequest hash ${selectedRun.admissionRequestHash}`,
  );
  appendRunProgressEvidence(selectedRun, definition);
  appendEvidenceSection(
    "Provider and model",
    `${selectedRun.model.provider} · ${selectedRun.model.model || "provider default"}`,
    (selectedRun.events ?? [])
      .filter((event) => event.provider || event.providerResponseId)
      .map(
        (event) =>
          `event ${event.sequence}: ${event.provider ?? selectedRun.model.provider} · ${event.model ?? selectedRun.model.model ?? "provider default"}${event.providerResponseId ? ` · response ${event.providerResponseId}` : ""}`,
      )
      .join("\n") || "No provider attempt has been persisted yet.",
  );
  appendEvidenceSection(
    "Provider usage and cost",
    "Unavailable",
    "The current provider response does not report token usage or cost; no estimate is fabricated.",
  );
  appendEvidenceSection(
    "Role and authority",
    definitionRoleId(definition),
    definition.toolAssignments.length
      ? definition.toolAssignments.join(" · ")
      : "No model-facing tools assigned",
  );
  appendEvidenceSection(
    "Context snapshot",
    `Captured ${formatTimestamp(selectedRun.context.capturedAtUtc)}`,
    `${selectedRun.context.manifestHash}\n${selectedRun.context.workspaceContextMessages.length} workspace context messages · ${selectedRun.context.invokingConversationMessages.length} conversation messages`,
  );
  if (selectedTrace)
    appendEvidenceSection(
      "Sensitive trace storage",
      formatBytes(selectedTrace.persistedArtifactUtf8Bytes),
      `${selectedTrace.persistedArtifactHash}\nExplicit deletion is irreversible. No trace is pruned automatically.`,
    );

  const manifest = selectedRun.context.sourceManifest ?? [];
  if (manifest.length > 0) {
    const manifestSection = evidenceSection("Admitted context source manifest");
    for (const source of manifest) {
      const details = node("details", "context-block");
      const disposition = source.omissionReason
        ? `omitted: ${source.omissionReason}`
        : `${source.usedCharacterCount}/${source.originalCharacterCount} chars${source.truncated ? " · truncated" : ""}`;
      details.append(
        node(
          "summary",
          "",
          `${source.order}. ${source.sourceType} · ${source.sourceId} · ${source.trustClass} · ${disposition}`,
        ),
      );
      details.append(
        node(
          "div",
          "evidence-code",
          `${source.sourcePath}\n${source.provenance} · ${source.role} · captured ${formatTimestamp(source.capturedAtUtc)}\n${source.contentHash}${source.truncationReason ? `\n${source.truncationReason}` : ""}`,
        ),
      );
      details.append(
        node(
          "pre",
          "",
          source.omissionReason ? "Content was not admitted." : source.content,
        ),
      );
      manifestSection.append(details);
    }
    elements.inspectorContent.append(manifestSection);
  }

  const contextEvents = (selectedRun.events ?? []).filter(
    (event) => (event.contextBlocks ?? []).length > 0,
  );
  if (contextEvents.length > 0) {
    const contextSection = evidenceSection("Resolved model context");
    for (const event of contextEvents) {
      const attempt = node("article", "context-attempt");
      const location = [
        `event ${event.sequence}`,
        event.iteration ? `iteration ${event.iteration}` : null,
        event.stepId ? `node ${event.stepId}` : null,
        event.attempt ? `attempt ${event.attempt}` : null,
      ]
        .filter(Boolean)
        .join(" · ");
      attempt.append(node("h4", "section-heading", location));
      const policy = resolvedEventPolicy(selectedRun.admittedDefinition, event);
      if (policy)
        attempt.append(
          node("div", "evidence-code", contextPolicyLines(policy).join("\n")),
        );
      for (const block of event.contextBlocks) {
        const details = node("details", "context-block");
        details.append(
          node(
            "summary",
            "",
            `${block.source} · ${block.included ? "included" : `omitted: ${block.omissionReason ?? "policy"}`} · ${block.characterCount} chars${block.truncated ? " · truncated" : ""}${block.sourceVersion ? ` · source ${block.sourceVersion}` : ""}`,
          ),
        );
        details.append(
          node(
            "div",
            "evidence-code",
            `source id ${block.sourceId}\nrole ${block.role}\nhash ${block.contentHash}\nsource version ${block.sourceVersion ?? "not versioned"}\ndisposition ${block.included ? "included" : `omitted: ${block.omissionReason ?? "policy"}`} · ${block.characterCount} chars · ${block.truncated ? "truncated" : "complete"}`,
          ),
        );
        details.append(
          node(
            "pre",
            "",
            block.included
              ? block.content
              : "Content omitted by the recorded policy.",
          ),
        );
        attempt.append(details);
      }
      contextSection.append(attempt);
    }
    elements.inspectorContent.append(contextSection);
  }

  const toolEvents = (selectedRun.events ?? []).filter(
    (event) => event.toolEvidence,
  );
  if (toolEvents.length > 0) {
    const toolSection = evidenceSection(
      "Tool requests, governance, and model-visible results",
    );
    for (const event of toolEvents)
      toolSection.append(renderToolEvidence(event.toolEvidence));
    elements.inspectorContent.append(toolSection);
  } else {
    const authorityEvent = (selectedRun.events ?? []).find(
      (event) => event.toolAuthority,
    );
    if (authorityEvent) {
      const authoritySection = evidenceSection("Attempt authority");
      authoritySection.append(
        renderToolAuthority(authorityEvent.toolAuthority),
      );
      elements.inspectorContent.append(authoritySection);
    }
  }

  const publicationDispositions =
    selectedRun.conversationPublicationDispositions ?? [];
  appendEvidenceSection(
    "Output disposition",
    selectedRun.finalOutput ?? "No terminal output",
    publicationDispositionLines(selectedRun, publicationDispositions).join(
      "\n",
    ),
  );
  if (selectedRun.failureCode || selectedRun.failureDetail)
    appendEvidenceSection(
      "Failure or recovery",
      selectedRun.failureCode ?? formatStatus(selectedRun.status),
      selectedRun.failureDetail ??
        "Inspect the ordered timeline for the persisted boundary.",
    );
}

function publicationDispositionLines(run, dispositions) {
  if (dispositions.length === 0)
    return [
      "No conversation publication requested; no durable publication operation was recorded.",
    ];

  return dispositions.flatMap((disposition) => {
    const phases = (run.events ?? [])
      .filter(
        (event) => event.conversationPublicationId === disposition.operationId,
      )
      .map(
        (event) => `event ${event.sequence} · ${publicationPhaseLabel(event)}`,
      );
    return [
      `${disposition.operationId}: ${formatPublicationDisposition(disposition)}${disposition.isDefinite ? " · definite" : " · review required"}`,
      `  ${disposition.detail}`,
      ...phases.map((phase) => `  ${phase}`),
    ];
  });
}

function formatPublicationDisposition(disposition) {
  const label = formatStatus(disposition.disposition);
  return disposition.hasIntegrityWarning
    ? `Integrity warning: ${label}`
    : label;
}

function publicationPhaseLabel(event) {
  switch (event.kind) {
    case "NodeOutcomeObserved":
    case "ExitDecisionCompleted":
      return "output policy selected";
    case "ConversationPublicationStarted":
      return "intent committed";
    case "ConversationPublished":
      return "terminal outcome recorded";
    default:
      return `${formatStatus(event.kind)} correlated evidence`;
  }
}

function publicationTimelineEvidence(event) {
  if (event.conversationPublicationId) {
    const disposition = (
      selectedRun?.conversationPublicationDispositions ?? []
    ).find((item) => item.operationId === event.conversationPublicationId);
    const terminal =
      event.kind === "ConversationPublished" && disposition
        ? ` · ${formatPublicationDisposition(disposition)}`
        : "";
    return `conversation publication ${publicationPhaseLabel(event)} · ${event.conversationPublicationId}${terminal}`;
  }

  if (event.publishedToInvokingConversation == null) return null;
  return `conversation publication ${event.publishedToInvokingConversation ? "selected" : "not selected"}`;
}

function appendRunProgressEvidence(run, definition) {
  const checkpoint = run.checkpoint;
  const events = run.events ?? [];
  const latest = events.at(-1);
  const accumulated = Number(
    run.executionClock?.accumulatedRunningMilliseconds ?? 0,
  );
  const activeSince = run.executionClock?.activeSinceUtc
    ? new Date(run.executionClock.activeSinceUtc).valueOf()
    : null;
  const activeElapsed = Number.isFinite(activeSince)
    ? Math.max(0, Date.now() - activeSince)
    : 0;
  const elapsed = Math.max(0, accumulated + activeElapsed);
  const deadline = Number(catalog?.limits?.maxRunExecutionMilliseconds);
  const remaining = Number.isFinite(deadline)
    ? Math.max(0, deadline - elapsed)
    : null;
  const terminal = ["Completed", "Failed", "Cancelled", "NeedsReview"].includes(
    run.status,
  );
  const nextStep = terminal
    ? "Terminal checkpoint"
    : checkpoint?.pendingExitDecision ||
        (checkpoint?.nextStepIndex >= definition.inferenceSteps.length &&
          definition.exitPolicy.maxAdditionalIterations > 0)
      ? "Exit decision"
      : checkpoint?.nextStepIndex >= definition.inferenceSteps.length
        ? "Deterministic completion boundary"
        : (definition.inferenceSteps[checkpoint?.nextStepIndex ?? 0]?.name ??
          "Unknown boundary");
  const current =
    [
      latest?.iteration
        ? `iteration ${latest.iteration}`
        : checkpoint?.iteration
          ? `iteration ${checkpoint.iteration}`
          : null,
      latest?.stepId,
      latest?.attempt ? `attempt ${latest.attempt}` : null,
    ]
      .filter(Boolean)
      .join(" · ") || "No model attempt dispatched";
  const deadlineText = Number.isFinite(deadline)
    ? `${formatDuration(elapsed)} elapsed · ${formatDuration(remaining)} remaining of ${formatDuration(deadline)}`
    : `${formatDuration(elapsed)} elapsed · deadline unavailable`;
  appendEvidenceSection(
    "Status and checkpoint",
    `${formatStatus(run.status)} · ${current}`,
    `run ${run.id}\nloop ${run.loopId} · role ${definitionRoleId(definition)} · ${run.surface} surface\nlifecycle version ${run.lifecycleVersion}\nexecution ${deadlineText}\nnext proved boundary ${nextStep}\niteration ${checkpoint?.iteration ?? "unknown"} · accepted repeats ${checkpoint?.acceptedRepeatCount ?? "unknown"} · tool requests ${checkpoint?.toolRequestsUsed ?? "unknown"}\nlast committed sequence ${checkpoint?.lastCommittedSequence ?? "none"} · latest event ${latest?.sequence ?? "none"} ${latest?.kind ? formatStatus(latest.kind) : ""}\npending approvals visible to this connection ${elements.approvals.children.length}`,
  );
}

function resolvedEventPolicy(definition, event) {
  const isExit =
    event.stepId === "exit" || String(event.kind ?? "").startsWith("Exit");
  const kind = isExit ? "exit" : "inference";
  const owner = isExit
    ? definition.exitPolicy
    : definition.inferenceSteps.find((step) => step.id === event.stepId);
  if (!owner?.contextPolicy) return null;
  return owner.contextPolicy.mode === "custom"
    ? owner.contextPolicy.customPolicy
    : definition.contextDefaults[kind];
}

function contextPolicyLines(policy) {
  return [
    `resolved context in · role ${yesNo(policy.contextIn.includeRoleContext)} · trigger ${yesNo(policy.contextIn.includeTriggerPrompt)} · conversation ${yesNo(policy.contextIn.includeInvokingConversation)} · retained outputs ${yesNo(policy.contextIn.includeEarlierRetainedOutputs)} · previous iteration ${yesNo(policy.contextIn.includePreviousIterationResult)}`,
    `resolved context out · loop reasoning ${yesNo(policy.contextOut.retainForLoopReasoning)} · invoking conversation ${yesNo(policy.contextOut.publishToInvokingConversation)}`,
  ];
}

function yesNo(value) {
  return value ? "included" : "excluded";
}

function appendQuotaEvidence() {
  if (!traceQuota) return;
  appendEvidenceSection(
    "Workspace trace quota",
    `${traceQuota.liveTraceCount}/${traceQuota.maximumLiveTraceCount} live · ${traceQuota.tombstoneCount}/${traceQuota.maximumTombstoneCount} tombstones · ${traceQuota.deletionOperationCount}/${traceQuota.maximumDeletionOperationCount} deletion receipts`,
    `${formatBytes(traceQuota.actualStoredUtf8Bytes)} physically stored\n${formatBytes(traceQuota.reservedCapacityUtf8Bytes)} reserved across ${traceQuota.activeReservationCount} trace reservation${traceQuota.activeReservationCount === 1 ? "" : "s"}\n${formatBytes(traceQuota.accountedUtf8Bytes)} accounted of ${formatBytes(traceQuota.maximumWorkspaceUtf8Bytes)} · no automatic pruning`,
  );
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

function definitionRoleId(definition) {
  return (
    definition?.roleId ?? definition?.owningRole?.identity?.roleId ?? "unknown"
  );
}

function renderList() {
  elements.list.replaceChildren();
  if (!catalog) return;
  const listOptions = [];
  const matchesSearch = (definition) => {
    const projectedDefinition =
      draft?.id === definition.id ? draft : definition;
    return (
      !loopSearchQuery ||
      [
        projectedDefinition.displayName,
        projectedDefinition.description,
        projectedDefinition.id,
      ].some((value) =>
        String(value ?? "")
          .toLocaleLowerCase()
          .includes(loopSearchQuery),
      )
    );
  };
  const visibleDefinitions = [
    ...catalog.customDefinitions,
    catalog.systemDefault,
  ].filter(matchesSearch);
  let visibleGroup = null;
  if (
    isNewLoopDraft() &&
    (!loopSearchQuery ||
      [draft.displayName, draft.description, "unsaved draft"].some((value) =>
        String(value ?? "")
          .toLocaleLowerCase()
          .includes(loopSearchQuery),
      ))
  ) {
    elements.list.append(node("div", "loop-list-group", "Draft"));
    visibleGroup = "Draft";
    const button = node("button", "loop-list-item selected");
    button.type = "button";
    button.disabled = mutationInFlight;
    button.setAttribute("role", "option");
    button.setAttribute("aria-selected", "true");
    button.tabIndex = 0;
    button.dataset.loopOptionKey = "draft:new-loop";
    button.append(node("span", "loop-icon custom", "D"));
    const copy = node("span", "loop-list-copy");
    copy.append(node("span", "loop-list-name", draft.displayName));
    const meta = node("span", "loop-list-meta");
    meta.append(
      node("span", "version-chip", "Unsaved draft"),
      node("span", "", "Not durable"),
    );
    copy.append(meta);
    button.append(copy);
    button.addEventListener("keydown", (event) =>
      moveLoopOptionFocus(event, button),
    );
    elements.list.append(button);
    listOptions.push(button);
  }
  for (const definition of visibleDefinitions) {
    const projectedDefinition =
      draft?.id === definition.id ? draft : definition;
    const group =
      definition.id === "default-conversation" ? "System" : "Custom loops";
    if (group !== visibleGroup) {
      elements.list.append(node("div", "loop-list-group", group));
      visibleGroup = group;
    }
    const button = node("button", "loop-list-item");
    button.type = "button";
    button.disabled = mutationInFlight;
    button.setAttribute("role", "option");
    button.setAttribute(
      "aria-selected",
      definition.id === currentDefinition?.id ? "true" : "false",
    );
    button.classList.toggle(
      "selected",
      definition.id === currentDefinition?.id,
    );
    button.tabIndex = definition.id === currentDefinition?.id ? 0 : -1;
    button.dataset.loopOptionKey = `definition:${definition.id}`;
    button.append(
      node(
        "span",
        `loop-icon ${definition.id === "default-conversation" ? "system" : "custom"}`,
        definition.id === "default-conversation" ? "◇" : "↻",
      ),
    );
    const copy = node("span", "loop-list-copy");
    copy.append(
      node("span", "loop-list-name", projectedDefinition.displayName),
    );
    const meta = node("span", "loop-list-meta");
    meta.append(
      node(
        "span",
        definition.id === "default-conversation"
          ? "system-chip"
          : "version-chip",
        definition.id === "default-conversation"
          ? "System loop"
          : `v${projectedDefinition.definitionVersion}`,
      ),
    );
    meta.append(
      node(
        "span",
        "",
        definition.id === "default-conversation"
          ? `${projectedDefinition.graph.nodes.length} nodes · ${projectedDefinition.graph.edges.length} edges`
          : projectedDefinition.inferenceSteps.length === 1
            ? "1 step"
            : `${projectedDefinition.inferenceSteps.length} steps`,
      ),
    );
    copy.append(meta);
    button.append(copy);
    button.addEventListener("click", () => selectDefinition(definition));
    button.addEventListener("keydown", (event) =>
      moveLoopOptionFocus(event, button),
    );
    elements.list.append(button);
    listOptions.push(button);
  }
  const knownLoopIds = new Set(
    allDefinitions().map((definition) => definition.id),
  );
  const archivedGroups = new Map();
  for (const run of recentRuns) {
    if (!knownLoopIds.has(run.loopId))
      archivedGroups.set(run.loopId, (archivedGroups.get(run.loopId) ?? 0) + 1);
  }
  const visibleArchivedGroups = [...archivedGroups].filter(
    ([loopId]) =>
      !loopSearchQuery || loopId.toLocaleLowerCase().includes(loopSearchQuery),
  );
  if (visibleArchivedGroups.length > 0)
    elements.list.append(node("div", "loop-list-group", "Archived evidence"));
  for (const [loopId, runCount] of visibleArchivedGroups) {
    const button = node("button", "loop-list-item");
    button.type = "button";
    button.disabled = mutationInFlight;
    button.setAttribute("role", "option");
    button.setAttribute(
      "aria-selected",
      loopId === historicalLoopId ? "true" : "false",
    );
    button.classList.toggle("selected", loopId === historicalLoopId);
    button.tabIndex = loopId === historicalLoopId ? 0 : -1;
    button.dataset.loopOptionKey = `archived:${loopId}`;
    button.append(node("span", "loop-icon archived", "A"));
    const copy = node("span", "loop-list-copy");
    copy.append(node("span", "loop-list-name", `Deleted loop · ${loopId}`));
    const meta = node("span", "loop-list-meta");
    meta.append(
      node("span", "system-chip", "Archived evidence"),
      node("span", "", `${runCount} run${runCount === 1 ? "" : "s"}`),
    );
    copy.append(meta);
    button.append(copy);
    button.addEventListener("click", () => selectHistoricalLoop(loopId));
    button.addEventListener("keydown", (event) =>
      moveLoopOptionFocus(event, button),
    );
    elements.list.append(button);
    listOptions.push(button);
  }
  if (
    !listOptions.some((option) => option.tabIndex === 0) &&
    listOptions.length > 0
  )
    listOptions[0].tabIndex = 0;
  if (elements.list.children.length === 0)
    elements.list.append(
      node("p", "empty-state", "No loops match this search."),
    );
  if (mutationInFlight)
    for (const item of elements.list.children) item.disabled = true;
}

async function selectDefinition(definition) {
  if (mutationInFlight) return;
  if (definition.id === currentDefinition?.id && !historicalLoopId) return;
  if (isNewLoopDraft() && newLoopDraftCommitState === "uncertain") {
    showBanner(
      "Resolve the uncertain first save by retrying the same Save request before leaving this draft.",
    );
    return;
  }
  if (dirty && !window.confirm("Discard unsaved loop edits?")) return;
  if (isNewLoopDraft()) resetNewLoopDraftState(true);
  runSelectionGeneration++;
  applyDefinition(definition);
  if (currentView === "runs") await loadRuns({ silent: false });
}

async function selectHistoricalLoop(loopId) {
  if (mutationInFlight) return;
  if (isNewLoopDraft() && newLoopDraftCommitState === "uncertain") {
    showBanner(
      "Resolve the uncertain first save by retrying the same Save request before leaving this draft.",
    );
    return;
  }
  if (dirty && !window.confirm("Discard unsaved loop edits?")) return;
  if (isNewLoopDraft()) resetNewLoopDraftState(true);
  runSelectionGeneration++;
  runEvidenceRequestGeneration++;
  historicalLoopId = loopId;
  currentDefinition = null;
  draft = null;
  selectedNodeId = "loop-settings";
  lastSelectedNodeId = "trigger";
  dirty = false;
  currentView = "runs";
  selectedRunId = recentRuns.find((run) => run.loopId === loopId)?.id ?? null;
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
    applyCanvasZoom();
    return;
  }

  if (isSystemLoop()) {
    renderSystemCanvas();
    applyCanvasZoom();
    return;
  }

  elements.canvas.append(
    createNodeCard(
      "trigger",
      "Manual trigger",
      "Manual trigger",
      triggerDescription(),
      "system",
      "admission",
      "Start",
    ),
  );
  appendConnector(0);
  draft.inferenceSteps.forEach((step, index) => {
    elements.canvas.append(
      createNodeCard(
        step.id ?? `local-${index}`,
        "Inference",
        step.name || `Step ${index + 1}`,
        step.instruction || "Instruction required",
        "inference",
        step.contextPolicy?.mode,
        `Step ${index + 1}`,
      ),
    );
    appendConnector(index + 1);
  });
  const exitPolicy = draft.exitPolicy;
  const exitSummary =
    exitPolicy.maxAdditionalIterations > 0
      ? `Make a tool-less continuation decision with a ceiling of ${exitPolicy.maxAdditionalIterations} additional iteration${exitPolicy.maxAdditionalIterations === 1 ? "" : "s"}.`
      : "Return the final retained output and complete without a continuation model call.";
  elements.canvas.append(
    createNodeCard(
      "exit",
      "Exit",
      "Exit",
      exitSummary,
      "exit",
      exitPolicy.contextPolicy?.mode,
      "Finish",
    ),
  );
  if (exitPolicy.maxAdditionalIterations > 0) {
    const rail = node("div", "repeat-rail");
    rail.append(
      node(
        "span",
        "",
        `Repeat may return to Step 1 · ceiling ${exitPolicy.maxAdditionalIterations}`,
      ),
    );
    elements.canvas.append(rail);
  }
  applyCanvasZoom();
}

function renderSystemCanvas() {
  if (draft.executionContract?.graphSemantics !== "validated-runner-contract") {
    draft.graph.nodes.forEach((graphNode, index) => {
      elements.canvas.append(createSystemNodeCard(graphNode, index));
      for (const edge of draft.graph.edges.filter(
        (candidate) => candidate.fromNodeId === graphNode.id,
      ))
        appendSystemConnector(edge, true);
    });
    return;
  }

  const sequence = systemGraphSequence();
  sequence.nodes.forEach((graphNode, index) => {
    elements.canvas.append(createSystemNodeCard(graphNode, index));
    const edge = sequence.edges[index];
    if (edge) appendSystemConnector(edge);
  });
}

function systemGraphSequence() {
  const graph = draft.graph;
  const nodesById = new Map(
    graph.nodes.map((graphNode) => [graphNode.id, graphNode]),
  );
  const nodes = [];
  const edges = [];
  const visited = new Set();
  let currentNodeId = graph.entryNodeId;
  while (currentNodeId && !visited.has(currentNodeId)) {
    const graphNode = nodesById.get(currentNodeId);
    if (!graphNode) break;
    visited.add(currentNodeId);
    nodes.push(graphNode);
    if (graph.terminalNodeIds.includes(currentNodeId)) break;
    const edge = graph.edges.find(
      (candidate) => candidate.fromNodeId === currentNodeId,
    );
    if (!edge) break;
    edges.push(edge);
    currentNodeId = edge.toNodeId;
  }
  return { nodes, edges };
}

function createSystemNodeCard(graphNode, index) {
  const className =
    graphNode.kind === "model-inference"
      ? "inference"
      : graphNode.kind === "run-finalization"
        ? "exit"
        : "system";
  const button = node("button", `node-card ${className}`);
  button.type = "button";
  button.classList.toggle("selected", selectedNodeId === graphNode.id);
  button.setAttribute(
    "aria-pressed",
    selectedNodeId === graphNode.id ? "true" : "false",
  );
  const header = node("span", "node-card-head");
  const kindCopy = node("span", "node-kind-wrap");
  kindCopy.append(
    node("span", "node-kind-dot"),
    node("span", "node-kind", capitalize(splitWords(graphNode.kind))),
  );
  header.append(
    kindCopy,
    node("span", "node-position", `Boundary ${index + 1}`),
  );
  button.append(
    header,
    node("span", "node-name", graphNode.displayName),
    node("span", "node-summary", graphNode.description),
  );
  const chips = node("span", "node-card-chips");
  chips.append(
    node("span", "node-chip", graphNode.id),
    node("span", "node-chip", "System locked"),
    node(
      "span",
      "node-chip",
      runnerContractLabel(graphNode.executionSemantics),
    ),
    node(
      "span",
      "node-chip",
      `${graphNode.capabilityIds.length} ${graphNode.capabilityIds.length === 1 ? "capability" : "capabilities"}`,
    ),
  );
  button.append(chips);
  button.addEventListener("click", () => {
    lastSelectedNodeId = graphNode.id;
    selectedNodeId = graphNode.id;
    renderCanvas();
    renderInspector();
    renderToolbar();
  });
  return button;
}

function appendSystemConnector(edge, includeEndpoints = false) {
  const connector = node("span", "connector system-connector");
  const endpoints = includeEndpoints
    ? ` · ${edge.fromNodeId} → ${edge.toNodeId}`
    : "";
  const label = node(
    "span",
    "system-connector-label",
    `${edge.id}${endpoints} · ${capitalize(splitWords(edge.condition))} · ${runnerContractLabel(edge.executionSemantics)}`,
  );
  label.title = edge.description;
  connector.append(label);
  elements.canvas.append(connector);
}

function createNodeCard(
  id,
  kind,
  name,
  summary,
  className,
  policyMode,
  position,
) {
  const button = node("button", `node-card ${className}`);
  button.type = "button";
  button.classList.toggle("selected", selectedNodeId === id);
  button.setAttribute("aria-pressed", selectedNodeId === id ? "true" : "false");
  const header = node("span", "node-card-head");
  const kindCopy = node("span", "node-kind-wrap");
  kindCopy.append(
    node("span", "node-kind-dot"),
    node("span", "node-kind", kind),
  );
  header.append(kindCopy, node("span", "node-position", position));
  button.append(
    header,
    node("span", "node-name", name),
    node("span", "node-summary", summary),
  );
  const chips = node("span", "node-card-chips");
  if (id === "trigger") {
    const trigger = draft.triggerPolicy;
    const promptLabel =
      trigger.promptSource === "preset"
        ? "Preset prompt"
        : trigger.promptSource === "none"
          ? "No prompt"
          : "Invoking user prompt";
    chips.append(
      node(
        "span",
        "node-chip",
        `${promptLabel} · conversation ${trigger.includeInvokingConversation ? "included" : "excluded"}`,
      ),
    );
  } else if (id === "exit") {
    chips.append(node("span", "node-chip", "evidence always retained"));
    chips.append(
      node(
        "span",
        "node-chip",
        draft.exitPolicy.maxAdditionalIterations > 0
          ? `Model-gated · up to ${draft.exitPolicy.maxAdditionalIterations} additional`
          : "Deterministic complete",
      ),
    );
  } else {
    const assignments = draft.toolAssignments.length;
    chips.append(node("span", "node-chip", `↳ ${draft.roleId}`));
    chips.append(
      node(
        "span",
        "node-chip",
        `${assignments} governed ${assignments === 1 ? "capability" : "capabilities"}`,
      ),
    );
    chips.append(
      node(
        "span",
        "node-chip",
        policyMode === "custom" ? "context customized" : "context inherited",
      ),
    );
  }
  button.append(chips);
  button.addEventListener("click", () => {
    lastSelectedNodeId = id;
    selectedNodeId = id;
    renderCanvas();
    renderInspector();
    renderToolbar();
  });
  return button;
}

function appendConnector(insertionIndex) {
  const connector = node("span", "connector");
  const canInsert =
    !isSystemLoop() &&
    !mutationInFlight &&
    draft.inferenceSteps.length < catalog.limits.maxInferenceSteps;
  if (canInsert) {
    const add = node("button", "connector-add", "+");
    add.type = "button";
    add.setAttribute("aria-label", "Add inference step here");
    add.addEventListener("click", () => insertInferenceStep(insertionIndex));
    connector.append(add);
  }
  elements.canvas.append(connector);
}

function triggerDescription() {
  const trigger = draft.triggerPolicy;
  if (trigger.promptSource === "preset")
    return `The saved preset enters the run${trigger.includeInvokingConversation ? " with a bounded conversation snapshot." : " without conversation history."}`;
  if (trigger.promptSource === "none")
    return `The run starts without a prompt${trigger.includeInvokingConversation ? " and includes a bounded conversation snapshot." : " or conversation history."}`;
  return `The invoking user prompt enters the run${trigger.includeInvokingConversation ? " with a bounded conversation snapshot." : " without conversation history."}`;
}

function renderInspector() {
  elements.inspectorContent.replaceChildren();
  const loopSettingsSelected = selectedNodeId === "loop-settings";
  elements.selectedNodeButton.classList.toggle("active", !loopSettingsSelected);
  elements.selectedNodeButton.setAttribute(
    "aria-selected",
    String(!loopSettingsSelected),
  );
  elements.loopSettingsButton.classList.toggle("active", loopSettingsSelected);
  elements.loopSettingsButton.setAttribute(
    "aria-selected",
    String(loopSettingsSelected),
  );
  elements.selectedNodeButton.tabIndex = loopSettingsSelected ? -1 : 0;
  elements.loopSettingsButton.tabIndex = loopSettingsSelected ? 0 : -1;
  elements.inspectorContent.setAttribute(
    "aria-labelledby",
    loopSettingsSelected ? "loopSettingsButton" : "selectedNodeButton",
  );
  if (!draft) {
    elements.inspectorTitle.textContent = "Loop settings";
    elements.inspectorContent.append(
      node("p", "empty-state", "No loop selected."),
    );
    return;
  }

  if (isSystemLoop()) {
    if (loopSettingsSelected) renderSystemLoopInspector();
    else renderSystemNodeInspector();
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
  const step =
    draft.inferenceSteps.find((item) => item.id === selectedNodeId) ??
    draft.inferenceSteps.find(
      (_, index) => `local-${index}` === selectedNodeId,
    );
  if (step) renderInferenceInspector(step);
  else renderLoopInspector();
}

function renderSystemLoopInspector() {
  elements.inspectorTitle.textContent = "System loop contract";
  const policy = section("Actual role, trigger, and context policy");
  policy.append(
    systemFact("Role", draft.roleId),
    systemFact("Trigger", capitalize(splitWords(draft.trigger))),
    systemFact(
      "Context and memory scope",
      capitalize(splitWords(draft.memoryScope)),
    ),
    systemFact("Review policy", capitalize(splitWords(draft.reviewPolicy))),
    systemFact("Failure policy", capitalize(splitWords(draft.failurePolicy))),
    systemFact("State", capitalize(splitWords(draft.state))),
    systemFact("Edit mode", capitalize(splitWords(draft.editMode))),
  );
  const authority = section("Loop-scoped capabilities");
  authority.append(
    node(
      "p",
      "field-hint",
      "These are the canonical default-loop capabilities, not authored custom-loop tool assignments. Governed workspace commands still pass through permissions, approvals, and audit.",
    ),
    systemFact("Capability IDs", draft.capabilityIds.join(", ")),
  );
  const execution = section("Current executor support");
  execution.append(
    systemFact("Dedicated runner", draft.executionContract.runner),
    systemFact(
      "Graph semantics",
      capitalize(splitWords(draft.executionContract.graphSemantics)),
    ),
    systemFact(
      "Generic graph dispatch",
      draft.executionContract.usesGenericGraphDispatcher
        ? "Supported"
        : "Not implemented",
    ),
    node("div", "context-note", draft.executionContract.detail),
  );
  const topology = section("Canonical topology");
  topology.append(
    systemFact("Entry node", draft.graph.entryNodeId),
    systemFact("Terminal nodes", draft.graph.terminalNodeIds.join(", ")),
    systemFact(
      "Structure",
      `${draft.graph.nodes.length} nodes · ${draft.graph.edges.length} edges`,
    ),
  );
  elements.inspectorContent.append(policy, authority, execution, topology);
}

function renderSystemNodeInspector() {
  const graphNode = draft.graph.nodes.find(
    (item) => item.id === selectedNodeId,
  );
  if (!graphNode) {
    renderSystemLoopInspector();
    return;
  }
  elements.inspectorTitle.textContent = graphNode.displayName;
  const boundary = section("Implemented boundary");
  boundary.append(
    node("div", "context-note", graphNode.description),
    systemFact("Stable node ID", graphNode.id),
    systemFact("Kind", capitalize(splitWords(graphNode.kind))),
    systemFact("Edit mode", capitalize(splitWords(graphNode.editMode))),
    systemFact("Capability IDs", graphNode.capabilityIds.join(", ") || "None"),
  );
  const execution = section("Execution semantics");
  execution.append(
    systemFact(
      "Semantics",
      capitalize(splitWords(graphNode.executionSemantics)),
    ),
    node("div", "context-note", draft.executionContract.detail),
  );
  const transitions = section("Canonical edges");
  const incoming = draft.graph.edges.filter(
    (edge) => edge.toNodeId === graphNode.id,
  );
  const outgoing = draft.graph.edges.filter(
    (edge) => edge.fromNodeId === graphNode.id,
  );
  for (const edge of incoming)
    transitions.append(
      systemFact(
        "Incoming",
        `${edge.id} · ${capitalize(splitWords(edge.condition))} · from ${edge.fromNodeId}. ${edge.description}`,
      ),
    );
  for (const edge of outgoing)
    transitions.append(
      systemFact(
        "Outgoing",
        `${edge.id} · ${capitalize(splitWords(edge.condition))} · to ${edge.toNodeId}. ${edge.description}`,
      ),
    );
  if (incoming.length === 0)
    transitions.append(systemFact("Incoming", "None · graph entry"));
  if (outgoing.length === 0)
    transitions.append(systemFact("Outgoing", "None · graph terminal"));
  elements.inspectorContent.append(boundary, execution, transitions);
}

function systemFact(label, value) {
  const fact = node("div", "context-note");
  fact.append(
    node("strong", "", `${label}: `),
    document.createTextNode(String(value)),
  );
  return fact;
}

function renderLoopInspector() {
  elements.inspectorTitle.textContent = "Loop settings";
  const role = section("Directory role");
  role.append(
    node(
      "div",
      "context-note",
      `${draft.roleId}. This loop belongs to the current directory role; wave one does not allow a loop to switch durable identity.`,
    ),
  );
  const model = section("Inherited provider and model");
  model.append(
    node(
      "div",
      "context-note",
      `${catalog.runtimeModel?.provider ?? "Provider unavailable"} · ${catalog.runtimeModel?.model || "provider default model"}. Provider and model cannot be overridden per loop in wave one.`,
    ),
  );
  const authority = section("Workspace tools · governed authority");
  authority.append(
    node(
      "p",
      "field-hint",
      "Assignments allow inference nodes to request governed capabilities. Permission, approval, and audit policy still decide whether each request may execute. Exit decisions are always tool-less.",
    ),
  );
  const assignableTools = catalog.tools?.customAssignable ?? [];
  const staleAssignments = [
    ...new Set(
      draft.toolAssignments.filter(
        (assignment) => !assignableTools.includes(assignment),
      ),
    ),
  ];
  for (const assignment of [...assignableTools, ...staleAssignments]) {
    const isStaleAssignment = staleAssignments.includes(assignment);
    authority.append(
      checkboxRow(
        capitalize(assignment),
        isStaleAssignment
          ? `This assignment is outside the current role authority. Uncheck it before saving the draft.`
          : `Allow inference nodes to request the governed ${assignment} command.`,
        draft.toolAssignments.includes(assignment),
        (checked) => {
          if (!canMutateDraft()) return;
          draft.toolAssignments = checked
            ? [...draft.toolAssignments, assignment]
            : draft.toolAssignments.filter((value) => value !== assignment);
          markDirty();
        },
        !canMutateDraft(),
      ),
    );
  }
  const defaults = section("Context defaults");
  defaults.append(
    node(
      "p",
      "field-hint",
      "Versioned server defaults are inspectable here. Context is customized at each Inference or Exit node.",
    ),
  );
  defaults.append(
    contextSummary("Inference", draft.contextDefaults.inference),
    contextSummary("Exit", draft.contextDefaults.exit),
  );
  defaults.append(evidenceNote());
  elements.inspectorContent.append(role, model, authority, defaults);
}

function renderTriggerInspector() {
  elements.inspectorTitle.textContent = "Manual trigger";
  const trigger = draft.triggerPolicy;
  const purpose = section("Context admitted to the run");
  const source = document.createElement("select");
  for (const [value, label] of [
    ["invocation", "Invoking user prompt"],
    ["preset", "Preset prompt"],
    ["none", "No prompt"],
  ]) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = label;
    option.selected = trigger.promptSource === value;
    source.append(option);
  }
  source.disabled = !canMutateDraft();
  source.addEventListener("change", (event) => {
    if (!canMutateDraft()) return;
    trigger.promptSource = event.target.value;
    if (trigger.promptSource !== "preset") trigger.presetPrompt = "";
    markDirty();
    renderInspector();
    renderCanvas();
  });
  purpose.append(
    field(
      "Prompt source",
      source,
      "Trigger admits exactly one typed prompt source; sources are never silently combined.",
    ),
  );
  if (trigger.promptSource === "preset") {
    const preset = document.createElement("textarea");
    preset.maxLength = catalog.limits.maxTriggerPromptCharacters;
    preset.value = trigger.presetPrompt;
    preset.disabled = !canMutateDraft();
    preset.addEventListener("input", (event) => {
      if (!canMutateDraft()) return;
      trigger.presetPrompt = event.target.value;
      markDirty();
    });
    purpose.append(
      field(
        "Preset prompt",
        preset,
        "Saved prompt supplied whenever this loop is invoked.",
      ),
    );
  }
  purpose.append(
    checkboxRow(
      "Include invoking conversation history",
      "Admit a bounded snapshot of the logical user session when one exists. Provider-thread history is never used.",
      trigger.includeInvokingConversation,
      (checked) => {
        if (!canMutateDraft()) return;
        trigger.includeInvokingConversation = checked;
        markDirty();
        renderCanvas();
      },
      !canMutateDraft(),
    ),
  );
  purpose.append(
    node(
      "div",
      "context-note",
      "The invoking prompt enters once. Trigger admission does not append it again or write durable memory.",
    ),
  );
  elements.inspectorContent.append(purpose);
}

function renderInferenceInspector(step) {
  const index = draft.inferenceSteps.indexOf(step);
  elements.inspectorTitle.textContent = `Inference step ${index + 1}`;
  const instruction = section("Step definition");
  instruction.append(
    node(
      "p",
      "inspector-subheading",
      "A model call inside the current role and loop authority.",
    ),
  );
  const name = document.createElement("input");
  name.maxLength = catalog.limits.maxNameCharacters;
  name.value = step.name;
  name.disabled = !canMutateDraft();
  name.addEventListener("input", (event) => {
    if (!canMutateDraft()) return;
    step.name = event.target.value;
    markDirty();
    renderCanvas();
  });
  const prompt = document.createElement("textarea");
  prompt.maxLength = catalog.limits.maxInstructionCharacters;
  prompt.value = step.instruction;
  prompt.disabled = !canMutateDraft();
  prompt.addEventListener("input", (event) => {
    if (!canMutateDraft()) return;
    step.instruction = event.target.value;
    markDirty();
    renderCanvas();
  });
  instruction.append(
    field("Node name", name),
    field(
      "Prompt-visible instruction",
      prompt,
      "Write the local objective. Trigger material and earlier output are supplied separately as governed context.",
    ),
  );
  const actions = node("div", "inline-actions");
  actions.append(
    actionButton(
      "↑ Move earlier",
      () => moveStep(index, -1),
      index === 0 || !canMutateDraft(),
    ),
    actionButton(
      "↓ Move later",
      () => moveStep(index, 1),
      index === draft.inferenceSteps.length - 1 || !canMutateDraft(),
    ),
    actionButton(
      "Remove",
      () => removeStep(index),
      draft.inferenceSteps.length === 1 || !canMutateDraft(),
      "danger-button",
    ),
  );
  instruction.append(actions);
  const effective = section("Effective role, model, and tools");
  effective.append(
    authorityCard("R", "Role", draft.roleId),
    authorityCard(
      "M",
      "Model",
      `${catalog.runtimeModel?.provider ?? "Unavailable"} / ${catalog.runtimeModel?.model || "provider default"}`,
    ),
    authorityCard(
      "T",
      "Tools",
      draft.toolAssignments.length
        ? draft.toolAssignments.join(", ")
        : "None assigned",
    ),
  );
  effective.append(
    node(
      "p",
      "field-hint",
      "Inherited from loop settings. Tool requests remain subject to the current role ceiling, permission rules, approvals, and audit.",
    ),
  );
  elements.inspectorContent.append(
    instruction,
    effective,
    contextEditor(step, "inference"),
  );
}

function authorityCard(icon, label, value) {
  const card = node("div", "authority-card");
  const copy = node("span", "authority-card-copy");
  copy.append(node("strong", "", label), node("span", "", value));
  card.append(
    node("span", "authority-card-icon", icon),
    copy,
    node("span", "inheritance-chip", "Inherited"),
  );
  return card;
}

function renderExitInspector() {
  elements.inspectorTitle.textContent = "Exit";
  const exit = draft.exitPolicy;
  const continuation = section("Conditional continuation");
  continuation.append(
    checkboxRow(
      "Allow continuation requests",
      "Exit may ask to return to Step 1. The ceiling never causes a repeat by itself.",
      exit.maxAdditionalIterations > 0,
      (checked) => {
        if (!canMutateDraft()) return;
        exit.maxAdditionalIterations = checked ? 1 : 0;
        markDirty();
        renderInspector();
        renderCanvas();
      },
      !canMutateDraft(),
    ),
  );
  if (exit.maxAdditionalIterations > 0) {
    const decision = document.createElement("textarea");
    decision.maxLength = catalog.limits.maxInstructionCharacters;
    decision.value = exit.decisionInstruction;
    decision.disabled = !canMutateDraft();
    decision.addEventListener("input", (event) => {
      if (!canMutateDraft()) return;
      exit.decisionInstruction = event.target.value;
      markDirty();
    });
    const ceiling = document.createElement("input");
    ceiling.type = "number";
    ceiling.min = "1";
    ceiling.max = String(catalog.limits.maxAdditionalIterations);
    ceiling.value = String(exit.maxAdditionalIterations);
    ceiling.disabled = !canMutateDraft();
    ceiling.addEventListener("change", (event) => {
      if (!canMutateDraft()) return;
      const value = Math.max(
        1,
        Math.min(
          catalog.limits.maxAdditionalIterations,
          Number.parseInt(event.target.value, 10) || 1,
        ),
      );
      exit.maxAdditionalIterations = value;
      event.target.value = String(value);
      markDirty();
      renderCanvas();
    });
    continuation.append(
      field(
        "Decision instruction",
        decision,
        "The trimmed response must be exactly one Complete or Repeat token (case-insensitive). Invalid or uncertain decisions never repeat.",
      ),
      field(
        "Maximum additional iterations",
        ceiling,
        "A hard ceiling, not a target. No Exit call is made once the ceiling is exhausted.",
      ),
    );
  }
  continuation.append(
    node(
      "div",
      "context-note",
      "Exit is tool-less. With continuation off, the run completes after one iteration without an Exit model call.",
    ),
  );
  elements.inspectorContent.append(continuation, contextEditor(exit, "exit"));
}

function contextEditor(owner, kind) {
  const container = section(`${capitalize(kind)} context`);
  const select = document.createElement("select");
  for (const [value, label] of [
    ["inherit", "Inherit loop defaults"],
    ["custom", "Customize this node"],
  ]) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = label;
    option.selected = owner.contextPolicy.mode === value;
    select.append(option);
  }
  select.disabled = !canMutateDraft();
  select.addEventListener("change", (event) => {
    if (!canMutateDraft()) return;
    owner.contextPolicy =
      event.target.value === "custom"
        ? { mode: "custom", customPolicy: clone(draft.contextDefaults[kind]) }
        : { mode: "inherit", customPolicy: null };
    markDirty();
    renderInspector();
    renderCanvas();
  });
  container.append(field("Policy source", select));
  const policy =
    owner.contextPolicy.mode === "custom"
      ? owner.contextPolicy.customPolicy
      : draft.contextDefaults[kind];
  const disabled = !canMutateDraft() || owner.contextPolicy.mode !== "custom";
  container.append(node("h3", "section-heading", "Context in"));
  const inputOptions = [
    [
      "includeRoleContext",
      "Directory role and startup context",
      "Role files and bounded workspace memory/context. Harness governance remains even when this is off.",
    ],
    [
      "includeTriggerPrompt",
      "Trigger prompt",
      "The invocation or preset prompt admitted by Trigger.",
    ],
    [
      "includeInvokingConversation",
      "Invoking conversation history",
      "Logical session history admitted by Trigger, never provider-thread history.",
    ],
    [
      "includeEarlierRetainedOutputs",
      "Earlier retained outputs",
      "Only outputs whose producer retained them for loop reasoning.",
    ],
    [
      "includePreviousIterationResult",
      "Previous iteration result",
      "The prior result after an accepted Exit Repeat decision.",
    ],
  ];
  const inputGrid = node("div", "context-grid");
  for (const [key, label, hint] of inputOptions)
    inputGrid.append(
      checkboxRow(
        label,
        hint,
        policy.contextIn[key],
        (checked) => {
          if (!canMutateDraft()) return;
          policy.contextIn[key] = checked;
          markDirty();
        },
        disabled,
      ),
    );
  container.append(inputGrid, node("h3", "section-heading", "Context out"));
  const outputGrid = node("div", "context-grid");
  outputGrid.append(
    checkboxRow(
      "Retain for later loop reasoning",
      "Makes this canonical output selectable at later model boundaries.",
      policy.contextOut.retainForLoopReasoning,
      (checked) => {
        if (!canMutateDraft()) return;
        policy.contextOut.retainForLoopReasoning = checked;
        markDirty();
      },
      disabled,
    ),
  );
  outputGrid.append(
    checkboxRow(
      "Publish to the invoking conversation",
      "Appends idempotently only to the server-bound invoking conversation when one exists.",
      policy.contextOut.publishToInvokingConversation,
      (checked) => {
        if (!canMutateDraft()) return;
        policy.contextOut.publishToInvokingConversation = checked;
        markDirty();
      },
      disabled,
    ),
  );
  container.append(outputGrid, evidenceNote());
  return container;
}

function contextSummary(label, policy) {
  const enabledIn = Object.values(policy.contextIn).filter(Boolean).length;
  const enabledOut = Object.values(policy.contextOut).filter(Boolean).length;
  return node(
    "div",
    "context-note",
    `${label}: ${enabledIn} context-in sources · ${enabledOut} context-out destinations`,
  );
}

function evidenceNote() {
  const note = node("div", "context-note");
  const strong = node("strong", "", "Evidence is independent of context. ");
  note.append(
    strong,
    document.createTextNode(
      "Even when both destinations are off, bounded output remains inspectable in the run trace. Durable memory writeback is a separate governed action and is not automatic.",
    ),
  );
  return note;
}

function renderToolbar() {
  const newDraft = isNewLoopDraft();
  const uncertainFirstSave =
    newDraft && newLoopDraftCommitState === "uncertain";
  const editable =
    Boolean(draft) &&
    !isSystemLoop() &&
    !mutationInFlight &&
    !uncertainFirstSave;
  const stepCount = isSystemLoop() ? 0 : (draft?.inferenceSteps.length ?? 0);
  const systemNodeCount = isSystemLoop() ? draft.graph.nodes.length : 0;
  const systemEdgeCount = isSystemLoop() ? draft.graph.edges.length : 0;
  const hasValidationErrors = validateDraft().length > 0;
  elements.name.disabled = !editable;
  elements.description.disabled = !editable;
  elements.saveButton.disabled =
    mutationInFlight ||
    !draft ||
    isSystemLoop() ||
    (!dirty && !uncertainFirstSave) ||
    hasValidationErrors;
  elements.saveButton.textContent = uncertainFirstSave ? "Retry save" : "Save";
  elements.reloadButton.disabled =
    mutationInFlight || !draft || (!newDraft && !dirty) || uncertainFirstSave;
  elements.reloadButton.textContent = newDraft
    ? "Discard draft"
    : "Reload saved version";
  elements.deleteButton.disabled = !editable || newDraft;
  elements.invokeButton.disabled = !editable || dirty || newDraft;
  elements.addStepButton.disabled =
    !editable || stepCount >= catalog.limits.maxInferenceSteps;
  elements.loopSettingsButton.disabled = mutationInFlight || !draft;
  elements.selectedNodeButton.disabled = mutationInFlight || !draft;
  elements.createLoopButton.disabled =
    mutationInFlight ||
    uncertainFirstSave ||
    !catalog ||
    catalog.customDefinitions.length >=
      catalog.limits.maxDefinitionsPerWorkspace;
  elements.loopSearch.disabled = mutationInFlight || !catalog;
  elements.zoomFitButton.disabled = !draft;
  elements.zoomInButton.disabled = !draft || canvasZoom >= 1.3;
  elements.zoomOutButton.disabled = !draft || canvasZoom <= 0.7;
  elements.saveState.textContent = historicalLoopId
    ? "Archived evidence"
    : !draft
      ? "No loop selected"
      : isSystemLoop()
        ? "System managed"
        : newDraft
          ? newLoopDraftCommitState === "uncertain"
            ? "First save uncertain · retry required"
            : newLoopDraftCommitState === "conflict"
              ? "First save conflict"
              : newLoopDraftCommitState === "failed"
                ? "First save failed"
                : "Unsaved draft · this tab only"
          : dirty
            ? "Unsaved changes"
            : hasValidationErrors
              ? `Saved · v${draft.definitionVersion} · needs attention`
              : `Saved · v${draft.definitionVersion}`;
  elements.canvasStepCount.textContent = isSystemLoop()
    ? `${systemNodeCount} system nodes · ${systemEdgeCount} edges`
    : `${stepCount} inference step${stepCount === 1 ? "" : "s"}`;
  elements.loopHeaderMeta.textContent = !draft
    ? "No loop selected"
    : isSystemLoop()
      ? `${draft.roleId} · Schema v${draft.schemaVersion} · ${systemNodeCount} nodes · ${systemEdgeCount} edges`
      : newDraft
        ? `${draft.roleId} · Unsaved client draft · ${stepCount} inference step${stepCount === 1 ? "" : "s"}`
        : `${draft.roleId} · Definition v${draft.definitionVersion} · ${stepCount} inference step${stepCount === 1 ? "" : "s"}`;
  elements.canvasAuthority.replaceChildren();
  if (draft) {
    if (isSystemLoop())
      elements.canvasAuthority.append(
        node("strong", "", `Authority: ${draft.roleId}`),
        document.createTextNode(
          ` · ${capitalize(splitWords(draft.trigger))} trigger · ${capitalize(splitWords(draft.memoryScope))} · ${draft.capabilityIds.join(", ")}`,
        ),
      );
    else
      elements.canvasAuthority.append(
        node("strong", "", `Authority: ${draft.roleId}`),
        document.createTextNode(
          ` · ${draft.toolAssignments.length ? draft.toolAssignments.join(", ") : "no model-facing tools assigned"} · all inference steps inherit this scope`,
        ),
      );
  }
}

function renderValidation() {
  const errors = validateDraft();
  elements.validationBanner.replaceChildren();
  elements.validationBanner.removeAttribute("aria-label");
  if (!draft) {
    elements.validationBanner.className = "validation-banner";
    return;
  }
  if (
    isNewLoopDraft() &&
    ["uncertain", "conflict", "failed"].includes(newLoopDraftCommitState)
  ) {
    const stateLabel =
      newLoopDraftCommitState === "uncertain"
        ? "First save outcome is uncertain"
        : newLoopDraftCommitState === "conflict"
          ? "First save operation conflicted"
          : "First save failed";
    const detail =
      newLoopDraftFailureDetail ??
      "The draft remains local and has not been treated as a runnable definition.";
    elements.validationBanner.textContent = `${stateLabel}. ${detail}`;
    elements.validationBanner.setAttribute(
      "aria-label",
      `${stateLabel}: ${detail}`,
    );
    elements.validationBanner.className = "validation-banner visible error";
    return;
  }
  if (errors.length === 0) {
    const copy = node("span", "validation-copy");
    const title = isSystemLoop()
      ? "System definition is valid and read-only"
      : isNewLoopDraft()
        ? "Unsaved draft is valid and ready for first save"
        : dirty
          ? "Draft is valid and ready to save"
          : `Definition v${draft.definitionVersion} is valid and runnable`;
    const detail = isSystemLoop()
      ? draft.executionContract.detail
      : isNewLoopDraft()
        ? "Save deliberately creates the first durable definition. This tab keeps the draft across navigation, reload, and reconnect; closing the tab or choosing Discard draft removes it."
        : dirty
          ? "Save this definition before starting a run."
          : "The server will validate again before saving or admitting a run.";
    copy.append(node("strong", "", title));
    copy.append(node("span", "", detail));
    elements.validationBanner.append(
      node("span", "validation-icon", "✓"),
      copy,
    );
    elements.validationBanner.className = "validation-banner visible success";
    return;
  }
  elements.validationBanner.textContent = errors[0];
  elements.validationBanner.setAttribute(
    "aria-label",
    `Definition needs attention: ${errors[0]}`,
  );
  elements.validationBanner.className = "validation-banner visible error";
}

function validateDraft() {
  if (!draft) return [];
  if (isSystemLoop()) {
    if (draft.executionContract?.graphSemantics !== "unknown") return [];
    return [
      draft.executionContract?.detail?.trim() ||
        "The dedicated runner did not validate this system definition.",
    ];
  }
  const errors = [];
  if (!draft.displayName.trim()) errors.push("Loop name is required.");
  if (
    draft.inferenceSteps.length < catalog.limits.minInferenceSteps ||
    draft.inferenceSteps.length > catalog.limits.maxInferenceSteps
  )
    errors.push(
      `Use ${catalog.limits.minInferenceSteps}–${catalog.limits.maxInferenceSteps} inference steps.`,
    );
  if (
    draft.inferenceSteps.some(
      (step) => !step.name.trim() || !step.instruction.trim(),
    )
  )
    errors.push("Every inference step needs a name and instruction.");
  if (
    draft.triggerPolicy.promptSource === "preset" &&
    !draft.triggerPolicy.presetPrompt.trim()
  )
    errors.push("Preset trigger prompt is required.");
  if (
    draft.triggerPolicy.promptSource !== "preset" &&
    draft.triggerPolicy.presetPrompt
  )
    errors.push("Unused preset prompt must be empty.");
  if (
    draft.exitPolicy.maxAdditionalIterations > 0 &&
    !draft.exitPolicy.decisionInstruction.trim()
  )
    errors.push(
      "Exit decision instruction is required when continuation is enabled.",
    );
  return errors;
}

function runnerContractLabel(executionSemantics) {
  return executionSemantics === "validated-runner-contract"
    ? "Validated runner contract"
    : executionSemantics === "authority-topology-only"
      ? "Authority topology only"
      : "Runner contract not validated";
}

function markDirty() {
  if (!canMutateDraft()) return;
  dirty = true;
  if (isNewLoopDraft()) {
    if (pendingCreateRequest) {
      newLoopDraftOperationId = newOperationId();
      pendingCreateRequest = null;
    }
    newLoopDraftCommitState = "editing";
    newLoopDraftFailureDetail = null;
    tryPersistNewLoopDraft();
  }
  renderList();
  renderToolbar();
  renderValidation();
}

function updateDraftValue(fieldName, value) {
  if (!canMutateDraft()) return;
  draft[fieldName] = value;
  markDirty();
}

async function createLoop() {
  if (mutationInFlight) return;
  if (isNewLoopDraft() && newLoopDraftCommitState === "uncertain") {
    showBanner(
      "Resolve the uncertain first save by retrying the same Save request before starting another draft.",
    );
    return;
  }
  if (
    dirty &&
    !window.confirm("Discard unsaved loop edits and create a new loop?")
  )
    return;
  if (isNewLoopDraft()) resetNewLoopDraftState(true);
  try {
    startNewLoopDraft();
    showToast("Draft started. Nothing has been saved yet.");
  } catch (error) {
    showBanner(`Draft unavailable: ${error.message}`);
  }
}

function definitionInputFromDraft() {
  return {
    displayName: draft.displayName,
    description: draft.description,
    triggerPolicy: clone(draft.triggerPolicy),
    inferenceSteps: draft.inferenceSteps.map((step) => ({
      id: step.id?.startsWith("local-") ? null : step.id,
      name: step.name,
      instruction: step.instruction,
      contextPolicy: clone(step.contextPolicy),
    })),
    toolAssignments: [...draft.toolAssignments],
    exitPolicy: clone(draft.exitPolicy),
  };
}

async function saveNewLoopDraft(definition) {
  const requestKey = JSON.stringify(definition);
  if (pendingCreateRequest?.key !== requestKey) {
    pendingCreateRequest = {
      key: requestKey,
      body: {
        operationId: newLoopDraftOperationId ?? newOperationId(),
        definition,
      },
    };
    newLoopDraftOperationId = pendingCreateRequest.body.operationId;
  }

  newLoopDraftCommitState = "saving";
  newLoopDraftFailureDetail = null;
  if (!tryPersistNewLoopDraft()) {
    renderToolbar();
    renderValidation();
    return;
  }

  let catalogRefreshFailure = null;
  setBusy(true, "Saving draft");
  try {
    const response = await requestJson("/api/loops", {
      method: "POST",
      body: JSON.stringify(pendingCreateRequest.body),
    });
    const committed = response.definition;
    if (
      response.isCommitted !== true ||
      !["Created", "Replayed", "CommittedWithAuditWarning"].includes(
        response.status,
      ) ||
      typeof committed?.id !== "string" ||
      committed.definitionVersion !== 1 ||
      committed.roleId !== catalog.roleId ||
      committed.lastMutationOperationId !==
        pendingCreateRequest.body.operationId
    ) {
      throw new Error(
        "The server returned an invalid first-save receipt, so the commit outcome remains uncertain.",
      );
    }
    resetNewLoopDraftState(true);
    catalog.customDefinitions = [
      ...catalog.customDefinitions.filter(
        (definition) => definition.id !== committed.id,
      ),
      committed,
    ];
    applyDefinition(committed);
    try {
      await loadCatalog(committed.id);
    } catch (error) {
      catalogRefreshFailure = error;
    }
    showToast(
      response.status === "CommittedWithAuditWarning"
        ? response.detail
        : "Loop saved for the first time.",
    );
  } catch (error) {
    const responseStatus =
      typeof error.payload?.status === "string" ? error.payload.status : null;
    if (error.status === 409 && responseStatus === "Conflict") {
      newLoopDraftCommitState = "conflict";
      newLoopDraftFailureDetail = `${error.message} A fresh operation identity is reserved for an explicit retry.`;
      pendingCreateRequest = null;
      newLoopDraftOperationId = newOperationId();
    } else if (error.status === 409 && responseStatus === "LimitExceeded") {
      newLoopDraftCommitState = "failed";
      newLoopDraftFailureDetail = `${error.message} A fresh operation identity is reserved so Save can retry after capacity is available.`;
      pendingCreateRequest = null;
      newLoopDraftOperationId = newOperationId();
    } else if (
      typeof error.status !== "number" ||
      (error.status >= 500 && responseStatus === null)
    ) {
      newLoopDraftCommitState = "uncertain";
      newLoopDraftFailureDetail = `${error.message} The server may have committed the definition. Retry Save to send the exact same request, or reload after reconnect to reconcile the catalog without another mutation.`;
    } else {
      newLoopDraftCommitState = "failed";
      newLoopDraftFailureDetail = error.message;
    }
    tryPersistNewLoopDraft();
  } finally {
    setBusy(false);
    if (catalogRefreshFailure)
      showBanner(
        `Loop saved, but the catalog could not be refreshed: ${catalogRefreshFailure.message}`,
      );
    else renderValidation();
  }
}

async function saveLoop() {
  if (mutationInFlight) return;
  const errors = validateDraft();
  if (errors.length > 0) {
    showBanner(errors[0]);
    return;
  }
  const definition = definitionInputFromDraft();
  if (isNewLoopDraft()) {
    await saveNewLoopDraft(definition);
    return;
  }
  setBusy(true, "Saving");
  try {
    const requestKey = JSON.stringify({
      loopId: draft.id,
      expectedDefinitionVersion: currentDefinition.definitionVersion,
      definition,
    });
    if (pendingUpdateRequest?.key !== requestKey) {
      pendingUpdateRequest = {
        key: requestKey,
        body: {
          expectedDefinitionVersion: currentDefinition.definitionVersion,
          operationId: newOperationId(),
          definition,
        },
      };
    }
    const response = await requestJson(
      `/api/loops/${encodeURIComponent(draft.id)}`,
      { method: "PUT", body: JSON.stringify(pendingUpdateRequest.body) },
    );
    await loadCatalog(response.definition.id);
    pendingUpdateRequest = null;
    showToast(
      response.status === "CommittedWithAuditWarning"
        ? response.detail
        : "Loop saved.",
    );
  } catch (error) {
    showResponseError(error);
  } finally {
    setBusy(false);
  }
}

async function deleteLoop() {
  if (mutationInFlight) return;
  if (
    !draft ||
    isNewLoopDraft() ||
    isSystemLoop() ||
    !window.confirm(
      `Delete “${draft.displayName}”? Historical run evidence will remain available.`,
    )
  )
    return;
  setBusy(true, "Deleting");
  try {
    const requestKey = JSON.stringify({
      loopId: draft.id,
      expectedDefinitionVersion: currentDefinition.definitionVersion,
    });
    if (pendingDeleteRequest?.key !== requestKey) {
      pendingDeleteRequest = {
        key: requestKey,
        body: {
          expectedDefinitionVersion: currentDefinition.definitionVersion,
          operationId: newOperationId(),
        },
      };
    }
    const response = await requestJson(
      `/api/loops/${encodeURIComponent(draft.id)}`,
      { method: "DELETE", body: JSON.stringify(pendingDeleteRequest.body) },
    );
    pendingDeleteRequest = null;
    currentDefinition = null;
    await loadCatalog("default-conversation");
    showToast(
      response.status === "CommittedWithAuditWarning"
        ? (response.detail ??
            "Loop deleted, but its outcome audit has an integrity warning. Historical run evidence was preserved.")
        : "Loop deleted. Historical run evidence was preserved.",
    );
  } catch (error) {
    showResponseError(error);
  } finally {
    setBusy(false);
  }
}

function openInvokeModal() {
  if (
    !draft ||
    isSystemLoop() ||
    isNewLoopDraft() ||
    dirty ||
    invocationInFlight
  )
    return;
  invokeReturnFocus = document.activeElement ?? elements.invokeButton;
  const trigger = draft.triggerPolicy;
  const promptRequired = trigger.promptSource === "invocation";
  const pendingRetry = findLatestPendingInvocationRequest(draft);
  elements.invocationPrompt.value = pendingRetry?.invocationPrompt ?? "";
  elements.invocationPrompt.maxLength =
    catalog.limits.maxTriggerPromptCharacters;
  elements.invocationPromptField.hidden = !promptRequired;
  elements.invokeSummary.textContent = `${draft.displayName} v${draft.definitionVersion} will run the saved definition with ${draft.inferenceSteps.length} ordered inference step${draft.inferenceSteps.length === 1 ? "" : "s"} using ${catalog.runtimeModel?.provider ?? "the configured provider"} · ${catalog.runtimeModel?.model || "provider default model"}. Trigger source: ${promptSourceLabel(trigger.promptSource)}. Invoking conversation: ${trigger.includeInvokingConversation ? "admitted as a bounded snapshot" : "excluded from model context"}.`;
  const destinations = [
    ...draft.inferenceSteps.map((step) => resolvedPolicy(step, "inference")),
    resolvedPolicy(draft.exitPolicy, "exit"),
  ].filter((policy) => policy.contextOut.publishToInvokingConversation).length;
  elements.invokeLimits.textContent = `Hard bounds: ${catalog.limits.maxModelAttemptsPerRun} model attempts per run, ${catalog.limits.maxGovernedToolRequestsPerAttempt} governed tool requests per attempt, and ${catalog.limits.maxGovernedToolRequestsPerRun} per run, within ${formatDuration(catalog.limits.maxRunExecutionMilliseconds)} of accumulated execution time. Each canonical model output is capped at ${catalog.limits.maxCanonicalModelOutputCharacters.toLocaleString()} characters. Conversation snapshots retain at most ${catalog.limits.maxInvokingConversationCharacters.toLocaleString()} characters across ${catalog.limits.maxInvokingConversationEntries} selected messages; older omissions are aggregated. Tool targets are capped at ${catalog.limits.maxGovernedToolTargetCharacters.toLocaleString()} characters, arguments at ${catalog.limits.maxGovernedToolArgumentCharacters.toLocaleString()}, and the exact formatted result returned to the model at ${catalog.limits.maxCanonicalToolResultCharacters.toLocaleString()} characters. Run evidence is capped at ${catalog.limits.maxTraceEventsPerRun} events, including ${catalog.limits.maxLifecycleControlEventsPerRun} lifecycle/control events, and ${formatBytes(catalog.limits.maxRunTraceUtf8Bytes)}. Assigned tools: ${draft.toolAssignments.length ? draft.toolAssignments.join(", ") : "none"}. ${destinations} node output destination${destinations === 1 ? "" : "s"} may publish to the bound invoking conversation.`;
  clearInvokeError();
  setInvokePreparationBusy(false);
  elements.appShell.inert = true;
  elements.invokeModal.classList.toggle("open", true);
  elements.invokeModal.setAttribute("aria-hidden", "false");
  window.setTimeout(
    () =>
      (promptRequired
        ? elements.invocationPrompt
        : elements.startRunButton
      ).focus?.(),
    0,
  );
}

function closeInvokeModal() {
  elements.invokeModal.classList.toggle("open", false);
  elements.invokeModal.setAttribute("aria-hidden", "true");
  elements.appShell.inert = false;
  invokeReturnFocus?.focus?.();
  invokeReturnFocus = null;
}

function cancelInvokeModal() {
  const attempt = activeInvocationAttempt;
  if (attempt && !attempt.dispatched) {
    attempt.cancelled = true;
    activeInvocationAttempt = null;
    invocationInFlight = false;
    setInvokePreparationBusy(false);
  }
  closeInvokeModal();
}

function setInvokePreparationBusy(busy) {
  elements.closeInvokeButton.disabled = false;
  elements.cancelInvokeButton.disabled = false;
  elements.invocationPrompt.disabled = busy;
  elements.startRunButton.disabled = busy;
  elements.startRunButton.textContent = busy ? "Preparing" : "Start run";
  elements.invokeModal.setAttribute("aria-busy", String(busy));
}

function clearInvokeError() {
  elements.invokeError.textContent = "";
  elements.invokeError.hidden = true;
}

function showInvokeError(message) {
  elements.invokeError.textContent = message;
  elements.invokeError.hidden = false;
  elements.invokeError.focus?.();
}

function trapInvokeModalFocus(event) {
  const controls = [
    elements.closeInvokeButton,
    ...(elements.invocationPromptField.hidden
      ? []
      : [elements.invocationPrompt]),
    elements.cancelInvokeButton,
    elements.startRunButton,
  ].filter((control) => !control.disabled && !control.hidden);
  if (controls.length === 0) return;
  const first = controls[0];
  const last = controls.at(-1);
  const currentIndex = controls.indexOf(document.activeElement);
  if (event.shiftKey && currentIndex <= 0) {
    event.preventDefault();
    last.focus();
  } else if (
    !event.shiftKey &&
    (currentIndex === -1 || currentIndex === controls.length - 1)
  ) {
    event.preventDefault();
    first.focus();
  }
}

async function startRun() {
  if (
    !draft ||
    dirty ||
    isSystemLoop() ||
    isNewLoopDraft() ||
    invocationInFlight
  )
    return;
  clearInvokeError();
  const invocationPrompt =
    draft.triggerPolicy.promptSource === "invocation"
      ? elements.invocationPrompt.value.normalize("NFC")
      : null;
  if (
    draft.triggerPolicy.promptSource === "invocation" &&
    !invocationPrompt.trim()
  ) {
    showInvokeError("This loop requires an initial user prompt.");
    return;
  }

  const invocationRequest = {
    loopId: draft.id,
    expectedDefinitionVersion: draft.definitionVersion,
    expectedDefinitionHash: draft.contentHash,
    invocationPrompt,
    runtimeProvider: catalog.runtimeModel?.provider,
    runtimeModel: catalog.runtimeModel?.model ?? null,
  };
  const attempt = { cancelled: false, dispatched: false };
  activeInvocationAttempt = attempt;
  invocationInFlight = true;
  setInvokePreparationBusy(true);
  let requestKey = null;
  let operationId = null;
  let reservationId = null;
  let reservationIdsBeforeDispatch = [];
  let dispatchAttempted = false;
  let wasPreviouslyDispatched = false;
  try {
    try {
      requestKey = await invocationRequestKey(invocationRequest);
    } catch (error) {
      if (attempt.cancelled) return;
      showInvokeError(`Run could not be prepared safely: ${error.message}`);
      return;
    }
    if (attempt.cancelled || activeInvocationAttempt !== attempt) return;

    const connection = await getHub();
    if (attempt.cancelled || activeInvocationAttempt !== attempt) return;
    let reservedInvocationRequest;
    try {
      reservedInvocationRequest = await reservePendingInvocationRequest(
        requestKey,
        invocationRequest,
      );
    } catch (error) {
      if (attempt.cancelled) return;
      showInvokeError(
        `Run was not sent because unresolved invocation identity could not be coordinated safely across browser tabs: ${error.message}`,
      );
      return;
    }
    if (attempt.cancelled || activeInvocationAttempt !== attempt) {
      if (reservedInvocationRequest)
        await releasePendingInvocationReservation(
          requestKey,
          reservedInvocationRequest.operationId,
          reservedInvocationRequest.reservationId,
        );
      return;
    }
    if (!reservedInvocationRequest) {
      showInvokeError(
        `Run was not started because ${maximumPendingInvocationRequests} invocation outcomes are still unresolved. Reconcile or definitively reject an existing operation before starting another.`,
      );
      return;
    }
    operationId = reservedInvocationRequest.operationId;
    reservationId = reservedInvocationRequest.reservationId;
    reservationIdsBeforeDispatch = reservedInvocationRequest.reservationIds;
    wasPreviouslyDispatched = reservedInvocationRequest.dispatchAttempted;
    try {
      await markPendingInvocationDispatched(
        requestKey,
        operationId,
        reservationId,
      );
    } catch (error) {
      await releasePendingInvocationReservation(
        requestKey,
        operationId,
        reservationId,
      );
      if (attempt.cancelled) return;
      showInvokeError(
        `Run was not sent because its dispatch state could not be persisted safely: ${error.message}`,
      );
      return;
    }
    if (attempt.cancelled || activeInvocationAttempt !== attempt) {
      await rollbackPendingInvocationDispatch(
        requestKey,
        operationId,
        reservationId,
        reservationIdsBeforeDispatch,
        wasPreviouslyDispatched,
      );
      return;
    }
    dispatchAttempted = true;
    attempt.dispatched = true;
    closeInvokeModal();
    const invocation = connection.invoke("InvokeLoop", {
      loopId: invocationRequest.loopId,
      expectedDefinitionVersion: invocationRequest.expectedDefinitionVersion,
      expectedDefinitionHash: invocationRequest.expectedDefinitionHash,
      operationId,
      invocationPrompt: invocationRequest.invocationPrompt,
    });
    currentView = "runs";
    selectedRunId = null;
    selectedRun = null;
    selectedTrace = null;
    renderAll();
    const response = await waitForRunOperation(invocation, {
      preferredAdmissionOperationId: operationId,
      preserveEmptySelection: true,
    });
    if (response?.admissionStatus === "AuditUnavailable" && response?.run) {
      const matchingRun = await selectExactInvocationRun(
        response.run,
        invocationRequest.loopId,
        operationId,
      );
      if (!matchingRun) {
        clearSelectedRunEvidence();
        showBanner(
          `The audit-unavailable response named ${response.run.id}, but its exact durable run evidence could not be verified. The start outcome remains unknown; retrying the exact request will reuse operation ${operationId}.`,
        );
        return;
      }
      await forgetPendingInvocationRequest(requestKey, operationId);
      renderAll();
      showBanner(
        response.detail ??
          "Run admission was parked because its invocation audit could not be completed. Inspect the durable run evidence before resuming.",
      );
      return;
    }
    if (
      ["OperationInProgress", "Conflict", "ReceiptUnavailable"].includes(
        response?.admissionStatus,
      ) ||
      (response?.admissionStatus === "WorkspaceHostUnavailable" &&
        wasPreviouslyDispatched)
    ) {
      await reconcileAndApplyInvocationOperation(
        invocationRequest,
        requestKey,
        operationId,
      );
      return;
    }
    if (response?.admissionStatus !== "Admitted" || !response?.run) {
      await forgetPendingInvocationRequest(requestKey, operationId);
      await loadRuns({ silent: true, preserveEmptySelection: true });
      renderAll();
      showBanner(
        `Run was not admitted: ${response?.detail ?? "The runtime rejected the invocation."}`,
      );
      return;
    }
    const matchingRun = await selectExactInvocationRun(
      response.run,
      invocationRequest.loopId,
      operationId,
    );
    if (!matchingRun) {
      clearSelectedRunEvidence();
      showBanner(
        `The admitted response named ${response.run.id}, but its exact durable run evidence could not be verified. The start outcome remains unknown; retrying the exact request will reuse operation ${operationId}.`,
      );
      return;
    }
    await forgetPendingInvocationRequest(requestKey, operationId);
    renderAll();
    showToast(
      response?.detail ??
        "Run finished. Durable evidence is available in Runs.",
    );
  } catch (error) {
    if (attempt.cancelled) return;
    if (!dispatchAttempted) {
      if (requestKey && operationId && reservationId)
        await releasePendingInvocationReservation(
          requestKey,
          operationId,
          reservationId,
        );
      showInvokeError(
        `Run could not be sent because the live connection was not established: ${error.message}`,
      );
      return;
    }
    if (error?.name === "SignalRPreDispatchError") {
      await rollbackPendingInvocationDispatch(
        requestKey,
        operationId,
        reservationId,
        reservationIdsBeforeDispatch,
        wasPreviouslyDispatched,
      );
      showBanner(
        `Run could not be sent because the live connection was not established: ${error.message}`,
      );
      return;
    }
    if (error?.message?.includes("unsupported_loop_persistence_schema")) {
      showBanner(
        `Run execution requires persistence cleanup: ${error.message} Retrying the exact request after cleanup will reuse operation ${operationId}.`,
      );
      return;
    }
    await reconcileAndApplyInvocationOperation(
      invocationRequest,
      requestKey,
      operationId,
    );
  } finally {
    if (activeInvocationAttempt === attempt) {
      activeInvocationAttempt = null;
      invocationInFlight = false;
      setInvokePreparationBusy(false);
    }
  }
}

async function invocationRequestKey(request) {
  if (!globalThis.crypto?.subtle || typeof TextEncoder !== "function")
    throw new Error("Secure request identity hashing is unavailable.");
  if (
    typeof request.runtimeProvider !== "string" ||
    !request.runtimeProvider ||
    request.runtimeProvider.length > 512 ||
    (request.runtimeModel !== null &&
      (typeof request.runtimeModel !== "string" ||
        !request.runtimeModel ||
        request.runtimeModel.length > 512))
  ) {
    throw new Error(
      "The configured provider and model identity is unavailable.",
    );
  }
  const canonicalRequest = JSON.stringify([
    request.loopId,
    request.expectedDefinitionVersion,
    request.expectedDefinitionHash,
    request.invocationPrompt,
    request.runtimeProvider,
    request.runtimeModel,
  ]);
  const digest = await globalThis.crypto.subtle.digest(
    "SHA-256",
    new TextEncoder().encode(canonicalRequest),
  );
  return [...new Uint8Array(digest)]
    .map((value) => value.toString(16).padStart(2, "0"))
    .join("");
}

function rememberPendingInvocationRequest(requestKey, request) {
  const next = new Map(pendingInvocationRequests);
  next.delete(requestKey);
  next.set(requestKey, normalizePendingInvocationRequest(request));
  commitPendingInvocationRequests(next);
}

async function reservePendingInvocationRequest(requestKey, request) {
  return withPendingInvocationRegistryLock(async () => {
    synchronizePendingInvocationRequestsFromStorage();
    let pending = pendingInvocationRequests.get(requestKey);
    if (
      !pending &&
      pendingInvocationRequests.size >= maximumPendingInvocationRequests
    ) {
      await reconcileStoredPendingInvocationRequests();
      pending = pendingInvocationRequests.get(requestKey);
    }
    if (
      !pending &&
      pendingInvocationRequests.size >= maximumPendingInvocationRequests
    )
      return null;
    if (pending?.reservationIds.length >= maximumPendingInvocationRequests) {
      throw new Error(
        `The unresolved invocation already has ${maximumPendingInvocationRequests} active browser reservations.`,
      );
    }
    const reservationId = newOperationId();
    const reserved = pending
      ? {
          ...pending,
          reservationIds: [
            ...new Set([...pending.reservationIds, reservationId]),
          ],
        }
      : {
          ...request,
          operationId: newOperationId(),
          reservationIds: [reservationId],
          dispatchAttempted: false,
        };
    rememberPendingInvocationRequest(requestKey, reserved);
    return { ...reserved, reservationId };
  });
}

async function forgetPendingInvocationRequest(requestKey, operationId) {
  if (!requestKey) return;
  try {
    await withPendingInvocationRegistryLock(async () => {
      synchronizePendingInvocationRequestsFromStorage();
      const pending = pendingInvocationRequests.get(requestKey);
      if (!pending || (operationId && pending.operationId !== operationId))
        return;
      const next = new Map(pendingInvocationRequests);
      next.delete(requestKey);
      tryCommitPendingInvocationRequests(next);
    });
  } catch {
    // Retaining an already resolved identity is safe when shared storage cannot be updated.
  }
}

async function releasePendingInvocationReservation(
  requestKey,
  operationId,
  reservationId,
) {
  try {
    await withPendingInvocationRegistryLock(async () => {
      synchronizePendingInvocationRequestsFromStorage();
      const pending = pendingInvocationRequests.get(requestKey);
      if (
        !pending ||
        pending.operationId !== operationId ||
        !pending.reservationIds.includes(reservationId)
      )
        return;
      const next = new Map(pendingInvocationRequests);
      const remainingReservationIds = pending.reservationIds.filter(
        (value) => value !== reservationId,
      );
      if (remainingReservationIds.length || pending.dispatchAttempted)
        next.set(requestKey, {
          ...pending,
          reservationIds: remainingReservationIds,
        });
      else next.delete(requestKey);
      tryCommitPendingInvocationRequests(next);
    });
  } catch {
    // Retaining the reservation is the fail-closed result when shared storage cannot be updated.
  }
}

async function markPendingInvocationDispatched(
  requestKey,
  operationId,
  reservationId,
) {
  await withPendingInvocationRegistryLock(async () => {
    synchronizePendingInvocationRequestsFromStorage();
    const pending = pendingInvocationRequests.get(requestKey);
    if (
      !pending ||
      pending.operationId !== operationId ||
      !pending.reservationIds.includes(reservationId)
    ) {
      throw new Error(
        "The reserved invocation identity is no longer available.",
      );
    }
    const next = new Map(pendingInvocationRequests);
    next.set(requestKey, {
      ...pending,
      dispatchAttempted: true,
      reservationIds: pending.reservationIds.filter(
        (value) => value !== reservationId,
      ),
    });
    commitPendingInvocationRequests(next);
  });
}

async function rollbackPendingInvocationDispatch(
  requestKey,
  operationId,
  reservationId,
  reservationIdsBeforeDispatch,
  wasPreviouslyDispatched,
) {
  try {
    await withPendingInvocationRegistryLock(async () => {
      synchronizePendingInvocationRequestsFromStorage();
      const pending = pendingInvocationRequests.get(requestKey);
      if (
        !pending ||
        pending.operationId !== operationId ||
        wasPreviouslyDispatched
      )
        return;
      const expectedReservationIds = reservationIdsBeforeDispatch.filter(
        (value) => value !== reservationId,
      );
      const dispatchStateIsUnchanged =
        pending.dispatchAttempted &&
        pending.reservationIds.length === expectedReservationIds.length &&
        expectedReservationIds.every((value) =>
          pending.reservationIds.includes(value),
        );
      if (!dispatchStateIsUnchanged) return;
      const next = new Map(pendingInvocationRequests);
      if (expectedReservationIds.length)
        next.set(requestKey, {
          ...pending,
          dispatchAttempted: false,
          reservationIds: expectedReservationIds,
        });
      else next.delete(requestKey);
      tryCommitPendingInvocationRequests(next);
    });
  } catch {
    // Retaining an uncertain dispatch is the fail-closed result when rollback cannot be proved or persisted.
  }
}

async function reconcileStoredPendingInvocationRequests() {
  const deadline =
    performance.now() +
    pendingInvocationRegistryReconciliationDeadlineMilliseconds;
  const completedRequestKeys = await Promise.all(
    [...pendingInvocationRequests.entries()].map(
      async ([requestKey, request]) => {
        try {
          const receipt = await requestJsonBeforeDeadline(
            `/api/loop-runs/invocations/${encodeURIComponent(request.operationId)}`,
            deadline,
          );
          if (
            receipt?.operationId !== request.operationId ||
            receipt.loopId !== request.loopId ||
            receipt.state !== "Complete"
          )
            return null;
          if (receipt.admissionStatus === "AuditUnavailable") {
            if (!receipt.runId)
              return receipt.outcome === "Rejected" ? requestKey : null;
            const evidence = await requestExactInvocationEvidence(
              receipt.runId,
              request.loopId,
              request.operationId,
              deadline,
            );
            return evidence ? requestKey : null;
          }
          if (["Rejected", "WorkspaceExecutionBusy"].includes(receipt.outcome))
            return requestKey;
          if (receipt.outcome === "Admitted" && receipt.runId) {
            const evidence = await requestExactInvocationEvidence(
              receipt.runId,
              request.loopId,
              request.operationId,
              deadline,
            );
            return evidence ? requestKey : null;
          }
          return null;
        } catch {
          return null;
        }
      },
    ),
  );
  const next = new Map(pendingInvocationRequests);
  for (const requestKey of completedRequestKeys)
    if (requestKey) next.delete(requestKey);
  tryCommitPendingInvocationRequests(next);
}

async function withPendingInvocationRegistryLock(callback) {
  const locks = globalThis.navigator?.locks;
  if (!locks?.request)
    throw new Error(
      "This browser does not provide the required cross-tab lock service.",
    );
  if (!pendingInvocationRegistryLockName)
    throw new Error("The workspace-scoped invocation registry is unavailable.");
  return locks.request(
    pendingInvocationRegistryLockName,
    { mode: "exclusive" },
    callback,
  );
}

function lifecycleRequestKey(kind, runId, expectedLifecycleVersion) {
  return JSON.stringify([kind, runId, expectedLifecycleVersion]);
}

async function reconcilePendingLifecycleRequest(
  request,
  receiptAbsenceIsDefinitive = false,
  deadline = performance.now() +
    pendingLifecycleReconciliationDeadlineMilliseconds,
) {
  let receipt;
  try {
    receipt = await requestLifecycleReceiptBeforeDeadline(
      `/api/loop-runs/controls/${encodeURIComponent(request.operationId)}`,
      deadline,
    );
  } catch (error) {
    if (error.status === 404 && receiptAbsenceIsDefinitive)
      return await tryForgetPendingLifecycleRequest(request);
    return false;
  }

  if (receipt?.operationId !== request.operationId) return false;
  const receiptMatchesRequest =
    String(receipt.kind ?? "").toLowerCase() === request.kind &&
    receipt.runId === request.runId &&
    receipt.expectedLifecycleVersion === request.expectedLifecycleVersion;
  if (!receiptMatchesRequest)
    return await tryForgetPendingLifecycleRequest(request);
  if (receipt.state !== "Complete" || receipt.completionDurablyProved !== true)
    return false;
  return await tryForgetPendingLifecycleRequest(request);
}

async function reconcilePendingLifecycleRequests(
  deadline = performance.now() +
    pendingLifecycleReconciliationDeadlineMilliseconds,
) {
  const requests = await withPendingLifecycleRegistryLock(async () => {
    synchronizePendingLifecycleRequestsFromStorage();
    return [...pendingLifecycleRequests.values()];
  });
  let nextIndex = 0;
  const workerCount = Math.min(
    maximumConcurrentLifecycleReceiptReads,
    requests.length,
  );
  await Promise.all(
    Array.from({ length: workerCount }, async () => {
      while (nextIndex < requests.length) {
        const request = requests[nextIndex++];
        await reconcilePendingLifecycleRequest(request, false, deadline);
      }
    }),
  );
}

async function requestLifecycleReceiptBeforeDeadline(url, deadline) {
  const remainingMilliseconds = deadline - performance.now();
  if (remainingMilliseconds <= 0)
    throw new Error("The lifecycle receipt reconciliation deadline elapsed.");
  const abortController = new AbortController();
  let timeoutHandle = null;
  try {
    const timeout = new Promise((_, reject) => {
      timeoutHandle = setTimeout(() => {
        abortController.abort();
        reject(
          new Error("The lifecycle receipt reconciliation deadline elapsed."),
        );
      }, remainingMilliseconds);
    });
    return await Promise.race([
      requestJson(url, { signal: abortController.signal }),
      timeout,
    ]);
  } finally {
    if (timeoutHandle !== null) clearTimeout(timeoutHandle);
  }
}

async function getOrCreatePendingLifecycleRequest(
  kind,
  runId,
  expectedLifecycleVersion,
) {
  await reconcilePendingLifecycleRequests();
  return withPendingLifecycleRegistryLock(async () => {
    synchronizePendingLifecycleRequestsFromStorage();
    const requestKey = lifecycleRequestKey(
      kind,
      runId,
      expectedLifecycleVersion,
    );
    const existing = pendingLifecycleRequests.get(requestKey);
    if (existing) return existing;
    if (pendingLifecycleRequests.size >= maximumPendingLifecycleRequests)
      throw new Error(
        `The workspace already has ${maximumPendingLifecycleRequests} unresolved lifecycle requests.`,
      );
    const pending = {
      kind,
      runId,
      expectedLifecycleVersion,
      operationId: newOperationId(),
    };
    const next = new Map(pendingLifecycleRequests);
    next.set(requestKey, pending);
    commitPendingLifecycleRequests(next);
    return pending;
  });
}

async function forgetPendingLifecycleRequest(request) {
  return withPendingLifecycleRegistryLock(async () => {
    synchronizePendingLifecycleRequestsFromStorage();
    const requestKey = lifecycleRequestKey(
      request.kind,
      request.runId,
      request.expectedLifecycleVersion,
    );
    const stored = pendingLifecycleRequests.get(requestKey);
    if (!stored || stored.operationId !== request.operationId) return;
    const next = new Map(pendingLifecycleRequests);
    next.delete(requestKey);
    commitPendingLifecycleRequests(next);
  });
}

async function tryForgetPendingLifecycleRequest(request) {
  try {
    await forgetPendingLifecycleRequest(request);
    return true;
  } catch {
    return false;
  }
}

async function withPendingLifecycleRegistryLock(callback) {
  const locks = globalThis.navigator?.locks;
  if (!locks?.request)
    throw new Error(
      "This browser does not provide the required cross-tab lock service.",
    );
  if (!pendingLifecycleRegistryLockName)
    throw new Error("The workspace-scoped lifecycle registry is unavailable.");
  return locks.request(
    pendingLifecycleRegistryLockName,
    { mode: "exclusive" },
    callback,
  );
}

async function configurePendingLifecycleRegistry(workspaceRoot, initialized) {
  if (typeof workspaceRoot !== "string" || !workspaceRoot)
    throw new Error("The workspace identity is unavailable.");
  const scope = encodeURIComponent(workspaceRoot.normalize("NFC"));
  pendingLifecycleStorageKey = `${pendingLifecycleStorageKeyPrefix}.${scope}`;
  pendingLifecycleRegistryLockName = `${pendingLifecycleRegistryLockNamePrefix}.${scope}`;
  synchronizePendingLifecycleRequestsFromStorage();
  if (
    initialized &&
    reconciledPendingLifecycleStorageKey !== pendingLifecycleStorageKey
  ) {
    await reconcilePendingLifecycleRequests();
    reconciledPendingLifecycleStorageKey = pendingLifecycleStorageKey;
  }
}

function synchronizePendingLifecycleRequestsFromStorage() {
  const stored = restorePendingLifecycleRequests();
  pendingLifecycleRequests.clear();
  for (const [requestKey, request] of stored)
    pendingLifecycleRequests.set(requestKey, request);
}

function restorePendingLifecycleRequests() {
  if (!pendingLifecycleStorageKey || !window.localStorage)
    throw new Error("Shared lifecycle storage is unavailable.");
  const stored = window.localStorage.getItem(pendingLifecycleStorageKey);
  if (!stored) return new Map();
  let payload;
  try {
    payload = JSON.parse(stored);
  } catch {
    throw new Error("The shared lifecycle registry is corrupt.");
  }
  if (payload?.schemaVersion !== 1 || !Array.isArray(payload.requests))
    throw new Error("The shared lifecycle registry schema is unsupported.");
  const requests = new Map();
  for (const request of payload.requests) {
    if (!isStoredPendingLifecycleRequest(request))
      throw new Error(
        "The shared lifecycle registry contains invalid entries.",
      );
    const requestKey = lifecycleRequestKey(
      request.kind,
      request.runId,
      request.expectedLifecycleVersion,
    );
    if (requests.has(requestKey))
      throw new Error(
        "The shared lifecycle registry contains duplicate entries.",
      );
    requests.set(requestKey, request);
  }
  return requests;
}

function isStoredPendingLifecycleRequest(request) {
  return (
    request &&
    ["pause", "cancel", "resume"].includes(request.kind) &&
    typeof request.runId === "string" &&
    request.runId.length > 0 &&
    request.runId.length <= 200 &&
    Number.isInteger(request.expectedLifecycleVersion) &&
    request.expectedLifecycleVersion > 0 &&
    typeof request.operationId === "string" &&
    /^[a-z0-9-]{8,128}$/.test(request.operationId)
  );
}

function persistPendingLifecycleRequests(requests) {
  if (!pendingLifecycleStorageKey || !window.localStorage)
    throw new Error("Shared lifecycle storage is unavailable.");
  if (!requests.size) {
    window.localStorage.removeItem(pendingLifecycleStorageKey);
    return;
  }
  window.localStorage.setItem(
    pendingLifecycleStorageKey,
    JSON.stringify({ schemaVersion: 1, requests: [...requests.values()] }),
  );
}

function commitPendingLifecycleRequests(next) {
  persistPendingLifecycleRequests(next);
  pendingLifecycleRequests.clear();
  for (const [requestKey, request] of next)
    pendingLifecycleRequests.set(requestKey, request);
}

function configureRetentionCleanupRegistry(workspaceRoot) {
  if (typeof workspaceRoot !== "string" || !workspaceRoot)
    throw new Error("The workspace identity is unavailable.");
  const scope = encodeURIComponent(workspaceRoot.normalize("NFC"));
  retentionCleanupStorageKey = `${retentionCleanupStorageKeyPrefix}.${scope}`;
  retentionCleanupRegistryLockName = `${retentionCleanupRegistryLockNamePrefix}.${scope}`;
  assertRetentionCleanupRegistryAvailable();
  synchronizeRetentionCleanupOperationIdsFromStorage();
}

function assertRetentionCleanupRegistryAvailable() {
  const locks = globalThis.navigator?.locks;
  if (!locks?.request || !retentionCleanupStorageKey || !window.localStorage)
    throw new Error("The workspace receipt-cleanup registry is unavailable.");
  const probeKey = `${retentionCleanupStorageKey}.availability-probe`;
  try {
    window.localStorage.setItem(probeKey, "available");
    if (window.localStorage.getItem(probeKey) !== "available")
      throw new Error("The workspace receipt-cleanup registry is unavailable.");
    window.localStorage.removeItem(probeKey);
  } catch {
    try {
      window.localStorage.removeItem(probeKey);
    } catch {
      // The registry remains disabled; never dispatch cleanup with an in-memory fallback.
    }
    throw new Error("The workspace receipt-cleanup registry is unavailable.");
  }
}

function synchronizeRetentionCleanupOperationIdsFromStorage() {
  const stored = restoreRetentionCleanupOperationIds();
  retentionCleanupOperationIds.clear();
  for (const [artifactClass, operationId] of stored)
    retentionCleanupOperationIds.set(artifactClass, operationId);
}

function restoreRetentionCleanupOperationIds() {
  if (!retentionCleanupStorageKey || !window.localStorage)
    throw new Error("Shared receipt-cleanup storage is unavailable.");
  const stored = window.localStorage.getItem(retentionCleanupStorageKey);
  if (!stored) return new Map();
  let payload;
  try {
    payload = JSON.parse(stored);
  } catch {
    throw new Error("The shared receipt-cleanup registry is corrupt.");
  }
  if (payload?.schemaVersion !== 1 || !Array.isArray(payload.operations))
    throw new Error(
      "The shared receipt-cleanup registry schema is unsupported.",
    );
  const operations = new Map();
  for (const operation of payload.operations) {
    if (!isStoredRetentionCleanupOperation(operation))
      throw new Error(
        "The shared receipt-cleanup registry contains invalid entries.",
      );
    if (operations.has(operation.artifactClass))
      throw new Error(
        "The shared receipt-cleanup registry contains duplicate entries.",
      );
    operations.set(operation.artifactClass, operation.operationId);
  }
  return operations;
}

function isStoredRetentionCleanupOperation(operation) {
  return (
    operation &&
    [
      "DefinitionMutationReceipt",
      "DefinitionTombstone",
      "LifecycleControlReceipt",
    ].includes(operation.artifactClass) &&
    typeof operation.operationId === "string" &&
    /^[a-z0-9-]{8,128}$/.test(operation.operationId)
  );
}

function persistRetentionCleanupOperationIds(operations) {
  if (!retentionCleanupStorageKey || !window.localStorage)
    throw new Error("Shared receipt-cleanup storage is unavailable.");
  if (!operations.size) {
    window.localStorage.removeItem(retentionCleanupStorageKey);
    return;
  }
  window.localStorage.setItem(
    retentionCleanupStorageKey,
    JSON.stringify({
      schemaVersion: 1,
      operations: [...operations].map(([artifactClass, operationId]) => ({
        artifactClass,
        operationId,
      })),
    }),
  );
}

function commitRetentionCleanupOperationIds(next) {
  persistRetentionCleanupOperationIds(next);
  retentionCleanupOperationIds.clear();
  for (const [artifactClass, operationId] of next)
    retentionCleanupOperationIds.set(artifactClass, operationId);
}

async function withRetentionCleanupRegistryLock(callback) {
  const locks = globalThis.navigator?.locks;
  if (!locks?.request || !retentionCleanupRegistryLockName)
    throw new Error("The workspace receipt-cleanup registry is unavailable.");
  return locks.request(
    retentionCleanupRegistryLockName,
    { mode: "exclusive" },
    callback,
  );
}

async function getOrCreateRetentionCleanupOperationId(artifactClass) {
  return withRetentionCleanupRegistryLock(async () => {
    synchronizeRetentionCleanupOperationIdsFromStorage();
    const existing = retentionCleanupOperationIds.get(artifactClass);
    if (existing) return existing;
    const operationId = newOperationId();
    const next = new Map(retentionCleanupOperationIds);
    next.set(artifactClass, operationId);
    commitRetentionCleanupOperationIds(next);
    return operationId;
  });
}

async function forgetRetentionCleanupOperationId(artifactClass, operationId) {
  if (!operationId) return;
  await withRetentionCleanupRegistryLock(async () => {
    synchronizeRetentionCleanupOperationIdsFromStorage();
    if (retentionCleanupOperationIds.get(artifactClass) !== operationId) return;
    const next = new Map(retentionCleanupOperationIds);
    next.delete(artifactClass);
    commitRetentionCleanupOperationIds(next);
  });
}

async function configurePendingInvocationRegistry(workspaceRoot) {
  if (typeof workspaceRoot !== "string" || !workspaceRoot)
    throw new Error("The workspace identity is unavailable.");
  const scope = encodeURIComponent(workspaceRoot.normalize("NFC"));
  pendingInvocationStorageKey = `${pendingInvocationStorageKeyPrefix}.${scope}`;
  pendingInvocationRegistryLockName = `${pendingInvocationRegistryLockNamePrefix}.${scope}`;
  synchronizePendingInvocationRequestsFromStorage();
}

function synchronizePendingInvocationRequestsFromStorage() {
  const stored = restorePendingInvocationRequests();
  const current = new Map(pendingInvocationRequests);
  pendingInvocationRequests.clear();
  for (const [requestKey, request] of stored) {
    const local = current.get(requestKey);
    const invocationPrompt =
      local?.operationId === request.operationId
        ? local.invocationPrompt
        : null;
    pendingInvocationRequests.set(requestKey, { ...request, invocationPrompt });
  }
}

function restorePendingInvocationRequests() {
  if (!pendingInvocationStorageKey || !window.localStorage)
    throw new Error("Shared invocation storage is unavailable.");
  const stored = window.localStorage.getItem(pendingInvocationStorageKey);
  if (!stored) return new Map();
  let payload;
  try {
    payload = JSON.parse(stored);
  } catch {
    throw new Error("The shared invocation registry is corrupt.");
  }
  if (payload?.schemaVersion !== 1 || !Array.isArray(payload.requests))
    throw new Error("The shared invocation registry schema is unsupported.");
  const requests = new Map();
  for (const entry of payload.requests) {
    if (
      !isStoredPendingInvocationRequest(entry) ||
      requests.has(entry.requestKey)
    )
      throw new Error(
        "The shared invocation registry contains invalid entries.",
      );
    requests.set(
      entry.requestKey,
      normalizePendingInvocationRequest({
        loopId: entry.loopId,
        expectedDefinitionVersion: entry.expectedDefinitionVersion,
        expectedDefinitionHash: entry.expectedDefinitionHash,
        invocationPrompt: null,
        runtimeProvider: entry.runtimeProvider,
        runtimeModel: entry.runtimeModel,
        operationId: entry.operationId,
        reservationIds: entry.reservationIds,
        dispatchAttempted: entry.dispatchAttempted,
      }),
    );
  }
  return requests;
}

function persistPendingInvocationRequests(requests) {
  if (!pendingInvocationStorageKey || !window.localStorage)
    throw new Error("Shared invocation storage is unavailable.");
  if (!requests.size) {
    window.localStorage.removeItem(pendingInvocationStorageKey);
    return;
  }
  const storedRequests = [...requests.entries()].map(
    ([requestKey, request]) => ({
      requestKey,
      loopId: request.loopId,
      expectedDefinitionVersion: request.expectedDefinitionVersion,
      expectedDefinitionHash: request.expectedDefinitionHash,
      runtimeProvider: request.runtimeProvider,
      runtimeModel: request.runtimeModel,
      operationId: request.operationId,
      reservationIds: request.reservationIds,
      dispatchAttempted: request.dispatchAttempted,
    }),
  );
  window.localStorage.setItem(
    pendingInvocationStorageKey,
    JSON.stringify({ schemaVersion: 1, requests: storedRequests }),
  );
}

function commitPendingInvocationRequests(next) {
  persistPendingInvocationRequests(next);
  pendingInvocationRequests.clear();
  for (const [requestKey, request] of next)
    pendingInvocationRequests.set(requestKey, request);
}

function tryCommitPendingInvocationRequests(next) {
  try {
    commitPendingInvocationRequests(next);
    return true;
  } catch {
    return false;
  }
}

function isStoredPendingInvocationRequest(entry) {
  return (
    entry &&
    /^[a-f0-9]{64}$/.test(entry.requestKey) &&
    typeof entry.loopId === "string" &&
    entry.loopId.length > 0 &&
    entry.loopId.length <= 200 &&
    Number.isInteger(entry.expectedDefinitionVersion) &&
    entry.expectedDefinitionVersion > 0 &&
    typeof entry.expectedDefinitionHash === "string" &&
    entry.expectedDefinitionHash.length > 0 &&
    entry.expectedDefinitionHash.length <= 256 &&
    typeof entry.runtimeProvider === "string" &&
    entry.runtimeProvider.length > 0 &&
    entry.runtimeProvider.length <= 512 &&
    (entry.runtimeModel === null ||
      (typeof entry.runtimeModel === "string" &&
        entry.runtimeModel.length > 0 &&
        entry.runtimeModel.length <= 512)) &&
    typeof entry.operationId === "string" &&
    /^[a-z0-9-]{8,128}$/.test(entry.operationId) &&
    typeof entry.dispatchAttempted === "boolean" &&
    Array.isArray(entry.reservationIds) &&
    entry.reservationIds.length <= 100 &&
    entry.reservationIds.every(
      (value) => typeof value === "string" && /^[a-z0-9-]{8,128}$/.test(value),
    ) &&
    new Set(entry.reservationIds).size === entry.reservationIds.length &&
    (entry.dispatchAttempted || entry.reservationIds.length > 0)
  );
}

function normalizePendingInvocationRequest(request) {
  const hasReservationState =
    Array.isArray(request.reservationIds) ||
    typeof request.dispatchAttempted === "boolean";
  const runtimeProvider = Object.hasOwn(request, "runtimeProvider")
    ? request.runtimeProvider
    : catalog?.runtimeModel?.provider;
  const runtimeModel = Object.hasOwn(request, "runtimeModel")
    ? request.runtimeModel
    : (catalog?.runtimeModel?.model ?? null);
  return {
    ...request,
    invocationPrompt: request.invocationPrompt ?? null,
    runtimeProvider,
    runtimeModel,
    reservationIds: request.reservationIds
      ? [...new Set(request.reservationIds)]
      : [],
    dispatchAttempted: hasReservationState
      ? request.dispatchAttempted === true
      : true,
  };
}

function findLatestPendingInvocationRequest(definition) {
  const requests = [...pendingInvocationRequests.values()];
  for (let index = requests.length - 1; index >= 0; index--) {
    const request = requests[index];
    if (
      request.loopId === definition.id &&
      request.expectedDefinitionVersion === definition.definitionVersion &&
      request.expectedDefinitionHash === definition.contentHash
    )
      return request;
  }
  return null;
}

async function reconcileAndApplyInvocationOperation(
  invocationRequest,
  requestKey,
  operationId,
) {
  const evidenceSelectionGeneration = runSelectionGeneration;
  const reconciliation = await reconcileInvocationOperation(
    operationId,
    invocationRequest.loopId,
  );
  await applyInvocationReconciliation(
    reconciliation,
    invocationRequest,
    requestKey,
    operationId,
    evidenceSelectionGeneration,
  );
}

async function applyInvocationReconciliation(
  reconciliation,
  invocationRequest,
  requestKey,
  operationId,
  evidenceSelectionGeneration = runSelectionGeneration,
) {
  if (
    reconciliation.kind === "admitted" ||
    reconciliation.kind === "audit-unavailable"
  ) {
    let evidence = null;
    try {
      evidence = await requestExactInvocationEvidence(
        reconciliation.receipt.runId,
        invocationRequest.loopId,
        operationId,
      );
    } catch {
      // Exact durable evidence is required before a receipt can release the operation identity.
    }
    const preserveNewerSelection =
      runSelectionGeneration !== evidenceSelectionGeneration;
    const matchingEvidence =
      evidence &&
      (await selectExactInvocationEvidence(
        evidence,
        invocationRequest.loopId,
        operationId,
        !preserveNewerSelection,
      ));
    if (!matchingEvidence) {
      if (!preserveNewerSelection) clearSelectedRunEvidence();
      showBanner(
        `The durable receipt names ${reconciliation.receipt.runId}, but matching run evidence (live or tombstone) for operation ${operationId} could not be verified. The start outcome remains unknown; retrying the exact request will reuse this operation.`,
      );
      return;
    }

    await forgetPendingInvocationRequest(requestKey, operationId);
    renderAll();
    if (reconciliation.kind === "audit-unavailable") {
      showBanner(
        reconciliation.receipt.detail ||
          "Run admission was parked because its invocation audit could not be completed. Inspect the durable run evidence before resuming.",
      );
    } else {
      showBanner(
        "The durable invocation receipt identified the exact admitted run; monitoring continues.",
      );
    }
    return;
  }

  if (reconciliation.kind === "rejected") {
    await forgetPendingInvocationRequest(requestKey, operationId);
    await loadRuns({ silent: true, preserveEmptySelection: true });
    renderAll();
    showBanner(
      `Run was not admitted: ${reconciliation.receipt.detail || reconciliation.receipt.admissionStatus || "The durable invocation receipt records a rejection."}`,
    );
  } else if (reconciliation.kind === "integrity-mismatch") {
    if (runSelectionGeneration === evidenceSelectionGeneration)
      clearSelectedRunEvidence();
    showBanner(
      `Durable invocation evidence did not match operation ${operationId} and loop ${invocationRequest.loopId}. The start outcome remains unknown; retrying the exact request will reuse this operation.`,
    );
  } else if (reconciliation.kind === "unavailable") {
    showBanner(
      `Durable invocation evidence is unavailable, so the start outcome is unknown. Retrying the exact request will reuse operation ${operationId}.`,
    );
  } else {
    showBanner(
      `No definitive invocation outcome appeared within ${invocationReconciliationDeadlineMilliseconds / 1000} seconds. The start outcome remains unknown; retrying the exact request will reuse operation ${operationId}.`,
    );
  }
}

async function selectExactInvocationRun(
  run,
  expectedLoopId,
  expectedOperationId,
) {
  if (
    !run ||
    run.loopId !== expectedLoopId ||
    run.admissionOperationId !== expectedOperationId
  )
    return false;
  recentRuns = mergeRunSummaries([liveRunSummary(run)], recentRuns);
  selectedRunId = run.id;
  selectedRun = run;
  selectedTrace = null;
  bindSelectedRunMonitor(run.id);
  renderAll();
  const hydrationSelectionGeneration = runSelectionGeneration;
  const loaded = await loadRuns({
    silent: true,
    preferredRunId: run.id,
    preserveEmptySelection: true,
  });
  if (
    runSelectionGeneration !== hydrationSelectionGeneration ||
    selectedRunId !== run.id
  )
    return true;
  if (
    !loaded ||
    selectedRun?.id !== run.id ||
    selectedRun.loopId !== expectedLoopId ||
    selectedRun.admissionOperationId !== expectedOperationId
  ) {
    recentRuns = mergeRunSummaries([liveRunSummary(run)], recentRuns);
    selectedRunId = run.id;
    selectedRun = run;
    selectedTrace = null;
    bindSelectedRunMonitor(run.id);
    renderAll();
    scheduleSelectedRunRefresh();
  }
  return true;
}

async function selectExactInvocationEvidence(
  evidence,
  expectedLoopId,
  expectedOperationId,
  selectEvidence = true,
) {
  if (evidence.run) {
    if (
      evidence.run.loopId !== expectedLoopId ||
      evidence.run.admissionOperationId !== expectedOperationId
    )
      return false;
    if (selectEvidence)
      return selectExactInvocationRun(
        evidence.run,
        expectedLoopId,
        expectedOperationId,
      );
    recentRuns = mergeRunSummaries([liveRunSummary(evidence.run)], recentRuns);
    return true;
  }
  if (
    !matchesExactInvocationTombstone(
      evidence.trace,
      evidence.runId,
      expectedLoopId,
      expectedOperationId,
    )
  )
    return false;
  recentRuns = mergeRunSummaries(
    [tombstoneRunSummary(evidence.trace)],
    recentRuns.filter((run) => run.id !== evidence.runId),
  );
  if (!selectEvidence) return true;
  selectedRunId = evidence.runId;
  selectedRun = null;
  selectedTrace = evidence.trace;
  bindSelectedRunMonitor(null);
  renderAll();
  return true;
}

async function requestExactInvocationEvidence(
  runId,
  expectedLoopId,
  expectedOperationId,
  deadline = null,
) {
  const request = (url) =>
    deadline === null
      ? requestJson(url)
      : requestJsonBeforeDeadline(url, deadline);
  try {
    const run = await request(`/api/loop-runs/${encodeURIComponent(runId)}`);
    return run?.id === runId &&
      run.loopId === expectedLoopId &&
      run.admissionOperationId === expectedOperationId
      ? { runId, run, trace: null }
      : null;
  } catch (error) {
    if (error.status !== 404) throw error;
  }

  const trace = await request(
    `/api/loop-runs/${encodeURIComponent(runId)}/trace`,
  );
  return matchesExactInvocationTombstone(
    trace,
    runId,
    expectedLoopId,
    expectedOperationId,
  )
    ? { runId, run: null, trace }
    : null;
}

function matchesExactInvocationTombstone(
  trace,
  runId,
  expectedLoopId,
  expectedOperationId,
) {
  return (
    trace?.isDeleted === true &&
    trace.runId === runId &&
    trace.loopId === expectedLoopId &&
    trace.tombstone?.runId === runId &&
    trace.tombstone.loopId === expectedLoopId &&
    trace.tombstone.admissionOperationId === expectedOperationId
  );
}

function clearSelectedRunEvidence() {
  selectedRunId = null;
  selectedRun = null;
  selectedTrace = null;
  bindSelectedRunMonitor(null);
  renderAll();
}

async function reconcileInvocationOperation(
  operationId,
  expectedLoopId = null,
  timeoutMilliseconds = invocationReconciliationDeadlineMilliseconds,
) {
  if (typeof expectedLoopId === "number") {
    timeoutMilliseconds = expectedLoopId;
    expectedLoopId = null;
  }
  const deadline = performance.now() + timeoutMilliseconds;
  for (
    let attempt = 0;
    attempt < invocationReconciliationMaximumAttempts;
    attempt++
  ) {
    try {
      const receipt = await requestJsonBeforeDeadline(
        `/api/loop-runs/invocations/${encodeURIComponent(operationId)}`,
        deadline,
      );
      if (
        receipt &&
        (receipt.operationId !== operationId ||
          (expectedLoopId && receipt.loopId !== expectedLoopId))
      )
        return { kind: "integrity-mismatch", receipt };
      if (
        receipt?.state === "Complete" &&
        receipt.outcome === "Admitted" &&
        receipt.runId
      )
        return { kind: "admitted", receipt };
      if (
        receipt?.state === "Complete" &&
        receipt.admissionStatus === "AuditUnavailable" &&
        receipt.runId
      )
        return { kind: "audit-unavailable", receipt };
      if (
        receipt?.state === "Complete" &&
        ["Rejected", "WorkspaceExecutionBusy"].includes(receipt.outcome)
      )
        return { kind: "rejected", receipt };
    } catch (error) {
      if (error.name === "InvocationReconciliationDeadlineExceeded")
        return { kind: "unknown" };
      if (error.status !== 404)
        return { kind: "unavailable", detail: error.message };
    }

    if (attempt + 1 < invocationReconciliationMaximumAttempts) {
      const remainingMilliseconds = deadline - performance.now();
      if (remainingMilliseconds <= 0) break;
      await waitForInvocationReconciliationRetry(
        Math.min(
          invocationReconciliationDelayMilliseconds,
          remainingMilliseconds,
        ),
      );
    }
  }

  return { kind: "unknown" };
}

async function waitForInvocationReconciliationRetry(milliseconds) {
  await new Promise((resolve) => setTimeout(resolve, milliseconds));
}

async function requestJsonBeforeDeadline(url, deadline) {
  const remainingMilliseconds = deadline - performance.now();
  if (remainingMilliseconds <= 0) throw invocationReconciliationDeadlineError();
  const abortController = new AbortController();
  let timeoutHandle = null;
  try {
    const timeout = new Promise((_, reject) => {
      timeoutHandle = setTimeout(() => {
        abortController.abort();
        reject(invocationReconciliationDeadlineError());
      }, remainingMilliseconds);
    });
    return await Promise.race([
      requestJson(url, { signal: abortController.signal }),
      timeout,
    ]);
  } finally {
    if (timeoutHandle !== null) clearTimeout(timeoutHandle);
  }
}

function invocationReconciliationDeadlineError() {
  const error = new Error("The invocation reconciliation deadline elapsed.");
  error.name = "InvocationReconciliationDeadlineExceeded";
  return error;
}

async function controlRun(action) {
  if (!selectedRun) return;
  const runId = selectedRun.id;
  const expectedLifecycleVersion = selectedRun.lifecycleVersion;
  let pending = null;
  try {
    pending = await getOrCreatePendingLifecycleRequest(
      action,
      runId,
      expectedLifecycleVersion,
    );
    const response = await requestJson(
      `/api/loop-runs/${encodeURIComponent(runId)}/${action}`,
      {
        method: "POST",
        body: JSON.stringify({
          expectedLifecycleVersion,
          operationId: pending.operationId,
        }),
      },
    );
    selectedRun = response.run ?? response;
    await loadRuns({ silent: true });
    const cleanupSucceeded = await reconcilePendingLifecycleRequest(
      pending,
      response?.operationId === pending.operationId,
    );
    showToast(response.detail ?? `${capitalize(action)} request recorded.`);
    if (!cleanupSucceeded)
      showBanner(
        `${capitalize(action)} returned, but its durable receipt is still pending or unreadable. Retrying the same operation is safe.`,
        "notice",
      );
  } catch (error) {
    const cleanupSucceeded =
      !pending ||
      (await reconcilePendingLifecycleRequest(
        pending,
        error.payload?.operationId === pending.operationId,
      ));
    const cleanupDetail = cleanupSucceeded
      ? ""
      : " The operation identity remains pending for safe replay.";
    showBanner(
      `${capitalize(action)} failed: ${error.message}${cleanupDetail}`,
    );
  }
}

async function resumeRun() {
  if (!selectedRun || selectedRun.status !== "Paused") return;
  const runId = selectedRun.id;
  const expectedLifecycleVersion = selectedRun.lifecycleVersion;
  let pending = null;
  try {
    pending = await getOrCreatePendingLifecycleRequest(
      "resume",
      runId,
      expectedLifecycleVersion,
    );
    const connection = await getHub();
    const invocation = connection.invoke("ResumeLoop", {
      runId,
      expectedLifecycleVersion,
      operationId: pending.operationId,
    });
    const response = await waitForRunOperation(invocation, {
      preferredRunId: runId,
    });
    const accepted = [
      "Resumed",
      "Completed",
      "Cancelled",
      "Paused",
      "NeedsReview",
      "AuditWarning",
    ].includes(response?.status);
    selectedRun = response?.run ?? selectedRun;
    if (!accepted) {
      await loadRuns({ silent: true, preferredRunId: runId });
      renderAll();
      const cleanupSucceeded = await reconcilePendingLifecycleRequest(
        pending,
        response?.operationId === pending.operationId,
      );
      const cleanupDetail = cleanupSucceeded
        ? ""
        : " The operation identity remains pending for safe replay.";
      showBanner(
        `Resume failed: ${response?.detail ?? "The runtime rejected the Resume operation."}${cleanupDetail}`,
      );
      return;
    }
    await loadRuns({ silent: true });
    const cleanupSucceeded = await reconcilePendingLifecycleRequest(
      pending,
      response?.operationId === pending.operationId,
    );
    showToast(response?.detail ?? "Resume completed.");
    if (!cleanupSucceeded)
      showBanner(
        "Resume returned, but its durable receipt is still pending or unreadable. Retrying the same operation is safe.",
        "notice",
      );
  } catch (error) {
    const cleanupSucceeded =
      !pending || (await reconcilePendingLifecycleRequest(pending));
    const cleanupDetail = cleanupSucceeded
      ? ""
      : " The operation identity remains pending for safe replay.";
    showBanner(`Resume failed: ${error.message}${cleanupDetail}`);
  }
}

async function waitForRunOperation(invocation, preferredSelection) {
  activeRunOperationMonitors++;
  let settled = false;
  invocation.then(
    () => {
      settled = true;
    },
    () => {
      settled = true;
    },
  );
  try {
    while (!settled) {
      const selectedMatches =
        selectedRun &&
        (preferredSelection.preferredRunId === selectedRun.id ||
          preferredSelection.preferredAdmissionOperationId ===
            selectedRun.admissionOperationId);
      const monitoredRunId = selectedMatches ? selectedRun.id : null;
      const monitored = monitoredRunId
        ? await refreshSelectedRunFromMonitor(monitoredRunId)
        : false;
      if (!monitored) {
        if (monitoredRunId && selectedRunMonitorFailureKind === "endpoint")
          await fallbackSelectedRunAfterMonitorFailure(monitoredRunId);
        else await loadRuns({ silent: true, ...preferredSelection });
      }
      if (!settled) await new Promise((resolve) => setTimeout(resolve, 500));
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
  if (
    !loopBuilderSurfaceActive ||
    !loopBuilderSessionAvailable ||
    selectedRunRefreshInFlight ||
    activeRunOperationMonitors > 0 ||
    currentView !== "runs" ||
    !selectedRun ||
    !isNonterminalRun(selectedRun)
  )
    return;
  const runId = selectedRun.id;
  selectedRunRefreshTimer = window.setTimeout(async () => {
    selectedRunRefreshTimer = null;
    if (
      !loopBuilderSurfaceActive ||
      !loopBuilderSessionAvailable ||
      currentView !== "runs" ||
      selectedRun?.id !== runId
    )
      return;
    selectedRunRefreshInFlight = true;
    try {
      const monitored = await refreshSelectedRunFromMonitor(runId);
      if (!monitored) await fallbackSelectedRunAfterMonitorFailure(runId);
    } finally {
      selectedRunRefreshInFlight = false;
      scheduleSelectedRunRefresh();
    }
  }, 1000);
}

function beginSessionRecovery() {
  suspendSession();
  void window.embodySenseSession?.recover();
}

function waitForLoopBuilderOperation(operation, signal) {
  if (!signal) return Promise.resolve(operation);
  if (signal.aborted)
    return Promise.reject(
      signal.reason ?? new Error("The browser session is unavailable."),
    );
  return new Promise((resolve, reject) => {
    let settled = false;
    const finish = (callback, value) => {
      if (settled) return;
      settled = true;
      signal.removeEventListener("abort", abort);
      callback(value);
    };
    const abort = () =>
      finish(
        reject,
        signal.reason ?? new Error("The browser session is unavailable."),
      );
    signal.addEventListener("abort", abort, { once: true });
    Promise.resolve(operation).then(
      (value) => finish(resolve, value),
      (error) => finish(reject, error),
    );
  });
}

function suspendSession() {
  loopBuilderSessionAvailable = false;
  if (workspaceInitializationInFlight) {
    workspaceInitializationGeneration++;
    workspaceInitializationInFlight = false;
    setWorkspaceInitializationOutcome(
      "disconnected",
      "The browser disconnected during initialization. No completion is assumed. Reconnect to load authoritative workspace state before retrying.",
    );
  } else {
    renderWorkspaceInitialization();
  }
  runEvidenceRequestGeneration++;
  if (selectedRunRefreshTimer != null) {
    window.clearTimeout(selectedRunRefreshTimer);
    selectedRunRefreshTimer = null;
  }
  if (!loopBuilderRefreshAbortController?.signal.aborted)
    loopBuilderRefreshAbortController?.abort(
      new Error("The browser session is being recovered."),
    );
  if (!loopBuilderSessionAbortController.signal.aborted)
    loopBuilderSessionAbortController.abort(
      new Error("The browser session is being recovered."),
    );
  setInteractive(false);
}

function resumeSession() {
  if (loopBuilderSessionAbortController.signal.aborted)
    loopBuilderSessionAbortController = new AbortController();
  loopBuilderSessionAvailable = true;
  if (workspaceInitializationPhase === "disconnected") {
    if (workspaceStatusSnapshot?.initialized && workspaceAuthoringHydrated)
      setWorkspaceInitializationOutcome(
        "succeeded",
        "Connection restored. The workspace is initialized and authoritative Loops state is loaded; no loop ran.",
      );
    else if (initializationState() === "partial")
      setWorkspaceInitializationOutcome(
        "partial",
        "Connection restored. Authoritative status shows an incomplete .agent scaffold. Retry initialization to repair it; no loop ran.",
      );
    else
      setWorkspaceInitializationOutcome(
        "idle",
        "Connection restored. Authoritative status shows that this workspace is not initialized. Review the effects and retry when ready.",
      );
  } else {
    renderWorkspaceInitialization();
  }
  if (loopBuilderSurfaceActive && !loopBuilderEventsBound) void activate();
  else scheduleSelectedRunRefresh();
}

function isNonterminalRun(run) {
  return [
    "Admitted",
    "Running",
    "PauseRequested",
    "Paused",
    "CancelRequested",
  ].includes(run?.status);
}

async function deleteSelectedTrace() {
  if (
    !selectedRun ||
    !selectedTrace ||
    selectedTrace.isDeleted ||
    !["Completed", "Failed", "Cancelled", "NeedsReview"].includes(
      selectedRun.status,
    )
  )
    return;
  const confirmed = window.confirm(
    `Permanently delete the sensitive trace content for ${selectedRun.id}?\n\nPrompts, captured context, outputs, and tool evidence will be removed. A small audited tombstone will remain. This cannot be undone.`,
  );
  if (!confirmed) return;

  const runId = selectedRun.id;
  const loopId = selectedRun.loopId;
  const expectedTraceHash = selectedTrace.persistedArtifactHash;
  if (
    pendingTraceDeletion?.runId !== runId ||
    pendingTraceDeletion.expectedTraceHash !== expectedTraceHash
  ) {
    pendingTraceDeletion = {
      runId,
      expectedTraceHash,
      operationId: newOperationId(),
    };
  }
  let deletion;
  try {
    deletion = await requestJson(
      `/api/loop-runs/${encodeURIComponent(runId)}/trace/delete`,
      {
        method: "POST",
        body: JSON.stringify({
          expectedTraceHash,
          operationId: pendingTraceDeletion.operationId,
        }),
      },
    );
  } catch (error) {
    if (
      error.payload?.status === "AuditUnavailable" &&
      error.payload?.isOutcomeCommitted === true
    )
      pendingTraceDeletion = null;
    showBanner(`Trace deletion failed: ${error.message}`);
    return;
  }

  pendingTraceDeletion = null;
  const tombstone = deletion.tombstone;
  const tombstoneSummary = tombstoneRunSummary({ tombstone });
  recentRuns = mergeRunSummaries(
    [tombstoneSummary],
    recentRuns.filter((run) => run.id !== runId),
  );
  if (selectedLoopId() !== loopId || selectedRunId !== runId) {
    let quotaRefreshed = true;
    try {
      traceQuota = await requestJson("/api/loop-runs/quota");
    } catch {
      quotaRefreshed = false;
    }
    renderAll();
    const warning =
      deletion.status === "CommittedWithAuditWarning"
        ? " The deletion committed, but its outcome audit has an integrity warning."
        : "";
    showToast(
      `Sensitive trace content deleted; the audited tombstone remains.${warning}`,
    );
    if (!quotaRefreshed)
      showBanner(
        "Trace deletion committed, but refreshed quota evidence could not be loaded. Reload Runs to inspect the durable outcome.",
      );
    return;
  }

  selectedRunId = runId;
  selectedRun = null;
  selectedTrace = null;
  renderAll();
  let refreshed = true;
  try {
    [selectedTrace, traceQuota] = await Promise.all([
      requestJson(`/api/loop-runs/${encodeURIComponent(runId)}/trace`),
      requestJson("/api/loop-runs/quota"),
    ]);
    renderAll();
  } catch {
    refreshed = false;
  }
  const warning =
    deletion.status === "CommittedWithAuditWarning"
      ? " The deletion committed, but its outcome audit has an integrity warning."
      : "";
  showToast(
    `Sensitive trace content deleted; the audited tombstone remains.${warning}`,
  );
  if (!refreshed)
    showBanner(
      "Trace deletion committed, but refreshed tombstone and quota evidence could not be loaded. Reload Runs to inspect the durable outcome.",
    );
}

async function getHub() {
  if (window.embodySenseSession) {
    const sharedConnection = await window.embodySenseSession.getHub();
    if (hub !== sharedConnection) {
      hub = sharedConnection;
      sharedConnection.on("ApprovalsChanged", (approvals) => {
        if (hub === sharedConnection) renderLoopApprovals(approvals);
      });
    }
    return sharedConnection;
  }
  if (hub?.connected) return hub;
  const connection = new JsonSignalRConnection(createHubUrl());
  hub = connection;
  connection.on("ApprovalsChanged", (approvals) => {
    if (hub === connection) renderLoopApprovals(approvals);
  });
  connection.onclose = () => {
    if (hub !== connection) return;
    renderLoopApprovals([]);
    hub = null;
  };
  try {
    await connection.start();
  } catch (error) {
    connection.stop();
    if (hub === connection) hub = null;
    throw error;
  }
  if (hub !== connection) {
    connection.stop();
    throw new SignalRPreDispatchError(
      "SignalR connection setup was superseded by a newer invocation.",
    );
  }
  return connection;
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
  item.append(
    node(
      "strong",
      "",
      `${formatStatus(approval.command)} ${approval.operation}`,
    ),
  );
  item.append(
    node(
      "div",
      "evidence-code",
      [
        `target ${approval.targetPath}`,
        `resolved ${approval.resolvedPath}`,
        `matched permission ${approval.matchedPath}`,
        approval.reason,
      ]
        .filter(Boolean)
        .join("\n"),
    ),
  );
  const actions = node("div", "approval-actions");
  const reject = actionButton(
    "Reject",
    () => decideLoopApproval(approval.requestId, false, reject),
    false,
    "danger-button",
  );
  const approve = actionButton(
    "Approve",
    () => decideLoopApproval(approval.requestId, true, approve),
    false,
    "primary-button",
  );
  actions.append(reject, approve);
  item.append(actions);
  return item;
}

async function decideLoopApproval(requestId, approved, button) {
  button.disabled = true;
  try {
    const connection = await getHub();
    const result = await connection.invoke("DecideApproval", requestId, {
      approved,
    });
    if (!result?.accepted)
      showBanner(result?.message ?? "The approval decision was not accepted.");
  } catch (error) {
    showBanner(`Approval decision failed: ${error.message}`);
  } finally {
    button.disabled = false;
  }
}

function createHubUrl() {
  const url = new URL("/hubs/session", window.location.href);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  return url.toString();
}

function resolvedPolicy(owner, kind) {
  return owner.contextPolicy.mode === "custom"
    ? owner.contextPolicy.customPolicy
    : draft.contextDefaults[kind];
}

function promptSourceLabel(value) {
  return value === "invocation"
    ? "initial user prompt"
    : value === "preset"
      ? "saved preset prompt"
      : "no prompt";
}

async function reloadCurrent() {
  if (mutationInFlight) return;
  if (isNewLoopDraft()) {
    if (newLoopDraftCommitState === "uncertain") {
      showBanner(
        "The draft cannot be discarded while its first-save outcome is uncertain. Retry Save to resolve the exact operation first.",
      );
      return;
    }
    if (
      window.confirm(
        "Discard this unsaved draft? No durable loop will be deleted.",
      )
    )
      discardNewLoopDraft();
    return;
  }
  if (!currentDefinition) return;
  if (dirty && !window.confirm("Discard unsaved loop edits?")) return;
  const loopId = currentDefinition.id;
  setBusy(true, "Reloading");
  try {
    await loadCatalog(loopId);
    showToast("Latest loop definition loaded.");
  } catch (error) {
    showBanner(`Reload failed: ${error.message}`);
  } finally {
    setBusy(false);
  }
}

function addInferenceStep() {
  insertInferenceStep(draft?.inferenceSteps.length ?? 0);
}

function insertInferenceStep(index) {
  if (
    !canMutateDraft() ||
    draft.inferenceSteps.length >= catalog.limits.maxInferenceSteps
  )
    return;
  const id = `local-${newOperationId()}`;
  const boundedIndex = Math.max(
    0,
    Math.min(index, draft.inferenceSteps.length),
  );
  draft.inferenceSteps.splice(boundedIndex, 0, {
    id,
    name: `Step ${boundedIndex + 1}`,
    instruction: "",
    contextPolicy: { mode: "inherit", customPolicy: null },
  });
  lastSelectedNodeId = id;
  selectedNodeId = id;
  markDirty();
  renderCanvas();
  renderInspector();
  renderToolbar();
}

function setCanvasZoom(value) {
  canvasZoom = Math.max(0.7, Math.min(1.3, Math.round(value * 10) / 10));
  if (elements.canvas.style) elements.canvas.style.zoom = String(canvasZoom);
  elements.zoomLevel.textContent = `${Math.round(canvasZoom * 100)}%`;
  renderToolbar();
}

function applyCanvasZoom() {
  if (elements.canvas.style) elements.canvas.style.zoom = String(canvasZoom);
  elements.zoomLevel.textContent = `${Math.round(canvasZoom * 100)}%`;
}

function fitCanvas() {
  const viewportWidth = Number(elements.canvas.parentElement?.clientWidth);
  if (!Number.isFinite(viewportWidth) || viewportWidth <= 0) {
    setCanvasZoom(1);
    return;
  }
  setCanvasZoom(Math.min(1, (viewportWidth - 48) / 520));
}

function moveStep(index, delta) {
  if (!canMutateDraft()) return;
  const next = index + delta;
  if (next < 0 || next >= draft.inferenceSteps.length) return;
  const [step] = draft.inferenceSteps.splice(index, 1);
  draft.inferenceSteps.splice(next, 0, step);
  markDirty();
  renderCanvas();
  renderInspector();
}

function removeStep(index) {
  if (!canMutateDraft()) return;
  if (draft.inferenceSteps.length <= 1) return;
  draft.inferenceSteps.splice(index, 1);
  selectedNodeId =
    draft.inferenceSteps[Math.min(index, draft.inferenceSteps.length - 1)].id;
  lastSelectedNodeId = selectedNodeId;
  markDirty();
  renderCanvas();
  renderInspector();
}

function setBusy(busy, label) {
  mutationInFlight = busy;
  for (const region of [
    elements.list,
    elements.builderView,
    elements.runsView,
    elements.retentionView,
  ]) {
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
  for (const button of [
    elements.createLoopButton,
    elements.saveButton,
    elements.deleteButton,
    elements.reloadButton,
    elements.addStepButton,
    elements.invokeButton,
    elements.builderTab,
    elements.runsTab,
    elements.retentionTab,
    elements.refreshRetentionButton,
    elements.selectedNodeButton,
    elements.loopSettingsButton,
    elements.zoomOutButton,
    elements.zoomInButton,
    elements.zoomFitButton,
  ])
    button.disabled = !enabled;
  for (const item of elements.list.children) item.disabled = !enabled;
  elements.name.disabled = !enabled;
  elements.description.disabled = !enabled;
  elements.loopSearch.disabled = !enabled;
}

function showResponseError(error) {
  const validation = error.payload?.validationErrors;
  const conflict = error.payload?.conflict;
  if (Array.isArray(validation) && validation.length > 0) {
    showBanner(`${validation[0].field}: ${validation[0].message}`);
  } else if (error.status === 409 && conflict?.actualDefinitionVersion) {
    showBanner(
      `${error.message} Server version ${conflict.actualDefinitionVersion}. Reload before applying the edit again.`,
    );
  } else {
    showBanner(error.message);
  }
}

function showBanner(message, style) {
  elements.validationBanner.removeAttribute("aria-label");
  elements.validationBanner.textContent = message;
  elements.validationBanner.className = `validation-banner visible${style ? ` ${style}` : ""}`;
}

function appendActivationRetry() {
  const retry = actionButton(
    "Retry",
    async () => {
      retry.disabled = true;
      await activate();
    },
    false,
    "secondary-button validation-retry",
  );
  elements.validationBanner.append(retry);
}

function showToast(message) {
  elements.toast.textContent = message;
  elements.toast.hidden = false;
  window.clearTimeout(showToast.timer);
  showToast.timer = window.setTimeout(() => {
    elements.toast.hidden = true;
  }, 4200);
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
  input.type = "checkbox";
  input.checked = Boolean(checked);
  input.disabled = Boolean(disabled);
  input.addEventListener("change", (event) => handler(event.target.checked));
  const copy = node("span", "", labelText);
  if (hint) copy.append(node("small", "", hint));
  label.append(input, copy);
  return label;
}

function actionButton(
  label,
  handler,
  disabled,
  className = "secondary-button",
) {
  const button = node("button", className, label);
  button.type = "button";
  button.disabled = disabled;
  button.addEventListener("click", handler);
  return button;
}

function node(tagName, className, text) {
  const element = document.createElement(tagName);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
}

function clone(value) {
  return typeof structuredClone === "function"
    ? structuredClone(value)
    : JSON.parse(JSON.stringify(value));
}

function newOperationId() {
  if (globalThis.crypto?.randomUUID)
    return globalThis.crypto.randomUUID().toLowerCase();
  return `op-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
}

function capitalize(value) {
  return value ? value[0].toUpperCase() + value.slice(1) : value;
}

function isSystemLoop() {
  return draft?.id === "default-conversation";
}

function statusClass(value) {
  return String(value ?? "unknown")
    .replace(/[^a-z0-9]/gi, "")
    .toLowerCase();
}

function formatStatus(value) {
  return splitWords(value || "Unknown");
}

function splitWords(value) {
  return String(value ?? "")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[_-]+/g, " ");
}

function formatTimestamp(value) {
  if (!value) return "Unknown time";
  const timestamp = new Date(value);
  return Number.isNaN(timestamp.valueOf())
    ? String(value)
    : timestamp.toLocaleString([], {
        dateStyle: "medium",
        timeStyle: "medium",
      });
}

function formatBytes(value) {
  const bytes = Number(value);
  if (!Number.isFinite(bytes) || bytes < 0) return "Unknown size";
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KiB", "MiB", "GiB"];
  let size = bytes / 1024;
  let index = 0;
  while (size >= 1024 && index < units.length - 1) {
    size /= 1024;
    index++;
  }
  return `${size >= 10 ? size.toFixed(1) : size.toFixed(2)} ${units[index]}`;
}

function formatDuration(value) {
  const milliseconds = Number(value);
  if (!Number.isFinite(milliseconds) || milliseconds < 0)
    return "unknown duration";
  const totalSeconds = Math.ceil(milliseconds / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return [
    hours ? `${hours}h` : null,
    minutes ? `${minutes}m` : null,
    seconds || (!hours && !minutes) ? `${seconds}s` : null,
  ]
    .filter(Boolean)
    .join(" ");
}

class SignalRPreDispatchError extends Error {
  constructor(message) {
    super(message);
    this.name = "SignalRPreDispatchError";
  }
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
    this.socket.onmessage = (event) => this.receive(event.data);
    this.socket.onclose = () => this.handleClose();
    await new Promise((resolve, reject) => {
      this.socket.onopen = resolve;
      this.socket.onerror = () =>
        reject(new Error("SignalR connection failed."));
    });
    const handshake = new Promise((resolve, reject) => {
      this.handshakeResolve = resolve;
      this.handshakeReject = reject;
      window.setTimeout(
        () => this.handshakeReject?.(new Error("SignalR handshake timed out.")),
        5000,
      );
    });
    this.socket.onerror = () => this.handleClose();
    this.sendRaw({ protocol: "json", version: 1 });
    await handshake;
    this.connected = true;
    this.startKeepAlive();
  }

  async invoke(target, ...args) {
    if (
      !this.connected ||
      !this.socket ||
      this.socket.readyState !== WebSocket.OPEN
    )
      throw new SignalRPreDispatchError("SignalR connection is not available.");
    const invocationId = String(this.nextInvocationId++);
    const completion = new Promise((resolve, reject) =>
      this.invocations.set(invocationId, { resolve, reject }),
    );
    try {
      this.sendRaw({ type: 1, invocationId, target, arguments: args });
    } catch (error) {
      this.invocations.delete(invocationId);
      throw new SignalRPreDispatchError(
        error?.message ?? "SignalR invocation could not be sent.",
      );
    }
    return await completion;
  }

  sendRaw(message) {
    this.socket.send(`${JSON.stringify(message)}${signalRRecordSeparator}`);
  }

  startKeepAlive() {
    this.stopKeepAlive();
    this.keepAliveTimer = window.setInterval(() => {
      if (this.connected && this.socket?.readyState === WebSocket.OPEN)
        this.sendRaw({ type: 6 });
    }, signalRKeepAliveMilliseconds);
  }

  stopKeepAlive() {
    if (this.keepAliveTimer == null) return;
    window.clearInterval(this.keepAliveTimer);
    this.keepAliveTimer = null;
  }

  stop() {
    this.closedByClient = true;
    try {
      this.socket?.close();
    } catch {
      // A failed or still-connecting socket can reject close; local state must still be released.
    }
    this.handleClose();
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
    for (const invocation of this.invocations.values())
      invocation.reject(new Error("SignalR connection closed."));
    this.invocations.clear();
    this.socket = null;
    if (!this.closedByClient) this.onclose?.();
  }
}

window.embodySenseLoopBuilder = Object.freeze({
  activate,
  deactivate,
  rehydrateSession,
  refreshWorkspace,
  resumeSession,
  suspendSession,
});
if (!elements.loopsView.hidden) void activate();
