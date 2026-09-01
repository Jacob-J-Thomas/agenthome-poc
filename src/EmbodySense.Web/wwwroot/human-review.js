const maximumPageItems = 50;
const maximumPreviewItems = 3;
const maximumEvidenceItems = 64;
const maximumDecisionItems = 16;
const maximumDisplayCharacters = 1024;
const maximumOperationEntries = 128;
const maximumOperationStorageCharacters = 64 * 1024;
const maximumOperationLifecycleVersion = 1_000_000;
const maximumDecisionFeedbackCharacters = 240;
const maximumReviewPages = 20;
const maximumAggregateItems = 500;
const humanReviewOperationStorageKeyPrefix =
  "embodysense.human-review.operations.v1";
const humanReviewOperationLockNamePrefix =
  "embodysense.human-review.operations.v1";
const humanReviewDecisionLockNamePrefix =
  "embodysense.human-review.decisions.v1";
const humanReviewOperationStorageSchemaVersion = 1;
const sha256Pattern = /^[0-9a-f]{64}$/;
const identifierPattern = /^[a-z0-9][a-z0-9._-]{0,119}$/;
const cursorPattern = /^[A-Za-z0-9_-]+$/;
const lifecycleStatuses = new Set([
  "pending",
  "awaiting-information",
  "approved",
  "rejected",
  "cancelled",
  "expired",
  "superseded",
  "conflicted",
]);
const runStatuses = new Set([
  "admitted",
  "running",
  "pause-requested",
  "paused",
  "cancel-requested",
  "completed",
  "failed",
  "cancelled",
  "needs-review",
  "waiting",
]);
const frontierStatuses = new Set([
  "active",
  "waiting",
  "review-blocked",
  "completed",
  "failed",
  "cancelled",
]);
const effectEvidenceStatuses = new Set([
  "invalid",
  "exact-not-started",
  "dispatched",
  "conclusive",
  "ambiguous",
  "terminal",
  "missing",
  "corrupt",
  "unavailable",
  "stale",
]);
const effectCertaintyStatuses = new Set([
  "unknown",
  "not-started",
  "dispatched",
  "conclusive",
  "ambiguous",
  "terminal",
]);
const supportedActions = Object.freeze([
  "approve",
  "reject",
  "cancel",
  "request-information",
]);
const actionLabels = Object.freeze({
  approve: "Approve",
  reject: "Reject",
  cancel: "Cancel",
  "request-information": "Request information",
});
const terminalLifecycleStatuses = new Set([
  "approved",
  "rejected",
  "cancelled",
  "expired",
  "superseded",
  "conflicted",
]);

export function normalizeHumanReviewStatus(value) {
  return String(value ?? "")
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replaceAll("_", "-")
    .toLowerCase();
}

export function humanReviewActionPath(action) {
  const normalized = normalizeHumanReviewStatus(action);
  return supportedActions.includes(normalized) ? normalized : null;
}

export function boundedHumanReviewText(
  value,
  maximum = maximumDisplayCharacters,
) {
  const text = String(value ?? "");
  if (text.length <= maximum) return text;
  return `${text.slice(0, Math.max(0, maximum - 1))}…`;
}

