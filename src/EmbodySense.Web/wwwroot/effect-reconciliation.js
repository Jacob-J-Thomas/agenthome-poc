const maximumPageItems = 50;
const maximumAggregateItems = 500;
const maximumDetailItems = 32;
const maximumReceiptHashes = 64;
const maximumDisplayCharacters = 1024;
const maximumOperationEntries = 128;
const maximumCursorCharacters = 1024;
const maximumCatalogPages = 20;
const maximumCasePages = 20;
const reconciliationBasePath = "/api/effect-reconciliation";
const sha256Pattern = /^[0-9a-f]{64}$/;
const utcTimestampPattern =
  /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|\+00:00)$/;
const controlCharacterPattern = /[\u0000-\u001f\u007f-\u009f]/;
const identifierPattern = /^[a-z0-9](?:[a-z0-9._-]{0,118}[a-z0-9])?$/;
const reservedIdentifiers = new Set(["con", "prn", "aux", "nul"]);
const casePostures = new Set([
  "open",
  "assessed",
  "accepted",
  "quarantined",
  "resolved",
]);
const pageStatuses = new Set(["ready", "invalid", "corrupt", "unavailable"]);
const readStatuses = new Set([
  "found",
  "not-found",
  "invalid",
  "corrupt",
  "unavailable",
]);
const operationStatuses = new Set([
  "applied",
  "replayed",
  "found",
  "not-found",
  "denied",
  "conflict",
  "invalid",
  "corrupt",
  "unavailable",
  "capacity-exceeded",
  "repair-required",
]);
const dispositionKinds = new Set([
  "accept-proved-not-applied",
  "accept-proved-applied",
  "quarantine-unresolved",
]);

export function normalizeEffectReconciliationStatus(value) {
  return String(value ?? "")
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replaceAll("_", "-")
    .toLowerCase();
}

export function boundedEffectReconciliationText(
  value,
  maximum = maximumDisplayCharacters,
) {
  const text = String(value ?? "");
  const limit = Number.isSafeInteger(maximum)
    ? Math.max(0, maximum)
    : maximumDisplayCharacters;
  if (text.length <= limit) return text;
  return limit === 0 ? "" : `${text.slice(0, limit - 1)}…`;
}

export function projectEffectReconciliationReference(reference) {
  if (
    !reference ||
    typeof reference !== "object" ||
    !isIdentifier(reference.caseId) ||
    !Number.isSafeInteger(reference.caseVersion) ||
    reference.caseVersion <= 0 ||
    !isHash(reference.contentHash) ||
    !isHash(reference.bindingHash)
  )
    return null;
  return Object.freeze({
    caseId: reference.caseId,
    caseVersion: reference.caseVersion,
    contentHash: reference.contentHash,
    bindingHash: reference.bindingHash,
  });
}

export function projectEffectReconciliationSummary(summary) {
  if (!summary || typeof summary !== "object") return null;
  const reference = projectEffectReconciliationReference(summary.reference);
  const posture = normalizeEffectReconciliationStatus(summary.posture);
  return reference && casePostures.has(posture)
    ? Object.freeze({ reference, posture })
    : null;
}

export function projectEffectReconciliationPage(response) {
  const status = normalizeEffectReconciliationStatus(response?.status);
  if (status !== "ready")
    return Object.freeze({
      status: pageStatuses.has(status) ? status : "unavailable",
      items: [],
      cursor: null,
    });
  if (
    !Array.isArray(response.items) ||
    response.items.length > maximumPageItems
  )
    return Object.freeze({ status: "invalid", items: [], cursor: null });
  const items = response.items.map(projectEffectReconciliationSummary);
  const cursor = response.nextCursor ?? response.continuationCursor ?? null;
  if (
    items.some((item) => item === null) ||
    (cursor !== null && !isValidCursor(cursor))
  )
    return Object.freeze({ status: "invalid", items: [], cursor: null });
  return Object.freeze({ status, items: Object.freeze(items), cursor });
}

export function projectEffectReconciliationProbeCatalog(response) {
  const status = normalizeEffectReconciliationStatus(response?.status);
  if (status !== "ready")
    return Object.freeze({
      status: pageStatuses.has(status) ? status : "unavailable",
      contracts: [],
      cursor: null,
    });
  if (
    !Array.isArray(response.contracts) ||
    response.contracts.length > maximumPageItems
  )
    return Object.freeze({ status: "invalid", contracts: [], cursor: null });
  const contracts = response.contracts.map(projectContract);
  const cursor = response.nextCursor ?? response.continuationCursor ?? null;
  if (
    contracts.some((contract) => contract === null) ||
    (cursor !== null && !isValidCursor(cursor))
  )
    return Object.freeze({ status: "invalid", contracts: [], cursor: null });
  return Object.freeze({ status, contracts: Object.freeze(contracts), cursor });
}

export function projectEffectReconciliationDetail(response) {
  const status = normalizeEffectReconciliationStatus(response?.status);
  if (status !== "found" && status !== "ready")
    return Object.freeze({
      status: readStatuses.has(status) ? status : "unavailable",
      detail: null,
    });
  const detail = projectDetail(response.detail ?? response);
  return detail
    ? Object.freeze({ status: "found", detail })
    : Object.freeze({ status: "invalid", detail: null });
}

export function projectEffectReconciliationOperation(response) {
  const status = normalizeEffectReconciliationStatus(response?.status);
  if (!operationStatuses.has(status))
    return Object.freeze({ status: "unavailable", detail: null });
  const detail =
    response.detail == null ? null : projectDetail(response.detail);
  if (response.detail != null && !detail)
    return Object.freeze({ status: "invalid", detail: null });
  return Object.freeze({ status, detail });
}

export function projectEffectReconciliationResolution(response) {
  const status = normalizeEffectReconciliationStatus(response?.status);
  if (status !== "found")
    return Object.freeze({
      status: readStatuses.has(status) ? status : "unavailable",
      resolution: null,
    });
  const resolution = projectResolution(response.resolution ?? response.detail);
  return resolution
    ? Object.freeze({ status, resolution })
    : Object.freeze({ status: "invalid", resolution: null });
}

export function effectReconciliationOperationIdentity(
  reference,
  action,
  kind = "",
) {
  const value = [
    reference.caseId,
    reference.caseVersion,
    reference.contentHash,
    reference.bindingHash,
    action,
    kind,
  ].join("\u001f");
  const seeds = [2166136261, 2654435761, 2246822519, 3266489917];
  return `web-effect-reconciliation-${seeds
    .map((seed) =>
      hashOperationIdentity(value, seed).toString(16).padStart(8, "0"),
    )
    .join("-")}`;
}

