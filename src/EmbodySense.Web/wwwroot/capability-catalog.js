const capabilityElements = {
  catalogNotice: document.getElementById("catalogNotice"),
  catalogRevision: document.getElementById("catalogRevision"),
  capabilityBadges: document.getElementById("capabilityBadges"),
  capabilityContent: document.getElementById("capabilityContent"),
  capabilityDependents: document.getElementById("capabilityDependents"),
  capabilityEmpty: document.getElementById("capabilityEmpty"),
  capabilityFacts: document.getElementById("capabilityFacts"),
  capabilityKind: document.getElementById("capabilityKind"),
  capabilityList: document.getElementById("capabilityList"),
  capabilityPurpose: document.getElementById("capabilityPurpose"),
  capabilitySearch: document.getElementById("capabilitySearch"),
  capabilityTitle: document.getElementById("capabilityTitle"),
  connectionDot: document.getElementById("connectionDot"),
  lifecycleNotice: document.getElementById("lifecycleNotice"),
  lifecycleOperation: document.getElementById("lifecycleOperation"),
  lifecyclePreview: document.getElementById("lifecyclePreview"),
  lifecyclePreviewForm: document.getElementById("lifecyclePreviewForm"),
  loadMoreCapabilitiesButton: document.getElementById(
    "loadMoreCapabilitiesButton",
  ),
  previewLifecycleButton: document.getElementById("previewLifecycleButton"),
  refreshCapabilitiesButton: document.getElementById(
    "refreshCapabilitiesButton",
  ),
  targetVersion: document.getElementById("targetVersion"),
  targetVersionLabel: document.getElementById("targetVersionLabel"),
  workspaceRoot: document.getElementById("workspaceRoot"),
  workspaceStatus: document.getElementById("workspaceStatus"),
};

const capabilityState = {
  capabilities: [],
  catalogRevision: null,
  nextCursor: null,
  pending: null,
  previewInFlight: false,
  selectedId: null,
  storageKey: null,
  storageLockName: null,
  storageReady: false,
  tabId: null,
  tabStorageKey: null,
};

const capabilityLifecycleRegistrySchemaVersion = 1;
const maximumPendingCapabilityOperations = 16;
const capabilityLifecycleStorageKeyPrefix =
  "embodysense.pending-capability-lifecycle.v1";
const capabilityLifecycleTabStorageKey =
  "embodysense.capability-lifecycle-tab.v1";

function capabilityNode(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = String(text);
  return element;
}

function capabilityToken(value) {
  return String(value ?? "unknown")
    .replaceAll("-", " ")
    .replace(/\b\w/g, (character) => character.toUpperCase());
}

async function capabilityFetchJson(url, options = {}) {
  const response = await fetch(url, {
    ...options,
    credentials: "same-origin",
    headers: {
      ...(options.body ? { "Content-Type": "application/json" } : {}),
      ...(options.headers ?? {}),
    },
  });
  const text = await response.text();
  let body = null;
  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = null;
    }
  }
  if (!response.ok) {
    const error = new Error(
      body?.detail ??
        body?.error?.message ??
        body?.error ??
        text ??
        `Request failed (${response.status}).`,
    );
    error.status = response.status;
    error.body = body;
    throw error;
  }
  return body;
}

async function capabilityBoot() {
  try {
    const session = await capabilityFetchJson("/api/session");
    const workspaceScope = requireCapabilityWorkspaceScope(session);
    const status = await capabilityFetchJson("/api/status");
    capabilityElements.workspaceRoot.textContent = status.workspaceRoot;
    capabilityElements.workspaceStatus.textContent = status.initialized
      ? "Initialized"
      : "Needs initialization";
    capabilityElements.connectionDot.classList.toggle(
      "ready",
      status.initialized,
    );
    try {
      configureCapabilityLifecycleStorage(workspaceScope);
    } catch (error) {
      capabilityState.storageReady = false;
      capabilityElements.previewLifecycleButton.disabled = true;
      showLifecycleNotice(
        `Lifecycle changes are disabled: ${error.message}`,
        true,
      );
    }
    if (!status.initialized) {
      showCatalogNotice(
        "Initialize this workspace from Chat or Loops before inspecting capabilities.",
        true,
      );
      return;
    }
    if ((await loadCapabilityCatalog(false)) && capabilityState.storageReady)
      await restorePendingCapabilityPreview();
  } catch (error) {
    showCatalogNotice(error.message, true);
  }
}

function requireCapabilityWorkspaceScope(session) {
  if (
    typeof session?.chatRequestScope !== "string" ||
    !/^[0-9a-f]{64}$/.test(session.chatRequestScope)
  ) {
    throw new Error(
      "The session endpoint returned an invalid non-secret workspace scope.",
    );
  }
  return session.chatRequestScope;
}

