let sessionGenerationId = null;
let status = null;
let configuration = null;
let activeConfigTab = "overview";
let activeAppView = "chat";
let activeAgentMessage = null;
let hub = null;
let chatRequestScope = "";
let chatRequestStorageKey = "";
let chatRequestStorageLockName = "";
let chatRequestRetryEntry = null;
let chatRequestStorageReady = false;
let chatRequestStorageError = "";
let chatRequestDispatchBlocked = false;
let chatRequestInFlight = false;
let sessionRecoveryGeneration = 0;
let sessionRecoveryAttempts = 0;
let sessionRecoveryPromise = null;
let sessionRecoveryTimer = null;
let sessionRecoveryTerminal = false;
let sessionWorkspaceRoot = null;
let sessionRecoveryAbortController = null;
let sessionRecoveryCandidate = null;
let sessionPageHidden = false;
let conversationSynchronization = Promise.resolve();
const synchronizedConversationOperations = new Set();
const synchronizedConversationOperationOrder = [];
const conversationSynchronizationRetries = new Map();
const maxSynchronizedConversationOperations = 128;
const maxConversationSynchronizationRetries = 40;
const initialConversationSynchronizationRetryMilliseconds = 25;
const maxConversationSynchronizationRetryMilliseconds = 1000;
const chatRequestStorageKeyPrefix = "embodysense.chat-requests.v1";
const chatRequestRegistrySchemaVersion = 1;
const maxPendingChatRequests = 1;
const maxPendingChatMessageCharacters = 24000;
const maxSessionRecoveryAttempts = 6;
const initialSessionRecoveryDelayMilliseconds = 250;
const maxSessionRecoveryDelayMilliseconds = 8000;
const sessionRecoveryAttemptTimeoutMilliseconds = 10000;
const signalRStartTimeoutMilliseconds = 5000;

const elements = {
  approvals: document.getElementById("approvals"),
  approvalCount: document.getElementById("approvalCount"),
  cancelButton: document.getElementById("cancelButton"),
  chatApprovalAlert: document.getElementById("chatApprovalAlert"),
  chatApprovalsTitle: document.getElementById("chatApprovalsTitle"),
  cliRole: document.getElementById("cliRole"),
  clientRole: document.getElementById("clientRole"),
  clientStatus: document.getElementById("clientStatus"),
  connectionDot: document.getElementById("connectionDot"),
  configContent: document.getElementById("configContent"),
  configTabs: Array.from(document.querySelectorAll("[data-config-tab]")),
  configurationSubtitle: document.getElementById("configurationSubtitle"),
  configurationTitle: document.getElementById("configurationTitle"),
  configurationView: document.getElementById("configurationView"),
  appTabs: Array.from(document.querySelectorAll("[data-app-view]")),
  chatView: document.getElementById("chatView"),
  humanReviewView: document.getElementById("humanReviewView"),
  initButton: document.getElementById("initButton"),
  loopsView: document.getElementById("loopsView"),
  messageForm: document.getElementById("messageForm"),
  messageInput: document.getElementById("messageInput"),
  refreshConfigButton: document.getElementById("refreshConfigButton"),
  retryConnectionButton: document.getElementById("retryConnectionButton"),
  sendButton: document.getElementById("sendButton"),
  surfaceTitle: document.getElementById("surfaceTitle"),
  transcript: document.getElementById("transcript"),
  verboseToggle: document.getElementById("verboseToggle"),
  workspaceRoot: document.getElementById("workspaceRoot"),
  workspaceStatus: document.getElementById("workspaceStatus"),
};

const recordSeparator = "\u001e";

const configurationViewCopy = {
  overview: ["Overview", "Runtime posture, paths, and implemented concepts."],
  permissions: [
    "Permissions",
    "The current workspace permission policy and governed reach.",
  ],
  agent: [
    "Agent",
    "Role, identity, personality, context, memory, and model documents.",
  ],
  audit: ["Audit", "Recent attributable actions and governance outcomes."],
  history: [
    "History",
    "Current and archived logical conversation transcripts.",
  ],
};
const configurationTabNames = [
  "overview",
  "permissions",
  "agent",
  "audit",
  "history",
];

function isConfigurationTabName(value) {
  return configurationTabNames.includes(value);
}

function selectAppView(view, sourceTab = null) {
  const previousAppView = activeAppView;
  activeAppView = ["chat", "loops", "reviews", "configuration"].includes(view)
    ? view
    : "chat";
  if (previousAppView === "loops" && activeAppView !== "loops") {
    window.embodySenseLoopBuilder?.deactivate();
  }
  elements.chatView.hidden = activeAppView !== "chat";
  elements.loopsView.hidden = activeAppView !== "loops";
  elements.humanReviewView.hidden = activeAppView !== "reviews";
  elements.configurationView.hidden = activeAppView !== "configuration";
  if (activeAppView === "loops") {
    void window.embodySenseLoopBuilder?.activate();
  } else if (activeAppView === "reviews") {
    void window.embodySenseHumanReview?.activate();
  }

  let selectedTab = null;
  for (const tab of elements.appTabs) {
    const selected = sourceTab
      ? tab === sourceTab
      : activeAppView === "configuration"
        ? tab.dataset.configTab === activeConfigTab
        : tab.dataset.appView === activeAppView;
    if (selected) selectedTab = tab;
    tab.classList.toggle("active", selected);
    tab.setAttribute("aria-selected", selected ? "true" : "false");
    tab.tabIndex = selected ? 0 : -1;
  }

  if (activeAppView === "configuration") {
    const [title, subtitle] = isConfigurationTabName(activeConfigTab)
      ? configurationViewCopy[activeConfigTab]
      : configurationViewCopy.overview;
    elements.configurationTitle.textContent = title;
    elements.configurationSubtitle.textContent = subtitle;
    elements.surfaceTitle.textContent = title;
    if (selectedTab?.id)
      elements.configurationView.setAttribute(
        "aria-labelledby",
        selectedTab.id,
      );
    renderConfiguration();
  } else {
    elements.surfaceTitle.textContent =
      activeAppView === "loops"
        ? "Loops"
        : activeAppView === "reviews"
          ? "Reviews"
          : "Chat";
  }

  if (sourceTab && window.history?.replaceState) {
    const route =
      activeAppView === "configuration" ? activeConfigTab : activeAppView;
    window.history.replaceState(null, "", `/?view=${route}`);
  }
}

async function boot() {
  await connectHub();
}

async function fetchJson(url, options = {}) {
  try {
    return await fetchJsonWithoutRecovery(url, options);
  } catch (error) {
    if (error.status === 401 && url !== "/api/session") {
      void startSessionRecovery("stale-auth", { newGeneration: true });
      const sessionError = new Error(
        "The local session changed. Recovery started, and the prior request was not replayed.",
      );
      sessionError.status = 401;
      sessionError.cause = error;
      throw sessionError;
    }

    throw error;
  }
}

async function fetchJsonWithoutRecovery(url, options = {}) {
  const request = {
    ...options,
    credentials: "same-origin",
    headers: { ...(options.headers ?? {}) },
  };
  const response = await fetch(url, request);
  if (!response.ok) {
    const text = await response.text();
    const error = new Error(text || `Request failed (${response.status}).`);
    error.status = response.status;
    throw error;
  }

  return await response.json();
}

async function refreshConfiguration() {
  elements.refreshConfigButton.disabled = true;
  renderConfigLoading();
  try {
    configuration = await fetchJson("/api/configuration");
    renderConfiguration();
  } catch (error) {
    renderConfigError(error.message);
  } finally {
    elements.refreshConfigButton.disabled = !hub?.connected;
  }
}

function applyStatus(nextStatus) {
  const wasInitialized = Boolean(status?.initialized);
  status = nextStatus;
  elements.workspaceRoot.textContent = status.workspaceRoot;
  elements.workspaceStatus.textContent = status.initialized
    ? "Initialized"
    : "Needs initialization";
  elements.connectionDot.classList.toggle(
    "ready",
    status.initialized && Boolean(hub?.connected),
  );
  if (hub?.connected) applyConnectedState();
  elements.clientRole.textContent = status.client;
  elements.cliRole.textContent = status.cliRole;
  elements.initButton.disabled = status.initialized || !hub?.connected;
  refreshChatControls();
  elements.verboseToggle.disabled = !status.initialized || !hub?.connected;
  if (!wasInitialized && status.initialized) {
    void window.embodySenseLoopBuilder?.refreshWorkspace();
  }
}