export function effectReconciliationReadMessage(status, httpStatus = null) {
  const normalized = normalizeEffectReconciliationStatus(status);
  if (normalized === "not-found" || httpStatus === 404)
    return "This reconciliation case is no longer available.";
  if (normalized === "invalid" || httpStatus === 400)
    return "The canonical reconciliation response was invalid. Refresh to try again.";
  if (normalized === "corrupt")
    return "Reconciliation evidence is corrupt. No action is enabled.";
  if (normalized === "conflict" || httpStatus === 409)
    return "The case changed while it was open. Reread canonical state before trying again.";
  return "Effect Reconciliation is temporarily unavailable. Refresh after the runtime is healthy.";
}

export function effectReconciliationOperationMessage(
  status,
  httpStatus = null,
) {
  const normalized = normalizeEffectReconciliationStatus(status);
  if (normalized === "applied")
    return "The operation was recorded. Rereading canonical state…";
  if (normalized === "replayed")
    return "This operation was already recorded. Rereading canonical state…";
  if (normalized === "denied" || httpStatus === 403)
    return "This server-owned actor is not authorized for the exact case.";
  if (normalized === "conflict" || httpStatus === 409)
    return "The case changed or the operation conflicted. Reread canonical state before trying again.";
  if (normalized === "invalid" || httpStatus === 400)
    return "The operation was not valid for this case. Reread canonical state.";
  if (normalized === "not-found" || httpStatus === 404)
    return "This reconciliation case is no longer available.";
  if (normalized === "corrupt")
    return "Canonical evidence is corrupt. No operation was enabled.";
  if (normalized === "repair-required")
    return "Canonical reconciliation requires explicit repair. No operation was retried.";
  return "Effect Reconciliation is temporarily unavailable. Refresh after the runtime is healthy.";
}