async function loadCapabilityCatalog(append) {
  capabilityElements.refreshCapabilitiesButton.disabled = true;
  capabilityElements.loadMoreCapabilitiesButton.disabled = true;
  showCatalogNotice(append ? "Loading more capabilities…" : "Loading catalog…");
  try {
    const cursor = append ? capabilityState.nextCursor : null;
    const query = new URLSearchParams({ maximumCount: "50" });
    if (cursor) query.set("cursor", cursor);
    const page = await capabilityFetchJson(`/api/capabilities?${query}`);
    capabilityState.catalogRevision = page.catalogRevision;
    capabilityState.nextCursor = page.nextCursor;
    capabilityState.capabilities = append
      ? [...capabilityState.capabilities, ...page.capabilities]
      : page.capabilities;
    capabilityElements.catalogRevision.textContent = `Catalog revision ${page.catalogRevision ?? "unavailable"}`;
    capabilityElements.loadMoreCapabilitiesButton.hidden = !page.nextCursor;
    renderCapabilityCatalog();
    const retained = capabilityState.capabilities.find(
      (item) => item.id === capabilityState.selectedId,
    );
    selectCapability(
      retained ?? capabilityState.capabilities[0] ?? null,
      false,
    );
    if (
      capabilityState.pending?.preview &&
      capabilityState.pending.selection.capabilityId ===
        capabilityState.selectedId
    )
      renderCapabilityPreview(capabilityState.pending.preview);
    showCatalogNotice(
      `${capabilityState.capabilities.length} safe capability ${capabilityState.capabilities.length === 1 ? "entry" : "entries"} loaded.`,
    );
    return true;
  } catch (error) {
    showCatalogNotice(error.message, true);
    return false;
  } finally {
    capabilityElements.refreshCapabilitiesButton.disabled = false;
    capabilityElements.loadMoreCapabilitiesButton.disabled = false;
  }
}

function renderCapabilityCatalog() {
  const query = capabilityElements.capabilitySearch.value
    .trim()
    .toLocaleLowerCase();
  const entries = capabilityState.capabilities.filter(
    (item) =>
      !query ||
      item.id.toLocaleLowerCase().includes(query) ||
      item.purpose.toLocaleLowerCase().includes(query),
  );
  const fragment = document.createDocumentFragment();
  for (const item of entries) {
    const button = capabilityNode("button", "capability-list-item");
    button.type = "button";
    button.role = "option";
    button.dataset.capabilityId = item.id;
    button.classList.toggle("selected", item.id === capabilityState.selectedId);
    button.setAttribute(
      "aria-selected",
      item.id === capabilityState.selectedId ? "true" : "false",
    );
    button.append(capabilityNode("span", "capability-list-name", item.id));
    const meta = capabilityNode("span", "capability-list-meta");
    meta.append(
      capabilityNode("span", "", `${item.kind} · ${item.version}`),
      capabilityNode("span", "", capabilityToken(item.state)),
    );
    button.append(meta);
    button.addEventListener("click", () => selectCapability(item));
    fragment.append(button);
  }
  capabilityElements.capabilityList.replaceChildren(fragment);
  if (entries.length === 0) {
    capabilityElements.capabilityList.append(
      capabilityNode(
        "p",
        "capability-notice",
        "No safe capability posture matches this filter.",
      ),
    );
  }
}

function selectCapability(item, focus = true) {
  capabilityState.selectedId = item?.id ?? null;
  renderCapabilityCatalog();
  capabilityElements.capabilityEmpty.hidden = Boolean(item);
  capabilityElements.capabilityContent.hidden = !item;
  capabilityElements.lifecyclePreview.hidden = true;
  capabilityElements.lifecyclePreview.replaceChildren();
  showLifecycleNotice("");
  if (!item) return;

  capabilityElements.capabilityKind.textContent = `${capabilityToken(item.kind)} · ${item.version}`;
  capabilityElements.capabilityTitle.textContent = item.id;
  capabilityElements.capabilityPurpose.textContent = item.purpose;
  capabilityElements.capabilityBadges.replaceChildren(
    badge(item.state),
    badge(item.enablement),
    badge(item.health),
    badge(item.trust),
  );
  capabilityElements.capabilityFacts.replaceChildren(
    fact("Provider", `${item.providerId} / ${item.implementationId}`),
    fact(
      "Provenance",
      `${capabilityToken(item.provenanceKind)} · ${item.sourceUri}`,
    ),
    fact("Contracts", `Schema 1 · ${item.descriptorHash}`),
    fact(
      "Compatibility",
      `${item.hostVersionRange} · ${item.supportedPlatforms.join(", ")} · ${item.isCurrentHostCompatible ? "current host compatible" : "current host incompatible"}`,
    ),
    fact(
      "Authority and egress",
      `${capabilityToken(item.sideEffectClass)} maximum · ${capabilityToken(item.egressMode)}${item.egressDestinations.length ? ` · ${item.egressDestinations.join(", ")}` : ""}`,
    ),
    fact(
      "Data and credentials",
      `${item.dataClasses.join(", ") || "No declared data classes"} · ${item.secretRequirements.join(", ") || "No secret references"}`,
    ),
    fact(
      "Catalog evidence",
      `entry ${item.entryRevision} · lifecycle ${item.lifecycleRevision ?? "unavailable"}${item.isRecovered ? " · recovered" : ""}`,
    ),
    fact(
      "Installation",
      `${capabilityToken(item.declaration)} · ${capabilityToken(item.installation)} · ${capabilityToken(item.retirement)}`,
    ),
    fact(
      "Dependent evidence",
      item.areDependentsAvailable
        ? `${item.dependents.length}${item.dependentsTruncated ? "+" : ""} safe dependents`
        : "Dependent set unavailable",
    ),
  );
  renderCapabilityDependents(item.dependents, item.areDependentsAvailable);
  if (focus) capabilityElements.capabilityTitle.focus();
}