async function connectHub() {
  return await startSessionRecovery("explicit-connect", {
    newGeneration: true,
  });
}

async function startSessionRecovery(
  reason,
  { newGeneration = false, manual = false } = {},
) {
  let supersededGeneration = false;
  if (sessionPageHidden) return false;
  if (sessionRecoveryPromise) {
    if (!manual) return await sessionRecoveryPromise;
    const supersededRecovery = sessionRecoveryPromise;
    sessionRecoveryGeneration++;
    sessionRecoveryAttempts = 0;
    supersededGeneration = true;
    cancelActiveSessionRecovery(
      "Manual recovery superseded the pending attempt.",
    );
    await supersededRecovery;
    if (sessionRecoveryPromise === supersededRecovery)
      sessionRecoveryPromise = null;
  }
  if (sessionRecoveryTerminal && !manual) return false;
  if (sessionRecoveryTimer != null && !manual) return false;
  if (sessionRecoveryTimer != null) {
    window.clearTimeout(sessionRecoveryTimer);
    sessionRecoveryTimer = null;
  }
  if (
    !supersededGeneration &&
    (newGeneration || manual || sessionRecoveryGeneration === 0)
  ) {
    sessionRecoveryGeneration++;
    sessionRecoveryAttempts = 0;
  }
  sessionRecoveryTerminal = false;
  const generation = sessionRecoveryGeneration;
  applyDisconnectedState(reason === "stale-auth" ? "renewing" : "retrying");
  const recovery = runSessionRecoveryAttempt(generation);
  sessionRecoveryPromise = recovery;
  try {
    return await recovery;
  } finally {
    if (sessionRecoveryPromise === recovery) sessionRecoveryPromise = null;
  }
}

async function runSessionRecoveryAttempt(generation) {
  sessionRecoveryAttempts++;
  let candidate = null;
  let candidateEvents = null;
  let candidateInstalled = false;
  const abortController = new AbortController();
  sessionRecoveryAbortController = abortController;
  const timeoutId = window.setTimeout(() => {
    abortController.abort(
      createRecoveryError(
        "transient",
        "Session recovery exceeded its bounded attempt deadline.",
      ),
    );
  }, sessionRecoveryAttemptTimeoutMilliseconds);
  try {
    const session = await waitForRecoveryOperation(
      fetchJsonWithoutRecovery("/api/session", {
        signal: abortController.signal,
      }),
      abortController.signal,
    );
    requireSessionGeneration(session);
    const nextChatRequestScope = requireChatRequestScope(session);
    if (generation !== sessionRecoveryGeneration) return false;

    candidate = new JsonSignalRConnection(createHubUrl());
    sessionRecoveryCandidate = candidate;
    candidateEvents = bindHubEvents(candidate, generation, (error) => {
      if (!abortController.signal.aborted) abortController.abort(error);
    });
    await waitForRecoveryOperation(
      candidate.start({ signal: abortController.signal }),
      abortController.signal,
    );
    const nextStatus = await waitForRecoveryOperation(
      fetchJsonWithoutRecovery("/api/status", {
        signal: abortController.signal,
      }),
      abortController.signal,
    );
    ensureWorkspaceDidNotChange(nextStatus.workspaceRoot);
    configureChatRequestStorageScope(nextChatRequestScope);
    try {
      await initializeChatRequestStorage();
      chatRequestStorageReady = true;
      chatRequestStorageError = "";
    } catch (error) {
      failChatRequestStorage(error);
    }
    if (hub === null && sessionWorkspaceRoot === null) {
      hub = candidate;
      sessionGenerationId = session.generationId;
      sessionWorkspaceRoot = nextStatus.workspaceRoot;
      applyStatus(nextStatus);
      candidateInstalled = true;
    }

    let transcriptHydrationError = null;
    const [nextConfiguration, currentTranscript, pendingApprovals] =
      await Promise.all([
        waitForRecoveryOperation(
          fetchJsonWithoutRecovery("/api/configuration", {
            signal: abortController.signal,
          }),
          abortController.signal,
        ),
        waitForRecoveryOperation(
          candidate.invoke("GetCurrentTranscript"),
          abortController.signal,
        ).catch((error) => {
          if (abortController.signal.aborted) throw error;
          transcriptHydrationError = error;
          return null;
        }),
        waitForRecoveryOperation(
          candidate.invoke("GetPendingApprovals"),
          abortController.signal,
        ),
      ]);
    if (generation !== sessionRecoveryGeneration) {
      candidate.stop();
      return false;
    }

    const loopRefresh = await waitForRecoveryOperation(
      window.embodySenseLoopBuilder?.rehydrateSession?.({
        approvals: pendingApprovals,
        signal: abortController.signal,
        workspaceRoot: nextStatus.workspaceRoot,
      }),
      abortController.signal,
    );
    if (loopRefresh?.requiresManualAction) {
      throw createRecoveryError(
        "workspace-changed",
        "The workspace changed while an unsaved loop draft was open.",
      );
    }
    if (loopRefresh && !loopRefresh.skipped && loopRefresh.refreshed !== true) {
      throw createRecoveryError(
        "transient",
        "Loop evidence could not be authoritatively rehydrated.",
      );
    }
    if (chatRequestStorageReady) {
      await reconcilePendingChatRequest(candidate);
    } else if (chatRequestStorageError) {
      appendMessage("error", chatRequestStorageError);
    }
    if (
      generation !== sessionRecoveryGeneration ||
      !candidate.connected ||
      candidateEvents.closed
    ) {
      throw createRecoveryError(
        "transient",
        "The replacement connection closed before recovery was promoted.",
      );
    }

    if (!candidateInstalled) {
      const previousHub = hub;
      previousHub?.stop();
      hub = candidate;
      sessionGenerationId = session.generationId;
      sessionWorkspaceRoot = nextStatus.workspaceRoot;
      applyStatus(nextStatus);
    }
    configuration = nextConfiguration;
    renderConfiguration();
    if (Array.isArray(currentTranscript)) replaceTranscript(currentTranscript);
    if (transcriptHydrationError)
      appendMessage(
        "error",
        `Transcript unavailable: ${transcriptHydrationError.message}`,
      );
    renderApprovals(pendingApprovals);
    void window.embodySenseHumanReview?.sessionRecovered?.();
    candidateEvents.promote();
    sessionRecoveryAttempts = 0;
    applyConnectedState();
    elements.refreshConfigButton.disabled = false;
    window.embodySenseLoopBuilder?.resumeSession?.();
    return true;
  } catch (caughtError) {
    const error = abortController.signal.aborted
      ? (abortController.signal.reason ?? caughtError)
      : caughtError;
    if (candidate) candidate.stop();
    if (hub === candidate) hub = null;
    if (generation !== sessionRecoveryGeneration) return false;
    if (error?.status === 401) {
      if (sessionRecoveryAttempts >= maxSessionRecoveryAttempts) {
        enterTerminalRecoveryState("terminal", error);
        return false;
      }
      const replacementGeneration = ++sessionRecoveryGeneration;
      scheduleSessionRecovery(replacementGeneration, error);
      return false;
    }
    const kind = classifyRecoveryError(error);
    if (
      kind !== "transient" ||
      sessionRecoveryAttempts >= maxSessionRecoveryAttempts
    ) {
      enterTerminalRecoveryState(kind, error);
      return false;
    }

    scheduleSessionRecovery(generation, error);
    return false;
  } finally {
    window.clearTimeout(timeoutId);
    if (sessionRecoveryAbortController === abortController)
      sessionRecoveryAbortController = null;
    if (sessionRecoveryCandidate === candidate) sessionRecoveryCandidate = null;
  }
}

function waitForRecoveryOperation(operation, signal) {
  if (operation === undefined) return Promise.resolve(undefined);
  if (signal.aborted) return Promise.reject(signal.reason);
  return new Promise((resolve, reject) => {
    let settled = false;
    const finish = (callback, value) => {
      if (settled) return;
      settled = true;
      signal.removeEventListener("abort", abort);
      callback(value);
    };
    const abort = () => finish(reject, signal.reason);
    signal.addEventListener("abort", abort, { once: true });
    Promise.resolve(operation).then(
      (value) => finish(resolve, value),
      (error) => finish(reject, error),
    );
  });
}

function cancelActiveSessionRecovery(message) {
  const error = createRecoveryError("transient", message);
  if (!sessionRecoveryAbortController?.signal.aborted)
    sessionRecoveryAbortController?.abort(error);
  sessionRecoveryCandidate?.stop();
}