export function createEffectReconciliationSurface({
  document,
  window: hostWindow,
  requestJson: suppliedRequestJson,
} = {}) {
  if (!document) throw new Error("Effect Reconciliation requires a document.");
  const requestJson =
    suppliedRequestJson ?? hostWindow?.embodySenseSession?.requestJson;
  if (typeof requestJson !== "function")
    throw new Error(
      "The authenticated Effect Reconciliation HTTP facade is unavailable.",
    );

  const elements = {
    actionStatus: document.getElementById("effectReconciliationActionStatus"),
    assessButton: document.getElementById("effectReconciliationAssessButton"),
    detailPanel: document.getElementById("effectReconciliationDetailPanel"),
    detailRefreshButton: document.getElementById(
      "effectReconciliationDetailRefreshButton",
    ),
    detailStatus: document.getElementById("effectReconciliationDetailStatus"),
    dispositionDetail: document.getElementById(
      "effectReconciliationDispositionDetail",
    ),
    dispositionKind: document.getElementById(
      "effectReconciliationDispositionKind",
    ),
    disposeButton: document.getElementById("effectReconciliationDisposeButton"),
    empty: document.getElementById("effectReconciliationEmpty"),
    evidence: document.getElementById("effectReconciliationEvidence"),
    evidenceSources: document.getElementById(
      "effectReconciliationEvidenceSources",
    ),
    identity: document.getElementById("effectReconciliationIdentity"),
    list: document.getElementById("effectReconciliationList"),
    listStatus: document.getElementById("effectReconciliationListStatus"),
    posture: document.getElementById("effectReconciliationPosture"),
    probeButton: document.getElementById("effectReconciliationProbeButton"),
    probeCatalog: document.getElementById("effectReconciliationProbeCatalog"),
    probeStatus: document.getElementById("effectReconciliationProbeStatus"),
    refreshButton: document.getElementById("effectReconciliationRefreshButton"),
    resolution: document.getElementById("effectReconciliationResolution"),
    summary: document.getElementById("effectReconciliationSummary"),
    title: document.getElementById("effectReconciliationTitle"),
  };
  const state = {
    active: false,
    cases: [],
    caseCursor: null,
    detail: null,
    detailStatus: "unavailable",
    operations: new Map(),
    probes: [],
    probeCursor: null,
    refreshPromise: null,
    selectedReference: null,
    selectionGeneration: 0,
  };

  function activate() {
    state.active = true;
    return refresh();
  }

  function deactivate() {
    state.active = false;
  }

  function sessionRecovered() {
    if (!state.active) return Promise.resolve(false);
    return refresh(state.selectedReference);
  }

  function refresh(reference = null) {
    if (state.refreshPromise) return state.refreshPromise;
    const operation = refreshCore(reference);
    const tracked = operation.finally(() => {
      if (state.refreshPromise === tracked) state.refreshPromise = null;
    });
    state.refreshPromise = tracked;
    return tracked;
  }

  async function refreshCore(reference) {
    setListBusy(true);
    try {
      state.caseCursor = null;
      state.probeCursor = null;
      const [page, catalog] = await Promise.all([
        readCasePages(),
        readProbePages(),
      ]);
      state.cases = page.status === "ready" ? page.items : [];
      state.probes = catalog.status === "ready" ? catalog.contracts : [];
      const requestedReference =
        projectEffectReconciliationReference(reference);
      state.selectedReference = findReference(requestedReference)
        ? requestedReference
        : (state.cases[0]?.reference ?? null);
      renderList(page);
      renderProbeCatalog(catalog);
      if (state.selectedReference) await readSelectedCase();
      else clearDetail();
      retireObsoleteOperations();
      return Object.freeze({ page, catalog });
    } catch (error) {
      state.cases = [];
      state.probes = [];
      renderList({ status: statusFromError(error), items: [], cursor: null });
      renderProbeCatalog({
        status: statusFromError(error),
        contracts: [],
        cursor: null,
      });
      clearDetail();
      return null;
    } finally {
      setListBusy(false);
      if (elements.detailRefreshButton)
        elements.detailRefreshButton.disabled = false;
    }
  }

  async function readCasePages() {
    const items = [];
    const identities = new Set();
    const cursors = new Set();
    let cursor = null;
    for (let pageNumber = 0; pageNumber < maximumCasePages; pageNumber++) {
      let response;
      try {
        response = await requestJson(pagePath("", cursor));
      } catch (error) {
        return {
          status: statusFromError(error),
          items: [],
          cursor: null,
          httpStatus: error?.status ?? null,
        };
      }
      const page = projectEffectReconciliationPage(response);
      if (page.status !== "ready") return page;
      if (items.length + page.items.length > maximumAggregateItems)
        return { status: "invalid", items: [], cursor: null };
      for (const item of page.items) {
        const identity = referenceIdentity(item.reference);
        if (identities.has(identity))
          return { status: "invalid", items: [], cursor: null };
        identities.add(identity);
        items.push(item);
      }
      if (!page.cursor) {
        state.caseCursor = null;
        return Object.freeze({
          status: "ready",
          items: Object.freeze(items),
          cursor: null,
        });
      }
      if (cursors.has(page.cursor))
        return { status: "invalid", items: [], cursor: null };
      cursors.add(page.cursor);
      cursor = page.cursor;
    }
    return { status: "invalid", items: [], cursor: null };
  }

  async function readProbePages() {
    const contracts = [];
    const identities = new Set();
    const cursors = new Set();
    let cursor = null;
    for (let pageNumber = 0; pageNumber < maximumCatalogPages; pageNumber++) {
      let response;
      try {
        response = await requestJson(pagePath("/probes", cursor));
      } catch (error) {
        return {
          status: statusFromError(error),
          contracts: [],
          cursor: null,
          httpStatus: error?.status ?? null,
        };
      }
      const page = projectEffectReconciliationProbeCatalog(response);
      if (page.status !== "ready") return page;
      if (contracts.length + page.contracts.length > maximumAggregateItems)
        return { status: "invalid", contracts: [], cursor: null };
      for (const contract of page.contracts) {
        const identity = `${contract.contractId}\u001f${contract.contractVersion}\u001f${contract.contractHash}`;
        if (identities.has(identity))
          return { status: "invalid", contracts: [], cursor: null };
        identities.add(identity);
        contracts.push(contract);
      }
      if (!page.cursor) {
        state.probeCursor = null;
        return Object.freeze({
          status: "ready",
          contracts: Object.freeze(contracts),
          cursor: null,
        });
      }
      if (cursors.has(page.cursor))
        return { status: "invalid", contracts: [], cursor: null };
      cursors.add(page.cursor);
      cursor = page.cursor;
    }
    return { status: "invalid", contracts: [], cursor: null };
  }

  async function readSelectedCase() {
    const reference = state.selectedReference;
    if (!reference) {
      clearDetail();
      return;
    }
    const generation = ++state.selectionGeneration;
    renderDetailLoading(reference);
    let detailResult = await readEndpoint(
      casePath(`/${encodeURIComponent(reference.caseId)}`, reference),
    );
    if (
      detailResult.status === "found" &&
      (!detailResult.detail ||
        !sameEffectReconciliationReference(
          detailResult.detail.reference,
          reference,
        ))
    ) {
      detailResult = {
        status: "conflict",
        detail: null,
        httpStatus: 409,
      };
    }
    let resolutionResult = { status: "not-found", resolution: null };
    if (detailResult.status === "found")
      resolutionResult = await readResolutionEndpoint(
        casePath(
          `/${encodeURIComponent(reference.caseId)}/resolution`,
          reference,
        ),
      );
    if (generation !== state.selectionGeneration) return;
    state.detailStatus = detailResult.status;
    state.detail = detailResult.detail;
    renderDetail(detailResult, resolutionResult);
  }

  async function readEndpoint(url) {
    try {
      return projectEffectReconciliationDetail(await requestJson(url));
    } catch (error) {
      return {
        status: statusFromError(error),
        detail: null,
        httpStatus: error?.status ?? null,
      };
    }
  }

  async function readResolutionEndpoint(url) {
    try {
      return projectEffectReconciliationResolution(await requestJson(url));
    } catch (error) {
      return {
        status: statusFromError(error),
        resolution: null,
        httpStatus: error?.status ?? null,
      };
    }
  }

  function selectCase(reference) {
    const projected = projectEffectReconciliationReference(reference);
    if (!projected || !findReference(projected)) return Promise.resolve(false);
    state.selectedReference = projected;
    renderList({ status: "ready", items: state.cases, cursor: null });
    return readSelectedCase().then(() => true);
  }

  async function submit(action) {
    const reference = state.selectedReference;
    if (!reference || !canOperate(state.detail, action)) return;
    const kind =
      action === "dispose"
        ? normalizeEffectReconciliationStatus(elements.dispositionKind?.value)
        : "";
    if (action === "dispose" && !dispositionKinds.has(kind)) {
      setActionStatus(
        "Choose a bounded disposition before submitting.",
        "warning",
      );
      return;
    }
    const key = `${referenceIdentity(reference)}\u001f${action}\u001f${kind}`;
    const operation = reserveOperation(key, reference, action, kind);
    if (!operation) {
      setActionStatus(
        "In-memory operation capacity is full. Refresh before trying again.",
        "warning",
      );
      return;
    }
    setActionBusy(true);
    const payload = {
      operationId: operation.operationId,
      case: reference,
    };
    if (action === "dispose") {
      payload.dispositionKind = kind;
      const detail = boundedEffectReconciliationText(
        elements.dispositionDetail?.value?.trim() ?? "",
      );
      payload.safeDetail = detail || null;
    }
    try {
      const response = await requestJson(
        casePath(
          `/${encodeURIComponent(reference.caseId)}/${action}`,
          reference,
        ),
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload),
        },
      );
      const result = projectEffectReconciliationOperation(response);
      if (result.status === "unavailable")
        throw Object.assign(new Error("invalid operation response"), {
          status: 503,
        });
      setActionStatus(
        effectReconciliationOperationMessage(result.status),
        result.status === "applied" || result.status === "replayed"
          ? "success"
          : "warning",
      );
      if (result.status === "applied" || result.status === "replayed")
        markOperationCompleted(key, operation);
      await refresh(reference);
    } catch (error) {
      setActionStatus(
        effectReconciliationOperationMessage(null, error?.status ?? null),
        "error",
      );
      await refresh(reference);
    } finally {
      setActionBusy(false);
    }
  }

  function reserveOperation(key, reference, action, kind) {
    const existing = state.operations.get(key);
    if (existing) return existing;
    if (state.operations.size >= maximumOperationEntries) return null;
    const operation = Object.freeze({
      action,
      kind,
      operationId: effectReconciliationOperationIdentity(
        reference,
        action,
        kind,
      ),
      reference,
    });
    state.operations.set(key, operation);
    return operation;
  }

  function markOperationCompleted(key, operation) {
    state.operations.set(key, Object.freeze({ ...operation, completed: true }));
  }

  function retireObsoleteOperations() {
    for (const [key, operation] of state.operations) {
      const current = state.cases.find(
        (item) =>
          item.reference.caseId === operation.reference.caseId &&
          item.reference.bindingHash === operation.reference.bindingHash,
      )?.reference;
      const detailReference =
        state.detail &&
        state.detail.reference.caseId === operation.reference.caseId &&
        state.detail.reference.bindingHash === operation.reference.bindingHash
          ? state.detail.reference
          : null;
      const canonical = detailReference ?? current;
      if (
        canonical &&
        (canonical.caseVersion > operation.reference.caseVersion ||
          (operation.completed &&
            detailReference &&
            ["accepted", "quarantined", "resolved"].includes(
              state.detail?.posture,
            )))
      )
        state.operations.delete(key);
    }
  }

  function findReference(reference) {
    return (
      reference &&
      state.cases.some(
        (item) =>
          referenceIdentity(item.reference) === referenceIdentity(reference),
      )
    );
  }

  function renderList(page) {
    if (!elements.list) return;
    elements.list.replaceChildren();
    const hasOptions = page.status === "ready" && state.cases.length > 0;
    setListSemantics(hasOptions);
    setStatus(
      elements.listStatus,
      page.status === "ready"
        ? `${state.cases.length} case${state.cases.length === 1 ? "" : "s"} · canonical state`
        : effectReconciliationReadMessage(page.status),
    );
    if (page.status !== "ready") {
      elements.list.append(
        createState(
          effectReconciliationReadMessage(page.status),
          "error",
          document,
        ),
      );
      return;
    }
    if (state.cases.length === 0) {
      elements.list.append(
        createState("No ambiguous effects require attention.", "", document),
      );
      return;
    }
    for (const item of state.cases) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "effect-reconciliation-list-item";
      button.setAttribute("role", "option");
      button.setAttribute(
        "aria-selected",
        referenceIdentity(item.reference) ===
          referenceIdentity(state.selectedReference)
          ? "true"
          : "false",
      );
      button.append(
        textLine(
          item.posture.replaceAll("-", " "),
          "effect-reconciliation-list-posture",
          document,
        ),
        textLine(
          item.reference.caseId,
          "effect-reconciliation-list-id",
          document,
        ),
        textLine(
          `v${item.reference.caseVersion} · ${item.reference.contentHash.slice(0, 12)}…`,
          "effect-reconciliation-list-hash",
          document,
        ),
      );
      button.addEventListener("click", () => void selectCase(item.reference));
      button.addEventListener("keydown", (event) =>
        moveListFocus(button, event),
      );
      elements.list.append(button);
    }
  }

  function setListSemantics(hasOptions) {
    if (hasOptions) {
      elements.list.setAttribute("role", "listbox");
      elements.list.setAttribute("aria-label", "Effect Reconciliation cases");
    } else {
      elements.list.removeAttribute?.("role");
      elements.list.removeAttribute?.("aria-label");
    }
  }

  function moveListFocus(current, event) {
    if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
    const options = Array.from(
      elements.list.querySelectorAll?.('[role="option"]') ?? [],
    );
    const index = options.indexOf(current);
    if (index < 0 || options.length === 0) return;
    const nextIndex =
      event.key === "Home"
        ? 0
        : event.key === "End"
          ? options.length - 1
          : (index + (event.key === "ArrowDown" ? 1 : -1) + options.length) %
            options.length;
    event.preventDefault();
    options[nextIndex]?.focus();
  }

  function renderProbeCatalog(catalog) {
    if (!elements.probeCatalog) return;
    elements.probeCatalog.replaceChildren();
    setStatus(
      elements.probeStatus,
      catalog.status === "ready"
        ? `${state.probes.length} registered read-only probe${state.probes.length === 1 ? "" : "s"}`
        : effectReconciliationReadMessage(catalog.status),
    );
    if (catalog.status !== "ready") {
      elements.probeCatalog.append(
        createState(
          effectReconciliationReadMessage(catalog.status),
          "error",
          document,
        ),
      );
      return;
    }
    if (state.probes.length === 0) {
      elements.probeCatalog.append(
        createState("No registered probe is available.", "", document),
      );
      return;
    }
    for (const contract of state.probes) {
      const item = document.createElement("div");
      item.className = "effect-reconciliation-probe-item";
      item.append(
        textLine(
          `${contract.contractId} · v${contract.contractVersion}`,
          "effect-reconciliation-probe-title",
          document,
        ),
        textLine(
          `Probe ${contract.probeContractId} · ${contract.probeContractHash.slice(0, 12)}…`,
          "effect-reconciliation-probe-detail",
          document,
        ),
      );
      elements.probeCatalog.append(item);
    }
  }

  function renderDetailLoading(reference) {
    elements.empty.hidden = true;
    elements.detailPanel.hidden = false;
    setStatus(elements.detailStatus, "Rereading canonical state…");
    elements.title.textContent = "Reconciliation case";
    elements.identity.textContent = `${reference.caseId} · v${reference.caseVersion}`;
    elements.summary.replaceChildren();
    elements.evidence.replaceChildren();
    elements.evidenceSources.replaceChildren();
    elements.resolution.replaceChildren();
    configureActions(null);
  }

  function renderDetail(result, resolutionResult) {
    if (result.status !== "found" || !result.detail) {
      elements.empty.hidden = false;
      elements.detailPanel.hidden = true;
      setStatus(
        elements.detailStatus,
        effectReconciliationReadMessage(result.status, result.httpStatus),
        "error",
      );
      return;
    }
    const detail = result.detail;
    elements.empty.hidden = true;
    elements.detailPanel.hidden = false;
    elements.title.textContent = "Reconciliation case";
    elements.identity.textContent = `${detail.reference.caseId} · v${detail.reference.caseVersion}`;
    elements.posture.textContent = formatToken(detail.posture);
    setStatus(
      elements.detailStatus,
      "Canonical state read successfully.",
      "success",
    );
    renderDefinitions(
      elements.summary,
      [
        ["Posture", formatToken(detail.posture)],
        ["Opened", formatTimestamp(detail.openedAtUtc)],
        ["Updated", formatTimestamp(detail.updatedAtUtc)],
        ["Case hash", detail.reference.contentHash],
        ["Binding hash", detail.reference.bindingHash],
        ["Receipts", String(detail.receiptHashes.length)],
      ],
      document,
    );
    renderContract(detail.contract);
    renderEvidence(detail);
    renderResolution(detail, resolutionResult);
    configureActions(detail);
  }

  function renderContract(contract) {
    const target = document.getElementById("effectReconciliationContract");
    if (!target) return;
    renderDefinitions(
      target,
      [
        ["Contract", `${contract.contractId} · v${contract.contractVersion}`],
        ["Contract hash", contract.contractHash],
        [
          "Probe contract",
          `${contract.probeContractId} · v${contract.probeContractVersion}`,
        ],
        ["Probe hash", contract.probeContractHash],
      ],
      document,
    );
  }

  function renderEvidence(detail) {
    elements.evidenceSources.replaceChildren();
    elements.evidence.replaceChildren();
    for (const source of detail.evidenceSources) {
      const item = document.createElement("li");
      item.append(
        textLine(
          `${source.sourceId} · ${formatToken(source.kind)}`,
          "effect-reconciliation-evidence-title",
          document,
        ),
        textLine(
          `${formatToken(source.reliabilityPosture)} · ${source.contractHash}`,
          "effect-reconciliation-evidence-detail",
          document,
        ),
      );
      elements.evidenceSources.append(item);
    }
    const values = [
      ...detail.observations.map((item) => ({
        title: `Observation · ${item.observationId}`,
        detail: `${formatToken(item.kind)} · ${formatToken(item.observedOutcome)} · ${item.contentHash}`,
      })),
      ...detail.assessments.map((item) => ({
        title: `Assessment · ${item.assessmentId}`,
        detail: `${formatToken(item.kind)} · ${item.contentHash}`,
      })),
      ...(detail.disposition
        ? [
            {
              title: `Disposition · ${detail.disposition.dispositionId}`,
              detail: `${formatToken(detail.disposition.kind)} · ${detail.disposition.contentHash}`,
            },
          ]
        : []),
    ];
    if (values.length === 0)
      elements.evidence.append(
        createState(
          "No value-free observations have been recorded.",
          "",
          document,
        ),
      );
    for (const value of values) {
      const item = document.createElement("li");
      item.append(
        textLine(value.title, "effect-reconciliation-evidence-title", document),
        textLine(
          value.detail,
          "effect-reconciliation-evidence-detail",
          document,
        ),
      );
      elements.evidence.append(item);
    }
  }

  function renderResolution(detail, result) {
    elements.resolution.replaceChildren();
    const resolution =
      detail.resolution ??
      (result.status === "found" ? result.resolution : null);
    if (!resolution) {
      elements.resolution.append(
        createState(
          result.status === "not-found"
            ? "No immutable resolution has been recorded."
            : effectReconciliationReadMessage(result.status, result.httpStatus),
          "",
          document,
        ),
      );
      return;
    }
    renderDefinitions(
      elements.resolution,
      [
        ["Resolution", resolution.resolutionId],
        ["Outcome", formatToken(resolution.outcome)],
        ["Resolved", formatTimestamp(resolution.resolvedAtUtc)],
        ["Content hash", resolution.contentHash],
      ],
      document,
    );
  }

  function clearDetail() {
    state.detail = null;
    state.selectedReference = null;
    elements.empty.hidden = false;
    elements.detailPanel.hidden = true;
    setStatus(
      elements.detailStatus,
      "Select a case to inspect canonical evidence.",
    );
  }

  function configureActions(detail) {
    const probeEnabled =
      Boolean(detail) && ["open", "assessed"].includes(detail.posture);
    const disposeEnabled = Boolean(detail) && detail.posture === "assessed";
    if (elements.probeButton) elements.probeButton.disabled = !probeEnabled;
    if (elements.assessButton) elements.assessButton.disabled = !probeEnabled;
    if (elements.disposeButton)
      elements.disposeButton.disabled = !disposeEnabled;
    if (elements.dispositionKind)
      elements.dispositionKind.disabled = !disposeEnabled;
    if (elements.dispositionDetail)
      elements.dispositionDetail.disabled = !disposeEnabled;
  }

  function setActionBusy(busy) {
    for (const button of [
      elements.probeButton,
      elements.assessButton,
      elements.disposeButton,
    ])
      if (button)
        button.disabled =
          busy ||
          !canOperate(
            state.detail,
            button === elements.probeButton
              ? "probe"
              : button === elements.assessButton
                ? "assess"
                : "dispose",
          );
    if (elements.dispositionKind) elements.dispositionKind.disabled = busy;
    if (elements.dispositionDetail) elements.dispositionDetail.disabled = busy;
  }

  function canOperate(detail, action) {
    if (!detail) return false;
    if (action === "probe" || action === "assess")
      return ["open", "assessed"].includes(detail.posture);
    return detail.posture === "assessed";
  }

  function setListBusy(busy) {
    if (elements.refreshButton) elements.refreshButton.disabled = busy;
    if (elements.list)
      elements.list.setAttribute("aria-busy", busy ? "true" : "false");
  }

  function setActionStatus(message, tone = "") {
    setStatus(elements.actionStatus, message, tone);
  }

  function bind() {
    elements.refreshButton?.addEventListener(
      "click",
      () => void refresh(state.selectedReference),
    );
    elements.detailRefreshButton?.addEventListener(
      "click",
      () => void refresh(state.selectedReference),
    );
    elements.probeButton?.addEventListener("click", () => void submit("probe"));
    elements.assessButton?.addEventListener(
      "click",
      () => void submit("assess"),
    );
    elements.disposeButton?.addEventListener(
      "click",
      () => void submit("dispose"),
    );
  }

  bind();
  renderList({ status: "ready", items: [], cursor: null });
  renderProbeCatalog({ status: "ready", contracts: [], cursor: null });
  clearDetail();

  return Object.freeze({
    activate,
    deactivate,
    refresh,
    selectCase,
    sessionRecovered,
  });

  function pagePath(suffix, cursor) {
    const query = new URLSearchParams({
      maximumCount: String(maximumPageItems),
    });
    if (cursor) query.set("cursor", cursor);
    return `${reconciliationBasePath}${suffix}?${query}`;
  }

  function casePath(suffix, reference) {
    const query = new URLSearchParams({
      caseVersion: String(reference.caseVersion),
      contentHash: reference.contentHash,
      bindingHash: reference.bindingHash,
    });
    return `${reconciliationBasePath}${suffix}?${query}`;
  }
}