function badge(value) {
  const token = String(value ?? "unknown");
  return capabilityNode(
    "span",
    `capability-badge ${token}`,
    capabilityToken(token),
  );
}

function fact(label, value) {
  const list = capabilityNode("dl", "capability-fact");
  list.append(capabilityNode("dt", "", label), capabilityNode("dd", "", value));
  return list;
}

function renderCapabilityDependents(dependents, available) {
  const fragment = document.createDocumentFragment();
  if (!available) {
    fragment.append(
      capabilityNode(
        "p",
        "capability-notice error",
        "The complete dependent set is unavailable, so lifecycle policy fails closed.",
      ),
    );
  }
  for (const item of dependents) {
    const isLoop = item.kind === "loop";
    const row = capabilityNode(isLoop ? "a" : "div", "capability-dependent");
    if (isLoop) {
      row.href = `/?view=loops&loopId=${encodeURIComponent(item.identity)}`;
      row.title = `Open Loops to inspect ${item.identity}`;
    }
    const identity = capabilityNode(
      "span",
      "",
      `${capabilityToken(item.kind)} · ${item.identity} · ${item.revision}`,
    );
    const posture = capabilityNode(
      "span",
      "",
      `${capabilityToken(item.requirementKind)} · ${capabilityToken(item.authorityPosture)}`,
    );
    row.append(identity, posture);
    fragment.append(row);
  }
  if (dependents.length === 0 && available) {
    fragment.append(
      capabilityNode(
        "p",
        "capability-notice",
        "No registered loop, skill, or package currently depends on this capability.",
      ),
    );
  }
  capabilityElements.capabilityDependents.replaceChildren(fragment);
}

async function previewCapabilityLifecycle(event) {
  event.preventDefault();
  if (!capabilityState.storageReady) {
    showLifecycleNotice(
      "Durable cross-tab browser coordination is unavailable, so no lifecycle preview was dispatched.",
      true,
    );
    return;
  }
  if (capabilityState.previewInFlight) {
    showLifecycleNotice(
      "The current lifecycle preview request is still being reconciled.",
      true,
    );
    return;
  }
  if (capabilityState.pending) {
    if (capabilityState.pending.preview) {
      showLifecycleNotice(
        "Discard or confirm the retained exact lifecycle preview before creating another operation.",
        true,
      );
      return;
    }
    await requestCapabilityPreview(capabilityState.pending.selection);
    return;
  }
  const capabilityId = capabilityState.selectedId;
  if (!capabilityId) return;
  const operation = capabilityElements.lifecycleOperation.value;
  const targetVersion = capabilityElements.targetVersion.value.trim() || null;
  if (operation === "upgrade" && !targetVersion) {
    showLifecycleNotice("Upgrade requires one exact target version.", true);
    return;
  }
  const selection = {
    operationId: createCapabilityOperationId(),
    operation,
    capabilityId,
    targetVersion,
  };
  capabilityState.pending = {
    selection,
    preview: null,
    disposition: "preview",
    evidence: null,
  };
  capabilityState.previewInFlight = true;
  try {
    await reservePendingCapabilityPreview(selection);
  } catch (error) {
    capabilityState.pending = null;
    showLifecycleNotice(error.message, true);
    return;
  } finally {
    capabilityState.previewInFlight = false;
  }
  await requestCapabilityPreview(selection);
}