function requireSessionGeneration(session) {
  if (
    !session ||
    typeof session.generationId !== "string" ||
    !session.generationId.trim()
  ) {
    throw createRecoveryError(
      "terminal",
      "The session endpoint returned an invalid process generation.",
    );
  }
}

function requireChatRequestScope(session) {
  if (
    !session ||
    typeof session.chatRequestScope !== "string" ||
    !/^[0-9a-f]{64}$/.test(session.chatRequestScope)
  ) {
    throw createRecoveryError(
      "terminal",
      "The session endpoint returned an invalid workspace chat-request scope.",
    );
  }

  return session.chatRequestScope;
}

function ensureWorkspaceDidNotChange(workspaceRoot) {
  if (
    sessionWorkspaceRoot &&
    workspaceRoot &&
    sessionWorkspaceRoot !== workspaceRoot
  ) {
    throw createRecoveryError(
      "workspace-changed",
      `The Web host now serves a different workspace (${workspaceRoot}).`,
    );
  }
}

function createRecoveryError(kind, message) {
  const error = new Error(message);
  error.recoveryKind = kind;
  return error;
}

function classifyRecoveryError(error) {
  if (error?.recoveryKind) return error.recoveryKind;
  if (
    Number.isInteger(error?.status) &&
    error.status >= 400 &&
    error.status < 500 &&
    ![408, 425, 429].includes(error.status)
  ) {
    return "terminal";
  }
  return "transient";
}

function bindHubEvents(connection, generation, closeCandidate) {
  const bufferedEvents = [];
  let promoted = false;
  let closed = false;
  const dispatch = (handler, ...args) => {
    if (generation !== sessionRecoveryGeneration) return;
    if (promoted && hub === connection) handler(...args);
    else if (!promoted && !closed) bufferedEvents.push({ args, handler });
  };
  connection.on("StatusChanged", (nextStatus) => {
    dispatch(applyStatus, nextStatus);
  });
  connection.on("ApprovalsChanged", (approvals) => {
    dispatch(renderApprovals, approvals);
  });
  connection.on("ConversationChanged", (notification) => {
    dispatch(queueConversationPublicationSynchronization, notification);
  });
  connection.on("HumanReviewChanged", (notification) => {
    dispatch(
      (value) => window.embodySenseHumanReview?.notifyChanged?.(value),
      notification,
    );
  });
  connection.on("StreamEvent", (event) => {
    dispatch(handleStreamEvent, event);
  });
  connection.onclose = () => {
    if (generation !== sessionRecoveryGeneration) return;
    closed = true;
    if (promoted && hub === connection) {
      hub = null;
      void startSessionRecovery("connection-lost", { newGeneration: true });
      return;
    }
    closeCandidate(
      createRecoveryError(
        "transient",
        "The replacement connection closed during recovery.",
      ),
    );
  };
  return {
    get closed() {
      return closed;
    },
    promote() {
      if (closed) return;
      promoted = true;
      for (const event of bufferedEvents.splice(0))
        event.handler(...event.args);
    },
  };
}

function scheduleSessionRecovery(generation, error, requestedDelay = null) {
  if (sessionRecoveryTimer != null) return;
  const delay = requestedDelay ?? sessionRecoveryDelay(sessionRecoveryAttempts);
  applyDisconnectedState("retrying", delay);
  sessionRecoveryTimer = window.setTimeout(() => {
    sessionRecoveryTimer = null;
    if (generation !== sessionRecoveryGeneration) return;
    void startSessionRecovery("retry", { newGeneration: false });
  }, delay);
  if (error?.message) elements.clientStatus.title = error.message;
}

function sessionRecoveryDelay(attempt) {
  const exponential = Math.min(
    initialSessionRecoveryDelayMilliseconds * 2 ** Math.max(0, attempt - 1),
    maxSessionRecoveryDelayMilliseconds,
  );
  return Math.max(1, Math.round(exponential * (0.75 + Math.random() * 0.25)));
}

function enterTerminalRecoveryState(kind, error) {
  sessionRecoveryTerminal = true;
  const posture = kind === "workspace-changed" ? kind : "terminal";
  applyDisconnectedState(posture);
  elements.retryConnectionButton.hidden = false;
  elements.clientStatus.title = error?.message ?? "Session recovery stopped.";
  appendMessage(
    "error",
    kind === "workspace-changed"
      ? `${error.message} This page was left unchanged to preserve local drafts. Restore the original workspace and retry, or reload intentionally.`
      : `Automatic session recovery stopped: ${error?.message ?? "unknown failure"} Retry when the local host is ready.`,
  );
}

function refreshChatControls() {
  elements.sendButton.disabled =
    !status?.initialized ||
    !hub?.connected ||
    !chatRequestStorageReady ||
    chatRequestDispatchBlocked ||
    chatRequestInFlight;
}

function queueConversationPublicationSynchronization(notification) {
  const retry = conversationSynchronizationRetries.get(
    notification?.operationId,
  );
  if (retry?.timeoutId != null) {
    return conversationSynchronization;
  }

  conversationSynchronization = conversationSynchronization.then(() =>
    synchronizeConversationPublication(notification),
  );
  return conversationSynchronization;
}

async function synchronizeConversationPublication(notification) {
  const operationId = notification?.operationId;
  if (!operationId || synchronizedConversationOperations.has(operationId)) {
    return;
  }

  rememberSynchronizedConversationOperation(operationId);

  try {
    const currentTranscript = await hub.invoke("GetCurrentTranscript");
    if (Array.isArray(currentTranscript)) {
      clearConversationSynchronizationRetry(operationId);
      replaceTranscript(currentTranscript);
    } else {
      forgetSynchronizedConversationOperation(operationId);
      scheduleConversationSynchronizationRetry(
        notification,
        "the retained runtime is temporarily unavailable",
      );
    }
  } catch (error) {
    forgetSynchronizedConversationOperation(operationId);
    scheduleConversationSynchronizationRetry(notification, error.message);
  }
}

function rememberSynchronizedConversationOperation(operationId) {
  synchronizedConversationOperations.add(operationId);
  synchronizedConversationOperationOrder.push(operationId);
  if (
    synchronizedConversationOperationOrder.length >
    maxSynchronizedConversationOperations
  ) {
    synchronizedConversationOperations.delete(
      synchronizedConversationOperationOrder.shift(),
    );
  }
}

function forgetSynchronizedConversationOperation(operationId) {
  synchronizedConversationOperations.delete(operationId);
  for (
    let index = synchronizedConversationOperationOrder.length - 1;
    index >= 0;
    index -= 1
  ) {
    if (synchronizedConversationOperationOrder[index] === operationId) {
      synchronizedConversationOperationOrder.splice(index, 1);
    }
  }
}

function scheduleConversationSynchronizationRetry(notification, detail) {
  const operationId = notification.operationId;
  const retry = conversationSynchronizationRetries.get(operationId) ?? {
    attempts: 0,
    timeoutId: null,
  };
  retry.attempts += 1;
  if (retry.attempts > maxConversationSynchronizationRetries) {
    conversationSynchronizationRetries.delete(operationId);
    appendMessage(
      "error",
      `Conversation synchronization unavailable: ${detail}`,
    );
    return;
  }

  const delay = Math.min(
    initialConversationSynchronizationRetryMilliseconds *
      2 ** (retry.attempts - 1),
    maxConversationSynchronizationRetryMilliseconds,
  );
  retry.timeoutId = window.setTimeout(() => {
    retry.timeoutId = null;
    queueConversationPublicationSynchronization(notification);
  }, delay);
  conversationSynchronizationRetries.set(operationId, retry);
}

function clearConversationSynchronizationRetry(operationId) {
  const retry = conversationSynchronizationRetries.get(operationId);
  if (retry?.timeoutId != null) {
    window.clearTimeout(retry.timeoutId);
  }

  conversationSynchronizationRetries.delete(operationId);
}

function createHubUrl() {
  const url = new URL("/hubs/session", window.location.href);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  return url.toString();
}

async function initializeChatRequestStorage() {
  validateChatRequestEnvironment();
  await withChatRequestRegistry((registry) => {
    if (registry.scope !== chatRequestScope) {
      throw chatRequestError(
        "Pending chat browser state belongs to a different workspace scope.",
        "storage-corrupt",
      );
    }

    return registry;
  });
}