function projectContract(value) {
  if (
    !value ||
    typeof value !== "object" ||
    !isIdentifier(value.contractId) ||
    !Number.isSafeInteger(value.contractVersion) ||
    value.contractVersion <= 0 ||
    !isHash(value.contractHash) ||
    !isIdentifier(value.probeContractId) ||
    !Number.isSafeInteger(value.probeContractVersion) ||
    value.probeContractVersion <= 0 ||
    !isHash(value.probeContractHash)
  )
    return null;
  return Object.freeze({
    contractId: value.contractId,
    contractVersion: value.contractVersion,
    contractHash: value.contractHash,
    probeContractId: value.probeContractId,
    probeContractVersion: value.probeContractVersion,
    probeContractHash: value.probeContractHash,
  });
}

function projectDetail(value) {
  if (!value || typeof value !== "object") return null;
  const reference = projectEffectReconciliationReference(value.reference);
  const posture = normalizeEffectReconciliationStatus(value.posture);
  const contract = projectContract(value.contract);
  const evidenceSources = projectArray(
    value.evidenceSources,
    maximumDetailItems,
    projectSource,
  );
  const observations = projectArray(
    value.observations,
    maximumDetailItems,
    projectObservation,
  );
  const assessments = projectArray(
    value.assessments,
    maximumDetailItems,
    projectAssessment,
  );
  const disposition =
    value.disposition == null ? null : projectDisposition(value.disposition);
  const resolution =
    value.resolution == null ? null : projectResolution(value.resolution);
  const receiptHashes = Array.isArray(value.receiptHashes)
    ? value.receiptHashes.filter(isHash)
    : null;
  if (
    !reference ||
    !casePostures.has(posture) ||
    !contract ||
    !evidenceSources ||
    !observations ||
    !assessments ||
    (value.disposition != null && !disposition) ||
    (value.resolution != null && !resolution) ||
    !receiptHashes ||
    receiptHashes.length !== value.receiptHashes.length ||
    receiptHashes.length > maximumReceiptHashes ||
    !isTimestamp(value.openedAtUtc) ||
    !isTimestamp(value.updatedAtUtc)
  )
    return null;
  const projected = {
    reference,
    posture,
    contract,
    evidenceSources,
    observations,
    assessments,
    disposition,
    resolution,
    receiptHashes: Object.freeze(receiptHashes),
    openedAtUtc: value.openedAtUtc,
    updatedAtUtc: value.updatedAtUtc,
  };
  return isDetailCompositionValid(projected) ? Object.freeze(projected) : null;
}