async function requestCapabilityPreview(selection) {
  if (capabilityState.previewInFlight) return false;
  capabilityState.previewInFlight = true;
  capabilityElements.previewLifecycleButton.disabled = true;
  showLifecycleNotice("Creating a durable server-owned preview…");
  try {
    const response = await capabilityFetchJson(
      "/api/capabilities/lifecycle/preview",
      { method: "POST", body: JSON.stringify(selection) },
    );
    if (!isExactCapabilityPreview(response?.preview, selection)) {
      throw new Error(
        "The server response did not prove the exact requested lifecycle preview identity.",
      );
    }
    const disposition = capabilityState.pending?.disposition ?? "preview";
    capabilityState.pending = {
      selection,
      preview: response.preview,
      disposition,
      evidence: capabilityPreviewEvidence(response.preview),
    };
    const retained = await persistPendingCapabilityPreview(
      disposition,
      response.preview,
    );
    if (capabilityState.selectedId === selection.capabilityId)
      renderCapabilityPreview(response.preview);
    showLifecycleNotice(
      `${response.preview.detail}${capabilityState.selectedId === selection.capabilityId ? "" : " Select the previewed capability to inspect or confirm this exact operation."}${retained ? "" : " The exact selection was retained before dispatch, but the browser could not cache the returned preview details."}`,
      !retained,
    );
    return true;
  } catch (error) {
    showLifecycleNotice(error.message, true);
    if (
      isDefinitiveCapabilityLifecycleRejection(error) &&
      capabilityState.pending?.selection.operationId === selection.operationId
    )
      await clearPendingCapabilityPreview(selection.operationId);
    return false;
  } finally {
    capabilityState.previewInFlight = false;
    capabilityElements.previewLifecycleButton.disabled = false;
  }
}

function isExactCapabilityPreview(preview, selection) {
  const targetMatches =
    selection.operation === "enable" && selection.targetVersion === null
      ? typeof preview?.targetVersion === "string" &&
        preview.targetVersion.length > 0 &&
        preview.targetVersion.length <= 128
      : (preview?.targetVersion ?? null) === (selection.targetVersion ?? null);
  return (
    preview &&
    preview.operationId === selection.operationId &&
    preview.operation === selection.operation &&
    preview.capabilityId === selection.capabilityId &&
    targetMatches
  );
}

function renderCapabilityPreview(preview) {
  const summary = capabilityNode(
    "div",
    "lifecycle-summary",
    `${capabilityToken(preview.operation)} ${preview.capabilityId}${preview.targetVersion ? ` to ${preview.targetVersion}` : ""}. Catalog ${preview.baselineCatalogRevision}; lifecycle ${preview.lifecycleRevision}; dependent set ${preview.dependentSetRevision}. ${preview.isBlocked ? "Required dependents block this mutation." : preview.hasDegradation ? "Optional dependents will degrade." : "No registered dependent blocks this mutation."}`,
  );
  const fragment = document.createDocumentFragment();
  const evidence = capabilityNode(
    "div",
    "lifecycle-evidence",
    `Audit correlation ${preview.operationId} · catalog ${preview.baselineCatalogRevision} · activation ${preview.baselineActivationRevision} · lifecycle ${preview.lifecycleRevision} · dependent set ${preview.dependentSetRevision} (${preview.dependentSetHash}) · preview ${preview.previewHash}`,
  );
  fragment.append(summary, evidence);
  for (const impact of preview.impacts) {
    const row = capabilityNode("div", "lifecycle-impact");
    row.append(
      capabilityNode(
        "span",
        "",
        `${capabilityToken(impact.dependentKind)} · ${impact.dependentIdentity} · ${impact.dependentRevision}`,
      ),
      capabilityNode(
        "span",
        "",
        `${capabilityToken(impact.requirementKind)} · ${capabilityToken(impact.outcome)}`,
      ),
    );
    fragment.append(row);
  }
  const actions = capabilityNode("div", "lifecycle-actions");
  const discard = capabilityNode(
    "button",
    "secondary-button",
    "Discard preview",
  );
  discard.type = "button";
  discard.addEventListener("click", () => discardCapabilityLifecycle(discard));
  const confirm = capabilityNode(
    "button",
    "primary-button",
    `Confirm ${capabilityToken(preview.operation)}`,
  );
  confirm.type = "button";
  confirm.disabled = preview.isBlocked;
  confirm.addEventListener("click", () => confirmCapabilityLifecycle(confirm));
  actions.append(discard, confirm);
  fragment.append(actions);
  capabilityElements.lifecyclePreview.replaceChildren(fragment);
  capabilityElements.lifecyclePreview.hidden = false;
}