async function reserveChatRequest(message) {
  if (message.length > maxPendingChatMessageCharacters) {
    throw chatRequestError(
      `Messages cannot exceed ${maxPendingChatMessageCharacters} characters because the exact pending request must fit in bounded browser state.`,
      "bounds",
    );
  }

  const retryEntry = getChatRequestRetryEntry();
  return await withChatRequestRegistry((registry) => {
    const existing = registry.entries[0];
    if (existing) {
      if (existing.message !== message) {
        throw chatRequestError(
          "A different chat request is still unresolved. Reconcile or retry that exact message before sending another.",
          "pending-conflict",
        );
      }

      return { registry, result: existing };
    }

    if (retryEntry) {
      throw chatRequestError(
        retryEntry.message === message
          ? "The retained chat identity was changed by another tab. Reconciliation must run again before retrying it."
          : "A retained chat identity was changed by another tab. Reconcile it before sending a different message.",
        retryEntry.message === message
          ? "reconciliation-required"
          : "pending-conflict",
      );
    }

    const requestId = `chat-${globalThis.crypto.randomUUID()}`;
    const entry = { requestId, message };
    return {
      registry: { ...registry, entries: [entry] },
      result: entry,
    };
  });
}

async function releaseChatRequest(entry) {
  await withChatRequestRegistry((registry) => {
    const current = registry.entries[0];
    if (
      current?.requestId !== entry.requestId ||
      current?.message !== entry.message
    ) {
      return registry;
    }

    return { ...registry, entries: [] };
  });
}

async function reconcilePendingChatRequest(connection = hub) {
  let entry;
  try {
    entry = await withChatRequestRegistry(
      (registry) => ({ registry, result: registry.entries[0] ?? null }),
      false,
    );
  } catch (error) {
    failChatRequestStorage(error);
    appendMessage("error", chatRequestStorageError);
    return;
  }

  entry ??= getChatRequestRetryEntry();
  if (!entry) {
    chatRequestDispatchBlocked = false;
    refreshChatControls();
    return;
  }

  retainChatRequestRetryEntry(entry);

  let reconciliation;
  try {
    reconciliation = await connection.invoke(
      "ReconcileMessage",
      entry.message,
      entry.requestId,
    );
  } catch (error) {
    appendMessage(
      "error",
      `Pending chat reconciliation unavailable: ${error.message}`,
    );
    elements.messageInput.value = entry.message;
    return;
  }

  const reconciliationStatus = normalizeChatRequestStatus(
    reconciliation?.status,
  );
  if (
    isReleasableChatRequestStatus(reconciliationStatus) &&
    reconciliation?.releaseRequestIdentity === true
  ) {
    try {
      await releaseChatRequest(entry);
      clearChatRequestRetryEntry(entry);
    } catch (error) {
      failChatRequestStorage(error);
      appendMessage("error", chatRequestStorageError);
      return;
    }

    chatRequestDispatchBlocked = false;
    refreshChatControls();

    try {
      await hydrateCurrentTranscript(connection);
    } catch (error) {
      appendMessage("error", `Transcript unavailable: ${error.message}`);
    }

    return;
  }

  if (
    reconciliationStatus === "needs-review" &&
    reconciliation?.releaseRequestIdentity === false
  ) {
    chatRequestDispatchBlocked = false;
    refreshChatControls();
    appendMessage(
      "system",
      "The prior provider outcome remains unknown and requires explicit review. Its browser request identity remains reserved; use `/review` and `/review resolve <turn-id>` to inspect and explicitly abandon it without redispatching provider work.",
    );
    return;
  }

  if (reconciliationStatus === "conflict") {
    chatRequestDispatchBlocked = true;
    refreshChatControls();
    appendMessage(
      "error",
      "The pending chat identity conflicts with durable turn evidence. Dispatch is blocked until the evidence is inspected.",
    );
    return;
  }

  if (!reconciliation?.retrySameRequest) {
    chatRequestDispatchBlocked = true;
    refreshChatControls();
    appendMessage(
      "error",
      "The pending chat request returned an unsupported reconciliation state. Dispatch remains blocked.",
    );
    return;
  }

  try {
    await restoreChatRequestRetryEntry(entry);
  } catch (error) {
    chatRequestDispatchBlocked = true;
    refreshChatControls();
    appendMessage(
      "error",
      `The retained chat identity could not be restored safely. ${error.message}`,
    );
    return;
  }

  elements.messageInput.value = entry.message;
  chatRequestDispatchBlocked = false;
  refreshChatControls();
  appendMessage(
    "system",
    reconciliationStatus === "pending"
      ? "A prior message is still being reconciled. Retrying it will reuse the same request identity and cannot automatically redispatch an outcome-unknown provider attempt."
      : "A prior message did not reach durable admission. Retrying it will reuse the same request identity.",
  );
}

function configureChatRequestStorageScope(scope = chatRequestScope) {
  if (!/^[0-9a-f]{64}$/.test(scope)) {
    throw chatRequestError(
      "Durable, cross-tab browser storage is unavailable.",
      "storage-unavailable",
    );
  }

  chatRequestScope = scope;
  chatRequestStorageKey = `${chatRequestStorageKeyPrefix}.${chatRequestScope}`;
  chatRequestStorageLockName = `${chatRequestStorageKeyPrefix}.${chatRequestScope}`;
  chatRequestRetryEntry = null;
}

function getChatRequestRetryEntry() {
  return chatRequestRetryEntry?.scope === chatRequestScope
    ? chatRequestRetryEntry.entry
    : null;
}

function retainChatRequestRetryEntry(entry) {
  chatRequestRetryEntry = {
    scope: chatRequestScope,
    entry: { requestId: entry.requestId, message: entry.message },
  };
}

function clearChatRequestRetryEntry(entry) {
  const retryEntry = getChatRequestRetryEntry();
  if (
    retryEntry?.requestId === entry.requestId &&
    retryEntry.message === entry.message
  ) {
    chatRequestRetryEntry = null;
  }
}

async function restoreChatRequestRetryEntry(entry) {
  await withChatRequestRegistry((registry) => {
    const current = registry.entries[0];
    if (!current) {
      return {
        ...registry,
        entries: [{ requestId: entry.requestId, message: entry.message }],
      };
    }

    if (
      current.requestId === entry.requestId &&
      current.message === entry.message
    ) {
      return registry;
    }

    throw chatRequestError(
      "A different chat identity is already retained in this workspace scope.",
      "pending-conflict",
    );
  });
}

async function hydrateCurrentTranscript(connection = hub) {
  const currentTranscript = await connection.invoke("GetCurrentTranscript");
  if (Array.isArray(currentTranscript)) {
    replaceTranscript(currentTranscript);
  }
}

async function withChatRequestRegistry(action, writeChanges = true) {
  validateChatRequestEnvironment();
  try {
    return await globalThis.navigator.locks.request(
      chatRequestStorageLockName,
      { mode: "exclusive" },
      async () => {
        const raw = globalThis.localStorage.getItem(chatRequestStorageKey);
        const registry =
          raw === null
            ? createEmptyChatRequestRegistry()
            : parseChatRequestRegistry(raw);
        const actionResult = await action(registry);
        const nextRegistry = actionResult?.registry ?? actionResult;
        const result = actionResult?.result;
        validateChatRequestRegistry(nextRegistry);

        if (writeChanges) {
          const serialized = JSON.stringify(nextRegistry);
          globalThis.localStorage.setItem(chatRequestStorageKey, serialized);
          if (
            globalThis.localStorage.getItem(chatRequestStorageKey) !==
            serialized
          ) {
            throw chatRequestError(
              "Browser storage did not preserve the pending chat registry exactly.",
              "storage-unavailable",
            );
          }
        }

        return Object.prototype.hasOwnProperty.call(
          actionResult ?? {},
          "result",
        )
          ? result
          : nextRegistry;
      },
    );
  } catch (error) {
    if (error.chatRequestCode) {
      throw error;
    }

    throw chatRequestError(
      `Durable browser coordination failed. ${error.message}`,
      "storage-unavailable",
    );
  }
}

function createEmptyChatRequestRegistry() {
  return {
    schemaVersion: chatRequestRegistrySchemaVersion,
    scope: chatRequestScope,
    entries: [],
  };
}

function parseChatRequestRegistry(raw) {
  let registry;
  try {
    registry = JSON.parse(raw);
  } catch {
    throw chatRequestError(
      "Pending chat browser state is corrupt.",
      "storage-corrupt",
    );
  }

  validateChatRequestRegistry(registry);
  return registry;
}

