const maximumPageItems = 50;
const maximumPages = 20;
const maximumAggregateItems = 500;
const maximumDisplayCharacters = 1024;
const maximumOperationEntries = 128;
const maximumEligibleRespondents = 16;
const maximumRerouteOptions = 16;
const maximumChoices = 16;
const maximumStructuredFields = 12;
const maximumResponseCharacters = 4000;
const maximumReferenceCharacters = 512;
const maximumPurposeCharacters = 240;
const maximumPromptCharacters = 4000;
const maximumRequestBodyBytes = 16_384;
const minimumRequestLifetimeMilliseconds = 60 * 1000;
const maximumRequestLifetimeMilliseconds = 30 * 24 * 60 * 60 * 1000;
const maximumCandidateLifetimeMilliseconds = 15 * 60 * 1000;
const sha256Pattern = /^[0-9a-f]{64}$/;
const identifierPattern = /^[a-z0-9][a-z0-9._-]{0,119}$/;
const cursorPattern = /^[A-Za-z0-9_-]+$/;
const lifecycleStatuses = new Set([
  "pending",
  "rejected",
  "cancelled",
  "expired",
  "superseded",
  "answered",
]);
const responseKinds = new Set([
  "text",
  "choice",
  "confirmation",
  "structured",
  "reference",
]);
const structuredFieldKinds = new Set(["text", "choice"]);
const privacyClasses = new Set(["private", "sensitive"]);
const conflictFamilies = new Set(["lifecycle", "response"]);
const responsePolicies = new Set([
  "first-valid",
  "quorum",
  "named-roles",
  "merge",
  "manual-selection",
]);
const continuationPolicies = new Set(["bound-node-and-checkpoint-only"]);
const referenceKinds = new Set(["artifact", "reference"]);
const terminalStatuses = new Set([
  "rejected",
  "cancelled",
  "expired",
  "superseded",
  "answered",
]);
const conflictTokenPattern = /^[a-z][a-z0-9-]{0,119}$/;
const opaqueCandidateKeyPattern = /^[A-Za-z0-9_-]{1,120}$/;

/** Normalizes server enum spellings without accepting caller-provided paths. */
export function normalizeHumanInputStatus(value) {
  return String(value ?? "")
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replaceAll("_", "-")
    .toLowerCase();
}

/** Bounds untrusted display text before it reaches a text node. */
export function boundedHumanInputText(
  value,
  maximum = maximumDisplayCharacters,
) {
  const text = String(value ?? "");
  if (text.length <= maximum) return text;
  return `${text.slice(0, Math.max(0, maximum - 1))}…`;
}