export function humanReviewOperationIdentity(randomUUID = null) {
  const value =
    randomUUID ??
    (typeof globalThis.crypto?.randomUUID === "function"
      ? globalThis.crypto.randomUUID()
      : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`);
  const normalized = String(value)
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-");
  return boundedHumanReviewText(`web-human-review-${normalized}`, 120);
}

export function projectHumanReviewPage(response) {
  const status = normalizeHumanReviewStatus(response?.status);
  if (status !== "ready")
    return Object.freeze({ status, items: [], cursor: null });
  if (
    !Array.isArray(response.items) ||
    response.items.length > maximumPageItems
  )
    return Object.freeze({ status: "invalid", items: [], cursor: null });
  const items = response.items.map(projectHumanReviewSummary);
  if (items.some((item) => item === null))
    return Object.freeze({ status: "invalid", items: [], cursor: null });
  const cursor =
    typeof response.continuationCursor === "string"
      ? response.continuationCursor
      : null;
  if (cursor !== null && !isValidHumanReviewCursor(cursor))
    return Object.freeze({ status: "invalid", items: [], cursor: null });
  return Object.freeze({
    status,
    items: Object.freeze(items),
    cursor,
  });
}

export function projectHumanReviewSummary(summary) {
  if (
    !summary ||
    !isIdentifier(summary.runId) ||
    !isIdentifier(summary.requestId) ||
    !sha256Pattern.test(summary.requestHash ?? "") ||
    !Number.isSafeInteger(summary.lifecycleVersion) ||
    summary.lifecycleVersion < 0
  )
    return null;
  const requestedDecisions = Array.isArray(summary.requestedDecisions)
    ? summary.requestedDecisions.map(normalizeHumanReviewStatus)
    : [];
  if (
    requestedDecisions.length === 0 ||
    requestedDecisions.length > supportedActions.length ||
    requestedDecisions.some((action) => !supportedActions.includes(action)) ||
    new Set(requestedDecisions).size !== requestedDecisions.length
  )
    return null;
  const lifecycleStatus = validatedStatus(
    summary.lifecycleStatus,
    lifecycleStatuses,
  );
  const runStatus = validatedStatus(summary.runStatus, runStatuses);
  const frontierStatus = validatedStatus(
    summary.frontierStatus,
    frontierStatuses,
  );
  if (!lifecycleStatus || !runStatus || !frontierStatus) return null;
  return Object.freeze({
    ...summary,
    requestedDecisions: Object.freeze(requestedDecisions),
    lifecycleStatus,
    runStatus,
    frontierStatus,
  });
}

export function projectHumanReviewEvidence(response) {
  const status = normalizeHumanReviewStatus(response?.status);
  if (status !== "ready")
    return Object.freeze({ status, evidence: [], effectEvidence: null });
  if (
    !Array.isArray(response.evidence) ||
    response.evidence.length > maximumEvidenceItems ||
    !Object.hasOwn(response, "effectEvidence")
  )
    return Object.freeze({
      status: "invalid",
      evidence: [],
      effectEvidence: null,
    });
  const effectEvidence = projectHumanReviewEffectEvidence(
    response.effectEvidence,
  );
  if (response.effectEvidence !== null && effectEvidence === null)
    return Object.freeze({
      status: "invalid",
      evidence: [],
      effectEvidence: null,
    });
  return Object.freeze({
    status,
    evidence: Object.freeze(response.evidence),
    effectEvidence,
  });
}

export function humanReviewOutcomeMessage(status, httpStatus = null) {
  const normalized = normalizeHumanReviewStatus(status);
  if (normalized === "accepted")
    return "Approval was recorded. Rereading canonical state…";
  if (normalized === "information-requested")
    return "Information request was recorded. Rereading canonical state…";
  if (normalized === "replayed")
    return "This operation was already recorded. Rereading canonical state…";
  if (normalized === "denied" || httpStatus === 403)
    return "This server-owned reviewer is not authorized for the exact review.";
  if (normalized === "expired")
    return "This review expired before the decision was accepted.";
  if (
    normalized === "conflict" ||
    normalized === "limit-exceeded" ||
    httpStatus === 409
  )
    return "The review changed or the operation conflicted. Reread canonical state before trying again.";
  if (normalized === "invalid" || httpStatus === 400)
    return "The decision was not valid for this review. Reread canonical state.";
  if (normalized === "not-found" || httpStatus === 404)
    return "This durable review is no longer available.";
  return "Human Review is temporarily unavailable. Retry after the runtime is healthy.";
}

export function humanReviewReadMessage(status, httpStatus = null) {
  const normalized = normalizeHumanReviewStatus(status);
  if (normalized === "not-found" || httpStatus === 404)
    return "This durable review is no longer available.";
  if (normalized === "invalid" || httpStatus === 400)
    return "The canonical review response was invalid. Refresh to try again.";
  if (
    normalized === "corrupt" ||
    normalized === "ambiguous" ||
    normalized === "stale" ||
    httpStatus === 409
  )
    return "Review evidence is conflicting. No decision is enabled until canonical state is repaired.";
  return "Human Review is temporarily unavailable. Retry after the runtime is healthy.";
}

export function createHumanReviewSurface({
  document,
  window: hostWindow,
  requestJson: suppliedRequestJson,
} = {}) {
  if (!document) throw new Error("Human Review requires a document.");
  const requestJson =
    suppliedRequestJson ?? hostWindow?.embodySenseSession?.requestJson;
  if (typeof requestJson !== "function")
    throw new Error(
      "The authenticated Human Review HTTP facade is unavailable.",
    );

  const elements = {
    actionSection: document.getElementById("humanReviewActionSection"),
    actionStatus: document.getElementById("humanReviewActionStatus"),
    actions: document.getElementById("humanReviewActions"),
    approveButton: document.getElementById("humanReviewApproveButton"),
    cancelButton: document.getElementById("humanReviewCancelButton"),
    decisionHistory: document.getElementById("humanReviewDecisionHistory"),
    detailPanel: document.getElementById("humanReviewDetailPanel"),
    detailRefreshButton: document.getElementById(
      "humanReviewDetailRefreshButton",
    ),
    detailStatus: document.getElementById("humanReviewDetailStatus"),
    effectEvidence: document.getElementById("humanReviewEffectEvidence"),
    evidence: document.getElementById("humanReviewEvidence"),
    evidencePosture: document.getElementById("humanReviewEvidencePosture"),
    empty: document.getElementById("humanReviewEmpty"),
    informationDetail: document.getElementById("humanReviewInformationDetail"),
    informationField: document.getElementById("humanReviewInformationField"),
    informationButton: document.getElementById(
      "humanReviewRequestInformationButton",
    ),
    list: document.getElementById("humanReviewList"),
    listStatus: document.getElementById("humanReviewListStatus"),
    lifecycleStatus: document.getElementById("humanReviewLifecycleStatus"),
    previewList: document.getElementById("humanReviewPreviews"),
    purpose: document.getElementById("humanReviewPurpose"),
    rejectButton: document.getElementById("humanReviewRejectButton"),
    refreshButton: document.getElementById("humanReviewRefreshButton"),
    summary: document.getElementById("humanReviewSummary"),
    title: document.getElementById("humanReviewTitle"),
    identity: document.getElementById("humanReviewIdentity"),
  };
  const state = {
    actionInFlight: false,
    active: false,
    detail: null,
    evidence: null,
    posture: null,
    items: [],
    operations: new Map(),
    operationDecisionLockName: null,
    operationStorage: null,
    operationStorageKey: null,
    operationStorageLockName: null,
    operationStorageScope: null,
    operationStorageEnabled: false,
    operationStorageStatus: "unconfigured",
    requestInformationDetails: new Map(),
    decisionFeedback: null,
    refreshPromise: null,
    selectedRunId: null,
    selectedSummary: null,
    selectionGeneration: 0,
    evidenceProjection: null,
    evidenceReady: false,
  };
  let requestInformationNonce = 0;

  function activate() {
    state.active = true;
    return refresh();
  }

  function sessionRecovered() {
    if (!state.active) return Promise.resolve(false);
    return refresh(state.selectedRunId);
  }

  function notifyChanged(notification) {
    if (!state.active) return Promise.resolve(false);
    const runId =
      typeof notification?.runId === "string" ? notification.runId : null;
    return refresh(runId === state.selectedRunId ? runId : null);
  }

  function refresh(runId = null) {
    if (state.refreshPromise) return state.refreshPromise;
    const operation = refreshCore(runId);
    const tracked = operation.finally(() => {
      if (state.refreshPromise === tracked) state.refreshPromise = null;
    });
    state.refreshPromise = tracked;
    return tracked;
  }

  async function refreshCore(runId) {
    setListBusy(true);
    try {
      const page = await readReviewPages();
      state.items = page.status === "ready" ? page.items : [];
      if (runId && state.items.some((item) => item.runId === runId))
        state.selectedRunId = runId;
      if (
        !state.selectedRunId ||
        !state.items.some((item) => item.runId === state.selectedRunId)
      )
        state.selectedRunId = state.items[0]?.runId ?? null;
      state.selectedSummary =
        state.items.find((item) => item.runId === state.selectedRunId) ?? null;
      renderList(page);
      if (state.selectedSummary) await readSelectedReview();
      else clearDetail();
      return page;
    } catch (error) {
      state.items = [];
      renderList({ status: statusFromError(error), items: [], cursor: null });
      clearDetail();
      return null;
    } finally {
      setListBusy(false);
      if (elements.detailRefreshButton && !state.actionInFlight)
        elements.detailRefreshButton.disabled = false;
    }
  }

  async function readReviewPages() {
    const items = [];
    const identities = new Set();
    const cursors = new Set();
    let cursor = null;
    for (let pageNumber = 0; pageNumber < maximumReviewPages; pageNumber++) {
      const query = cursor ? `&cursor=${encodeURIComponent(cursor)}` : "";
      const page = projectHumanReviewPage(
        await requestJson(
          `/api/human-reviews?maximumCount=${maximumPageItems}${query}`,
        ),
      );
      if (page.status !== "ready") return page;
      if (items.length + page.items.length > maximumAggregateItems)
        return { status: "invalid", items: [], cursor: null };
      for (const item of page.items) {
        const identity = humanReviewSummaryIdentity(item);
        if (identities.has(identity))
          return { status: "invalid", items: [], cursor: null };
        identities.add(identity);
        items.push(item);
      }
      if (!page.cursor)
        return Object.freeze({
          status: "ready",
          items: Object.freeze(items),
          cursor: null,
        });
      if (cursors.has(page.cursor))
        return { status: "invalid", items: [], cursor: null };
      cursors.add(page.cursor);
      cursor = page.cursor;
    }
    return { status: "invalid", items: [], cursor: null };
  }

  async function readSelectedReview() {
    const summary = state.selectedSummary;
    if (!summary) {
      clearDetail();
      return;
    }
    const generation = ++state.selectionGeneration;
    state.detail = null;
    state.evidence = null;
    state.posture = null;
    state.evidenceProjection = null;
    state.evidenceReady = false;
    renderDetailLoading(summary);
    const encodedRunId = encodeURIComponent(summary.runId);
    const [detail, evidence, posture] = await Promise.all([
      readEndpoint(`/api/human-reviews/${encodedRunId}`),
      readEndpoint(`/api/human-reviews/${encodedRunId}/evidence`),
      readEndpoint(`/api/human-reviews/${encodedRunId}/posture`),
    ]);
    if (generation !== state.selectionGeneration) return;
    state.detail = detail;
    state.evidence = evidence;
    state.evidenceProjection =
      evidence.status === "ready"
        ? projectHumanReviewEvidence(evidence.value)
        : Object.freeze({
            status: evidence.status,
            evidence: [],
            effectEvidence: null,
          });
    state.evidenceReady = state.evidenceProjection.status === "ready";
    state.posture = posture;
    renderDetail(summary, detail, state.evidenceProjection, posture);
  }

  async function readEndpoint(url) {
    try {
      return { status: "ready", value: await requestJson(url) };
    } catch (error) {
      return {
        status: statusFromError(error),
        httpStatus: error?.status ?? null,
      };
    }
  }

  function selectReview(runId) {
    if (!state.items.some((item) => item.runId === runId))
      return Promise.resolve(false);
    if (state.selectedRunId !== runId) {
      state.requestInformationDetails.clear();
      state.decisionFeedback = null;
    }
    state.selectedRunId = runId;
    state.selectedSummary =
      state.items.find((item) => item.runId === runId) ?? null;
    renderList({ status: "ready", items: state.items, cursor: null });
    return readSelectedReview().then(() => true);
  }

  async function decide(action, button) {
    const normalizedAction = humanReviewActionPath(action);
    const summary = state.selectedSummary;
    if (!normalizedAction || !summary || state.actionInFlight) return;
    const detail =
      normalizedAction === "request-information"
        ? boundedHumanReviewText(
            elements.informationDetail?.value?.trim() ?? "",
          )
        : "";
    if (normalizedAction === "request-information" && detail.length === 0) {
      elements.informationField.hidden = false;
      elements.informationDetail.focus?.();
      setActionStatus(
        "Enter a bounded information request, then submit it again.",
        "warning",
      );
      return;
    }
    state.decisionFeedback = null;
    const intent = humanReviewDecisionIntent(summary, normalizedAction);
    if (
      !state.operationStorageEnabled ||
      state.operationStorageStatus !== "ready"
    ) {
      setActionStatus(
        "Human Review operation recovery is unavailable. No decision was sent.",
        "error",
      );
      return;
    }
    state.actionInFlight = true;
    setActionButtonsDisabled(true);
    if (button) button.setAttribute("aria-busy", "true");
    let submittedOperation = null;
    let submittedOperationKey = null;
    try {
      const response = await withHumanReviewDecisionLock(
        intent.key,
        async () => {
          try {
            const operation = await reserveHumanReviewOperation(
              intent,
              summary,
              normalizedAction,
              detail,
            );
            if (!operation) return null;
            submittedOperation = operation;
            submittedOperationKey =
              humanReviewOperationStorageEntryKey(operation);
            const payload = {
              expectedLifecycleVersion: operation.expectedLifecycleVersion,
              operationId: operation.operationId,
            };
            if (normalizedAction === "request-information")
              payload.detail = detail;
            const result = await requestJson(
              `/api/human-reviews/${encodeURIComponent(summary.runId)}/${normalizedAction}`,
              {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(payload),
              },
            );
            await forgetOperation(submittedOperationKey);
            return result;
          } catch (error) {
            if (isDefinitiveHumanReviewDecisionError(error))
              await forgetOperation(submittedOperationKey ?? intent.key);
            throw error;
          }
        },
      );
      if (!response) {
        setActionStatus(
          "Human Review operation recovery is unavailable. No decision was sent.",
          "error",
        );
        return;
      }
      setDecisionFeedback(summary, submittedOperation, response);
      if (
        normalizedAction === "request-information" &&
        elements.informationDetail
      )
        elements.informationDetail.value = "";
      await refresh(summary.runId);
    } catch (error) {
      setActionStatus(
        humanReviewOutcomeMessage(null, error?.status ?? null),
        "error",
      );
      await refresh(summary.runId);
    } finally {
      state.actionInFlight = false;
      setActionButtonsDisabled(false);
      button?.removeAttribute("aria-busy");
      if (
        state.selectedSummary &&
        state.detail?.status === "ready" &&
        state.detail.value?.status === "ready"
      )
        configureActions(
          state.selectedSummary,
          state.evidenceProjection?.effectEvidence,
          true,
          state.evidenceReady,
        );
    }
  }

  function configureWorkspaceScope(scope) {
    state.operations.clear();
    state.requestInformationDetails.clear();
    state.decisionFeedback = null;
    state.operationDecisionLockName = null;
    state.operationStorageScope = null;
    state.operationStorageKey = null;
    state.operationStorageLockName = null;
    state.operationStorage = null;
    state.operationStorageEnabled = false;
    if (typeof scope !== "string" || !sha256Pattern.test(scope)) {
      state.operationStorageStatus = "unavailable";
      return false;
    }
    state.operationStorageScope = scope;
    state.operationDecisionLockName = `${humanReviewDecisionLockNamePrefix}.${scope}`;
    state.operationStorageKey = `${humanReviewOperationStorageKeyPrefix}.${scope}`;
    state.operationStorageLockName = `${humanReviewOperationLockNamePrefix}.${scope}`;
    let storage;
    try {
      storage = hostWindow?.localStorage;
    } catch {
      storage = null;
    }
    if (
      !storage ||
      typeof storage.getItem !== "function" ||
      typeof storage.removeItem !== "function" ||
      typeof storage.setItem !== "function" ||
      typeof hostWindow?.navigator?.locks?.request !== "function"
    ) {
      state.operationStorageStatus = "unavailable";
      return false;
    }
    state.operationStorage = storage;
    const current = readHumanReviewOperationStorage(
      hostWindow,
      state.operationStorageKey,
      state.operationStorageScope,
    );
    if (!current.enabled || current.status !== "ready") {
      state.operationStorageStatus = "unavailable";
      return false;
    }
    state.operationStorage = current.storage;
    replaceOperations(current.entries);
    state.operationStorageEnabled = true;
    state.operationStorageStatus = "ready";
    return true;
  }

  function humanReviewRequestInformationOperationIdentity(summary) {
    const stableOperationId = stableHumanReviewOperationIdentity(
      summary.runId,
      summary.requestId,
      summary.requestHash,
      "request-information",
      summary.lifecycleVersion,
    );
    let operationId;
    do {
      const randomUuid =
        typeof hostWindow?.crypto?.randomUUID === "function"
          ? hostWindow.crypto.randomUUID()
          : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
      const nonce = hashOperationIdentity(
        `${randomUuid}-${++requestInformationNonce}`,
        2166136261,
      )
        .toString(16)
        .padStart(8, "0");
      operationId = `${stableOperationId}-u${nonce}`;
    } while (
      Array.from(state.operations.values()).some(
        (operation) => operation.operationId === operationId,
      )
    );
    return operationId;
  }

  async function reserveHumanReviewOperation(intent, summary, action, detail) {
    if (
      !state.operationStorageEnabled ||
      state.operationStorageStatus !== "ready"
    )
      return null;
    try {
      return await withHumanReviewOperationStorageLock(async () => {
        const current = readHumanReviewOperationStorage(
          hostWindow,
          state.operationStorageKey,
          state.operationStorageScope,
        );
        if (!current.enabled || current.status !== "ready")
          throw new Error("operation storage unavailable");
        state.operationStorage = current.storage;
        replaceOperations(current.entries);
        let operation = null;
        const candidates = Array.from(state.operations.values()).filter(
          (candidate) =>
            humanReviewDecisionIntent(candidate, candidate.action).key ===
            intent.key,
        );
        if (action === "request-information") {
          operation = candidates.find(
            (candidate) =>
              state.requestInformationDetails.get(candidate.operationId) ===
              detail,
          );
          if (
            !operation &&
            candidates.some(
              (candidate) =>
                !state.requestInformationDetails.has(candidate.operationId),
            )
          )
            return null;
        } else {
          operation = candidates[0] ?? null;
        }
        if (!operation) {
          if (state.operations.size >= maximumOperationEntries)
            throw new Error("operation storage capacity reached");
          operation = Object.freeze({
            runId: summary.runId,
            requestId: summary.requestId,
            requestHash: summary.requestHash,
            action,
            operationId:
              action === "request-information"
                ? humanReviewRequestInformationOperationIdentity(summary)
                : stableHumanReviewOperationIdentity(
                    summary.runId,
                    summary.requestId,
                    summary.requestHash,
                    action,
                    summary.lifecycleVersion,
                  ),
            expectedLifecycleVersion: summary.lifecycleVersion,
          });
          const operationKey = humanReviewOperationStorageEntryKey(operation);
          state.operations.set(operationKey, operation);
          if (action === "request-information")
            state.requestInformationDetails.set(operation.operationId, detail);
          if (!persistHumanReviewOperations(state))
            throw new Error("operation storage unavailable");
        }
        return operation;
      });
    } catch {
      state.operationStorageStatus = "unavailable";
      state.operations.clear();
      state.requestInformationDetails.clear();
      return null;
    }
  }

  async function forgetOperation(key) {
    if (
      !state.operationStorageEnabled ||
      state.operationStorageStatus !== "ready"
    )
      return false;
    try {
      await withHumanReviewOperationStorageLock(async () => {
        const current = readHumanReviewOperationStorage(
          hostWindow,
          state.operationStorageKey,
          state.operationStorageScope,
        );
        if (!current.enabled || current.status !== "ready")
          throw new Error("operation storage unavailable");
        state.operationStorage = current.storage;
        replaceOperations(current.entries);
        const operation = state.operations.get(key);
        if (!operation) return;
        state.operations.delete(key);
        if (operation.action === "request-information")
          state.requestInformationDetails.delete(operation.operationId);
        if (!persistHumanReviewOperations(state))
          throw new Error("operation storage unavailable");
      });
      return true;
    } catch {
      state.operationStorageStatus = "unavailable";
      return false;
    }
  }

  function replaceOperations(entries) {
    const previousDetails = state.requestInformationDetails;
    state.operations.clear();
    state.requestInformationDetails = new Map();
    for (const [, operation] of entries) {
      const key = humanReviewOperationStorageEntryKey(operation);
      state.operations.set(key, operation);
      if (operation.action === "request-information") {
        const detail = previousDetails.get(operation.operationId);
        if (detail !== undefined)
          state.requestInformationDetails.set(operation.operationId, detail);
      }
    }
  }

  function withHumanReviewOperationStorageLock(callback) {
    const locks = hostWindow?.navigator?.locks;
    if (
      !state.operationStorageEnabled ||
      !state.operationStorageKey ||
      !state.operationStorageLockName ||
      typeof locks?.request !== "function"
    )
      return Promise.reject(new Error("operation storage lock unavailable"));
    return locks.request(
      state.operationStorageLockName,
      { mode: "exclusive" },
      callback,
    );
  }

  function withHumanReviewDecisionLock(intentKey, callback) {
    const locks = hostWindow?.navigator?.locks;
    if (
      !state.operationStorageEnabled ||
      !state.operationDecisionLockName ||
      typeof locks?.request !== "function"
    )
      return Promise.reject(new Error("decision operation lock unavailable"));
    return locks.request(
      `${state.operationDecisionLockName}.${intentKey}`,
      { mode: "exclusive" },
      callback,
    );
  }

  function renderList(page) {
    if (!elements.list || !elements.listStatus) return;
    elements.list.replaceChildren();
    if (page.status !== "ready") {
      elements.listStatus.textContent = humanReviewReadMessage(page.status);
      elements.listStatus.classList.add("error");
      return;
    }
    elements.listStatus.classList.remove("error");
    elements.listStatus.textContent =
      page.items.length === 0
        ? "No retained Human Review requests were found."
        : `${page.items.length} durable review${page.items.length === 1 ? "" : "s"} shown. Select one to reread canonical state.`;
    for (const summary of page.items) {
      const item = document.createElement("button");
      item.type = "button";
      item.className = "human-review-list-item";
      item.dataset.runId = summary.runId;
      item.dataset.testid = "human-review-item";
      item.setAttribute("role", "option");
      item.setAttribute(
        "aria-selected",
        summary.runId === state.selectedRunId ? "true" : "false",
      );
      item.addEventListener("click", () => void selectReview(summary.runId));
      const title = document.createElement("strong");
      title.textContent = boundedHumanReviewText(
        summary.purpose || "Human Review",
        120,
      );
      const stateLine = document.createElement("span");
      stateLine.textContent = `${formatToken(summary.lifecycleStatus)} · version ${summary.lifecycleVersion}`;
      const run = document.createElement("small");
      run.textContent = boundedHumanReviewText(summary.runId, 120);
      item.append(title, stateLine, run);
      elements.list.append(item);
    }
  }

  function renderDetailLoading(summary) {
    if (!elements.empty || !elements.detailPanel) return;
    elements.empty.hidden = true;
    elements.detailPanel.hidden = false;
    elements.detailStatus.textContent =
      "Rereading canonical review, evidence, and runtime posture…";
    elements.detailStatus.className = "human-review-status";
    elements.title.textContent = "Review detail";
    elements.purpose.textContent = formatToken(
      summary.purpose || "Human Review",
    );
    elements.identity.textContent = `Request ${boundedHumanReviewText(summary.requestId, 120)}`;
    clearCollection(elements.summary);
    clearCollection(elements.previewList);
    clearCollection(elements.evidence);
    clearCollection(elements.decisionHistory);
    elements.effectEvidence.replaceChildren();
    elements.evidencePosture.textContent = "Reading…";
    setActionButtonsDisabled(true);
  }

  function renderDetail(summary, detailResult, evidenceResult, postureResult) {
    elements.empty.hidden = true;
    elements.detailPanel.hidden = false;
    const detail = detailResult?.status === "ready" ? detailResult.value : null;
    const detailSummary =
      detail?.status === "ready"
        ? projectHumanReviewSummary(detail.detail?.summary)
        : null;
    const detailReady =
      detailResult?.status === "ready" &&
      detail?.status === "ready" &&
      detailSummary !== null &&
      sameHumanReviewIdentity(summary, detailSummary) &&
      detail.detail !== null;
    const projectedDetail = detailReady ? detail.detail : null;
    const exactSummary = detailReady ? detailSummary : summary;
    state.selectedSummary = exactSummary;
    elements.title.textContent = `Review ${boundedHumanReviewText(exactSummary.runId, 120)}`;
    elements.purpose.textContent = formatToken(
      exactSummary.purpose || "Human Review",
    );
    elements.identity.textContent = `Request ${boundedHumanReviewText(exactSummary.requestId, 120)} · ${shortHash(exactSummary.requestHash)}`;
    const evidenceReady = evidenceResult?.status === "ready";
    elements.detailStatus.textContent =
      detailReady && evidenceReady
        ? "Canonical state reread. Browser content is a redacted projection; authority remains server-owned."
        : detailReady
          ? "Canonical review loaded, but evidence posture is not ready. Approval is disabled until evidence is ready."
          : humanReviewReadMessage(
              detailResult?.status === "ready" && detail?.status === "ready"
                ? "invalid"
                : detailResult?.status,
              detailResult?.status === "ready"
                ? null
                : detailResult?.httpStatus,
            );
    elements.detailStatus.className =
      detailReady && evidenceReady
        ? "human-review-status"
        : "human-review-status error";
    renderSummary(
      exactSummary,
      projectedDetail?.runtime ?? postureResult?.value?.posture,
    );
    renderPreviews(projectedDetail?.previews);
    renderEvidence(evidenceResult, evidenceReady);
    renderDecisions(projectedDetail?.decisions);
    configureActions(
      exactSummary,
      evidenceResult?.effectEvidence,
      detailReady,
      evidenceReady,
    );
  }

  function renderSummary(summary, runtime) {
    clearCollection(elements.summary);
    const values = [
      ["Lifecycle", formatToken(summary.lifecycleStatus)],
      ["Run", formatToken(summary.runStatus)],
      ["Frontier", formatToken(summary.frontierStatus)],
      ["Lifecycle version", summary.lifecycleVersion],
      [
        "Requested decisions",
        summary.requestedDecisions.map(formatToken).join(", "),
      ],
      ["Expires", formatTimestamp(summary.expiresAtUtc)],
      ["Updated", formatTimestamp(summary.updatedAtUtc)],
    ];
    if (runtime) {
      values.push(
        [
          "Runtime posture",
          formatToken(runtime.lifecycleStatus || runtime.frontierStatus),
        ],
        ["Evidence count", boundedNumber(runtime.evidenceCount)],
        ["Decision count", boundedNumber(runtime.decisionCount)],
      );
    }
    for (const [label, value] of values)
      appendDefinition(elements.summary, label, value);
    elements.lifecycleStatus.textContent = formatToken(summary.lifecycleStatus);
  }

  function renderPreviews(previews) {
    clearCollection(elements.previewList);
    const values = Array.isArray(previews)
      ? previews.slice(0, maximumPreviewItems)
      : [];
    if (values.length === 0) {
      appendEmpty(
        elements.previewList,
        "No redacted request preview is available.",
      );
      return;
    }
    for (const preview of values) {
      const item = document.createElement("article");
      item.className = "human-review-preview";
      const heading = document.createElement("strong");
      heading.textContent = boundedHumanReviewText(
        preview?.label || formatToken(preview?.kind),
        120,
      );
      const detail = document.createElement("p");
      detail.textContent = boundedHumanReviewText(preview?.detail);
      item.append(heading, detail);
      elements.previewList.append(item);
    }
  }

  function renderEvidence(evidence, evidenceReady) {
    clearCollection(elements.evidence);
    const values =
      evidenceReady && Array.isArray(evidence?.evidence)
        ? evidence.evidence.slice(0, maximumEvidenceItems)
        : [];
    const effectEvidence = evidenceReady ? evidence.effectEvidence : null;
    elements.evidencePosture.textContent = evidenceReady
      ? effectEvidence
        ? `Effect posture: ${formatToken(effectEvidence.status)} · certainty: ${formatToken(effectEvidence.certainty)}`
        : "No exact effect evidence is retained."
      : `Canonical evidence is ${formatToken(evidence?.status)}. Approval remains disabled until it is ready.`;
    elements.effectEvidence.replaceChildren();
    if (effectEvidence || !evidenceReady) {
      const posture = document.createElement("p");
      posture.textContent = effectEvidence
        ? "Effect values and authority material are intentionally withheld. " +
          `Current evidence is ${formatToken(effectEvidence.status)}.`
        : "The canonical evidence read is not ready. Approval cannot dispatch an effect from this surface.";
      elements.effectEvidence.append(posture);
    }
    if (values.length === 0) {
      appendEmpty(
        elements.evidence,
        "No detached evidence entries are available.",
      );
      return;
    }
    for (const entry of values) {
      const item = document.createElement("li");
      item.className = "human-review-evidence-item";
      const heading = document.createElement("strong");
      heading.textContent = `${formatToken(entry?.kind)} · ${formatTimestamp(entry?.recordedAtUtc)}`;
      const detail = document.createElement("p");
      const previews = Array.isArray(entry?.previews)
        ? entry.previews.slice(0, maximumPreviewItems)
        : [];
      detail.textContent =
        previews
          .map((preview) => boundedHumanReviewText(preview?.detail))
          .filter(Boolean)
          .join(" · ") || "Redacted evidence retained.";
      item.append(heading, detail);
      elements.evidence.append(item);
    }
  }

  function renderDecisions(decisions) {
    clearCollection(elements.decisionHistory);
    const values = Array.isArray(decisions)
      ? decisions.slice(0, maximumDecisionItems)
      : [];
    if (values.length === 0) {
      appendEmpty(
        elements.decisionHistory,
        "No accepted decisions are retained yet.",
      );
      return;
    }
    for (const decision of values) {
      const item = document.createElement("li");
      item.className = "human-review-decision-item";
      const heading = document.createElement("strong");
      heading.textContent = `${formatToken(decision?.kind)} · ${formatTimestamp(decision?.decidedAtUtc)}`;
      const detail = document.createElement("p");
      detail.textContent = boundedHumanReviewText(
        decision?.detail || "No reviewer detail supplied.",
      );
      item.append(heading, detail);
      elements.decisionHistory.append(item);
    }
  }

  function configureActions(
    summary,
    effectEvidence,
    detailReady = true,
    evidenceReady = true,
  ) {
    const lifecycle = normalizeHumanReviewStatus(summary.lifecycleStatus);
    const requested = new Set(summary.requestedDecisions);
    const effectStatus = normalizeHumanReviewStatus(effectEvidence?.status);
    const approvalBlocked = [
      "ambiguous",
      "dispatched",
      "corrupt",
      "stale",
      "unavailable",
      "invalid",
    ].includes(effectStatus);
    const pending = !terminalLifecycleStatuses.has(lifecycle);
    let hasReplayableOperation = false;
    for (const [action, button] of actionButtons()) {
      const retainedOperation =
        findReplayableHumanReviewOperation(summary, action) !== null;
      const replayable = !pending && retainedOperation;
      hasReplayableOperation ||= replayable;
      button.hidden = false;
      button.textContent = replayable
        ? `Retry recorded ${actionLabels[action].toLowerCase()}`
        : actionLabels[action];
      button.disabled =
        state.actionInFlight ||
        !detailReady ||
        (!pending && !replayable) ||
        !requested.has(action) ||
        (action === "approve" &&
          !replayable &&
          (!evidenceReady || approvalBlocked));
      button.setAttribute("aria-disabled", button.disabled ? "true" : "false");
    }
    elements.informationField.hidden =
      !requested.has("request-information") || !pending;
    const feedback = matchingDecisionFeedback(summary);
    if (feedback) setActionStatus(feedback.message, feedback.tone);
    else if (hasReplayableOperation)
      setActionStatus(
        "A recorded decision response remains unresolved. Retry that exact decision to reread its durable receipt; no new decision identity will be created.",
        "warning",
      );
    else if (!evidenceReady)
      setActionStatus(
        "Canonical effect evidence is not ready. Approval is disabled until the evidence read succeeds.",
        "warning",
      );
    else if (approvalBlocked && requested.has("approve"))
      setActionStatus(
        "Approval is blocked because exact effect evidence needs revalidation. No effect is dispatched by this surface.",
        "warning",
      );
    else if (!pending)
      setActionStatus(
        "This review is terminal. The durable decision history remains available for rereading.",
        "warning",
      );
  }

  function findReplayableHumanReviewOperation(summary, action) {
    const intentKey = humanReviewDecisionIntent(summary, action).key;
    return (
      Array.from(state.operations.values()).find(
        (operation) =>
          humanReviewDecisionIntent(operation, operation.action).key ===
            intentKey &&
          (action !== "request-information" ||
            state.requestInformationDetails.has(operation.operationId)),
      ) ?? null
    );
  }

  function clearDetail() {
    state.detail = null;
    state.evidence = null;
    state.posture = null;
    state.selectedSummary = null;
    state.requestInformationDetails.clear();
    state.decisionFeedback = null;
    state.selectionGeneration++;
    elements.empty.hidden = false;
    elements.detailPanel.hidden = true;
    setActionStatus("", "");
  }

  function setListBusy(busy) {
    if (elements.refreshButton) elements.refreshButton.disabled = busy;
    if (elements.detailRefreshButton && state.actionInFlight === false)
      elements.detailRefreshButton.disabled = busy;
  }

  function setActionButtonsDisabled(disabled) {
    for (const [, button] of actionButtons()) button.disabled = disabled;
  }

  function setActionStatus(message, tone) {
    if (!elements.actionStatus) return;
    elements.actionStatus.textContent = boundedHumanReviewText(message);
    elements.actionStatus.className = tone
      ? `human-review-action-status ${tone}`
      : "human-review-action-status";
  }

  function setDecisionFeedback(summary, operation, response) {
    const status = normalizeHumanReviewStatus(response?.status);
    state.decisionFeedback = Object.freeze({
      runId: summary.runId,
      requestId: summary.requestId,
      requestHash: summary.requestHash,
      action: operation?.action ?? null,
      operationId: operation?.operationId ?? null,
      message: boundedHumanReviewText(
        humanReviewOutcomeMessage(status),
        maximumDecisionFeedbackCharacters,
      ),
      tone: ["accepted", "information-requested", "replayed"].includes(status)
        ? "success"
        : "warning",
    });
  }

  function matchingDecisionFeedback(summary) {
    const feedback = state.decisionFeedback;
    return feedback &&
      feedback.runId === summary.runId &&
      feedback.requestId === summary.requestId &&
      feedback.requestHash === summary.requestHash
      ? feedback
      : null;
  }

  function bind() {
    elements.refreshButton?.addEventListener(
      "click",
      () => void refresh(state.selectedRunId),
    );
    elements.detailRefreshButton?.addEventListener(
      "click",
      () => void refresh(state.selectedRunId),
    );
    for (const [action, button] of actionButtons())
      button.addEventListener("click", () => void decide(action, button));
  }

  bind();
  renderList({ status: "ready", items: [], cursor: null });

  return Object.freeze({
    activate,
    configureWorkspaceScope,
    notifyChanged,
    refresh,
    selectReview,
    sessionRecovered,
  });

  function actionButtons() {
    return [
      ["approve", elements.approveButton],
      ["reject", elements.rejectButton],
      ["cancel", elements.cancelButton],
      ["request-information", elements.informationButton],
    ].filter(([, button]) => button);
  }
}

function appendDefinition(parent, label, value) {
  if (!parent) return;
  const wrapper =
    parent.ownerDocument?.createElement?.("div") ??
    document.createElement("div");
  const name =
    wrapper.ownerDocument?.createElement?.("dt") ??
    document.createElement("dt");
  const content =
    wrapper.ownerDocument?.createElement?.("dd") ??
    document.createElement("dd");
  name.textContent = boundedHumanReviewText(label, 120);
  content.textContent = boundedHumanReviewText(value, 512);
  wrapper.append(name, content);
  parent.append(wrapper);
}

function appendEmpty(parent, message) {
  if (!parent) return;
  const item =
    parent.ownerDocument?.createElement?.("p") ?? document.createElement("p");
  item.className = "human-review-empty-message";
  item.textContent = boundedHumanReviewText(message);
  parent.append(item);
}

function clearCollection(element) {
  element?.replaceChildren?.();
}

function validatedStatus(value, allowed) {
  const normalized = normalizeHumanReviewStatus(value);
  return allowed.has(normalized) ? normalized : null;
}

function projectHumanReviewEffectEvidence(effectEvidence) {
  if (effectEvidence === null) return null;
  if (
    !effectEvidence ||
    typeof effectEvidence !== "object" ||
    !effectEvidenceStatuses.has(
      normalizeHumanReviewStatus(effectEvidence.status),
    ) ||
    (effectEvidence.certainty !== null &&
      !effectCertaintyStatuses.has(
        normalizeHumanReviewStatus(effectEvidence.certainty),
      ))
  )
    return null;
  return Object.freeze({
    ...effectEvidence,
    status: normalizeHumanReviewStatus(effectEvidence.status),
    certainty:
      effectEvidence.certainty === null
        ? null
        : normalizeHumanReviewStatus(effectEvidence.certainty),
  });
}

function isValidHumanReviewCursor(cursor) {
  return (
    typeof cursor === "string" &&
    cursor.length <= 1024 &&
    cursor.length > 0 &&
    cursor.length % 4 !== 1 &&
    cursorPattern.test(cursor)
  );
}

function humanReviewSummaryIdentity(summary) {
  return `${summary.runId}\u001f${summary.requestId}\u001f${summary.requestHash}`;
}

function readHumanReviewOperationStorage(hostWindow, storageKey, storageScope) {
  let storage;
  try {
    storage = hostWindow?.localStorage;
  } catch {
    return {
      enabled: true,
      status: "unavailable",
      storage: null,
      entries: new Map(),
    };
  }
  if (
    !storage ||
    typeof storage.getItem !== "function" ||
    typeof storage.removeItem !== "function" ||
    typeof storage.setItem !== "function"
  )
    return {
      enabled: false,
      status: "ephemeral",
      storage: null,
      entries: new Map(),
    };
  let raw;
  try {
    raw = storage.getItem(storageKey);
  } catch {
    return {
      enabled: true,
      status: "unavailable",
      storage,
      entries: new Map(),
    };
  }
  if (raw === null)
    return { enabled: true, status: "ready", storage, entries: new Map() };
  if (typeof raw !== "string" || raw.length > maximumOperationStorageCharacters)
    return {
      enabled: true,
      status: "unavailable",
      storage,
      entries: new Map(),
    };
  let parsed;
  try {
    parsed = JSON.parse(raw);
    if (JSON.stringify(parsed) !== raw)
      throw new Error("non-canonical storage");
  } catch {
    return {
      enabled: true,
      status: "unavailable",
      storage,
      entries: new Map(),
    };
  }
  if (!isValidHumanReviewOperationStorageEnvelope(parsed, storageScope))
    return {
      enabled: true,
      status: "unavailable",
      storage,
      entries: new Map(),
    };
  const entries = new Map();
  for (const entry of parsed.entries) {
    const key = humanReviewOperationStorageEntryKey(entry);
    if (entries.has(key))
      return {
        enabled: true,
        status: "unavailable",
        storage,
        entries: new Map(),
      };
    entries.set(key, Object.freeze({ ...entry }));
  }
  return { enabled: true, status: "ready", storage, entries };
}

function isValidHumanReviewOperationStorageEnvelope(value, expectedScope) {
  if (
    !value ||
    typeof value !== "object" ||
    Array.isArray(value) ||
    Object.keys(value).length !== 3 ||
    !Object.hasOwn(value, "schemaVersion") ||
    !Object.hasOwn(value, "entries") ||
    !Object.hasOwn(value, "scope") ||
    value.schemaVersion !== humanReviewOperationStorageSchemaVersion ||
    value.scope !== expectedScope ||
    !Array.isArray(value.entries) ||
    value.entries.length > maximumOperationEntries
  )
    return false;
  return value.entries.every(isValidHumanReviewOperationStorageEntry);
}

function isValidHumanReviewOperationStorageEntry(value) {
  if (
    !value ||
    typeof value !== "object" ||
    Array.isArray(value) ||
    Object.keys(value).length !== 6
  )
    return false;
  const expectedKeys = [
    "action",
    "expectedLifecycleVersion",
    "operationId",
    "requestHash",
    "requestId",
    "runId",
  ];
  if (expectedKeys.some((key) => !Object.hasOwn(value, key))) return false;
  if (
    !isIdentifier(value.runId) ||
    !isIdentifier(value.requestId) ||
    !sha256Pattern.test(value.requestHash) ||
    !supportedActions.includes(value.action) ||
    !Number.isSafeInteger(value.expectedLifecycleVersion) ||
    value.expectedLifecycleVersion < 0 ||
    value.expectedLifecycleVersion > maximumOperationLifecycleVersion ||
    !/^web-human-review-[a-f0-9-u-]{1,110}$/.test(value.operationId)
  )
    return false;
  const stableOperationId = stableHumanReviewOperationIdentity(
    value.runId,
    value.requestId,
    value.requestHash,
    value.action,
    value.expectedLifecycleVersion,
  );
  return (
    value.operationId === stableOperationId ||
    (value.action === "request-information" &&
      value.operationId.startsWith(`${stableOperationId}-u`) &&
      /^-u[a-f0-9]{8}$/.test(value.operationId.slice(stableOperationId.length)))
  );
}

function persistHumanReviewOperations(state) {
  if (state.operationStorageStatus !== "ready")
    return state.operationStorageStatus === "ephemeral";
  const entries = Array.from(state.operations.values()).map((operation) => ({
    runId: operation.runId,
    requestId: operation.requestId,
    requestHash: operation.requestHash,
    action: operation.action,
    operationId: operation.operationId,
    expectedLifecycleVersion: operation.expectedLifecycleVersion,
  }));
  const raw = JSON.stringify({
    schemaVersion: humanReviewOperationStorageSchemaVersion,
    scope: state.operationStorageScope,
    entries,
  });
  if (raw.length > maximumOperationStorageCharacters) {
    state.operationStorageStatus = "unavailable";
    return false;
  }
  try {
    if (entries.length === 0) {
      state.operationStorage.removeItem(state.operationStorageKey);
      if (state.operationStorage.getItem(state.operationStorageKey) !== null)
        throw new Error("operation storage did not clear");
    } else {
      state.operationStorage.setItem(state.operationStorageKey, raw);
      if (state.operationStorage.getItem(state.operationStorageKey) !== raw)
        throw new Error("operation storage did not preserve exact metadata");
    }
    return true;
  } catch {
    state.operationStorageStatus = "unavailable";
    return false;
  }
}

function isIdentifier(value) {
  return typeof value === "string" && identifierPattern.test(value);
}

function shortHash(value) {
  return sha256Pattern.test(value ?? "")
    ? `${value.slice(0, 12)}…`
    : "hash unavailable";
}

function sameHumanReviewIdentity(left, right) {
  return (
    left.runId === right.runId &&
    left.requestId === right.requestId &&
    left.requestHash === right.requestHash &&
    left.lifecycleVersion === right.lifecycleVersion
  );
}

function humanReviewDecisionIntent(summary, action) {
  return {
    key: [summary.runId, summary.requestId, summary.requestHash, action].join(
      "\u001f",
    ),
  };
}

function humanReviewOperationStorageEntryKey(operation) {
  const intentKey = humanReviewDecisionIntent(operation, operation.action).key;
  return operation.action === "request-information"
    ? `${intentKey}\u001e${operation.operationId}`
    : intentKey;
}

function stableHumanReviewOperationIdentity(
  runId,
  requestId,
  requestHash,
  action,
  expectedLifecycleVersion,
) {
  const source = [
    runId,
    requestId,
    requestHash,
    action,
    expectedLifecycleVersion,
  ].join("\u001f");
  const first = hashOperationIdentity(source, 2166136261);
  const second = hashOperationIdentity(source, 2654435761);
  const third = hashOperationIdentity(source, 2246822519);
  const fourth = hashOperationIdentity(source, 3266489917);
  return `web-human-review-${first.toString(16).padStart(8, "0")}-${second.toString(16).padStart(8, "0")}-${third.toString(16).padStart(8, "0")}-${fourth.toString(16).padStart(8, "0")}`;
}

function hashOperationIdentity(value, seed) {
  let hash = seed >>> 0;
  for (let index = 0; index < value.length; index++)
    hash = Math.imul(hash ^ value.charCodeAt(index), 16777619) >>> 0;
  return hash;
}

function boundedNumber(value) {
  return Number.isSafeInteger(value) && value >= 0
    ? String(Math.min(value, 1_000_000))
    : "unavailable";
}

function formatToken(value) {
  const text = normalizeHumanReviewStatus(value);
  if (!text) return "Unknown";
  return text
    .replaceAll("-", " ")
    .replace(/(^| )\w/g, (character) => character.toUpperCase());
}

function formatTimestamp(value) {
  if (typeof value !== "string" || value.length === 0)
    return "time unavailable";
  const date = new Date(value);
  return Number.isNaN(date.valueOf())
    ? "time unavailable"
    : date.toLocaleString();
}

function statusFromError(error) {
  if (error?.status === 400) return "invalid";
  if (error?.status === 403) return "denied";
  if (error?.status === 404) return "not-found";
  if (error?.status === 409) return "conflict";
  return "unavailable";
}

function isDefinitiveHumanReviewDecisionError(error) {
  return [400, 403, 404, 409].includes(error?.status);
}

if (typeof window !== "undefined" && typeof document !== "undefined") {
  try {
    const humanReviewSurface = createHumanReviewSurface({ document, window });
    window.embodySenseHumanReview = humanReviewSurface;
    if (typeof window.embodySenseWorkspaceRequestScope === "string")
      humanReviewSurface.configureWorkspaceScope(
        window.embodySenseWorkspaceRequestScope,
      );
    if (!document.getElementById("humanReviewView")?.hidden)
      void humanReviewSurface.activate();
  } catch {
    // The shared shell may load before authenticated session composition. The
    // app can expose the surface after session recovery when dependencies exist.
  }
}