async function discardCapabilityLifecycle(button = null) {
  const pending = capabilityState.pending;
  if (!pending || (!pending.preview && !pending.evidence)) return;
  if (
    !(await persistPendingCapabilityPreview(
      "discard",
      pending.preview ?? pending.evidence,
    ))
  ) {
    showLifecycleNotice(
      "The discard intent could not be retained durably, so the server-owned preview was not retired.",
      true,
    );
    return;
  }

  if (button) button.disabled = true;
  showLifecycleNotice("Retiring the exact server-owned preview…");
  try {
    const result = await capabilityFetchJson(
      "/api/capabilities/lifecycle/discard",
      {
        method: "POST",
        body: JSON.stringify(capabilityDispositionInput(pending)),
      },
    );
    const cleared = await clearPendingCapabilityPreview(
      pending.selection.operationId,
    );
    capabilityElements.lifecyclePreview.hidden = true;
    showLifecycleNotice(
      `${capabilityToken(result.status)}. Operation ${pending.selection.operationId}. ${result.detail}${result.outcomeAuditPending ? " Audit repair remains pending." : ""}${cleared ? "" : " Browser reconciliation state could not be retired; reload will replay this exact discard intent."}`,
      result.outcomeAuditPending || !cleared,
    );
  } catch (error) {
    if (isDefinitiveCapabilityLifecycleRejection(error)) {
      const cleared = await clearPendingCapabilityPreview(
        pending.selection.operationId,
      );
      capabilityElements.lifecyclePreview.hidden = true;
      showLifecycleNotice(
        `${error.message}${cleared ? "" : " Browser reconciliation state could not be cleared."}`,
        true,
      );
    } else {
      showLifecycleNotice(
        `${error.message} The exact discard intent remains retained for replay.`,
        true,
      );
    }
  } finally {
    if (button) button.disabled = false;
  }
}

async function confirmCapabilityLifecycle(button = null, automatic = false) {
  const pending = capabilityState.pending;
  if (!pending || (!pending.preview && !pending.evidence)) return;
  const preview = pending.preview;
  if (!automatic && !preview) return;
  const confirmed =
    automatic ||
    window.confirm(
      `Confirm ${capabilityToken(preview.operation)} for ${preview.capabilityId}? This applies only the exact preview hash ${preview.previewHash} and does not assign capability authority.`,
    );
  if (!confirmed) {
    showLifecycleNotice(
      "Confirmation declined; the durable preview remains available.",
    );
    return;
  }

  if (
    !(await persistPendingCapabilityPreview(
      "confirm",
      preview ?? pending.evidence,
    ))
  ) {
    showLifecycleNotice(
      "The confirmation intent could not be retained durably, so no lifecycle mutation was dispatched.",
      true,
    );
    return;
  }

  if (button) button.disabled = true;
  showLifecycleNotice("Applying the exact confirmed preview…");
  const input = { ...capabilityDispositionInput(pending), confirmed: true };
  try {
    const result = await capabilityFetchJson(
      "/api/capabilities/lifecycle/confirm",
      { method: "POST", body: JSON.stringify(input) },
    );
    const cleared = await clearPendingCapabilityPreview(
      pending.selection.operationId,
    );
    capabilityElements.lifecyclePreview.hidden = true;
    await loadCapabilityCatalog(false);
    showLifecycleNotice(
      `${capabilityToken(result.status)}. Operation ${pending.selection.operationId}. ${result.detail}${result.outcomeAuditPending ? " Audit repair remains pending." : ""}${cleared ? "" : " Browser reconciliation state could not be cleared; a later reload may replay this exact terminal operation."}`,
      result.outcomeAuditPending || !cleared,
    );
  } catch (error) {
    if (isDefinitiveCapabilityLifecycleRejection(error)) {
      const cleared = await clearPendingCapabilityPreview(
        pending.selection.operationId,
      );
      capabilityElements.lifecyclePreview.hidden = true;
      showLifecycleNotice(
        `${error.message}${cleared ? "" : " Browser reconciliation state could not be cleared."}`,
        true,
      );
    } else {
      showLifecycleNotice(error.message, true);
    }
  } finally {
    if (button) button.disabled = false;
  }
}