function validateChatRequestRegistry(registry) {
  if (
    !registry ||
    typeof registry !== "object" ||
    Array.isArray(registry) ||
    !hasExactKeys(registry, ["entries", "schemaVersion", "scope"]) ||
    registry.schemaVersion !== chatRequestRegistrySchemaVersion ||
    typeof registry.scope !== "string" ||
    !/^[0-9a-f]{64}$/.test(registry.scope) ||
    !Array.isArray(registry.entries) ||
    registry.entries.length > maxPendingChatRequests
  ) {
    throw chatRequestError(
      "Pending chat browser state is invalid or exceeds its bounded registry.",
      "storage-corrupt",
    );
  }

  for (const entry of registry.entries) {
    if (
      !entry ||
      typeof entry !== "object" ||
      Array.isArray(entry) ||
      !hasExactKeys(entry, ["message", "requestId"]) ||
      typeof entry.requestId !== "string" ||
      !/^chat-[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(
        entry.requestId,
      ) ||
      typeof entry.message !== "string" ||
      !entry.message ||
      entry.message !== entry.message.trim() ||
      entry.message.length > maxPendingChatMessageCharacters
    ) {
      throw chatRequestError(
        "Pending chat browser state contains an invalid request entry.",
        "storage-corrupt",
      );
    }
  }
}

function validateChatRequestEnvironment() {
  if (
    !/^[0-9a-f]{64}$/.test(chatRequestScope) ||
    !globalThis.localStorage?.getItem ||
    !globalThis.localStorage?.setItem ||
    !globalThis.navigator?.locks?.request ||
    !globalThis.crypto?.randomUUID
  ) {
    throw chatRequestError(
      "Durable, cross-tab browser storage is unavailable.",
      "storage-unavailable",
    );
  }
}

function hasExactKeys(value, expected) {
  const keys = Object.keys(value).sort();
  return (
    keys.length === expected.length &&
    keys.every((key, index) => key === expected[index])
  );
}

function chatRequestError(message, code) {
  const error = new Error(message);
  error.chatRequestCode = code;
  return error;
}

function failChatRequestStorage(error) {
  chatRequestStorageReady = false;
  chatRequestStorageError = `Chat dispatch disabled: ${error.message}`;
  refreshChatControls();
}

function normalizeChatRequestStatus(value) {
  return String(value ?? "")
    .trim()
    .toLowerCase();
}

function isTerminalChatRequestStatus(value) {
  return ["completed", "rejected", "needs-review"].includes(value);
}

function isReleasableChatRequestStatus(value) {
  return ["completed", "rejected"].includes(value);
}

function isDefaultConversationReviewCommand(message) {
  const normalized = message.toLowerCase();
  return normalized === "/review" || normalized.startsWith("/review resolve ");
}

async function sendDefaultConversationReviewCommand(message) {
  chatRequestInFlight = true;
  activeAgentMessage = null;
  appendMessage("user", message);
  elements.messageInput.value = "";
  refreshChatControls();

  try {
    const result = await hub.invoke("SendMessage", message, null);
    const resultStatus = normalizeChatRequestStatus(result?.status);
    if (
      !isReleasableChatRequestStatus(resultStatus) ||
      result?.releaseRequestIdentity !== true
    ) {
      throw new Error("The review command returned no conclusive disposition.");
    }

    await reconcilePendingChatRequest();
  } catch (error) {
    elements.messageInput.value = message;
    appendMessage("error", `Review command failed: ${error.message}`);
  } finally {
    chatRequestInFlight = false;
    refreshChatControls();
  }
}

function applyConnectedState() {
  elements.clientStatus.textContent = "Web primary";
  elements.clientStatus.title = sessionGenerationId
    ? `Connected to process generation ${sessionGenerationId}`
    : "Connected";
  elements.retryConnectionButton.hidden = true;
  elements.connectionDot.classList.toggle(
    "ready",
    Boolean(status?.initialized),
  );
}

function applyDisconnectedState(kind = "retrying", delay = null) {
  window.embodySenseLoopBuilder?.suspendSession?.();
  elements.clientStatus.textContent =
    kind === "workspace-changed"
      ? "Web workspace changed"
      : kind === "terminal"
        ? "Web recovery stopped"
        : kind === "renewing"
          ? "Web renewing session"
          : delay == null
            ? "Web reconnecting"
            : `Web retrying in ${Math.ceil(delay / 1000)}s`;
  elements.connectionDot.classList.toggle("ready", false);
  elements.initButton.disabled = true;
  elements.sendButton.disabled = true;
  elements.cancelButton.disabled = true;
  elements.verboseToggle.disabled = true;
  elements.refreshConfigButton.disabled = true;
}

function renderConfigLoading() {
  elements.configContent.replaceChildren(createState("Loading configuration"));
}

function renderConfigError(message) {
  elements.configContent.replaceChildren(
    createState(`Configuration unavailable: ${message}`, "error"),
  );
}

function renderConfiguration() {
  if (!configuration) {
    renderConfigLoading();
    return;
  }

  for (const tab of elements.configTabs) {
    const selected =
      activeAppView === "configuration" &&
      tab.dataset.configTab === activeConfigTab;
    tab.classList.toggle("active", selected);
    tab.setAttribute("aria-selected", selected ? "true" : "false");
  }

  elements.configContent.replaceChildren(
    renderConfigurationTab(activeConfigTab),
  );
}

function renderConfigurationTab(tabName) {
  switch (tabName) {
    case "permissions":
      return renderPermissionsTab();
    case "agent":
      return renderAgentTab();
    case "audit":
      return renderAuditTab();
    case "history":
      return renderHistoryTab();
    default:
      return renderOverviewTab();
  }
}

function renderOverviewTab() {
  const fragment = document.createDocumentFragment();
  fragment.append(
    renderMetricGrid([
      [
        "Workspace",
        configuration.status.initialized
          ? "Initialized"
          : "Needs initialization",
      ],
      ["Surface", configuration.runtime.surface],
      ["Model", configuration.runtime.model],
      [
        "Codex runtime",
        configuration.runtime.codexRuntime?.compatibility ?? "unknown",
      ],
      [
        "Codex version",
        configuration.runtime.codexRuntime?.version ?? "unknown",
      ],
      [
        "Codex executable",
        configuration.runtime.codexRuntime?.resolvedExecutablePath ??
          configuration.runtime.codexExecutablePath,
      ],
      ["Sandbox", configuration.runtime.codexSandbox],
      ["Audit events", String(configuration.audit.events.length)],
      [
        "Transcripts",
        String(configuration.conversationHistory.transcripts.length),
      ],
    ]),
  );
  fragment.append(renderPathGroup(configuration.paths));
  if (
    configuration.runtime.codexRuntime &&
    configuration.runtime.codexRuntime.compatibility !== "compatible"
  ) {
    fragment.append(
      renderProblems([configuration.runtime.codexRuntime.detail]),
    );
  }
  fragment.append(renderConcepts(configuration.concepts));
  return fragment;
}

function renderPermissionsTab() {
  const fragment = document.createDocumentFragment();
  fragment.append(
    renderMetricGrid([
      ["File", configuration.permissions.exists ? "Present" : "Missing"],
      ["Parsed", configuration.permissions.parsed ? "Yes" : "No"],
      ["Version", configuration.permissions.version ?? "Missing"],
      ["Scope", configuration.permissions.scope || "Missing"],
      ["Default", configuration.permissions.defaultAccess],
    ]),
  );
  fragment.append(renderProblems(configuration.permissions.readProblems));
  fragment.append(
    renderRuleSection("Approved", configuration.permissions.approved),
  );
  fragment.append(
    renderRuleSection("Denied", configuration.permissions.denied),
  );
  fragment.append(
    renderDetails(
      "permissions.json",
      configuration.permissions.rawJson || "Missing",
    ),
  );
  return fragment;
}

function renderAgentTab() {
  const fragment = document.createDocumentFragment();
  const documents = groupBy(
    configuration.documents,
    (document) => document.category,
  );
  for (const [category, items] of documents) {
    fragment.append(renderSectionHeading(category));
    for (const documentItem of items) {
      fragment.append(renderDocument(documentItem));
    }
  }

  return fragment;
}

function renderAuditTab() {
  const fragment = document.createDocumentFragment();
  fragment.append(
    renderMetricGrid([
      ["Path", configuration.audit.path],
      ["File", configuration.audit.exists ? "Present" : "Missing"],
      ["Events", String(configuration.audit.events.length)],
    ]),
  );
  fragment.append(renderProblems(configuration.audit.readProblems));
  if (configuration.audit.events.length === 0) {
    fragment.append(createState("No audit events"));
    return fragment;
  }

  for (const event of [...configuration.audit.events].reverse()) {
    fragment.append(renderAuditEvent(event));
  }

  return fragment;
}

function renderHistoryTab() {
  const fragment = document.createDocumentFragment();
  fragment.append(
    renderMetricGrid([
      ["Directory", configuration.conversationHistory.directoryPath],
      ["Current", configuration.conversationHistory.currentPath],
      ["Archive", configuration.conversationHistory.archivePath],
      [
        "Transcripts",
        String(configuration.conversationHistory.transcripts.length),
      ],
    ]),
  );
  fragment.append(
    renderProblems(configuration.conversationHistory.readProblems),
  );
  for (const transcript of configuration.conversationHistory.transcripts) {
    fragment.append(renderTranscript(transcript));
  }

  return fragment;
}

function renderMetricGrid(items) {
  const grid = document.createElement("dl");
  grid.className = "config-metrics";
  for (const [label, value] of items) {
    const item = document.createElement("div");
    const term = document.createElement("dt");
    term.textContent = label;
    const description = document.createElement("dd");
    description.textContent = value ?? "";
    item.append(term, description);
    grid.append(item);
  }

  return grid;
}

function renderPathGroup(paths) {
  const section = document.createElement("section");
  section.className = "config-group";
  section.append(renderSectionHeading("Paths"));
  for (const path of paths) {
    const item = document.createElement("article");
    item.className = "config-row";
    item.append(
      renderRowHeader(path.name, path.exists ? "Present" : "Missing"),
    );
    item.append(textLine(path.category, "muted"));
    item.append(textLine(path.path, "path"));
    item.append(textLine(path.description, "muted"));
    section.append(item);
  }

  return section;
}

function renderConcepts(concepts) {
  const section = document.createElement("section");
  section.className = "config-group";
  section.append(renderSectionHeading("Concepts"));
  for (const concept of concepts) {
    const item = document.createElement("article");
    item.className = "config-row";
    item.append(renderRowHeader(concept.name, concept.status));
    item.append(textLine(concept.category, "muted"));
    item.append(textLine(concept.detail, "muted"));
    section.append(item);
  }

  return section;
}

function renderRuleSection(title, rules) {
  const section = document.createElement("section");
  section.className = "config-group";
  section.append(renderSectionHeading(`${title} (${rules.length})`));
  if (rules.length === 0) {
    section.append(createState("No rules"));
    return section;
  }

  for (const rule of rules) {
    const item = document.createElement("article");
    item.className = "config-row permission-rule";
    item.append(
      renderRowHeader(
        rule.path,
        rule.requiresApproval ? "Approval" : rule.effect,
      ),
    );
    item.append(renderChipList(rule.operations));
    item.append(textLine(rule.detail, "muted"));
    section.append(item);
  }

  return section;
}

function renderDocument(documentItem) {
  const details = document.createElement("details");
  details.className = "config-document";
  if (
    documentItem.exists &&
    ["Role guide", "Context", "Memory", "Models"].includes(documentItem.name)
  ) {
    details.open = true;
  }

  const summary = document.createElement("summary");
  summary.append(
    renderRowHeader(
      documentItem.name,
      documentItem.exists ? "Present" : "Missing",
    ),
  );
  details.append(summary);
  details.append(
    renderMetricGrid([
      ["Path", documentItem.path],
      ["Size", `${documentItem.sizeBytes} bytes`],
      ["Modified", formatDate(documentItem.lastModifiedUtc)],
    ]),
  );
  details.append(renderCodeBlock(documentItem.content || "Missing"));
  return details;
}

function renderAuditEvent(event) {
  const item = document.createElement("article");
  item.className = "config-row audit-event";
  item.append(
    renderRowHeader(`${event.sequence}. ${event.action}`, event.outcome),
  );
  item.append(
    renderMetricGrid([
      ["Time", formatDate(event.timestampUtc)],
      ["Actor", event.actor],
      ["Target", event.target],
      ["Detail", event.detail],
    ]),
  );
  const metadata = Object.entries(event.metadata ?? {});
  if (metadata.length > 0) {
    item.append(renderKeyValueList("Metadata", metadata));
  }

  return item;
}

function renderTranscript(transcript) {
  const details = document.createElement("details");
  details.className = "config-document transcript-detail";
  details.open = transcript.isCurrent;

  const summary = document.createElement("summary");
  summary.append(
    renderRowHeader(
      transcript.conversationId,
      transcript.exists ? `${transcript.messageCount} messages` : "Missing",
    ),
  );
  details.append(summary);
  details.append(
    renderMetricGrid([
      ["Path", transcript.path],
      ["First", formatDate(transcript.firstTimestampUtc)],
      ["Last", formatDate(transcript.lastTimestampUtc)],
      ["First prompt", transcript.firstPrompt || "None"],
    ]),
  );
  if (transcript.messages.length === 0) {
    details.append(createState("No messages"));
    return details;
  }

  for (const message of transcript.messages) {
    const item = document.createElement("article");
    item.className = "history-message";
    item.append(
      renderRowHeader(
        `${message.sequence}. ${message.role}`,
        formatDate(message.timestampUtc),
      ),
    );
    item.append(textLine(message.content, "content"));
    details.append(item);
  }

  return details;
}

function renderProblems(problems) {
  const section = document.createElement("section");
  section.className = "config-problems";
  if (!problems || problems.length === 0) {
    return section;
  }

  section.append(renderSectionHeading("Read problems"));
  for (const problem of problems) {
    section.append(textLine(problem, "error-text"));
  }

  return section;
}

function renderDetails(title, content) {
  const details = document.createElement("details");
  details.className = "config-document";
  const summary = document.createElement("summary");
  summary.textContent = title;
  details.append(summary, renderCodeBlock(content));
  return details;
}

function renderKeyValueList(title, entries) {
  const section = document.createElement("section");
  section.className = "config-group";
  section.append(renderSectionHeading(title));
  const list = document.createElement("dl");
  list.className = "metadata-list";
  for (const [key, value] of entries) {
    const item = document.createElement("div");
    const term = document.createElement("dt");
    term.textContent = key;
    const description = document.createElement("dd");
    description.textContent = value;
    item.append(term, description);
    list.append(item);
  }

  section.append(list);
  return section;
}

function renderChipList(values) {
  const list = document.createElement("div");
  list.className = "chip-list";
  for (const value of values) {
    const chip = document.createElement("span");
    chip.textContent = value;
    list.append(chip);
  }

  return list;
}

function renderRowHeader(title, statusText) {
  const header = document.createElement("div");
  header.className = "row-header";
  const strong = document.createElement("strong");
  strong.textContent = title;
  const statusBadge = document.createElement("span");
  statusBadge.textContent = statusText;
  header.append(strong, statusBadge);
  return header;
}

function renderSectionHeading(text) {
  const heading = document.createElement("h3");
  heading.textContent = text;
  return heading;
}

function renderCodeBlock(content) {
  const pre = document.createElement("pre");
  pre.textContent = content;
  return pre;
}

function createState(text, kind = "") {
  const state = document.createElement("p");
  state.className = kind ? `empty-state ${kind}` : "empty-state";
  state.textContent = text;
  return state;
}

function textLine(text, className = "") {
  const line = document.createElement("p");
  line.className = className;
  line.textContent = text ?? "";
  return line;
}

function formatDate(value) {
  if (!value) {
    return "None";
  }

  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? String(value) : date.toLocaleString();
}

function groupBy(items, selector) {
  const groups = new Map();
  for (const item of items) {
    const key = selector(item);
    if (!groups.has(key)) {
      groups.set(key, []);
    }

    groups.get(key).push(item);
  }

  return groups;
}

function renderApprovals(approvals) {
  const pending = Array.isArray(approvals) ? approvals : [];
  elements.approvalCount.textContent = `${pending.length} pending`;
  elements.chatApprovalAlert.textContent = `${pending.length} chat approval${pending.length === 1 ? "" : "s"} · Review`;
  elements.chatApprovalAlert.hidden = pending.length === 0;
  elements.approvals.replaceChildren(...pending.map(renderApproval));
}

function renderApproval(approval) {
  const item = document.createElement("article");
  item.className = "approval";

  const title = document.createElement("strong");
  title.textContent = `${approval.command} ${approval.operation}`;
  item.append(title);

  for (const text of [
    `Target: ${approval.targetPath}`,
    `Resolved: ${approval.resolvedPath}`,
    `Matched: ${approval.matchedPath}`,
    approval.reason,
  ]) {
    const line = document.createElement("p");
    line.textContent = text;
    item.append(line);
  }

  const actions = document.createElement("div");
  actions.className = "approval-actions";

  const reject = document.createElement("button");
  reject.className = "reject";
  reject.type = "button";
  reject.textContent = "Reject";
  reject.setAttribute(
    "aria-label",
    `Reject ${approval.command} ${approval.operation} for ${approval.targetPath}`,
  );
  reject.addEventListener("click", () =>
    decideApproval(approval.requestId, false),
  );

  const approve = document.createElement("button");
  approve.className = "approve";
  approve.type = "button";
  approve.textContent = "Approve";
  approve.setAttribute(
    "aria-label",
    `Approve ${approval.command} ${approval.operation} for ${approval.targetPath}`,
  );
  approve.addEventListener("click", () =>
    decideApproval(approval.requestId, true),
  );

  actions.append(reject, approve);
  item.append(actions);
  return item;
}

async function decideApproval(requestId, approved) {
  const result = await hub.invoke("DecideApproval", requestId, { approved });
  if (!result.accepted) {
    appendMessage("error", result.message);
  }
}

function appendMessage(kind, text) {
  const message = createMessage(kind, text);
  elements.transcript.append(message);
  elements.transcript.scrollTop = elements.transcript.scrollHeight;
  return message;
}

function createMessage(kind, text) {
  const message = document.createElement("div");
  message.className = `message ${kind}`;
  const role = document.createElement("strong");
  role.className = "message-role";
  role.textContent = messageRoleLabel(kind);
  const content = document.createElement("p");
  content.className = "message-content";
  content.textContent = text;
  message.append(role, content);
  return message;
}

function messageRoleLabel(kind) {
  if (kind === "user") {
    return "User";
  }

  if (kind === "agent") {
    return "Assistant";
  }

  if (kind === "tool") {
    return "Tool";
  }

  if (kind === "system") {
    return "System";
  }

  return "Error";
}

function getMessageContent(message) {
  return message.querySelector(".message-content") ?? message;
}

function replaceTranscript(messages) {
  activeAgentMessage = null;
  const renderedMessages = (messages ?? []).map((message) =>
    createMessage(messageKind(message.role), message.content ?? ""),
  );
  elements.transcript.replaceChildren(...renderedMessages);
  elements.transcript.scrollTop = elements.transcript.scrollHeight;
}

function messageKind(role) {
  const normalizedRole = String(role ?? "").toLowerCase();
  if (normalizedRole === "user") {
    return "user";
  }

  if (normalizedRole === "assistant") {
    return "agent";
  }

  if (normalizedRole === "tool") {
    return "tool";
  }

  return "system";
}

function appendAgentDelta(text) {
  if (!activeAgentMessage) {
    activeAgentMessage = appendMessage("agent", "");
  }

  getMessageContent(activeAgentMessage).textContent += text;
  elements.transcript.scrollTop = elements.transcript.scrollHeight;
}

function finalizeAgentMessage(text) {
  if (activeAgentMessage) {
    getMessageContent(activeAgentMessage).textContent = text;
  } else {
    activeAgentMessage = appendMessage("agent", text);
  }

  elements.transcript.scrollTop = elements.transcript.scrollHeight;
  activeAgentMessage = null;
}

function discardActiveAgentMessage() {
  if (activeAgentMessage) {
    elements.transcript.replaceChildren(
      ...Array.from(elements.transcript.children).filter(
        (message) => message !== activeAgentMessage,
      ),
    );
  }
  activeAgentMessage = null;
}

elements.initButton.addEventListener("click", async () => {
  elements.initButton.disabled = true;
  const nextStatus = await hub.invoke("InitializeWorkspace");
  applyStatus(nextStatus);
  await refreshConfiguration();
});

elements.refreshConfigButton.addEventListener("click", refreshConfiguration);
elements.retryConnectionButton.addEventListener("click", async () => {
  elements.retryConnectionButton.disabled = true;
  try {
    await startSessionRecovery("manual-retry", {
      manual: true,
      newGeneration: true,
    });
  } finally {
    elements.retryConnectionButton.disabled = false;
  }
});

elements.verboseToggle.addEventListener("change", async () => {
  const enabled = elements.verboseToggle.checked;
  elements.verboseToggle.disabled = true;
  try {
    await hub.invoke("SetVerboseMode", enabled);
  } catch (error) {
    elements.verboseToggle.checked = !enabled;
    appendMessage("error", error.message);
  } finally {
    applyStatus(status);
  }
});

for (const tab of elements.appTabs) {
  tab.addEventListener("click", () => {
    if (isConfigurationTabName(tab.dataset.configTab))
      activeConfigTab = tab.dataset.configTab;
    selectAppView(tab.dataset.appView ?? "chat", tab);
  });
  tab.addEventListener("keydown", (event) => moveAppTabFocus(event, tab));
}

elements.chatApprovalAlert.addEventListener("click", () => {
  const chatTab = elements.appTabs.find(
    (tab) => tab.dataset.appView === "chat",
  );
  selectAppView("chat", chatTab);
  elements.chatApprovalsTitle.focus();
});

function moveAppTabFocus(event, currentTab) {
  if (!["ArrowUp", "ArrowDown", "Home", "End"].includes(event.key)) return;
  event.preventDefault();
  const currentIndex = elements.appTabs.indexOf(currentTab);
  const nextIndex =
    event.key === "Home"
      ? 0
      : event.key === "End"
        ? elements.appTabs.length - 1
        : (currentIndex +
            (event.key === "ArrowUp" ? -1 : 1) +
            elements.appTabs.length) %
          elements.appTabs.length;
  const nextTab = elements.appTabs[nextIndex];
  nextTab.focus();
  nextTab.click();
}

elements.cancelButton.addEventListener("click", async () => {
  elements.cancelButton.disabled = true;
  const cancelled = await hub.invoke("CancelCurrentTurn");
  if (!cancelled) {
    appendMessage("error", "No active agent turn is running.");
  }
});

elements.messageForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  const message = elements.messageInput.value.trim();
  const reviewCommand = isDefaultConversationReviewCommand(message);
  if (
    !message ||
    !status?.initialized ||
    !hub?.connected ||
    !chatRequestStorageReady ||
    (chatRequestDispatchBlocked && !reviewCommand) ||
    chatRequestInFlight
  ) {
    return;
  }

  if (reviewCommand) {
    await sendDefaultConversationReviewCommand(message);
    return;
  }

  let request;
  try {
    request = await reserveChatRequest(message);
  } catch (error) {
    if (
      error.chatRequestCode === "storage-corrupt" ||
      error.chatRequestCode === "storage-unavailable"
    ) {
      failChatRequestStorage(error);
      appendMessage("error", chatRequestStorageError);
    } else if (error.chatRequestCode === "reconciliation-required") {
      await reconcilePendingChatRequest();
    } else {
      appendMessage("error", error.message);
    }

    return;
  }

  chatRequestInFlight = true;
  activeAgentMessage = null;
  appendMessage("user", message);
  elements.messageInput.value = "";
  refreshChatControls();
  elements.cancelButton.disabled = false;

  let terminalResultReceived = false;
  try {
    const result = await hub.invoke(
      "SendMessage",
      request.message,
      request.requestId,
    );
    const resultStatus = normalizeChatRequestStatus(result?.status);
    if (!isTerminalChatRequestStatus(resultStatus)) {
      throw new Error(
        "The chat invocation returned no conclusive disposition.",
      );
    }

    if (resultStatus === "needs-review") {
      if (result?.releaseRequestIdentity !== false) {
        throw new Error(
          "The review-required disposition did not preserve its request identity.",
        );
      }

      chatRequestDispatchBlocked = false;
    } else {
      if (result?.releaseRequestIdentity !== true) {
        throw new Error(
          "The terminal chat disposition did not authorize identity retirement.",
        );
      }

      terminalResultReceived = true;
      await releaseChatRequest(request);
      clearChatRequestRetryEntry(request);
    }
  } catch (error) {
    if (terminalResultReceived) {
      failChatRequestStorage(error);
      appendMessage(
        "error",
        `The message reached a conclusive terminal outcome, but its browser identity could not be retired safely. ${chatRequestStorageError}`,
      );
    } else {
      elements.messageInput.value = request.message;
      appendMessage(
        "error",
        `Message outcome is unknown; retrying will reuse the same request identity. ${error.message}`,
      );
    }
  } finally {
    chatRequestInFlight = false;
    elements.cancelButton.disabled = true;
    refreshChatControls();
  }
});