/** Creates an opaque, bounded operation identity without encoding response data. */
export function humanInputOperationIdentity(randomUUID = null) {
  const value =
    randomUUID ??
    (typeof globalThis.crypto?.randomUUID === "function"
      ? globalThis.crypto.randomUUID()
      : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`);
  const normalized = String(value)
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-");
  return boundedHumanInputText(`web-human-input-${normalized}`, 120);
}

/** Projects one bounded canonical Human Input list page and drops all unknown fields. */
export function projectHumanInputPage(response) {
  const status = normalizeHumanInputStatus(response?.status);
  if (status !== "ready")
    return Object.freeze({ status, items: [], cursor: null });
  if (
    !response ||
    typeof response !== "object" ||
    !Array.isArray(response.requests) ||
    response.requests.length > maximumPageItems
  )
    return Object.freeze({ status: "invalid", items: [], cursor: null });
  const items = response.requests.map(projectHumanInputPosture);
  if (items.some((item) => item === null))
    return Object.freeze({ status: "invalid", items: [], cursor: null });
  const cursor = response.nextCursor === null ? null : response.nextCursor;
  if (cursor !== null && !isValidCursor(cursor))
    return Object.freeze({ status: "invalid", items: [], cursor: null });
  return Object.freeze({ status, items: Object.freeze(items), cursor });
}

/** Projects the bounded opaque route alternatives returned by Startup. */
export function projectHumanInputReroutePreparation(response) {
  const status = normalizeHumanInputStatus(response?.status);
  if (status !== "ready")
    return Object.freeze({
      status,
      options: Object.freeze([]),
      expiresAtUtc: null,
    });
  if (!response || typeof response !== "object")
    return Object.freeze({
      status: "invalid",
      options: Object.freeze([]),
      expiresAtUtc: null,
    });
  const responseFields = [
    "status",
    "requestId",
    "options",
    "expiresAtUtc",
    "error",
  ];
  if (Object.keys(response).some((field) => !responseFields.includes(field)))
    return Object.freeze({
      status: "invalid",
      options: Object.freeze([]),
      expiresAtUtc: null,
    });
  if (!Array.isArray(response.options))
    return Object.freeze({
      status: "invalid",
      options: Object.freeze([]),
      expiresAtUtc: null,
    });
  if (!isIdentifier(response.requestId))
    return Object.freeze({
      status: "invalid",
      options: Object.freeze([]),
      expiresAtUtc: null,
    });
  if (
    response.options.length < 1 ||
    response.options.length > maximumRerouteOptions
  )
    return Object.freeze({
      status: "invalid",
      options: Object.freeze([]),
      expiresAtUtc: null,
    });
  const responseExpiry = validTimestamp(response.expiresAtUtc);
  if (!responseExpiry)
    return Object.freeze({
      status: "invalid",
      options: Object.freeze([]),
      expiresAtUtc: null,
    });
  const options = response.options.map((option) =>
    projectHumanInputRerouteOption(option, responseExpiry),
  );
  if (
    options.some((option) => option === null) ||
    new Set(options.map((option) => option.candidateKey)).size !==
      options.length
  )
    return Object.freeze({
      status: "invalid",
      options: Object.freeze([]),
      expiresAtUtc: null,
    });
  return Object.freeze({
    status: "ready",
    requestId: response.requestId,
    options: Object.freeze(options),
    expiresAtUtc: responseExpiry,
  });
}

/** Projects one server-owned opaque successor candidate without exposing route or binding data. */
export function projectHumanInputCandidatePreparation(response) {
  const status = normalizeHumanInputStatus(response?.status);
  if (status !== "ready")
    return Object.freeze({ status, candidateKey: null, expiresAtUtc: null });
  if (!response || typeof response !== "object")
    return Object.freeze({
      status: "invalid",
      candidateKey: null,
      expiresAtUtc: null,
    });
  if (!isIdentifier(response.requestId))
    return Object.freeze({
      status: "invalid",
      candidateKey: null,
      expiresAtUtc: null,
    });
  const candidateKey = response.candidateKey;
  const responseFields = [
    "status",
    "requestId",
    "candidateKey",
    "expiresAtUtc",
    "error",
  ];
  if (Object.keys(response).some((field) => !responseFields.includes(field)))
    return Object.freeze({
      status: "invalid",
      candidateKey: null,
      expiresAtUtc: null,
    });
  const expiresAtUtc = validTimestamp(response.expiresAtUtc);
  if (!expiresAtUtc)
    return Object.freeze({
      status: "invalid",
      candidateKey: null,
      expiresAtUtc: null,
    });
  if (
    typeof candidateKey !== "string" ||
    !opaqueCandidateKeyPattern.test(candidateKey)
  )
    return Object.freeze({
      status: "invalid",
      candidateKey: null,
      expiresAtUtc: null,
    });
  return Object.freeze({
    status: "ready",
    requestId: response.requestId,
    candidateKey,
    expiresAtUtc,
  });
}

function projectHumanInputRerouteOption(value, responseExpiry) {
  if (!value || typeof value !== "object") return null;
  const optionFields = [
    "candidateKey",
    "label",
    "eligibleRespondentCount",
    "expiresAtUtc",
  ];
  if (Object.keys(value).some((field) => !optionFields.includes(field)))
    return null;
  const candidateKey = value.candidateKey;
  const label = value.label;
  const eligibleRespondentCount = value.eligibleRespondentCount;
  const expiresAtUtc = validTimestamp(value.expiresAtUtc);
  if (
    typeof candidateKey !== "string" ||
    !opaqueCandidateKeyPattern.test(candidateKey) ||
    !boundedString(label, 240) ||
    !boundedEligibleCount(eligibleRespondentCount) ||
    !expiresAtUtc ||
    (responseExpiry !== null && expiresAtUtc !== responseExpiry)
  )
    return null;
  return Object.freeze({
    candidateKey,
    label,
    eligibleRespondentCount,
    expiresAtUtc,
  });
}

/** Projects one exact posture while excluding routing, authority, and binding material. */
export function projectHumanInputPosture(value) {
  if (!value || typeof value !== "object") return null;
  const requestId = value.requestId;
  const lifecycleVersion = value.lifecycleVersion;
  const status = normalizeHumanInputStatus(value.status);
  const currentRequest = projectRequestReference(value.currentRequest);
  const presentation = projectHumanInputPresentation(value.presentation);
  if (
    value.schemaVersion !== 1 ||
    !isIdentifier(requestId) ||
    !Number.isSafeInteger(lifecycleVersion) ||
    lifecycleVersion < 1 ||
    !lifecycleStatuses.has(status) ||
    !currentRequest ||
    currentRequest.requestId !== requestId ||
    !presentation ||
    presentation.requestVersionId !== currentRequest.requestVersionId ||
    presentation.requestHash !== currentRequest.requestHash
  )
    return null;
  const counts = [
    value.reminderCount,
    value.acceptedResponseCount,
    value.activeResponseCount,
    value.withdrawnResponseCount,
  ];
  if (counts.some((count) => !boundedCount(count))) return null;
  const latestConflict = projectHumanInputConflict(value.latestConflict);
  if (
    value.latestConflict !== null &&
    value.latestConflict !== undefined &&
    !latestConflict
  )
    return null;
  const supersedesRequestId = value.supersedesRequestId ?? null;
  const supersededByRequestId = value.supersededByRequestId ?? null;
  if (
    (supersedesRequestId !== null && !isIdentifier(supersedesRequestId)) ||
    (supersededByRequestId !== null && !isIdentifier(supersededByRequestId))
  )
    return null;
  const updatedAtUtc = validTimestamp(value.updatedAtUtc);
  if (!updatedAtUtc) return null;
  return Object.freeze({
    schemaVersion: value.schemaVersion,
    requestId,
    lifecycleVersion,
    status,
    currentRequest,
    presentation,
    reminderCount: counts[0],
    supersedesRequestId,
    supersededByRequestId,
    updatedAtUtc,
    acceptedResponseCount: counts[1],
    activeResponseCount: counts[2],
    withdrawnResponseCount: counts[3],
    isAnswered: value.isAnswered === true,
    latestConflict,
  });
}

function projectHumanInputConflict(value) {
  if (!value || typeof value !== "object") return null;
  const operationId = value.operationId;
  const operationFamily = normalizeHumanInputStatus(value.operationFamily);
  const operationKind = normalizeHumanInputStatus(value.operationKind);
  const failureCode = normalizeHumanInputStatus(value.failureCode);
  const recordedAtUtc = validTimestamp(value.recordedAtUtc);
  if (
    !isIdentifier(operationId) ||
    !conflictFamilies.has(operationFamily) ||
    !conflictTokenPattern.test(operationKind) ||
    !conflictTokenPattern.test(failureCode) ||
    !recordedAtUtc
  )
    return null;
  return Object.freeze({
    operationId,
    operationFamily,
    operationKind,
    failureCode,
    recordedAtUtc,
  });
}

/** Projects the display-safe request presentation and only the approved aggregate recipient posture. */
export function projectHumanInputPresentation(value) {
  if (!value || typeof value !== "object") return null;
  const requestVersionId = value.requestVersionId;
  const requestHash = value.requestHash;
  const purpose = value.purpose;
  const prompt = value.prompt;
  const responseSchema = projectResponseSchema(value.responseSchema);
  const privacyClass = normalizeHumanInputStatus(value.privacyClass);
  const responsePolicyKind = normalizeHumanInputStatus(
    value.responsePolicyKind,
  );
  const continuationPolicyKind = normalizeHumanInputStatus(
    value.continuationPolicyKind,
  );
  const eligibleRespondentCount = value.eligibleRespondentCount;
  const requiredResponseCount = value.requiredResponseCount ?? null;
  const responsePolicyCountValid =
    responsePolicyKind === "quorum"
      ? Number.isSafeInteger(requiredResponseCount) &&
        requiredResponseCount >= 2 &&
        requiredResponseCount <= eligibleRespondentCount
      : responsePolicyKind === "merge"
        ? Number.isSafeInteger(requiredResponseCount) &&
          requiredResponseCount >= 1 &&
          requiredResponseCount <= eligibleRespondentCount
        : requiredResponseCount === null;
  if (
    !isIdentifier(requestVersionId) ||
    !sha256Pattern.test(requestHash ?? "") ||
    !boundedString(purpose, maximumPurposeCharacters) ||
    !boundedString(prompt, maximumPromptCharacters) ||
    !responseSchema ||
    !privacyClasses.has(privacyClass) ||
    !responsePolicies.has(responsePolicyKind) ||
    !continuationPolicies.has(continuationPolicyKind) ||
    !boundedEligibleCount(eligibleRespondentCount) ||
    !responsePolicyCountValid
  )
    return null;
  const timing = projectTiming(value.timing);
  if (!timing) return null;
  return Object.freeze({
    requestVersionId,
    requestHash,
    purpose,
    prompt,
    responseSchema,
    privacyClass,
    timing,
    responsePolicyKind,
    requiredResponseCount,
    eligibleRespondentCount,
    continuationPolicyKind,
  });
}

/** Returns a safe user-facing message for canonical read failures. */
export function humanInputReadMessage(status, httpStatus = null) {
  const normalized = normalizeHumanInputStatus(status);
  if (normalized === "not-found" || httpStatus === 404)
    return "This durable data request is no longer available.";
  if (normalized === "invalid" || httpStatus === 400)
    return "The canonical data request was invalid. Refresh to try again.";
  if (normalized === "stale" || normalized === "conflict" || httpStatus === 409)
    return "The request changed. Reread canonical state before responding.";
  if (normalized === "ambiguous")
    return "Canonical request evidence is conflicting. No response is enabled.";
  return "Human Input is temporarily unavailable. Retry after the runtime is healthy.";
}

/** Returns an explicit, privacy-safe message for a lifecycle or response outcome. */
export function humanInputOutcomeMessage(status, httpStatus = null) {
  const normalized = normalizeHumanInputStatus(status);
  if (normalized === "committed")
    return "The Human Input operation was recorded. Rereading canonical state…";
  if (normalized === "replayed")
    return "This exact operation was already recorded. Rereading canonical state…";
  if (normalized === "denied" || httpStatus === 403)
    return "This server-owned respondent is not authorized for the exact request.";
  if (normalized === "late")
    return "The response window closed before this operation was accepted.";
  if (normalized === "limit-exceeded" || normalized === "capacity-exceeded")
    return "The bounded Human Input lifecycle limit was reached. Reread canonical state before trying again.";
  if (
    normalized === "conflict" ||
    normalized === "ambiguous" ||
    httpStatus === 409
  )
    return "The request changed or the operation conflicted. Reread canonical state.";
  if (normalized === "invalid" || httpStatus === 400)
    return "The response was not valid for this request. Review the bounded schema.";
  if (normalized === "not-found" || httpStatus === 404)
    return "This durable data request is no longer available.";
  return "Human Input is temporarily unavailable. Retry after the runtime is healthy.";
}

/** Explains why a new operation is rejected without evicting an exact retry identity. */
export function humanInputOperationCapacityMessage() {
  return `The browser has reached the bounded limit of ${maximumOperationEntries} retained Human Input operations. No new request was sent; reuse an existing operation or reload before trying again.`;
}

/** Explains why a serialized Human Input request is rejected before transport. */
export function humanInputRequestBodyCapacityMessage() {
  return `The Human Input payload exceeds the server limit of ${maximumRequestBodyBytes.toLocaleString("en-US")} UTF-8 bytes. Shorten the response, explanation, or successor text and try again; no request was sent.`;
}

/** Creates the isolated Human Input surface over the authenticated Startup facade. */
export function createHumanInputSurface({
  document,
  window: hostWindow,
  requestJson: suppliedRequestJson,
} = {}) {
  if (!document) throw new Error("Human Input requires a document.");
  const requestJson =
    suppliedRequestJson ?? hostWindow?.embodySenseSession?.requestJson;
  if (typeof requestJson !== "function")
    throw new Error(
      "The authenticated Human Input HTTP facade is unavailable.",
    );

  const elements = {
    actionsSection: document.getElementById("humanInputActionsSection"),
    cancelButton: document.getElementById("humanInputCancelButton"),
    detailPanel: document.getElementById("humanInputDetailPanel"),
    detailRefreshButton: document.getElementById(
      "humanInputDetailRefreshButton",
    ),
    detailStatus: document.getElementById("humanInputDetailStatus"),
    empty: document.getElementById("humanInputEmpty"),
    amendButton: document.getElementById("humanInputAmendButton"),
    amendExpiresAt: document.getElementById("humanInputAmendExpiresAt"),
    amendPrivacyClass: document.getElementById("humanInputAmendPrivacyClass"),
    amendPrompt: document.getElementById("humanInputAmendPrompt"),
    amendPurpose: document.getElementById("humanInputAmendPurpose"),
    amendSection: document.getElementById("humanInputAmendSection"),
    amendStatus: document.getElementById("humanInputAmendStatus"),
    explanation: document.getElementById("humanInputExplanation"),
    identity: document.getElementById("humanInputIdentity"),
    lifecycleStatus: document.getElementById("humanInputLifecycleStatus"),
    list: document.getElementById("humanInputList"),
    listStatus: document.getElementById("humanInputListStatus"),
    remindButton: document.getElementById("humanInputRemindButton"),
    privacySummary: document.getElementById("humanInputPrivacySummary"),
    prompt: document.getElementById("humanInputPrompt"),
    purpose: document.getElementById("humanInputPurpose"),
    refreshButton: document.getElementById("humanInputRefreshButton"),
    rejectButton: document.getElementById("humanInputRejectButton"),
    responseEditor: document.getElementById("humanInputResponseEditor"),
    responseForm: document.getElementById("humanInputResponseForm"),
    responseSection: document.getElementById("humanInputResponseSection"),
    responseStatus: document.getElementById("humanInputResponseStatus"),
    responseSchema: document.getElementById("humanInputResponseSchema"),
    responseSubmitButton: document.getElementById(
      "humanInputResponseSubmitButton",
    ),
    summary: document.getElementById("humanInputSummary"),
    supersedeButton: document.getElementById("humanInputSupersedeButton"),
    supersedePrompt: document.getElementById("humanInputSupersedePrompt"),
    supersedePurpose: document.getElementById("humanInputSupersedePurpose"),
    supersedeSection: document.getElementById("humanInputSupersedeSection"),
    supersedeStatus: document.getElementById("humanInputSupersedeStatus"),
    rerouteButton: document.getElementById("humanInputRerouteButton"),
    rerouteExpiresAt: document.getElementById("humanInputRerouteExpiresAt"),
    rerouteOptions: document.getElementById("humanInputRerouteOptions"),
    rerouteSection: document.getElementById("humanInputRerouteSection"),
    rerouteStatus: document.getElementById("humanInputRerouteStatus"),
    title: document.getElementById("humanInputTitle"),
  };
  const state = {
    actionInFlight: false,
    active: false,
    candidate: null,
    amendCandidate: null,
    amendOperation: null,
    amendOperationKey: null,
    amendCandidateExpiresAtUtc: null,
    controlNumber: 0,
    editor: null,
    items: [],
    operationNumber: 0,
    operations: new Map(),
    operationFeedback: null,
    lifecycleReplay: null,
    responseDraft: null,
    rerouteCandidate: null,
    rerouteOperation: null,
    rerouteOperationKey: null,
    rerouteOptions: [],
    reroutePreparationGeneration: 0,
    refreshPromise: null,
    selectedPosture: null,
    selectionGeneration: 0,
    amendPreparationGeneration: 0,
  };

  function activate() {
    state.active = true;
    return refresh(state.selectedPosture?.requestId ?? null);
  }

  function sessionRecovered() {
    if (!state.active) return Promise.resolve(false);
    return refresh(state.selectedPosture?.requestId ?? null);
  }

  function notifyChanged() {
    if (!state.active) return Promise.resolve(false);
    return refresh(state.selectedPosture?.requestId ?? null);
  }

  function refresh(requestId = null) {
    if (state.refreshPromise) return state.refreshPromise;
    const operation = refreshCore(requestId);
    const tracked = operation.finally(() => {
      if (state.refreshPromise === tracked) state.refreshPromise = null;
    });
    state.refreshPromise = tracked;
    return tracked;
  }

  async function refreshCore(requestId) {
    setBusy(true);
    try {
      const page = await readPages();
      state.items = page.status === "ready" ? page.items : [];
      if (requestId && state.items.some((item) => item.requestId === requestId))
        state.selectedPosture =
          state.items.find((item) => item.requestId === requestId) ?? null;
      else if (
        !state.selectedPosture ||
        !state.items.some(
          (item) => item.requestId === state.selectedPosture.requestId,
        )
      )
        state.selectedPosture = state.items[0] ?? null;
      renderList(page);
      if (state.selectedPosture) await readSelectedPosture();
      else clearDetail();
      return page;
    } catch (error) {
      state.items = [];
      renderList({ status: statusFromError(error), items: [], cursor: null });
      clearDetail(true);
      return null;
    } finally {
      setBusy(false);
    }
  }

  async function readPages() {
    const items = [];
    const identities = new Set();
    const cursors = new Set();
    let cursor = null;
    for (let pageNumber = 0; pageNumber < maximumPages; pageNumber++) {
      const query = cursor ? `&cursor=${encodeURIComponent(cursor)}` : "";
      const page = projectHumanInputPage(
        await requestJson(
          `/api/human-input?maximumCount=${maximumPageItems}${query}`,
        ),
      );
      if (page.status !== "ready") return page;
      if (items.length + page.items.length > maximumAggregateItems)
        return { status: "invalid", items: [], cursor: null };
      for (const item of page.items) {
        const identity = humanInputIdentity(item);
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

  async function readSelectedPosture() {
    const selected = state.selectedPosture;
    if (!selected) {
      clearDetail();
      return;
    }
    const generation = ++state.selectionGeneration;
    renderDetailLoading(selected);
    let result;
    try {
      result = {
        status: "ready",
        value: await requestJson(
          `/api/human-input/${encodeURIComponent(selected.requestId)}`,
        ),
      };
    } catch (error) {
      result = {
        status: statusFromError(error),
        httpStatus: error?.status ?? null,
      };
    }
    if (generation !== state.selectionGeneration) return;
    const posture =
      result.status === "ready" ? projectHumanInputPosture(result.value) : null;
    const ready = posture && sameIdentity(selected, posture);
    state.selectedPosture = ready ? posture : selected;
    if (ready) {
      state.candidate = null;
      clearLifecycleCandidates();
      if (terminalStatuses.has(posture.status)) state.responseDraft = null;
    } else clearLifecycleCandidates();
    renderDetail(selected, result, ready ? posture : null);
  }

  function selectRequest(requestId) {
    const selected = state.items.find((item) => item.requestId === requestId);
    if (!selected) return Promise.resolve(false);
    clearLifecycleReplay();
    clearActionDrafts();
    clearLifecycleCandidates();
    clearOperationFeedback();
    setSupersedeStatus("", "");
    state.selectedPosture = selected;
    state.candidate = null;
    state.responseDraft = null;
    renderList({ status: "ready", items: state.items, cursor: null });
    return readSelectedPosture().then(() => true);
  }

  async function submitAnswer() {
    const posture = state.selectedPosture;
    if (!posture || state.actionInFlight || !canRespond(posture)) return;
    clearLifecycleReplayForDifferentAction("answer");
    const value = collectResponseValue();
    if (!value) {
      setResponseStatus(
        "Complete the bounded response fields before submitting.",
        "warning",
      );
      return;
    }
    state.responseDraft = value;
    const explanation = boundedInputValue(elements.explanation, 1000);
    const key = `${humanInputIdentity(posture)}:answer:${JSON.stringify(value)}:${explanation}`;
    const operationHandle = getOperation(key);
    if (!operationHandle) {
      state.responseDraft = null;
      setOperationCapacityFeedback();
      return;
    }
    const { operation } = operationHandle;
    const payload = {
      operationId: operation.operationId,
      expectedLifecycleVersion: posture.lifecycleVersion,
      expectedLifecycleStatus: posture.status,
      expectedRequest: requestReference(posture),
      responseId: operation.responseId,
      value,
      explanation: explanation || null,
    };
    await submitOperation(
      "answer",
      payload,
      operationHandle,
      posture.requestId,
    );
  }

  async function submitLifecycle(action) {
    const posture = state.selectedPosture;
    if (!posture || state.actionInFlight || !canLifecycle(posture)) return;
    clearLifecycleReplayForDifferentAction(action);
    const reason = action;
    const key = `${humanInputIdentity(posture)}:${action}`;
    const operationHandle = getOperation(key);
    if (!operationHandle) {
      setOperationCapacityFeedback();
      return;
    }
    const { operation } = operationHandle;
    const payload = {
      operationId: operation.operationId,
      expectedLifecycleVersion: posture.lifecycleVersion,
      expectedLifecycleStatus: posture.status,
      expectedRequest: requestReference(posture),
      reason,
    };
    await submitOperation(action, payload, operationHandle, posture.requestId);
  }

  async function submitRemind() {
    await submitLifecycle("remind");
  }

  async function prepareOrCommitReroute() {
    const posture = state.selectedPosture;
    if (!posture || state.actionInFlight || !canReroute(posture)) return;
    const replay = getLifecycleReplay("reroute", posture);
    if (replay) {
      await submitOperation(
        "reroute",
        replay.payload,
        replayOperationHandle(replay),
        posture.requestId,
      );
      return;
    }
    clearLifecycleReplayForDifferentAction("reroute");
    if (state.rerouteCandidate?.requestId === posture.requestId) {
      const operationHandle =
        state.rerouteOperation ??
        getOperation(
          `${humanInputIdentity(posture)}:reroute:${state.rerouteCandidate.expiresAtUtc}`,
        );
      if (!operationHandle) {
        setRerouteStatus(humanInputOperationCapacityMessage(), "warning");
        return;
      }
      const { operation } = operationHandle;
      await submitOperation(
        "reroute",
        {
          operationId: operation.operationId,
          expectedLifecycleVersion: posture.lifecycleVersion,
          expectedLifecycleStatus: posture.status,
          expectedRequest: requestReference(posture),
          reason: "reroute",
          candidateKey: state.rerouteCandidate.candidateKey,
        },
        operationHandle,
        posture.requestId,
      );
      return;
    }
    const expiresAtUtc = candidateExpiry(elements.rerouteExpiresAt);
    if (!expiresAtUtc) {
      setRerouteStatus(
        "Choose a bounded future expiry before preparing a reroute.",
        "warning",
      );
      return;
    }
    const operationKey = `${humanInputIdentity(posture)}:reroute:${expiresAtUtc}`;
    const exactOperationHandle =
      state.rerouteOperation && state.rerouteOperationKey === operationKey
        ? state.rerouteOperation
        : getOperation(operationKey);
    if (!exactOperationHandle) {
      setRerouteStatus(humanInputOperationCapacityMessage(), "warning");
      return;
    }
    clearLifecycleReplayForDifferentOperation(
      exactOperationHandle.operation.operationId,
    );
    const { operation } = exactOperationHandle;
    const preparationGeneration = state.reroutePreparationGeneration;
    const body = serializeHumanInputRequest({
      operationId: operation.operationId,
      expectedLifecycleVersion: posture.lifecycleVersion,
      expectedLifecycleStatus: posture.status,
      expectedRequest: requestReference(posture),
      candidateExpiresAtUtc: expiresAtUtc,
    });
    if (!body) {
      releaseLocallyRejectedOperation(exactOperationHandle);
      setRerouteStatus(humanInputRequestBodyCapacityMessage(), "warning");
      return;
    }
    state.actionInFlight = true;
    state.rerouteOperation = exactOperationHandle;
    state.rerouteOperationKey = operationKey;
    setBusy(true);
    try {
      const response = await requestJson(
        `/api/human-input/${encodeURIComponent(posture.requestId)}/reroute/prepare`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body,
        },
      );
      const preparation = projectHumanInputReroutePreparation(response);
      if (preparationGeneration !== state.reroutePreparationGeneration) {
        setRerouteStatus(
          "Reroute inputs changed while preparation was in progress. Prepare again.",
          "warning",
        );
        return;
      }
      if (
        preparation.status === "ready" &&
        preparation.requestId === posture.requestId
      ) {
        state.rerouteOptions = preparation.options;
        state.rerouteCandidate = null;
        renderRerouteOptions(preparation.options);
        selectRerouteCandidate(preparation.options[0]?.candidateKey ?? null);
        setRerouteStatus(
          "A server-owned reroute is prepared. Select an opaque option, then commit.",
          "success",
        );
      } else {
        setRerouteStatus(
          humanInputOutcomeMessage(preparation.status),
          "warning",
        );
      }
    } catch (error) {
      setRerouteStatus(
        humanInputOutcomeMessage(
          error?.payload?.status ?? null,
          error?.status ?? null,
        ),
        "error",
      );
    } finally {
      state.actionInFlight = false;
      setBusy(false);
      if (state.selectedPosture?.requestId === posture.requestId)
        configureControls(state.selectedPosture, true);
    }
  }

  async function prepareOrCommitAmend() {
    const posture = state.selectedPosture;
    if (!posture || state.actionInFlight || !canAmend(posture)) return;
    const replay = getLifecycleReplay("amend", posture);
    if (replay) {
      await submitOperation(
        "amend",
        replay.payload,
        replayOperationHandle(replay),
        posture.requestId,
      );
      return;
    }
    clearLifecycleReplayForDifferentAction("amend");
    if (state.amendCandidate?.requestId === posture.requestId) {
      const operationHandle =
        state.amendOperation ??
        (state.amendCandidate.operationId
          ? {
              operation: { operationId: state.amendCandidate.operationId },
              key: null,
              isNew: false,
            }
          : getOperation(
              `${humanInputIdentity(posture)}:amend:${state.amendCandidate.requestExpiresAtUtc}`,
            ));
      if (!operationHandle) {
        setAmendStatus(humanInputOperationCapacityMessage(), "warning");
        return;
      }
      const { operation } = operationHandle;
      await submitOperation(
        "amend",
        {
          operationId: operation.operationId,
          expectedLifecycleVersion: posture.lifecycleVersion,
          expectedLifecycleStatus: posture.status,
          expectedRequest: requestReference(posture),
          reason: "amend",
          candidateKey: state.amendCandidate.candidateKey,
        },
        operationHandle,
        posture.requestId,
      );
      return;
    }
    const purpose = boundedInputValue(
      elements.amendPurpose,
      maximumPurposeCharacters,
    ).trim();
    const prompt = boundedInputValue(
      elements.amendPrompt,
      maximumPromptCharacters,
    ).trim();
    const privacyClass = normalizeHumanInputStatus(
      boundedInputValue(elements.amendPrivacyClass, 32),
    );
    const requestExpiresAtUtc = requestExpiry(elements.amendExpiresAt);
    if (
      !boundedString(purpose, maximumPurposeCharacters) ||
      !boundedString(prompt, maximumPromptCharacters) ||
      !privacyClasses.has(privacyClass) ||
      !requestExpiresAtUtc
    ) {
      setAmendStatus(
        "Provide bounded purpose, prompt, privacy, and future expiry values.",
        "warning",
      );
      return;
    }
    const operationKey = `${humanInputIdentity(posture)}:amend:${purpose}:${prompt}:${privacyClass}:${requestExpiresAtUtc}`;
    const operationHandle =
      state.amendOperation && state.amendOperationKey === operationKey
        ? state.amendOperation
        : getOperation(operationKey);
    if (!operationHandle) {
      setAmendStatus(humanInputOperationCapacityMessage(), "warning");
      return;
    }
    clearLifecycleReplayForDifferentOperation(
      operationHandle.operation.operationId,
    );
    const candidateExpiresAtUtc =
      state.amendOperationKey === operationKey &&
      state.amendCandidateExpiresAtUtc
        ? state.amendCandidateExpiresAtUtc
        : candidateExpiryValue();
    const { operation } = operationHandle;
    const body = serializeHumanInputRequest({
      operationId: operation.operationId,
      expectedLifecycleVersion: posture.lifecycleVersion,
      expectedLifecycleStatus: posture.status,
      expectedRequest: requestReference(posture),
      purpose,
      prompt,
      privacyClass,
      requestExpiresAtUtc,
      candidateExpiresAtUtc,
    });
    if (!body) {
      releaseLocallyRejectedOperation(operationHandle);
      setAmendStatus(humanInputRequestBodyCapacityMessage(), "warning");
      return;
    }
    const preparationGeneration = state.amendPreparationGeneration;
    state.actionInFlight = true;
    state.amendOperation = operationHandle;
    state.amendOperationKey = operationKey;
    state.amendCandidateExpiresAtUtc = candidateExpiresAtUtc;
    setBusy(true);
    try {
      const response = await requestJson(
        `/api/human-input/${encodeURIComponent(posture.requestId)}/amend/prepare`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body,
        },
      );
      const preparation = projectHumanInputCandidatePreparation(response);
      if (preparationGeneration !== state.amendPreparationGeneration) {
        setAmendStatus(
          "Amendment inputs changed while preparation was in progress. Prepare again.",
          "warning",
        );
        return;
      }
      if (
        preparation.status === "ready" &&
        preparation.requestId === posture.requestId
      ) {
        state.amendOperation = operationHandle;
        state.amendCandidate = Object.freeze({
          requestId: posture.requestId,
          candidateKey: preparation.candidateKey,
          expiresAtUtc: preparation.expiresAtUtc,
          operationId: operation.operationId,
          requestExpiresAtUtc,
        });
        setAmendStatus(
          "A server-owned amendment is prepared. Select the button again to commit.",
          "success",
        );
      } else {
        setAmendStatus(humanInputOutcomeMessage(preparation.status), "warning");
      }
    } catch (error) {
      setAmendStatus(
        humanInputOutcomeMessage(
          error?.payload?.status ?? null,
          error?.status ?? null,
        ),
        "error",
      );
    } finally {
      state.actionInFlight = false;
      setBusy(false);
      if (state.selectedPosture?.requestId === posture.requestId)
        configureControls(state.selectedPosture, true);
    }
  }

  async function prepareOrCommitSupersede() {
    const posture = state.selectedPosture;
    if (!posture || state.actionInFlight || !canSupersede(posture)) return;
    clearLifecycleReplayForDifferentAction("supersede");
    if (state.candidate?.requestId === posture.requestId) {
      const operationHandle = state.candidate.operationId
        ? {
            operation: { operationId: state.candidate.operationId },
            key: null,
            isNew: false,
          }
        : getOperation(
            `${humanInputIdentity(posture)}:supersede:${state.candidate.candidateKey}`,
          );
      if (!operationHandle) {
        setSupersedeStatus(humanInputOperationCapacityMessage(), "warning");
        return;
      }
      const { operation } = operationHandle;
      await submitOperation(
        "supersede",
        {
          operationId: operation.operationId,
          expectedLifecycleVersion: posture.lifecycleVersion,
          expectedLifecycleStatus: posture.status,
          expectedRequest: requestReference(posture),
          reason: "supersede",
          candidateKey: state.candidate.candidateKey,
        },
        operationHandle,
        posture.requestId,
      );
      return;
    }
    const purpose = boundedInputValue(
      elements.supersedePurpose,
      maximumPurposeCharacters,
    ).trim();
    const prompt = boundedInputValue(
      elements.supersedePrompt,
      maximumPromptCharacters,
    ).trim();
    if (!purpose || !prompt) {
      setSupersedeStatus(
        "Enter a bounded successor purpose and prompt first.",
        "warning",
      );
      return;
    }
    const operationHandle = getOperation(
      `${humanInputIdentity(posture)}:prepare:${purpose}:${prompt}`,
    );
    if (!operationHandle) {
      setSupersedeStatus(humanInputOperationCapacityMessage(), "warning");
      return;
    }
    const { operation } = operationHandle;
    const payload = {
      operationId: operation.operationId,
      expectedLifecycleVersion: posture.lifecycleVersion,
      expectedLifecycleStatus: posture.status,
      expectedRequest: requestReference(posture),
      successor: {
        purpose,
        prompt,
        responseSchema: posture.presentation.responseSchema,
        privacyClass: posture.presentation.privacyClass,
        expiresAtUtc: new Date(Date.now() + 60 * 60 * 1000).toISOString(),
        responsePolicy: { kind: "preserve-canonical" },
      },
    };
    const body = serializeHumanInputRequest(payload);
    if (!body) {
      releaseLocallyRejectedOperation(operationHandle);
      setSupersedeStatus(humanInputRequestBodyCapacityMessage(), "warning");
      return;
    }
    state.actionInFlight = true;
    setBusy(true);
    try {
      const response = await requestJson(
        `/api/human-input/${encodeURIComponent(posture.requestId)}/supersede/prepare`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body,
        },
      );
      if (
        normalizeHumanInputStatus(response?.status) === "ready" &&
        response.candidateKey
      ) {
        state.candidate = Object.freeze({
          requestId: posture.requestId,
          candidateKey: response.candidateKey,
          expiresAtUtc: response.expiresAtUtc ?? null,
          operationId: operation.operationId,
        });
        configureControls(posture, true);
        setSupersedeStatus(
          "Successor prepared in server-owned memory. Select the button again to commit.",
          "success",
        );
      } else {
        setSupersedeStatus(
          humanInputOutcomeMessage(response?.status),
          "warning",
        );
      }
    } catch (error) {
      setSupersedeStatus(
        humanInputOutcomeMessage(
          error?.payload?.status ?? null,
          error?.status ?? null,
        ),
        "error",
      );
    } finally {
      state.actionInFlight = false;
      setBusy(false);
      if (state.selectedPosture?.requestId === posture.requestId)
        configureControls(state.selectedPosture, true);
    }
  }

  async function submitOperation(action, payload, operationHandle, requestId) {
    const body = serializeHumanInputRequest(payload);
    if (!body) {
      releaseLocallyRejectedOperation(operationHandle);
      setOperationFeedback(humanInputRequestBodyCapacityMessage(), "warning");
      return;
    }
    state.actionInFlight = true;
    setBusy(true);
    try {
      const response = await requestJson(
        `/api/human-input/${encodeURIComponent(requestId)}/${action}`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body,
        },
      );
      setOperationFeedback(
        humanInputOutcomeMessage(response?.status),
        ["committed", "replayed"].includes(
          normalizeHumanInputStatus(response?.status),
        )
          ? "success"
          : "warning",
      );
      clearLifecycleReplay(payload.operationId);
      await refresh(requestId);
    } catch (error) {
      if (
        isTransportFailure(error) &&
        (action === "reroute" || action === "amend")
      )
        retainLifecycleReplay(action, payload, requestId);
      else clearLifecycleReplay(payload.operationId);
      setOperationFeedback(
        humanInputOutcomeMessage(
          error?.payload?.status ?? null,
          error?.status ?? null,
        ),
        "error",
      );
      await refresh(requestId);
    } finally {
      state.actionInFlight = false;
      setBusy(false);
      if (
        operationHandle?.operation?.operationId &&
        state.selectedPosture?.requestId === requestId
      )
        configureControls(state.selectedPosture, true);
    }
  }

  function getOperation(key) {
    let operation = state.operations.get(key);
    if (operation) return Object.freeze({ operation, key, isNew: false });
    if (state.operations.size >= maximumOperationEntries) return null;
    const operationSeed = `${randomUUID(hostWindow) ?? "operation"}-${state.operationNumber++}`;
    const responseSeed = `${randomUUID(hostWindow) ?? "response"}-${state.operationNumber++}`;
    const operationId = humanInputOperationIdentity(operationSeed);
    operation = Object.freeze({
      operationId,
      responseId: humanInputOperationIdentity(responseSeed),
    });
    state.operations.set(key, operation);
    return Object.freeze({ operation, key, isNew: true });
  }

  function releaseLocallyRejectedOperation(operationHandle) {
    if (!operationHandle?.isNew || !operationHandle.key) return;
    if (state.operations.get(operationHandle.key) !== operationHandle.operation)
      return;
    state.operations.delete(operationHandle.key);
    state.responseDraft = null;
  }

  function retainLifecycleReplay(action, payload, requestId) {
    if (
      !payload?.operationId ||
      !payload?.candidateKey ||
      !payload?.expectedRequest ||
      !["reroute", "amend"].includes(action)
    )
      return;
    const expiresAtUtc =
      action === "reroute"
        ? state.rerouteCandidate?.expiresAtUtc
        : state.amendCandidate?.expiresAtUtc;
    state.lifecycleReplay = Object.freeze({
      action,
      operationId: payload.operationId,
      requestId,
      expectedLifecycleVersion: payload.expectedLifecycleVersion,
      expectedLifecycleStatus: payload.expectedLifecycleStatus,
      expectedRequest: Object.freeze({ ...payload.expectedRequest }),
      candidateKey: payload.candidateKey,
      expiresAtUtc: expiresAtUtc ?? null,
      payload: Object.freeze({
        operationId: payload.operationId,
        expectedLifecycleVersion: payload.expectedLifecycleVersion,
        expectedLifecycleStatus: payload.expectedLifecycleStatus,
        expectedRequest: Object.freeze({ ...payload.expectedRequest }),
        reason: payload.reason,
        candidateKey: payload.candidateKey,
      }),
    });
  }

  function getLifecycleReplay(action, posture) {
    const replay = state.lifecycleReplay;
    if (!replay) return null;
    if (replay.action !== action || replay.requestId !== posture.requestId)
      return null;
    if (!replay.expiresAtUtc || Date.parse(replay.expiresAtUtc) <= Date.now()) {
      clearLifecycleReplay();
      return null;
    }
    return replay;
  }

  function replayOperationHandle(replay) {
    return {
      operation: { operationId: replay.operationId },
      key: null,
      isNew: false,
    };
  }

  function isTransportFailure(error) {
    return error?.status === undefined || error?.status === null;
  }

  function clearLifecycleReplay(operationId = null) {
    if (
      operationId !== null &&
      state.lifecycleReplay?.operationId !== operationId
    )
      return;
    state.lifecycleReplay = null;
  }

  function clearLifecycleReplayForDifferentOperation(operationId) {
    if (state.lifecycleReplay?.operationId !== operationId)
      clearLifecycleReplay();
  }

  function clearLifecycleReplayForDifferentAction(action) {
    if (state.lifecycleReplay?.action !== action) clearLifecycleReplay();
  }

  function renderList(page) {
    if (!elements.list || !elements.listStatus) return;
    elements.list.replaceChildren();
    if (page.status !== "ready") {
      elements.listStatus.textContent = humanInputReadMessage(page.status);
      elements.listStatus.classList.add("error");
      return;
    }
    elements.listStatus.classList.remove("error");
    elements.listStatus.textContent = page.items.length
      ? `${page.items.length} durable data request${page.items.length === 1 ? "" : "s"} shown.`
      : "No retained Human Input requests were found.";
    for (const posture of page.items) {
      const item = document.createElement("button");
      item.type = "button";
      item.className = "human-input-list-item";
      item.dataset.requestId = posture.requestId;
      item.dataset.testid = "human-input-item";
      item.setAttribute("role", "option");
      item.setAttribute(
        "aria-selected",
        posture.requestId === state.selectedPosture?.requestId
          ? "true"
          : "false",
      );
      item.addEventListener(
        "click",
        () => void selectRequest(posture.requestId),
      );
      const title = document.createElement("strong");
      title.textContent = boundedHumanInputText(
        posture.presentation.purpose,
        120,
      );
      const stateLine = document.createElement("span");
      stateLine.textContent = `${formatToken(posture.status)} · version ${posture.lifecycleVersion}`;
      const request = document.createElement("small");
      request.textContent = boundedHumanInputText(posture.requestId, 120);
      item.append(title, stateLine, request);
      elements.list.append(item);
    }
  }

  function renderDetailLoading(posture) {
    if (!elements.empty || !elements.detailPanel) return;
    elements.empty.hidden = true;
    elements.detailPanel.hidden = false;
    elements.detailStatus.textContent =
      "Rereading canonical Human Input state…";
    elements.detailStatus.className = "human-input-status";
    elements.title.textContent = "Request detail";
    elements.purpose.textContent = boundedHumanInputText(
      posture.presentation.purpose,
      120,
    );
    elements.identity.textContent = `Request ${boundedHumanInputText(posture.requestId, 120)}`;
    clearCollection(elements.summary);
    clearCollection(elements.privacySummary);
    clearCollection(elements.responseEditor);
    elements.prompt.textContent = "";
    elements.responseSchema.textContent = "";
    clearLifecycleCandidates();
    renderOperationFeedback();
    configureControls(posture, false);
  }

  function renderDetail(selected, result, posture) {
    elements.empty.hidden = true;
    elements.detailPanel.hidden = false;
    const ready = result.status === "ready" && posture !== null;
    elements.title.textContent = ready
      ? `Request ${boundedHumanInputText(posture.requestId, 120)}`
      : "Request detail";
    elements.purpose.textContent = ready
      ? boundedHumanInputText(posture.presentation.purpose, 120)
      : "Human Input";
    elements.identity.textContent = `Request ${boundedHumanInputText(selected.requestId, 120)}`;
    elements.detailStatus.textContent = ready
      ? "Canonical state reread. Response data remains in this form only until submission."
      : humanInputReadMessage(result.status, result.httpStatus);
    elements.detailStatus.className = ready
      ? "human-input-status"
      : "human-input-status error";
    if (!ready) {
      configureControls(selected, false);
      renderOperationFeedback();
      return;
    }
    renderSummary(posture);
    elements.prompt.textContent = boundedHumanInputText(
      posture.presentation.prompt,
      maximumPromptCharacters,
    );
    elements.responseSchema.textContent = schemaDescription(
      posture.presentation.responseSchema,
    );
    renderPrivacy(posture);
    renderResponseEditor(posture.presentation.responseSchema);
    if (elements.supersedePurpose && !elements.supersedePurpose.value)
      elements.supersedePurpose.value = boundedHumanInputText(
        posture.presentation.purpose,
        maximumPurposeCharacters,
      );
    if (elements.supersedePrompt && !elements.supersedePrompt.value)
      elements.supersedePrompt.value = boundedHumanInputText(
        posture.presentation.prompt,
        maximumPromptCharacters,
      );
    if (elements.amendPurpose && !elements.amendPurpose.value)
      elements.amendPurpose.value = boundedHumanInputText(
        posture.presentation.purpose,
        maximumPurposeCharacters,
      );
    if (elements.amendPrompt && !elements.amendPrompt.value)
      elements.amendPrompt.value = boundedHumanInputText(
        posture.presentation.prompt,
        maximumPromptCharacters,
      );
    if (elements.amendPrivacyClass) {
      renderAmendPrivacyOptions(posture.presentation.privacyClass);
      elements.amendPrivacyClass.value = posture.presentation.privacyClass;
    }
    if (elements.amendExpiresAt && !elements.amendExpiresAt.value)
      elements.amendExpiresAt.value = inputDateValue(
        posture.presentation.timing.expiresAtUtc,
      );
    if (elements.rerouteExpiresAt && !elements.rerouteExpiresAt.value)
      elements.rerouteExpiresAt.value = inputDateValue(candidateExpiryValue());
    configureControls(posture, true);
    renderOperationFeedback();
  }

  function renderSummary(posture) {
    clearCollection(elements.summary);
    for (const [label, value] of [
      ["Lifecycle", formatToken(posture.status)],
      ["Lifecycle version", posture.lifecycleVersion],
      [
        "Response window",
        `${formatTimestamp(posture.presentation.timing.requestedAtUtc)} – ${formatTimestamp(posture.presentation.timing.expiresAtUtc)}`,
      ],
      ["Responses retained", posture.acceptedResponseCount],
      ["Supersedes request", posture.supersedesRequestId ?? "None"],
      ["Superseded by request", posture.supersededByRequestId ?? "None"],
      ["Latest conflict", latestConflictSummary(posture.latestConflict)],
      ["Updated", formatTimestamp(posture.updatedAtUtc)],
    ])
      appendDefinition(elements.summary, label, value);
    elements.lifecycleStatus.textContent = formatToken(posture.status);
  }

  function renderPrivacy(posture) {
    clearCollection(elements.privacySummary);
    for (const [label, value] of [
      ["Privacy class", formatToken(posture.presentation.privacyClass)],
      ["Eligible respondents", posture.presentation.eligibleRespondentCount],
      ["Response policy", formatToken(posture.presentation.responsePolicyKind)],
      [
        "Continuation visibility",
        formatToken(posture.presentation.continuationPolicyKind),
      ],
    ])
      appendDefinition(elements.privacySummary, label, value);
  }

  function renderResponseEditor(schema) {
    clearCollection(elements.responseEditor);
    state.editor = null;
    if (!schema) return;
    if (schema.kind === "text" || schema.kind === "reference") {
      const field = createLabeledInput(
        schema.kind === "text" ? "Response" : "Opaque reference",
        schema.kind === "text" ? "textarea" : "input",
        schema.kind === "text"
          ? schema.maxTextCharacters
          : schema.referencePolicy.maxReferenceCharacters,
      );
      elements.responseEditor.append(field.wrapper);
      state.editor = { kind: schema.kind, control: field.control, schema };
      applyResponseDraft();
      return;
    }
    if (schema.kind === "choice") {
      const field = createLabeledInput("Select one response", "select", null);
      appendOptions(field.control, schema.choices);
      elements.responseEditor.append(field.wrapper);
      state.editor = { kind: schema.kind, control: field.control, schema };
      applyResponseDraft();
      return;
    }
    if (schema.kind === "confirmation") {
      const wrapper = document.createElement("label");
      wrapper.className = "human-input-confirmation-field";
      const control = document.createElement("input");
      control.type = "checkbox";
      const text = document.createElement("span");
      text.textContent = "I confirm this data selection.";
      wrapper.append(control, text);
      elements.responseEditor.append(wrapper);
      state.editor = { kind: schema.kind, control, schema };
      applyResponseDraft();
      return;
    }
    if (schema.kind === "structured") {
      const controls = [];
      for (const fieldSchema of schema.structuredFields) {
        const field = createLabeledInput(
          `${fieldSchema.fieldId}${fieldSchema.required ? " (required)" : ""}`,
          fieldSchema.kind === "text" ? "textarea" : "select",
          fieldSchema.kind === "text" ? fieldSchema.maxTextCharacters : null,
        );
        if (fieldSchema.kind === "choice")
          appendOptions(field.control, fieldSchema.choices);
        elements.responseEditor.append(field.wrapper);
        controls.push({ schema: fieldSchema, control: field.control });
      }
      state.editor = { kind: schema.kind, controls, schema };
      applyResponseDraft();
    }
  }

  function renderRerouteOptions(options) {
    if (!elements.rerouteOptions) return;
    elements.rerouteOptions.replaceChildren();
    for (const option of options) {
      const choice = document.createElement("option");
      choice.value = option.candidateKey;
      choice.textContent = `${boundedHumanInputText(option.label, 240)} · ${option.eligibleRespondentCount} eligible · expires ${formatTimestamp(option.expiresAtUtc)}`;
      elements.rerouteOptions.append(choice);
    }
    elements.rerouteOptions.disabled = options.length === 0;
  }

  function renderAmendPrivacyOptions(currentPrivacyClass) {
    if (!elements.amendPrivacyClass) return;
    const options =
      currentPrivacyClass === "sensitive"
        ? ["sensitive"]
        : ["private", "sensitive"];
    elements.amendPrivacyClass.replaceChildren();
    for (const value of options) {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = formatToken(value);
      elements.amendPrivacyClass.append(option);
    }
  }

  function selectRerouteCandidate(candidateKey) {
    const option = state.rerouteOptions.find(
      (item) => item.candidateKey === candidateKey,
    );
    if (!option) {
      state.rerouteCandidate = null;
      if (elements.rerouteOptions) elements.rerouteOptions.value = "";
      return;
    }
    if (elements.rerouteOptions) elements.rerouteOptions.value = candidateKey;
    if (
      state.lifecycleReplay?.action === "reroute" &&
      state.lifecycleReplay.candidateKey !== candidateKey
    )
      clearLifecycleReplay();
    state.rerouteCandidate = Object.freeze({
      requestId: state.selectedPosture?.requestId ?? "",
      candidateKey: option.candidateKey,
      expiresAtUtc: option.expiresAtUtc,
    });
  }

  function configureControls(posture, detailReady) {
    const pending = posture.status === "pending";
    const canAnswer = detailReady && pending && state.editor !== null;
    if (elements.responseSubmitButton)
      elements.responseSubmitButton.disabled =
        !canAnswer || state.actionInFlight;
    if (elements.explanation)
      elements.explanation.disabled = !canAnswer || state.actionInFlight;
    if (elements.rejectButton)
      elements.rejectButton.disabled =
        !detailReady || !pending || state.actionInFlight;
    if (elements.cancelButton)
      elements.cancelButton.disabled =
        !detailReady || !pending || state.actionInFlight;
    const lifecycleAllowed = detailReady && pending && !state.actionInFlight;
    if (elements.remindButton)
      elements.remindButton.disabled = !lifecycleAllowed;
    const rerouteAllowed = detailReady && pending;
    if (elements.rerouteSection)
      elements.rerouteSection.hidden = !rerouteAllowed;
    if (elements.rerouteButton) {
      elements.rerouteButton.disabled =
        !rerouteAllowed ||
        state.actionInFlight ||
        (state.rerouteOptions.length > 0 && !state.rerouteCandidate);
      elements.rerouteButton.textContent = getLifecycleReplay(
        "reroute",
        posture,
      )
        ? "Retry reroute"
        : state.rerouteCandidate
          ? "Commit reroute"
          : "Prepare reroute";
    }
    if (elements.rerouteOptions && state.rerouteOptions.length === 0)
      elements.rerouteOptions.disabled = true;
    const amendAllowed = detailReady && pending;
    if (elements.amendSection) elements.amendSection.hidden = !amendAllowed;
    if (elements.amendButton) {
      elements.amendButton.disabled = !amendAllowed || state.actionInFlight;
      elements.amendButton.textContent = getLifecycleReplay("amend", posture)
        ? "Retry amendment"
        : state.amendCandidate
          ? "Commit amendment"
          : "Prepare amendment";
    }
    const supersedeAllowed = detailReady && pending && canSupersede(posture);
    if (elements.supersedeSection)
      elements.supersedeSection.hidden = !supersedeAllowed;
    if (elements.supersedeButton) {
      elements.supersedeButton.disabled =
        !supersedeAllowed || state.actionInFlight;
      elements.supersedeButton.textContent =
        state.candidate?.requestId === posture.requestId
          ? "Commit successor"
          : "Prepare successor";
    }
    if (elements.responseSection)
      elements.responseSection.hidden = !detailReady;
  }

  function clearDetail(preserveOperationFeedback = false) {
    state.selectedPosture = null;
    state.editor = null;
    state.candidate = null;
    clearLifecycleCandidates();
    state.responseDraft = null;
    state.selectionGeneration++;
    clearCollection(elements.summary);
    clearCollection(elements.privacySummary);
    clearCollection(elements.responseEditor);
    if (elements.prompt) elements.prompt.textContent = "";
    if (elements.responseSchema) elements.responseSchema.textContent = "";
    if (elements.purpose) elements.purpose.textContent = "Human Input";
    if (elements.title) elements.title.textContent = "Request detail";
    if (elements.identity) elements.identity.textContent = "";
    if (elements.lifecycleStatus) elements.lifecycleStatus.textContent = "";
    clearActionDrafts();
    clearLifecycleReplay();
    if (!preserveOperationFeedback) clearOperationFeedback();
    setSupersedeStatus("", "");
    if (elements.empty) elements.empty.hidden = false;
    if (elements.detailPanel) elements.detailPanel.hidden = true;
    renderOperationFeedback();
  }

  function setBusy(busy) {
    const blocked = busy || state.actionInFlight;
    if (elements.refreshButton) elements.refreshButton.disabled = busy;
    if (elements.detailRefreshButton)
      elements.detailRefreshButton.disabled = busy;
    if (elements.responseSubmitButton && state.selectedPosture)
      elements.responseSubmitButton.disabled =
        blocked || !canRespond(state.selectedPosture);
    if (elements.rejectButton && state.selectedPosture)
      elements.rejectButton.disabled =
        blocked || !canLifecycle(state.selectedPosture);
    if (elements.cancelButton && state.selectedPosture)
      elements.cancelButton.disabled =
        blocked || !canLifecycle(state.selectedPosture);
    if (elements.remindButton && state.selectedPosture)
      elements.remindButton.disabled =
        blocked || !canLifecycle(state.selectedPosture);
    if (elements.supersedeButton && state.selectedPosture)
      elements.supersedeButton.disabled =
        blocked || !canSupersede(state.selectedPosture);
    if (elements.rerouteButton && state.selectedPosture)
      elements.rerouteButton.disabled =
        blocked ||
        !canReroute(state.selectedPosture) ||
        (state.rerouteOptions.length > 0 && !state.rerouteCandidate);
    if (elements.amendButton && state.selectedPosture)
      elements.amendButton.disabled =
        blocked || !canAmend(state.selectedPosture);
  }

  function setResponseStatus(message, tone) {
    if (!elements.responseStatus) return;
    elements.responseStatus.textContent = boundedHumanInputText(message);
    elements.responseStatus.className = tone
      ? `human-input-response-status ${tone}`
      : "human-input-response-status";
  }

  function setOperationFeedback(message, tone) {
    state.operationFeedback = Object.freeze({
      message: boundedHumanInputText(message),
      tone: tone || "",
    });
    renderOperationFeedback();
  }

  function setOperationCapacityFeedback() {
    setOperationFeedback(humanInputOperationCapacityMessage(), "warning");
  }

  function renderOperationFeedback() {
    const feedback = state.operationFeedback;
    setResponseStatus(feedback?.message ?? "", feedback?.tone ?? "");
  }

  function clearOperationFeedback() {
    state.operationFeedback = null;
  }

  function clearActionDrafts() {
    if (elements.explanation) elements.explanation.value = "";
    if (elements.amendExpiresAt) elements.amendExpiresAt.value = "";
    if (elements.amendPrivacyClass) elements.amendPrivacyClass.value = "";
    if (elements.amendPurpose) elements.amendPurpose.value = "";
    if (elements.amendPrompt) elements.amendPrompt.value = "";
    if (elements.rerouteExpiresAt) elements.rerouteExpiresAt.value = "";
    if (elements.supersedePurpose) elements.supersedePurpose.value = "";
    if (elements.supersedePrompt) elements.supersedePrompt.value = "";
  }

  function clearLifecycleCandidates() {
    state.amendPreparationGeneration++;
    state.reroutePreparationGeneration++;
    state.amendCandidate = null;
    state.amendOperation = null;
    state.amendOperationKey = null;
    state.amendCandidateExpiresAtUtc = null;
    state.rerouteCandidate = null;
    state.rerouteOperation = null;
    state.rerouteOperationKey = null;
    state.rerouteOptions = [];
    renderRerouteOptions([]);
    setRerouteStatus("", "");
    setAmendStatus("", "");
  }

  function setSupersedeStatus(message, tone) {
    if (!elements.supersedeStatus) return;
    elements.supersedeStatus.textContent = boundedHumanInputText(message);
    elements.supersedeStatus.className = tone
      ? `human-input-response-status ${tone}`
      : "human-input-response-status";
  }

  function setRerouteStatus(message, tone) {
    if (!elements.rerouteStatus) return;
    elements.rerouteStatus.textContent = boundedHumanInputText(message);
    elements.rerouteStatus.className = tone
      ? `human-input-response-status ${tone}`
      : "human-input-response-status";
  }

  function setAmendStatus(message, tone) {
    if (!elements.amendStatus) return;
    elements.amendStatus.textContent = boundedHumanInputText(message);
    elements.amendStatus.className = tone
      ? `human-input-response-status ${tone}`
      : "human-input-response-status";
  }

  function invalidateReroutePreparation() {
    state.reroutePreparationGeneration++;
    const hadPreparation =
      state.rerouteCandidate !== null ||
      state.rerouteOptions.length > 0 ||
      state.rerouteOperation !== null;
    state.rerouteCandidate = null;
    state.rerouteOperation = null;
    state.rerouteOperationKey = null;
    state.rerouteOptions = [];
    clearLifecycleReplay();
    renderRerouteOptions([]);
    if (hadPreparation) {
      setRerouteStatus(
        "Reroute inputs changed. Prepare a new server-owned option before committing.",
        "warning",
      );
      if (state.selectedPosture) configureControls(state.selectedPosture, true);
    }
  }

  function invalidateAmendPreparation() {
    state.amendPreparationGeneration++;
    const hadPreparation =
      state.amendCandidate !== null || state.amendOperation !== null;
    state.amendCandidate = null;
    state.amendOperation = null;
    state.amendOperationKey = null;
    state.amendCandidateExpiresAtUtc = null;
    clearLifecycleReplay();
    if (hadPreparation) {
      setAmendStatus(
        "Amendment inputs changed. Prepare a new server-owned candidate before committing.",
        "warning",
      );
      if (state.selectedPosture) configureControls(state.selectedPosture, true);
    }
  }

  function bind() {
    elements.refreshButton?.addEventListener(
      "click",
      () => void refresh(state.selectedPosture?.requestId ?? null),
    );
    elements.detailRefreshButton?.addEventListener(
      "click",
      () => void refresh(state.selectedPosture?.requestId ?? null),
    );
    elements.responseForm?.addEventListener("submit", (event) => {
      event.preventDefault?.();
      void submitAnswer();
    });
    elements.responseSubmitButton?.addEventListener(
      "click",
      () => void submitAnswer(),
    );
    elements.rejectButton?.addEventListener(
      "click",
      () => void submitLifecycle("reject"),
    );
    elements.cancelButton?.addEventListener(
      "click",
      () => void submitLifecycle("cancel"),
    );
    elements.remindButton?.addEventListener("click", () => void submitRemind());
    elements.rerouteButton?.addEventListener(
      "click",
      () => void prepareOrCommitReroute(),
    );
    elements.rerouteOptions?.addEventListener("change", () => {
      selectRerouteCandidate(elements.rerouteOptions.value);
      if (state.selectedPosture) configureControls(state.selectedPosture, true);
    });
    for (const eventName of ["input", "change"]) {
      elements.rerouteExpiresAt?.addEventListener(
        eventName,
        invalidateReroutePreparation,
      );
      elements.amendPurpose?.addEventListener(
        eventName,
        invalidateAmendPreparation,
      );
      elements.amendPrompt?.addEventListener(
        eventName,
        invalidateAmendPreparation,
      );
      elements.amendPrivacyClass?.addEventListener(
        eventName,
        invalidateAmendPreparation,
      );
      elements.amendExpiresAt?.addEventListener(
        eventName,
        invalidateAmendPreparation,
      );
    }
    elements.amendButton?.addEventListener(
      "click",
      () => void prepareOrCommitAmend(),
    );
    elements.supersedeButton?.addEventListener(
      "click",
      () => void prepareOrCommitSupersede(),
    );
  }

  bind();
  renderList({ status: "ready", items: [], cursor: null });

  return Object.freeze({
    activate,
    notifyChanged,
    refresh,
    selectRequest,
    sessionRecovered,
  });

  function canRespond(posture) {
    return posture.status === "pending" && state.editor !== null;
  }

  function canLifecycle(posture) {
    return posture.status === "pending";
  }

  function canSupersede(posture) {
    return (
      posture.status === "pending" &&
      responsePolicies.has(posture.presentation.responsePolicyKind)
    );
  }

  function canReroute(posture) {
    return posture.status === "pending";
  }

  function canAmend(posture) {
    return posture.status === "pending";
  }

  function candidateExpiry(element) {
    if (!element) return candidateExpiryValue();
    const value = String(element.value ?? "").trim();
    if (!value) return null;
    const milliseconds = Date.parse(value);
    if (!Number.isFinite(milliseconds)) return null;
    const lifetime = milliseconds - Date.now();
    if (
      lifetime < minimumRequestLifetimeMilliseconds ||
      lifetime > maximumCandidateLifetimeMilliseconds
    )
      return null;
    return new Date(milliseconds).toISOString();
  }

  function candidateExpiryValue() {
    return new Date(Date.now() + 10 * 60 * 1000).toISOString();
  }

  function requestExpiry(element) {
    if (!element) return null;
    const value = String(element.value ?? "").trim();
    if (!value) return null;
    const milliseconds = Date.parse(value);
    if (!Number.isFinite(milliseconds)) return null;
    const lifetime = milliseconds - Date.now();
    if (
      lifetime < minimumRequestLifetimeMilliseconds ||
      lifetime > maximumRequestLifetimeMilliseconds
    )
      return null;
    return new Date(milliseconds).toISOString();
  }

  function inputDateValue(value) {
    const date = new Date(value ?? "");
    if (Number.isNaN(date.valueOf())) return "";
    const pad = (part) => String(part).padStart(2, "0");
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
  }

  function collectResponseValue() {
    if (!state.editor) return null;
    if (state.editor.kind === "text") {
      const text = boundedInputValue(
        state.editor.control,
        state.editor.schema.maxTextCharacters,
      );
      return text.length > 0 ? { kind: "text", text } : null;
    }
    if (state.editor.kind === "reference") {
      const reference = boundedInputValue(
        state.editor.control,
        state.editor.schema.referencePolicy.maxReferenceCharacters,
      );
      return reference.length > 0
        ? {
            kind: "reference",
            reference: {
              kind: state.editor.schema.referencePolicy.kind,
              value: reference,
            },
          }
        : null;
    }
    if (state.editor.kind === "choice") {
      const choiceId = boundedInputValue(state.editor.control, 120);
      return choiceId ? { kind: "choice", choiceId } : null;
    }
    if (state.editor.kind === "confirmation")
      return {
        kind: "confirmation",
        confirmation: state.editor.control.checked === true,
      };
    if (state.editor.kind === "structured") {
      const fields = [];
      for (const item of state.editor.controls) {
        const value = boundedInputValue(
          item.control,
          item.schema.maxTextCharacters ?? maximumResponseCharacters,
        );
        if (item.schema.required && !value) return null;
        if (!value) continue;
        const field = { fieldId: item.schema.fieldId };
        if (item.schema.kind === "text") field.text = value;
        else field.choiceId = value;
        fields.push(field);
      }
      return { kind: "structured", structuredFields: fields };
    }
    return null;
  }

  function applyResponseDraft() {
    const draft = state.responseDraft;
    if (!draft || !state.editor) return;
    if (state.editor.kind === "text") {
      state.editor.control.value = draft.text ?? "";
      return;
    }
    if (state.editor.kind === "choice") {
      state.editor.control.value = draft.choiceId ?? "";
      return;
    }
    if (state.editor.kind === "reference") {
      state.editor.control.value = draft.reference?.value ?? "";
      return;
    }
    if (state.editor.kind === "confirmation") {
      state.editor.control.checked = draft.confirmation === true;
      return;
    }
    const fields = new Map(
      (draft.structuredFields ?? []).map((field) => [field.fieldId, field]),
    );
    for (const item of state.editor.controls) {
      const field = fields.get(item.schema.fieldId);
      if (field) item.control.value = field.text ?? field.choiceId ?? "";
    }
  }

  function createLabeledInput(labelText, tagName, maximum) {
    const wrapper = document.createElement("label");
    wrapper.className = "human-input-field";
    const label = document.createElement("span");
    label.textContent = boundedHumanInputText(labelText, 120);
    const control = document.createElement(tagName);
    state.controlNumber++;
    control.id = `human-input-response-${state.selectionGeneration}-${state.controlNumber}`;
    control.setAttribute("autocomplete", "off");
    if (maximum) control.setAttribute("maxlength", String(maximum));
    control.setAttribute("aria-label", boundedHumanInputText(labelText, 120));
    wrapper.append(label, control);
    return { wrapper, control };
  }

  function appendOptions(select, choices) {
    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = "Select one…";
    select.append(placeholder);
    for (const choice of choices) {
      const option = document.createElement("option");
      option.value = choice.choiceId;
      option.textContent = choice.displayText;
      select.append(option);
    }
  }
}

function projectRequestReference(value) {
  if (!value || typeof value !== "object") return null;
  if (
    value.schemaVersion !== 1 ||
    !isIdentifier(value.requestId) ||
    !isIdentifier(value.requestVersionId) ||
    !sha256Pattern.test(value.requestHash ?? "")
  )
    return null;
  return Object.freeze({
    schemaVersion: 1,
    requestId: value.requestId,
    requestVersionId: value.requestVersionId,
    requestHash: value.requestHash,
  });
}

function projectResponseSchema(value) {
  if (!value || typeof value !== "object") return null;
  const kind = normalizeHumanInputStatus(value.kind);
  if (!responseKinds.has(kind)) return null;
  if (kind === "text") {
    return boundedSchemaNumber(value.maxTextCharacters)
      ? Object.freeze({ kind, maxTextCharacters: value.maxTextCharacters })
      : null;
  }
  if (kind === "choice") {
    const choices = projectChoices(value.choices);
    return choices ? Object.freeze({ kind, choices }) : null;
  }
  if (kind === "confirmation") return Object.freeze({ kind });
  if (kind === "structured") {
    if (
      !Array.isArray(value.structuredFields) ||
      value.structuredFields.length < 1 ||
      value.structuredFields.length > maximumStructuredFields
    )
      return null;
    const fields = value.structuredFields.map(projectStructuredField);
    return fields.some((field) => field === null) ||
      new Set(fields.map((field) => field?.fieldId)).size !== fields.length
      ? null
      : Object.freeze({ kind, structuredFields: Object.freeze(fields) });
  }
  const referencePolicy = value.referencePolicy;
  if (!referencePolicy || typeof referencePolicy !== "object") return null;
  const referenceKind = normalizeHumanInputStatus(referencePolicy.kind);
  if (
    !referenceKinds.has(referenceKind) ||
    !boundedReferenceNumber(referencePolicy.maxReferenceCharacters)
  )
    return null;
  return Object.freeze({
    kind,
    referencePolicy: Object.freeze({
      kind: referenceKind,
      maxReferenceCharacters: referencePolicy.maxReferenceCharacters,
    }),
  });
}

function projectChoices(value) {
  if (
    !Array.isArray(value) ||
    value.length < 2 ||
    value.length > maximumChoices
  )
    return null;
  const choices = value.map((choice) => {
    if (
      !choice ||
      typeof choice !== "object" ||
      !isIdentifier(choice.choiceId) ||
      !boundedString(choice.displayText, 240)
    )
      return null;
    return Object.freeze({
      choiceId: choice.choiceId,
      displayText: choice.displayText,
    });
  });
  return choices.some((choice) => choice === null) ||
    new Set(choices.map((choice) => choice?.choiceId)).size !== choices.length
    ? null
    : Object.freeze(choices);
}

function projectStructuredField(value) {
  if (!value || typeof value !== "object") return null;
  const kind = normalizeHumanInputStatus(value.kind);
  if (
    !structuredFieldKinds.has(kind) ||
    !isIdentifier(value.fieldId) ||
    typeof value.required !== "boolean"
  )
    return null;
  if (kind === "text") {
    return boundedSchemaNumber(value.maxTextCharacters)
      ? Object.freeze({
          fieldId: value.fieldId,
          kind,
          required: value.required,
          maxTextCharacters: value.maxTextCharacters,
        })
      : null;
  }
  const choices = projectChoices(value.choices);
  return choices
    ? Object.freeze({
        fieldId: value.fieldId,
        kind,
        required: value.required,
        choices,
      })
    : null;
}

function projectTiming(value) {
  if (!value || typeof value !== "object") return null;
  const requestedAtUtc = validTimestamp(value.requestedAtUtc);
  const expiresAtUtc = validTimestamp(value.expiresAtUtc);
  const requestedAtMilliseconds = Date.parse(requestedAtUtc ?? "");
  const expiresAtMilliseconds = Date.parse(expiresAtUtc ?? "");
  const lifetimeMilliseconds = expiresAtMilliseconds - requestedAtMilliseconds;
  if (
    !requestedAtUtc ||
    !expiresAtUtc ||
    !Number.isFinite(lifetimeMilliseconds) ||
    lifetimeMilliseconds < minimumRequestLifetimeMilliseconds ||
    lifetimeMilliseconds > maximumRequestLifetimeMilliseconds
  )
    return null;
  return Object.freeze({ requestedAtUtc, expiresAtUtc });
}

function validTimestamp(value) {
  return typeof value === "string" &&
    value.length <= 80 &&
    !Number.isNaN(Date.parse(value))
    ? value
    : null;
}

function boundedString(value, maximum) {
  return (
    typeof value === "string" && value.length > 0 && value.length <= maximum
  );
}

function boundedCount(value) {
  return Number.isSafeInteger(value) && value >= 0 && value <= 1_000_000;
}

function boundedEligibleCount(value) {
  return (
    Number.isSafeInteger(value) &&
    value >= 1 &&
    value <= maximumEligibleRespondents
  );
}

function boundedSchemaNumber(value) {
  return (
    Number.isSafeInteger(value) &&
    value >= 1 &&
    value <= maximumResponseCharacters
  );
}

function boundedReferenceNumber(value) {
  return (
    Number.isSafeInteger(value) &&
    value >= 1 &&
    value <= maximumReferenceCharacters
  );
}

function isValidCursor(value) {
  return (
    typeof value === "string" &&
    value.length > 0 &&
    value.length <= 1024 &&
    value.length % 4 !== 1 &&
    cursorPattern.test(value)
  );
}

function isIdentifier(value) {
  return typeof value === "string" && identifierPattern.test(value);
}

function humanInputIdentity(posture) {
  return `${posture.requestId}\u001f${posture.currentRequest.requestVersionId}\u001f${posture.currentRequest.requestHash}\u001f${posture.lifecycleVersion}`;
}

function sameIdentity(left, right) {
  return humanInputIdentity(left) === humanInputIdentity(right);
}

function requestReference(posture) {
  return {
    requestId: posture.currentRequest.requestId,
    requestVersionId: posture.currentRequest.requestVersionId,
    requestHash: posture.currentRequest.requestHash,
  };
}

function schemaDescription(schema) {
  return boundedHumanInputText(JSON.stringify(schema), 2048);
}

function serializeHumanInputRequest(payload) {
  const body = JSON.stringify(payload);
  return new TextEncoder().encode(body).byteLength <= maximumRequestBodyBytes
    ? body
    : null;
}

function formatToken(value) {
  const text = normalizeHumanInputStatus(value);
  if (!text) return "Unknown";
  return text
    .replaceAll("-", " ")
    .replace(/(^| )\w/g, (character) => character.toUpperCase());
}

function formatTimestamp(value) {
  const date = new Date(value);
  return Number.isNaN(date.valueOf())
    ? "time unavailable"
    : date.toLocaleString();
}

function latestConflictSummary(conflict) {
  if (!conflict) return "None";
  return `${formatToken(conflict.operationFamily)} · ${formatToken(conflict.operationKind)} · ${formatToken(conflict.failureCode)} · operation ${boundedHumanInputText(conflict.operationId, 120)} · ${formatTimestamp(conflict.recordedAtUtc)}`;
}

function boundedInputValue(element, maximum) {
  return boundedHumanInputText(element?.value ?? "", maximum);
}

function appendDefinition(parent, label, value) {
  if (!parent) return;
  const ownerDocument = parent.ownerDocument ?? document;
  const wrapper = ownerDocument.createElement("div");
  const name = ownerDocument.createElement("dt");
  const content = ownerDocument.createElement("dd");
  name.textContent = boundedHumanInputText(label, 120);
  content.textContent = boundedHumanInputText(value, 512);
  wrapper.append(name, content);
  parent.append(wrapper);
}

function clearCollection(element) {
  element?.replaceChildren?.();
}

function randomUUID(hostWindow) {
  return typeof hostWindow?.crypto?.randomUUID === "function"
    ? hostWindow.crypto.randomUUID()
    : null;
}

function statusFromError(error) {
  if (error?.status === 400) return "invalid";
  if (error?.status === 403) return "denied";
  if (error?.status === 404) return "not-found";
  if (error?.status === 409) return "conflict";
  return "unavailable";
}

if (typeof window !== "undefined" && typeof document !== "undefined") {
  try {
    const humanInputSurface = createHumanInputSurface({ document, window });
    window.embodySenseHumanInput = humanInputSurface;
    if (!document.getElementById("humanInputView")?.hidden)
      void humanInputSurface.activate();
  } catch {
    // The shared shell can load before authenticated session composition.
  }
}