async function restorePendingCapabilityPreview() {
  if (!capabilityState.storageKey) return;
  let retained = null;
  try {
    retained = await withCapabilityLifecycleRegistry(
      (registry) => ({
        registry,
        result:
          registry.entries.find(
            (entry) => entry.ownerId === capabilityState.tabId,
          ) ?? null,
      }),
      false,
    );
  } catch (error) {
    capabilityState.storageReady = false;
    capabilityElements.previewLifecycleButton.disabled = true;
    showLifecycleNotice(
      `Lifecycle changes are disabled: ${error.message}`,
      true,
    );
    return;
  }
  if (!retained) return;
  const selection = boundedRetainedCapabilitySelection(retained.selection);
  capabilityState.pending = {
    selection,
    preview: null,
    disposition: retained.disposition,
    evidence: retained.evidence,
  };
  if (retained.disposition === "confirm") {
    await confirmCapabilityLifecycle(null, true);
    return;
  }
  if (retained.disposition === "discard") {
    await discardCapabilityLifecycle();
    return;
  }
  await requestCapabilityPreview(selection);
  if (!capabilityState.pending?.preview) return;

  let capability = capabilityState.capabilities.find(
    (item) => item.id === selection.capabilityId,
  );
  if (!capability) {
    try {
      const response = await capabilityFetchJson(
        `/api/capabilities/detail?capabilityId=${encodeURIComponent(selection.capabilityId)}`,
      );
      capability = response.capability;
      if (!capability)
        throw new Error(
          "The exact capability posture was unavailable for pending-operation reconciliation.",
        );
      capabilityState.capabilities = [
        ...capabilityState.capabilities,
        capability,
      ].sort((left, right) => left.id.localeCompare(right.id));
    } catch (error) {
      showCatalogNotice(
        `${error.message} The exact pending operation remains retained for a later refresh.`,
        true,
      );
      return;
    }
  }
  selectCapability(capability, false);
  renderCapabilityPreview(capabilityState.pending.preview);
}

function configureCapabilityLifecycleStorage(scope) {
  if (
    !/^[0-9a-f]{64}$/.test(scope) ||
    !globalThis.localStorage?.getItem ||
    !globalThis.localStorage?.setItem ||
    !globalThis.sessionStorage?.getItem ||
    !globalThis.sessionStorage?.setItem ||
    !globalThis.navigator?.locks?.request ||
    !globalThis.crypto?.randomUUID
  ) {
    throw new Error("Durable cross-tab browser storage is unavailable.");
  }

  capabilityState.storageKey = `${capabilityLifecycleStorageKeyPrefix}.${scope}`;
  capabilityState.storageLockName = capabilityState.storageKey;
  capabilityState.tabStorageKey = capabilityLifecycleTabStorageKey;
  let tabId = globalThis.sessionStorage.getItem(capabilityState.tabStorageKey);
  if (tabId === null) {
    tabId = `capability-tab-${globalThis.crypto.randomUUID()}`;
    globalThis.sessionStorage.setItem(capabilityState.tabStorageKey, tabId);
    if (
      globalThis.sessionStorage.getItem(capabilityState.tabStorageKey) !== tabId
    ) {
      throw new Error("Browser tab identity was not retained exactly.");
    }
  }
  if (!isCapabilityTabId(tabId)) {
    throw new Error("The retained browser tab identity is invalid.");
  }
  capabilityState.tabId = tabId;
  capabilityState.storageReady = true;
}

function boundedRetainedCapabilitySelection(value) {
  const operations = new Set([
    "enable",
    "disable",
    "upgrade",
    "rollback",
    "remove",
  ]);
  if (
    !value ||
    typeof value.operationId !== "string" ||
    value.operationId.length < 1 ||
    value.operationId.length > 128 ||
    !/^[\x21-\x7e]+$/.test(value.operationId) ||
    typeof value.capabilityId !== "string" ||
    value.capabilityId.length < 1 ||
    value.capabilityId.length > 192 ||
    !operations.has(value.operation) ||
    (value.targetVersion !== null &&
      value.targetVersion !== undefined &&
      (typeof value.targetVersion !== "string" ||
        value.targetVersion.length < 1 ||
        value.targetVersion.length > 128))
  )
    return null;
  const targetVersion = value.targetVersion ?? null;
  if (
    (value.operation === "upgrade" && !targetVersion) ||
    (!["enable", "upgrade"].includes(value.operation) && targetVersion)
  )
    return null;
  return {
    operationId: value.operationId,
    operation: value.operation,
    capabilityId: value.capabilityId,
    targetVersion,
  };
}

async function reservePendingCapabilityPreview(selection) {
  if (!capabilityState.storageReady || !capabilityState.tabId) {
    throw new Error(
      "Browser reconciliation storage is unavailable, so no lifecycle preview was dispatched.",
    );
  }
  await withCapabilityLifecycleRegistry((registry) => {
    if (
      registry.entries.some((entry) => entry.ownerId === capabilityState.tabId)
    ) {
      throw new Error(
        "This browser tab already owns a retained lifecycle operation.",
      );
    }
    if (registry.entries.length >= maximumPendingCapabilityOperations) {
      throw new Error(
        `The workspace already has ${maximumPendingCapabilityOperations} retained browser lifecycle operations. Reconcile one before creating another.`,
      );
    }
    return {
      ...registry,
      entries: [
        ...registry.entries,
        {
          ownerId: capabilityState.tabId,
          selection,
          disposition: "preview",
          evidence: null,
        },
      ],
    };
  });
}