function handleStreamEvent(event) {
  if (event.type === "assistant_delta") {
    appendAgentDelta(event.text ?? "");
  } else if (event.type === "assistant_final") {
    finalizeAgentMessage(event.text ?? "");
  } else if (event.type === "history_loaded") {
    replaceTranscript(event.messages ?? []);
  } else if (event.type === "verbose_context" || event.type === "system") {
    appendMessage("system", event.text ?? "");
  } else if (event.type === "cancelled") {
    appendMessage("error", event.text ?? "Message cancelled.");
    activeAgentMessage = null;
  } else if (event.type === "needs_review") {
    discardActiveAgentMessage();
    appendMessage(
      "system",
      event.text ?? "This turn requires explicit review.",
    );
  } else if (event.type === "error") {
    appendMessage("error", event.error ?? "Request failed.");
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
    this.handshake = null;
    this.handshakeReject = null;
    this.handshakeResolve = null;
    this.startReject = null;
    this.onclose = null;
  }

  on(target, handler) {
    const handlers = this.handlers.get(target) ?? new Set();
    handlers.add(handler);
    this.handlers.set(target, handlers);
    return () => handlers.delete(handler);
  }

  async start({ signal = null } = {}) {
    this.closedByClient = false;
    this.isClosed = false;
    this.socket = new WebSocket(this.url);
    this.socket.onmessage = (event) => this.receive(event.data);
    this.socket.onclose = () => this.handleClose();
    try {
      await this.waitForOpen(signal);
      this.socket.onerror = () => this.handleClose();
      this.handshake = this.waitForHandshake(signal);
      this.sendRaw({ protocol: "json", version: 1 });
      await this.handshake;
      this.connected = true;
    } catch (error) {
      this.stop();
      throw error;
    }
  }

  waitForOpen(signal) {
    return new Promise((resolve, reject) => {
      let settled = false;
      const timeoutId = window.setTimeout(
        () => finish(reject, new Error("SignalR connection timed out.")),
        signalRStartTimeoutMilliseconds,
      );
      const abort = () => finish(reject, signal.reason);
      const finish = (callback, value) => {
        if (settled) return;
        settled = true;
        window.clearTimeout(timeoutId);
        signal?.removeEventListener("abort", abort);
        if (this.startReject === fail) this.startReject = null;
        callback(value);
      };
      const fail = (error) => finish(reject, error);
      this.startReject = fail;
      this.socket.onopen = () => finish(resolve);
      this.socket.onerror = () => fail(new Error("SignalR connection failed."));
      if (signal?.aborted) abort();
      else signal?.addEventListener("abort", abort, { once: true });
    });
  }

  waitForHandshake(signal) {
    return new Promise((resolve, reject) => {
      let settled = false;
      const timeoutId = window.setTimeout(
        () => finish(reject, new Error("SignalR handshake timed out.")),
        signalRStartTimeoutMilliseconds,
      );
      const abort = () => finish(reject, signal.reason);
      const finish = (callback, value) => {
        if (settled) return;
        settled = true;
        window.clearTimeout(timeoutId);
        signal?.removeEventListener("abort", abort);
        this.handshakeResolve = null;
        this.handshakeReject = null;
        callback(value);
      };
      this.handshakeResolve = (value) => finish(resolve, value);
      this.handshakeReject = (error) => finish(reject, error);
      if (signal?.aborted) abort();
      else signal?.addEventListener("abort", abort, { once: true });
    });
  }

  async invoke(target, ...args) {
    if (
      !this.connected ||
      !this.socket ||
      this.socket.readyState !== WebSocket.OPEN
    ) {
      throw new Error("SignalR connection is not available.");
    }

    const invocationId = String(this.nextInvocationId++);
    const completion = new Promise((resolve, reject) => {
      this.invocations.set(invocationId, { resolve, reject });
    });
    this.sendRaw({ type: 1, invocationId, target, arguments: args });
    return await completion;
  }

  sendRaw(message) {
    this.socket.send(`${JSON.stringify(message)}${recordSeparator}`);
  }

  async receive(data) {
    const text = typeof data === "string" ? data : await data.text();
    this.buffer += text;
    const messages = this.buffer.split(recordSeparator);
    this.buffer = messages.pop() ?? "";

    for (const messageText of messages) {
      if (!messageText) {
        continue;
      }

      const message = JSON.parse(messageText);
      if (!message.type) {
        if (message.error) {
          this.handshakeReject?.(new Error(message.error));
        } else {
          this.handshakeResolve?.();
        }

        continue;
      }

      this.handleMessage(message);
    }
  }

  handleMessage(message) {
    if (message.type === 1) {
      for (const handler of this.handlers.get(message.target) ?? [])
        handler(...(message.arguments ?? []));
    } else if (message.type === 3) {
      const invocation = this.invocations.get(message.invocationId);
      if (!invocation) {
        return;
      }

      this.invocations.delete(message.invocationId);
      if (message.error) {
        invocation.reject(new Error(message.error));
      } else {
        invocation.resolve(message.result);
      }
    } else if (message.type === 7) {
      this.handleClose();
    }
  }

  stop() {
    this.closedByClient = true;
    try {
      this.socket?.close?.();
    } catch {
      // A failed or still-connecting socket can reject close; local state must still be released.
    }
    this.handleClose();
  }

  handleClose() {
    if (this.isClosed) {
      return;
    }

    this.isClosed = true;
    this.connected = false;
    this.startReject?.(new Error("SignalR connection closed."));
    this.handshakeReject?.(new Error("SignalR connection closed."));
    for (const invocation of this.invocations.values()) {
      invocation.reject(new Error("SignalR connection closed."));
    }

    this.invocations.clear();
    this.socket = null;
    if (!this.closedByClient && this.onclose) {
      this.onclose();
    }
  }
}