function isDetailCompositionValid(detail) {
  if (
    new Date(detail.updatedAtUtc) < new Date(detail.openedAtUtc) ||
    (detail.disposition &&
      new Date(detail.disposition.disposedAtUtc) >
        new Date(detail.updatedAtUtc)) ||
    (detail.resolution &&
      new Date(detail.resolution.resolvedAtUtc) > new Date(detail.updatedAtUtc))
  )
    return false;
  if (
    !isStrictlyCanonical(
      detail.evidenceSources.map((source) => source.sourceId),
    ) ||
    !hasUnique(detail.evidenceSources.map((source) => source.sourceId)) ||
    !hasUnique(detail.evidenceSources.map((source) => source.contentHash)) ||
    !isStrictlyCanonical(
      detail.observations.map((observation) => observation.observationId),
    ) ||
    !hasUnique(
      detail.observations.map((observation) => observation.observationId),
    ) ||
    !hasUnique(
      detail.observations.map((observation) => observation.contentHash),
    ) ||
    !isStrictlyCanonical(
      detail.assessments.map((assessment) => assessment.assessmentId),
    ) ||
    !hasUnique(
      detail.assessments.map((assessment) => assessment.assessmentId),
    ) ||
    !hasUnique(
      detail.assessments.map((assessment) => assessment.contentHash),
    ) ||
    !isStrictlyCanonical(detail.receiptHashes) ||
    !hasUnique(detail.receiptHashes)
  )
    return false;

  const sourceById = new Map();
  for (const source of detail.evidenceSources) {
    if (
      source.kind === "informational" &&
      source.reliabilityPosture === "authoritative"
    )
      return false;
    if (
      new Date(source.registeredAtUtc) > new Date(detail.updatedAtUtc) ||
      (source.retiredAtUtc &&
        new Date(source.retiredAtUtc) < new Date(source.registeredAtUtc)) ||
      (source.retiredAtUtc &&
        new Date(source.retiredAtUtc) > new Date(detail.updatedAtUtc)) ||
      source.contractHash !== detail.contract.contractHash
    )
      return false;
    sourceById.set(source.sourceId, source);
  }

  for (const observation of detail.observations) {
    const source = sourceById.get(observation.sourceId);
    if (
      !source ||
      source.contentHash !== observation.sourceRegistrationHash ||
      source.reliabilityPosture !== observation.reliabilityPosture ||
      new Date(observation.recordedAtUtc) > new Date(detail.updatedAtUtc) ||
      new Date(observation.recordedAtUtc) < new Date(source.registeredAtUtc) ||
      (source.retiredAtUtc &&
        new Date(observation.recordedAtUtc) > new Date(source.retiredAtUtc)) ||
      (observation.observedAtUtc &&
        new Date(observation.observedAtUtc) >
          new Date(observation.recordedAtUtc)) ||
      !isObservationCompositionValid(observation)
    )
      return false;
  }

  const observationByHash = new Map(
    detail.observations.map((observation) => [
      observation.contentHash,
      observation,
    ]),
  );
  const authoritativeObservations = detail.observations.filter(
    (observation) => {
      const source = sourceById.get(observation.sourceId);
      return (
        source?.kind === "authoritative" &&
        source.reliabilityPosture === "authoritative" &&
        observation.reliabilityPosture === "authoritative" &&
        observation.kind === "evidence" &&
        observation.observedOutcome !== "unknown" &&
        observation.observedAtUtc &&
        new Date(observation.observedAtUtc) >= new Date(detail.openedAtUtc)
      );
    },
  );

  for (const assessment of detail.assessments) {
    if (
      !isStrictlyCanonical(assessment.observationHashes) ||
      !hasUnique(assessment.observationHashes) ||
      assessment.observationHashes.some(
        (hash) => !observationByHash.has(hash),
      ) ||
      assessment.observationHashes.some(
        (hash) =>
          new Date(observationByHash.get(hash).recordedAtUtc) >
          new Date(assessment.assessedAtUtc),
      ) ||
      new Date(assessment.assessedAtUtc) > new Date(detail.updatedAtUtc) ||
      (assessment.kind !== "inconclusive" &&
        assessment.observationHashes.length === 0) ||
      !assessmentKindMatchesEvidence(
        assessment.kind,
        assessment.observationHashes
          .map((hash) => observationByHash.get(hash))
          .filter((observation) =>
            authoritativeObservations.includes(observation),
          ),
      )
    )
      return false;
  }

  const currentAssessment = latestAssessment(detail.assessments);
  const expectedPosture = detail.resolution
    ? "resolved"
    : detail.disposition?.kind === "quarantine-unresolved"
      ? "quarantined"
      : detail.disposition
        ? "accepted"
        : currentAssessment
          ? "assessed"
          : "open";
  if (detail.posture !== expectedPosture) return false;

  if (!currentAssessment) {
    return detail.disposition === null && detail.resolution === null;
  }
  if (detail.disposition === null && detail.resolution !== null) return false;
  if (detail.disposition === null) return true;
  if (
    detail.disposition.assessmentHash !== currentAssessment.contentHash ||
    new Date(detail.disposition.disposedAtUtc) <
      new Date(currentAssessment.assessedAtUtc) ||
    !isDispositionAllowed(currentAssessment.kind, detail.disposition.kind)
  )
    return false;
  if (detail.disposition.kind === "quarantine-unresolved")
    return detail.resolution === null;
  if (detail.resolution === null) return true;
  return isResolutionCompositionValid(
    detail,
    currentAssessment,
    observationByHash,
    authoritativeObservations,
  );
}