async function persistPendingCapabilityPreview(disposition, preview) {
  if (
    !capabilityState.storageReady ||
    !capabilityState.pending ||
    !["preview", "confirm", "discard"].includes(disposition)
  )
    return false;
  const evidence = capabilityPreviewEvidence(preview);
  try {
    await withCapabilityLifecycleRegistry((registry) => {
      const index = registry.entries.findIndex(
        (entry) =>
          entry.ownerId === capabilityState.tabId &&
          entry.selection.operationId ===
            capabilityState.pending.selection.operationId,
      );
      if (index < 0) {
        throw new Error(
          "The exact pending lifecycle operation is no longer retained by this tab.",
        );
      }
      const entries = [...registry.entries];
      entries[index] = { ...entries[index], disposition, evidence };
      return { ...registry, entries };
    });
    capabilityState.pending.disposition = disposition;
    capabilityState.pending.evidence = evidence;
    return true;
  } catch {
    return false;
  }
}

async function clearPendingCapabilityPreview(operationId) {
  if (!capabilityState.storageReady || !capabilityState.storageKey) {
    capabilityState.pending = null;
    return false;
  }
  try {
    await withCapabilityLifecycleRegistry((registry) => ({
      ...registry,
      entries: registry.entries.filter(
        (entry) =>
          entry.ownerId !== capabilityState.tabId ||
          entry.selection.operationId !== operationId,
      ),
    }));
    capabilityState.pending = null;
    return true;
  } catch {
    capabilityState.pending = null;
    return false;
  }
}