function stopSessionForPageHide() {
  if (sessionPageHidden) return;
  sessionPageHidden = true;
  sessionRecoveryGeneration++;
  if (sessionRecoveryTimer != null) {
    window.clearTimeout(sessionRecoveryTimer);
    sessionRecoveryTimer = null;
  }
  cancelActiveSessionRecovery("The page was hidden during session recovery.");
  hub?.stop();
  hub = null;
  window.embodySenseLoopBuilder?.suspendSession?.();
}

function resumeSessionFromPageShow(event) {
  if (!sessionPageHidden || event?.persisted !== true) return;
  sessionPageHidden = false;
  void startSessionRecovery("page-restored", { newGeneration: true });
}

window.addEventListener?.("pagehide", stopSessionForPageHide);
window.addEventListener?.("pageshow", resumeSessionFromPageShow);

window.embodySenseSession = Object.freeze({
  async getHub() {
    if (!hub?.connected) {
      await startSessionRecovery("session-consumer", {
        newGeneration: sessionRecoveryGeneration === 0,
      });
    }
    if (!hub?.connected) {
      throw new Error(
        sessionRecoveryTerminal
          ? "Session recovery requires manual attention."
          : "Session recovery is still in progress.",
      );
    }
    return hub;
  },
  getState() {
    return Object.freeze({
      attempts: sessionRecoveryAttempts,
      connected: Boolean(hub?.connected),
      generation: sessionRecoveryGeneration,
      processGenerationId: sessionGenerationId,
      terminal: sessionRecoveryTerminal,
      workspaceRoot: sessionWorkspaceRoot,
    });
  },
  recover() {
    return startSessionRecovery("session-consumer", {
      newGeneration: true,
    });
  },
  requestJson: fetchJson,
});

elements.cancelButton.disabled = true;
elements.refreshConfigButton.disabled = true;
renderConfigLoading();
const requestedView = new URL(window.location.href).searchParams.get("view");
activeConfigTab = isConfigurationTabName(requestedView)
  ? requestedView
  : "overview";
selectAppView(
  requestedView === "loops"
    ? "loops"
    : requestedView === "reviews"
      ? "reviews"
      : isConfigurationTabName(requestedView)
        ? "configuration"
        : "chat",
);
boot().catch((error) => appendMessage("error", error.message));