function isObservationCompositionValid(observation) {
  const hasEvidenceReference = observation.evidenceReference !== null;
  const hasEvidenceHash = observation.evidenceHash !== null;
  if (hasEvidenceReference !== hasEvidenceHash) return false;
  if (observation.kind === "evidence") {
    return (
      observation.observedOutcome !== "unknown" &&
      hasEvidenceReference &&
      observation.observedAtUtc !== null
    );
  }
  if (["missing", "timed-out", "cancelled"].includes(observation.kind)) {
    return (
      observation.observedOutcome === "unknown" &&
      !hasEvidenceReference &&
      observation.observedAtUtc === null
    );
  }
  return true;
}

function assessmentKindMatchesEvidence(kind, observations) {
  const outcomes = [
    ...new Set(observations.map((observation) => observation.observedOutcome)),
  ];
  const expected =
    outcomes.length === 0
      ? "inconclusive"
      : outcomes.length > 1
        ? "conflicting"
        : ({
            "not-applied": "proved-not-applied",
            "applied-succeeded": "proved-applied-succeeded",
            "applied-failed": "proved-applied-failed",
            "applied-outcome-unknown": "proved-applied-outcome-unknown",
          }[outcomes[0]] ?? "unknown");
  return kind === expected;
}

function latestAssessment(assessments) {
  return assessments.reduce((latest, assessment) => {
    if (!latest) return assessment;
    const assessmentTime = new Date(assessment.assessedAtUtc).valueOf();
    const latestTime = new Date(latest.assessedAtUtc).valueOf();
    return assessmentTime > latestTime ||
      (assessmentTime === latestTime &&
        assessment.assessmentId > latest.assessmentId)
      ? assessment
      : latest;
  }, null);
}