async function withCapabilityLifecycleRegistry(action, writeChanges = true) {
  if (
    !capabilityState.storageReady ||
    !capabilityState.storageKey ||
    !capabilityState.storageLockName
  ) {
    throw new Error("Durable cross-tab browser storage is unavailable.");
  }
  try {
    return await globalThis.navigator.locks.request(
      capabilityState.storageLockName,
      { mode: "exclusive" },
      async () => {
        const raw = globalThis.localStorage.getItem(capabilityState.storageKey);
        const registry =
          raw === null
            ? createEmptyCapabilityLifecycleRegistry()
            : parseCapabilityLifecycleRegistry(raw);
        const actionResult = await action(registry);
        const nextRegistry = actionResult?.registry ?? actionResult;
        const result = actionResult?.result;
        validateCapabilityLifecycleRegistry(nextRegistry);
        if (writeChanges) {
          const serialized = JSON.stringify(nextRegistry);
          globalThis.localStorage.setItem(
            capabilityState.storageKey,
            serialized,
          );
          if (
            globalThis.localStorage.getItem(capabilityState.storageKey) !==
            serialized
          ) {
            throw new Error(
              "Browser storage did not preserve the pending capability registry exactly.",
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
    throw new Error(`Durable browser coordination failed. ${error.message}`);
  }
}

function createEmptyCapabilityLifecycleRegistry() {
  return {
    schemaVersion: capabilityLifecycleRegistrySchemaVersion,
    scope: capabilityState.storageKey.slice(
      capabilityLifecycleStorageKeyPrefix.length + 1,
    ),
    entries: [],
  };
}

function parseCapabilityLifecycleRegistry(raw) {
  let registry;
  try {
    registry = JSON.parse(raw);
  } catch {
    throw new Error("Pending capability browser state is corrupt.");
  }
  validateCapabilityLifecycleRegistry(registry);
  return registry;
}

function validateCapabilityLifecycleRegistry(registry) {
  const expectedScope = capabilityState.storageKey.slice(
    capabilityLifecycleStorageKeyPrefix.length + 1,
  );
  if (
    !registry ||
    typeof registry !== "object" ||
    Array.isArray(registry) ||
    !hasExactCapabilityKeys(registry, ["entries", "schemaVersion", "scope"]) ||
    registry.schemaVersion !== capabilityLifecycleRegistrySchemaVersion ||
    registry.scope !== expectedScope ||
    !Array.isArray(registry.entries) ||
    registry.entries.length > maximumPendingCapabilityOperations
  ) {
    throw new Error(
      "Pending capability browser state is invalid or exceeds its bounded registry.",
    );
  }

  const owners = new Set();
  const operations = new Set();
  for (const entry of registry.entries) {
    const selection = boundedRetainedCapabilitySelection(entry?.selection);
    if (
      !entry ||
      typeof entry !== "object" ||
      Array.isArray(entry) ||
      !hasExactCapabilityKeys(entry, [
        "disposition",
        "evidence",
        "ownerId",
        "selection",
      ]) ||
      !isCapabilityTabId(entry.ownerId) ||
      owners.has(entry.ownerId) ||
      !selection ||
      !hasExactCapabilityKeys(entry.selection, [
        "capabilityId",
        "operation",
        "operationId",
        "targetVersion",
      ]) ||
      operations.has(selection.operationId) ||
      !["preview", "confirm", "discard"].includes(entry.disposition) ||
      !isCapabilityPreviewEvidence(entry.evidence, entry.disposition)
    ) {
      throw new Error(
        "Pending capability browser state contains an invalid operation entry.",
      );
    }
    owners.add(entry.ownerId);
    operations.add(selection.operationId);
  }
}

function isCapabilityPreviewEvidence(evidence, disposition) {
  if (evidence === null) return disposition === "preview";
  return (
    evidence &&
    typeof evidence === "object" &&
    !Array.isArray(evidence) &&
    hasExactCapabilityKeys(evidence, [
      "baselineActivationRevision",
      "baselineCatalogRevision",
      "dependentSetHash",
      "dependentSetRevision",
      "lifecycleRevision",
      "previewHash",
    ]) &&
    Number.isSafeInteger(evidence.baselineCatalogRevision) &&
    evidence.baselineCatalogRevision >= 0 &&
    Number.isSafeInteger(evidence.baselineActivationRevision) &&
    evidence.baselineActivationRevision >= 0 &&
    Number.isSafeInteger(evidence.lifecycleRevision) &&
    evidence.lifecycleRevision >= 1 &&
    Number.isSafeInteger(evidence.dependentSetRevision) &&
    evidence.dependentSetRevision >= 0 &&
    isCapabilityDigest(evidence.dependentSetHash) &&
    isCapabilityDigest(evidence.previewHash)
  );
}

function capabilityPreviewEvidence(preview) {
  if (!preview) return null;
  return {
    baselineCatalogRevision: preview.baselineCatalogRevision,
    baselineActivationRevision: preview.baselineActivationRevision,
    lifecycleRevision: preview.lifecycleRevision,
    dependentSetRevision: preview.dependentSetRevision,
    dependentSetHash: preview.dependentSetHash,
    previewHash: preview.previewHash,
  };
}

function capabilityDispositionInput(pending) {
  const evidence =
    pending.evidence ?? capabilityPreviewEvidence(pending.preview);
  return { ...pending.selection, ...evidence };
}

function isCapabilityTabId(value) {
  return (
    typeof value === "string" &&
    /^capability-tab-[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(
      value,
    )
  );
}

function isCapabilityDigest(value) {
  return (
    typeof value === "string" &&
    value.length >= 8 &&
    value.length <= 192 &&
    /^sha256:[\x21-\x7e]+$/.test(value)
  );
}

function hasExactCapabilityKeys(value, expected) {
  const keys = Object.keys(value).sort();
  return (
    keys.length === expected.length &&
    keys.every((key, index) => key === expected[index])
  );
}

function createCapabilityOperationId() {
  const suffix = globalThis.crypto?.randomUUID
    ? globalThis.crypto.randomUUID()
    : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  return `web-capability-${suffix}`;
}

function isDefinitiveCapabilityLifecycleRejection(error) {
  return (
    Number.isInteger(error?.status) && error.status >= 400 && error.status < 500
  );
}

function updateTargetVersionVisibility() {
  const operation = capabilityElements.lifecycleOperation.value;
  const relevant = operation === "enable" || operation === "upgrade";
  capabilityElements.targetVersionLabel.hidden = !relevant;
  capabilityElements.targetVersion.required = operation === "upgrade";
  if (!relevant) capabilityElements.targetVersion.value = "";
}

function showCatalogNotice(message, error = false) {
  capabilityElements.catalogNotice.textContent = message;
  capabilityElements.catalogNotice.classList.toggle("error", error);
}

function showLifecycleNotice(message, error = false) {
  capabilityElements.lifecycleNotice.textContent = message;
  capabilityElements.lifecycleNotice.classList.toggle("error", error);
}

capabilityElements.refreshCapabilitiesButton.addEventListener("click", () =>
  refreshCapabilityCatalog(),
);
capabilityElements.loadMoreCapabilitiesButton.addEventListener("click", () =>
  loadCapabilityCatalog(true),
);
capabilityElements.capabilitySearch.addEventListener(
  "input",
  renderCapabilityCatalog,
);
capabilityElements.lifecycleOperation.addEventListener(
  "change",
  updateTargetVersionVisibility,
);
capabilityElements.lifecyclePreviewForm.addEventListener(
  "submit",
  previewCapabilityLifecycle,
);
updateTargetVersionVisibility();
void capabilityBoot();

async function refreshCapabilityCatalog() {
  if ((await loadCapabilityCatalog(false)) && capabilityState.pending)
    await restorePendingCapabilityPreview();
}

globalThis.embodySenseCapabilityCatalog = {
  capabilityFetchJson,
  capabilityState,
  capabilityToken,
  clearPendingCapabilityPreview,
  renderCapabilityPreview,
  requestCapabilityPreview,
  selectCapability,
};