function isDispositionAllowed(assessmentKind, dispositionKind) {
  return (
    (assessmentKind === "proved-not-applied" &&
      dispositionKind === "accept-proved-not-applied") ||
    (["proved-applied-succeeded", "proved-applied-failed"].includes(
      assessmentKind,
    ) &&
      dispositionKind === "accept-proved-applied") ||
    (["inconclusive", "conflicting", "proved-applied-outcome-unknown"].includes(
      assessmentKind,
    ) &&
      dispositionKind === "quarantine-unresolved")
  );
}

function isResolutionCompositionValid(
  detail,
  assessment,
  observationByHash,
  authoritativeObservations,
) {
  const resolution = detail.resolution;
  if (
    resolution.assessmentHash !== assessment.contentHash ||
    resolution.dispositionHash !== detail.disposition.contentHash ||
    new Date(resolution.resolvedAtUtc) <
      new Date(detail.disposition.disposedAtUtc)
  )
    return false;
  const expectedOutcome = {
    "proved-not-applied": "not-applied",
    "proved-applied-succeeded": "succeeded",
    "proved-applied-failed": "failed",
  }[assessment.kind];
  if (resolution.outcome !== expectedOutcome) return false;
  const needsOutcomeEvidence = ["succeeded", "failed"].includes(
    resolution.outcome,
  );
  const hasAnyOutcomeEvidence =
    resolution.outcomeEvidenceId !== null ||
    resolution.outcomeEvidenceHash !== null;
  const hasOutcomeEvidencePair =
    resolution.outcomeEvidenceId !== null &&
    resolution.outcomeEvidenceHash !== null;
  if (
    hasAnyOutcomeEvidence !== hasOutcomeEvidencePair ||
    needsOutcomeEvidence !== hasOutcomeEvidencePair
  )
    return false;
  if (!needsOutcomeEvidence) return true;
  const expectedObservedOutcome =
    resolution.outcome === "succeeded" ? "applied-succeeded" : "applied-failed";
  return assessment.observationHashes.some((hash) => {
    const observation = observationByHash.get(hash);
    return (
      authoritativeObservations.includes(observation) &&
      observation?.observedOutcome === expectedObservedOutcome &&
      observation.evidenceReference === resolution.outcomeEvidenceId &&
      observation.evidenceHash === resolution.outcomeEvidenceHash
    );
  });
}

function hasUnique(values) {
  return new Set(values).size === values.length;
}

function isStrictlyCanonical(values) {
  for (let index = 1; index < values.length; index++) {
    if (values[index - 1] >= values[index]) return false;
  }
  return true;
}

function projectSource(value) {
  if (
    !value ||
    !isIdentifier(value.sourceId) ||
    !["authoritative", "informational"].includes(
      normalizeEffectReconciliationStatus(value.kind),
    ) ||
    !["authoritative", "corroborating", "untrusted"].includes(
      normalizeEffectReconciliationStatus(value.reliabilityPosture),
    ) ||
    !isHash(value.contractHash) ||
    !isHash(value.contentHash) ||
    !isTimestamp(value.registeredAtUtc) ||
    (value.retiredAtUtc != null && !isTimestamp(value.retiredAtUtc))
  )
    return null;
  return Object.freeze({
    sourceId: value.sourceId,
    kind: normalizeEffectReconciliationStatus(value.kind),
    reliabilityPosture: normalizeEffectReconciliationStatus(
      value.reliabilityPosture,
    ),
    contractHash: value.contractHash,
    registeredAtUtc: value.registeredAtUtc,
    retiredAtUtc: value.retiredAtUtc ?? null,
    contentHash: value.contentHash,
  });
}

function projectObservation(value) {
  const kind = normalizeEffectReconciliationStatus(value?.kind);
  const reliabilityPosture = normalizeEffectReconciliationStatus(
    value?.reliabilityPosture,
  );
  const observedOutcome = normalizeEffectReconciliationStatus(
    value?.observedOutcome,
  );
  if (
    !value ||
    !isIdentifier(value.observationId) ||
    !isIdentifier(value.sourceId) ||
    !isHash(value.sourceRegistrationHash) ||
    ![
      "evidence",
      "missing",
      "timed-out",
      "cancelled",
      "prose",
      "caller-assertion",
      "unproven-hash",
    ].includes(kind) ||
    !["authoritative", "corroborating", "untrusted"].includes(
      reliabilityPosture,
    ) ||
    ![
      "unknown",
      "not-applied",
      "applied-succeeded",
      "applied-failed",
      "applied-outcome-unknown",
    ].includes(observedOutcome) ||
    (value.evidenceReference != null &&
      !isIdentifier(value.evidenceReference)) ||
    (value.evidenceHash != null && !isHash(value.evidenceHash)) ||
    (value.observedAtUtc != null && !isTimestamp(value.observedAtUtc)) ||
    !isTimestamp(value.recordedAtUtc) ||
    !isHash(value.contentHash)
  )
    return null;
  return Object.freeze({
    observationId: value.observationId,
    sourceId: value.sourceId,
    sourceRegistrationHash: value.sourceRegistrationHash,
    kind,
    reliabilityPosture,
    observedOutcome,
    evidenceReference: value.evidenceReference ?? null,
    evidenceHash: value.evidenceHash ?? null,
    observedAtUtc: value.observedAtUtc ?? null,
    recordedAtUtc: value.recordedAtUtc,
    contentHash: value.contentHash,
  });
}

function projectAssessment(value) {
  const kind = normalizeEffectReconciliationStatus(value?.kind);
  if (
    !value ||
    !isIdentifier(value.assessmentId) ||
    ![
      "inconclusive",
      "conflicting",
      "proved-not-applied",
      "proved-applied-succeeded",
      "proved-applied-failed",
      "proved-applied-outcome-unknown",
    ].includes(kind) ||
    !Array.isArray(value.observationHashes) ||
    value.observationHashes.length > maximumDetailItems ||
    value.observationHashes.some((hash) => !isHash(hash)) ||
    !isTimestamp(value.assessedAtUtc) ||
    !isHash(value.contentHash)
  )
    return null;
  return Object.freeze({
    assessmentId: value.assessmentId,
    kind,
    observationHashes: Object.freeze([...value.observationHashes]),
    assessedAtUtc: value.assessedAtUtc,
    contentHash: value.contentHash,
  });
}

function projectDisposition(value) {
  const kind = normalizeEffectReconciliationStatus(value?.kind);
  return value &&
    isIdentifier(value.dispositionId) &&
    dispositionKinds.has(kind) &&
    isHash(value.assessmentHash) &&
    isTimestamp(value.disposedAtUtc) &&
    isHash(value.contentHash)
    ? Object.freeze({
        dispositionId: value.dispositionId,
        kind,
        assessmentHash: value.assessmentHash,
        disposedAtUtc: value.disposedAtUtc,
        contentHash: value.contentHash,
      })
    : null;
}

function projectResolution(value) {
  const outcome = normalizeEffectReconciliationStatus(value?.outcome);
  return value &&
    isIdentifier(value.resolutionId) &&
    isHash(value.assessmentHash) &&
    isHash(value.dispositionHash) &&
    ["not-applied", "succeeded", "failed"].includes(outcome) &&
    (value.outcomeEvidenceId == null ||
      isIdentifier(value.outcomeEvidenceId)) &&
    (value.outcomeEvidenceHash == null || isHash(value.outcomeEvidenceHash)) &&
    isTimestamp(value.resolvedAtUtc) &&
    isHash(value.contentHash)
    ? Object.freeze({
        resolutionId: value.resolutionId,
        assessmentHash: value.assessmentHash,
        dispositionHash: value.dispositionHash,
        outcome,
        outcomeEvidenceId: value.outcomeEvidenceId ?? null,
        outcomeEvidenceHash: value.outcomeEvidenceHash ?? null,
        resolvedAtUtc: value.resolvedAtUtc,
        contentHash: value.contentHash,
      })
    : null;
}

function projectArray(value, maximum, projector) {
  if (!Array.isArray(value) || value.length > maximum) return null;
  const projected = value.map(projector);
  return projected.some((item) => item === null)
    ? null
    : Object.freeze(projected);
}

function referenceIdentity(reference) {
  return [
    reference.caseId,
    reference.caseVersion,
    reference.contentHash,
    reference.bindingHash,
  ].join("\u001f");
}

function sameEffectReconciliationReference(left, right) {
  return (
    Boolean(left && right) &&
    referenceIdentity(left) === referenceIdentity(right)
  );
}

function isIdentifier(value) {
  if (
    typeof value !== "string" ||
    value.length > 120 ||
    !identifierPattern.test(value)
  )
    return false;
  const baseName = value.split(".", 1)[0];
  return (
    !reservedIdentifiers.has(baseName) && !/^((com|lpt)[1-9])$/.test(baseName)
  );
}

function isHash(value) {
  return typeof value === "string" && sha256Pattern.test(value);
}

function isValidCursor(value) {
  return (
    typeof value === "string" &&
    value.length > 0 &&
    value.length <= maximumCursorCharacters &&
    !controlCharacterPattern.test(value)
  );
}

function isTimestamp(value) {
  return (
    typeof value === "string" &&
    value.length <= 80 &&
    utcTimestampPattern.test(value) &&
    !Number.isNaN(Date.parse(value))
  );
}

function statusFromError(error) {
  if (error?.status === 400) return "invalid";
  if (error?.status === 404) return "not-found";
  if (error?.status === 409) return "conflict";
  return "unavailable";
}

function setStatus(element, message, tone = "") {
  if (!element) return;
  element.textContent = boundedEffectReconciliationText(message);
  element.className = tone
    ? `effect-reconciliation-status ${tone}`
    : "effect-reconciliation-status";
}

function createState(message, kind = "", ownerDocument = globalThis.document) {
  const element = ownerDocument.createElement("p");
  element.className = kind
    ? `effect-reconciliation-empty-state ${kind}`
    : "effect-reconciliation-empty-state";
  element.textContent = boundedEffectReconciliationText(message);
  return element;
}

function textLine(value, className = "", ownerDocument = globalThis.document) {
  const element = ownerDocument.createElement("span");
  element.className = className;
  element.textContent = boundedEffectReconciliationText(value);
  return element;
}

function renderDefinitions(
  parent,
  values,
  ownerDocument = globalThis.document,
) {
  if (!parent) return;
  parent.replaceChildren();
  for (const [label, value] of values) {
    const wrapper = ownerDocument.createElement("div");
    const term = ownerDocument.createElement("dt");
    term.textContent = label;
    const description = ownerDocument.createElement("dd");
    description.textContent = boundedEffectReconciliationText(value);
    wrapper.append(term, description);
    parent.append(wrapper);
  }
}

function formatToken(value) {
  const text = normalizeEffectReconciliationStatus(value);
  return text
    ? text
        .replaceAll("-", " ")
        .replace(/^\w/, (character) => character.toUpperCase())
    : "Unknown";
}

function formatTimestamp(value) {
  const date = new Date(value);
  return Number.isNaN(date.valueOf())
    ? "time unavailable"
    : date.toLocaleString();
}

function hashOperationIdentity(value, seed) {
  let hash = seed >>> 0;
  for (let index = 0; index < value.length; index++)
    hash = Math.imul(hash ^ value.charCodeAt(index), 16777619) >>> 0;
  return hash;
}

if (typeof window !== "undefined" && typeof document !== "undefined") {
  try {
    const effectReconciliationSurface = createEffectReconciliationSurface({
      document,
      window,
    });
    window.embodySenseEffectReconciliation = effectReconciliationSurface;
    if (!document.getElementById("effectReconciliationView")?.hidden)
      void effectReconciliationSurface.activate();
  } catch {
    // The shared shell can load before authenticated session composition.
  }
}
