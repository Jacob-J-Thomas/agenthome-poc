import {
  addCatalogNode,
  candidateFromGraph,
  clientShapeErrors,
  compatibleBindings,
  configureGraphModelRouting,
  configureInferenceModelRouting,
  configureNodeRetryPolicy,
  configureNodeParameter,
  connectBinding,
  connectControl,
  connectorDecision,
  createGraphCandidate,
  currentGraph,
  descriptorKey,
  exactRoutingPolicyIntent,
  inheritedRoutingPolicyIntent,
  indexServerErrors,
  layoutOnlyMove,
  mutationInput,
  moveOrderedProfileSelection,
  removeGraphNode,
  selectHydratedNodeId,
  updateOrderedProfileSelection,
} from "./governed-graph-authoring.js";

const selectionKeyPrefix = "embodysense.governed-graph-selection.v1";
const pendingMutationKeyPrefix =
  "embodysense.governed-graph-pending-mutation.v1";
const retryableMutationKinds = new Set([
  "create-draft",
  "replace-draft",
  "publish",
  "disable",
  "archive",
]);
const conclusiveMutationStatuses = new Set([
  "committed",
  "replayed",
  "invalid",
  "validation-rejected",
  "limit-exceeded",
  "not-found",
  "conflict",
  "publication-rejected",
  "unauthorized",
]);

export function createGovernedGraphWorkspace({
  document,
  window,
  requestJson,
  operationId,
  runtimeCatalog,
  invokePublishedGraph,
}) {
  const elements = bindElements(document);
  let catalog = null;
  let aggregate = null;
  let graph = null;
  let selectedNodeId = null;
  let errors = [];
  let outcome = "";
  let active = false;
  let inFlight = false;
  let dirty = false;
  let storageScope = null;
  let selectionKey = null;
  let pendingMutationKey = null;
  let pendingMutation = null;
  let graphReadGeneration = 0;
  let activeGraphReadGeneration = null;
  let routingPreview = null;
  let routingPreviewGeneration = 0;
  let retryPolicyPreview = null;
  let graphFallbackOrder = [];
  let invocationPreparation = null;
  let invocationInFlight = false;
  let invocationOutcome = "";
  let invocationGrantSelection = null;
  let invocationOperationId = null;
  let invocationGeneration = 0;

  bindEvents();

  return Object.freeze({
    configureWorkspace(workspaceRoot) {
      if (typeof workspaceRoot !== "string" || !workspaceRoot)
        throw new Error(
          "The governed graph workspace identity is unavailable.",
        );
      const nextScope = encodeURIComponent(workspaceRoot.normalize("NFC"));
      if (storageScope === nextScope) return;
      if (storageScope !== null && (dirty || pendingMutation))
        throw new Error(
          "The governed graph workspace changed while an unresolved draft or mutation was retained.",
        );

      clearLegacyUnscopedStorage();
      invalidateGraphRead();
      storageScope = nextScope;
      selectionKey = `${selectionKeyPrefix}.${storageScope}`;
      pendingMutationKey = `${pendingMutationKeyPrefix}.${storageScope}`;
      aggregate = null;
      catalog = null;
      graph = null;
      selectedNodeId = null;
      errors = [];
      outcome = "";
      dirty = false;
      routingPreview = null;
      routingPreviewGeneration++;
      retryPolicyPreview = null;
      graphFallbackOrder = [];
      pendingMutation = restorePendingMutation();
      elements.graphId.value = "";
      restoreSelection();
      if (active && pendingMutation) restorePendingCandidate();
      if (active) render();
    },
    async activate() {
      active = true;
      const activationSelection = restoreSelection();
      if (!catalog) await refreshCatalog();
      if (pendingMutation) restorePendingCandidate();
      render();
      // Issue #470: https://github.com/Jacob-J-Thomas/agenthome-poc/issues/470 — only the exact pre-activation selection owns auto-hydration after this await.
      if (
        activationSelection &&
        elements.graphId.value.trim() === activationSelection &&
        !dirty &&
        !aggregate &&
        !graph &&
        !pendingMutation
      )
        await readGraph(true);
    },
    deactivate() {
      active = false;
    },
    async refresh() {
      if (!active) return;
      // Follow-up: https://github.com/Jacob-J-Thomas/agenthome-poc/issues/470 tracks making restored graph selection hydration conclusive across session reloads.
      await refreshCatalog();
      if (pendingMutation) {
        outcome =
          "The exact unresolved graph mutation was restored. Retry it before refreshing durable graph evidence.";
        render();
      } else if (elements.graphId.value && !dirty) await readGraph(true);
      else if (dirty) {
        outcome =
          "Authoritative catalog refreshed. Unsaved graph edits remain local until you explicitly reload durable evidence.";
        render();
      }
    },
    isDirty() {
      return dirty || Boolean(pendingMutation);
    },
  });

  function bindEvents() {
    elements.newButton.addEventListener("click", startNew);
    elements.loadButton.addEventListener("click", () => readGraph(false));
    elements.refreshButton.addEventListener("click", refreshDurable);
    elements.saveButton.addEventListener("click", saveDraft);
    elements.publishButton.addEventListener("click", publishDraft);
    elements.disableButton.addEventListener("click", disablePublication);
    elements.archiveButton.addEventListener("click", archivePublication);
    elements.prepareInvokeButton.addEventListener("click", prepareInvocation);
    elements.confirmInvokeButton.addEventListener("click", confirmAndInvoke);
    elements.grantSelection.addEventListener("change", () => {
      const choice =
        invocationPreparation?.eligibleGrants?.[
          Number(elements.grantSelection.value)
        ];
      invocationGrantSelection = visibleGrantSelection(choice);
      invocationOperationId = null;
      renderInvocation();
    });
    elements.graphId.addEventListener("input", () => {
      invalidateGraphRead();
      render();
    });
    elements.invocationPrompt.addEventListener("input", () => {
      invocationOperationId = null;
    });
    elements.revisionId.addEventListener("input", updateIdentity);
    elements.displayName.addEventListener("input", updateIdentity);
    elements.purpose.addEventListener("input", updateIdentity);
    elements.role.addEventListener("change", () => {
      updateIdentity();
      void refreshRoutingPreview();
    });
    elements.modelProfile.addEventListener("change", () => {
      graphFallbackOrder =
        updateOrderedProfileSelection(
          graphFallbackOrder,
          selectedOptionValues(elements.fallbackProfiles),
          elements.modelProfile.value,
        ) ?? [];
      updateGraphModelProfile();
    });
    elements.modelRoutingMode.addEventListener(
      "change",
      updateGraphModelProfile,
    );
    elements.fallbackProfiles.addEventListener("change", () => {
      graphFallbackOrder =
        updateOrderedProfileSelection(
          graphFallbackOrder,
          selectedOptionValues(elements.fallbackProfiles),
          elements.modelProfile.value,
        ) ?? [];
      updateGraphModelProfile();
    });
    elements.addControlButton.addEventListener("click", addControlEdge);
    elements.addBindingButton.addEventListener("click", addTypedBinding);
    elements.connectionFrom.addEventListener("change", renderConnections);
    elements.connectionTo.addEventListener("change", renderConnections);
  }

  async function refreshCatalog() {
    inFlight = true;
    outcome = "Loading the authoritative executable-node and role catalog…";
    render();
    try {
      catalog = await requestJson("/api/governed-graphs/catalog");
      outcome = "Authoritative catalog loaded.";
    } catch (error) {
      catalog = null;
      outcome = `Graph catalog unavailable: ${error.message}`;
    } finally {
      inFlight = false;
      render();
      if (graph) void refreshRoutingPreview();
    }
  }

  function startNew() {
    if (pendingMutation) {
      outcome =
        "Retry the exact unresolved graph mutation before starting another draft.";
      render();
      return;
    }
    if (dirty && !window.confirm("Discard this unsaved governed graph?"))
      return;
    const role = selectedRole();
    graph = createGraphCandidate({
      graphId: elements.graphId.value.trim(),
      revisionId: elements.revisionId.value.trim() || "revision-1",
      purpose: elements.purpose.value.trim() || "Execute one governed graph.",
      role,
      displayName: elements.displayName.value.trim(),
      defaultModelRoutingPolicy: selectedGraphRoutingPolicy(),
    });
    invalidateGraphRead();
    aggregate = null;
    selectedNodeId = null;
    errors = clientShapeErrors(graph);
    dirty = true;
    outcome =
      "Local draft created. Add only server-advertised executable nodes.";
    rememberSelection();
    render();
  }

  async function readGraph(silent) {
    if (pendingMutation) {
      outcome =
        "Retry the exact unresolved graph mutation before replacing it with durable evidence.";
      render();
      return;
    }
    const graphId = elements.graphId.value.trim();
    if (!graphId) {
      outcome = "Enter a canonical graph ID before loading.";
      render();
      return;
    }
    if (
      !silent &&
      dirty &&
      !window.confirm(
        "Discard unsaved governed graph edits and reload durable evidence?",
      )
    )
      return;
    invalidateInvocation();
    const readGeneration = beginGraphRead();
    if (!silent) outcome = "Reading immutable graph history…";
    render();
    try {
      const read = await requestJson(
        `/api/governed-graphs/detail?graphId=${encodeURIComponent(graphId)}`,
      );
      if (!ownsGraphRead(readGeneration, graphId)) return;
      // Issue #491: https://github.com/Jacob-J-Thomas/agenthome-poc/issues/491 — only the current detail request may replace locally owned graph state.
      aggregate = read;
      graph = candidateFromGraph(currentGraph(read));
      selectedNodeId = selectHydratedNodeId(graph?.nodes, selectedNodeId);
      errors = [];
      dirty = false;
      outcome = `Loaded durable ${read.lifecycle?.status ?? "unknown"} lifecycle version ${read.lifecycle?.lifecycleVersion ?? 0}.`;
      syncFieldsFromGraph();
      rememberSelection();
    } catch (error) {
      if (!ownsGraphRead(readGeneration, graphId)) return;
      aggregate = null;
      graph = null;
      selectedNodeId = null;
      errors = error.payload?.errors ?? [];
      outcome =
        error.status === 404
          ? "No durable governed graph has this ID. Start a new local draft to create it."
          : `Graph read unavailable: ${error.message}`;
    } finally {
      if (activeGraphReadGeneration === readGeneration) {
        activeGraphReadGeneration = null;
        inFlight = false;
        render();
        if (graph) void refreshRoutingPreview();
      }
    }
  }

  async function refreshDurable() {
    await refreshCatalog();
    if (pendingMutation) {
      outcome =
        "The catalog was refreshed, but the exact unresolved mutation must be retried before a durable graph reload.";
      render();
    } else if (elements.graphId.value.trim()) await readGraph(false);
  }

  function publishedInvocationSelector() {
    const revision = aggregate?.lifecycle?.publishedRevision?.revision;
    if (
      !revision?.graphId ||
      !revision?.revisionId ||
      elements.graphId.value.trim() !== revision.graphId ||
      dirty ||
      pendingMutation
    )
      return null;
    return { graphId: revision.graphId, revisionId: revision.revisionId };
  }

  async function prepareInvocation() {
    const selector = publishedInvocationSelector();
    if (!selector || invocationInFlight) return;
    const preparationGeneration = invalidateInvocation();
    invocationInFlight = true;
    renderInvocation();
    try {
      const preparation = await requestJson(
        "/api/governed-graphs/invocation-preparation",
        { method: "POST", body: JSON.stringify(selector) },
      );
      if (preparationGeneration !== invocationGeneration) return;
      invocationPreparation = preparation;
      invocationGrantSelection = visibleGrantSelection(
        invocationPreparation?.eligibleGrants?.[0],
      );
    } catch (error) {
      if (preparationGeneration !== invocationGeneration) return;
      invocationPreparation = {
        status: "unavailable",
        detail: `Preparation unavailable: ${error.message}`,
      };
    } finally {
      if (preparationGeneration === invocationGeneration) {
        invocationInFlight = false;
        renderInvocation();
      }
    }
  }

  async function confirmAndInvoke() {
    const selector = publishedInvocationSelector();
    const preparation = invocationPreparation;
    if (!selector || !preparation || invocationInFlight) return;
    const confirmationRequired = preparation.status === "confirmation-required";
    const ready = preparation.status === "ready";
    if (!confirmationRequired && !(ready && invocationGrantSelection)) return;
    const previewHash = confirmationRequired
      ? preparation.preview?.semanticHash
      : null;
    if (confirmationRequired && !previewHash) return;

    const invocationGenerationAtDispatch = invocationGeneration;
    invocationOperationId ??= operationId("governed-invoke");
    invocationInFlight = true;
    renderInvocation();
    try {
      const response = await invokePublishedGraph({
        graphId: selector.graphId,
        revisionId: selector.revisionId,
        previewHash,
        grantSelection: confirmationRequired ? null : invocationGrantSelection,
        operationId: invocationOperationId,
        invocationPrompt: elements.invocationPrompt.value.normalize("NFC"),
      });
      if (invocationGenerationAtDispatch !== invocationGeneration) return;
      invocationPreparation = null;
      invocationOperationId = null;
      const runId = response?.run?.id;
      invocationOutcome = runId
        ? `${humanize(response.status)} · exact run ${runId} is open in Runs.`
        : `${humanize(response?.status)} · ${response?.detail ?? "The server did not admit this invocation."}`;
    } catch (error) {
      if (invocationGenerationAtDispatch !== invocationGeneration) return;
      invocationOutcome = `Invocation unavailable: ${error.message}`;
    } finally {
      if (invocationGenerationAtDispatch === invocationGeneration) {
        invocationInFlight = false;
        renderInvocation();
      }
    }
  }

  async function saveDraft() {
    if (!graph || inFlight) return;
    if (pendingMutation) {
      await mutate(pendingMutation);
      return;
    }
    updateIdentity();
    const localErrors = clientShapeErrors(graph);
    if (localErrors.length) {
      errors = localErrors;
      outcome = "Correct the local shape errors before server validation.";
      render();
      return;
    }
    const kind = aggregate?.lifecycle ? "replace-draft" : "create-draft";
    await mutate(
      mutationInput(
        kind,
        graph,
        aggregate?.lifecycle,
        operationId("graph-draft"),
      ),
    );
  }

  async function publishDraft() {
    if (inFlight) return;
    if (pendingMutation) {
      await mutate(pendingMutation);
      return;
    }
    if (!aggregate?.lifecycle?.draftRevision || dirty) return;
    await mutate(
      mutationInput(
        "publish",
        graph,
        aggregate.lifecycle,
        operationId("graph-publish"),
      ),
    );
  }

  async function disablePublication() {
    await retirePublication("disable");
  }

  async function archivePublication() {
    await retirePublication("archive");
  }

  async function retirePublication(kind) {
    if (inFlight) return;
    if (pendingMutation) {
      await mutate(pendingMutation);
      return;
    }
    const lifecycle = aggregate?.lifecycle;
    if (!lifecycle?.publishedRevision) return;
    if (
      !window.confirm(
        `${humanize(kind)} the exact published revision of ${lifecycle.graphId}?`,
      )
    )
      return;
    await mutate(
      mutationInput(kind, graph, lifecycle, operationId(`graph-${kind}`)),
    );
  }

  async function mutate(input) {
    if (!persistPendingMutation(input)) {
      outcome =
        "The exact mutation identity could not be retained in this tab, so no graph mutation was sent.";
      render();
      return;
    }
    pendingMutation = input;
    invalidateGraphRead();
    inFlight = true;
    outcome = "Submitting exact graph and optimistic lifecycle evidence…";
    errors = [];
    render();
    try {
      const response = await requestJson("/api/governed-graphs/mutate", {
        method: "POST",
        body: JSON.stringify(input),
      });
      if (
        response?.operationId !== input.operationId ||
        !conclusiveMutationStatuses.has(response?.status)
      ) {
        throw new Error(
          "The mutation response did not prove the exact operation outcome. Retry the retained operation.",
        );
      }
      clearPendingMutation();
      aggregate = response.current;
      graph = candidateFromGraph(currentGraph(response.current)) ?? graph;
      selectedNodeId = selectHydratedNodeId(graph?.nodes, selectedNodeId);
      syncFieldsFromGraph();
      rememberSelection();
      errors = response.errors ?? [];
      dirty = false;
      outcome = `${humanize(response.status)} · ${humanize(response.changeKind)} · request ${response.authoringRequestHash || "not published"}`;
    } catch (error) {
      const payload = error.payload ?? {};
      if (
        payload.operationId === input.operationId &&
        conclusiveMutationStatuses.has(payload.status)
      )
        clearPendingMutation();
      if (payload.current) applyCurrentAggregate(payload.current);
      errors = payload.errors ?? [];
      outcome = pendingMutation
        ? `${humanize(payload.status ?? "unavailable")} · ${error.message} · exact operation retained for retry`
        : `${humanize(payload.status ?? "unavailable")} · ${error.message}`;
    } finally {
      inFlight = false;
      render();
      if (graph) void refreshRoutingPreview();
    }
  }

  function applyCurrentAggregate(current) {
    aggregate = current;
    graph = candidateFromGraph(currentGraph(current));
    selectedNodeId = graph?.nodes?.some((item) => item.id === selectedNodeId)
      ? selectedNodeId
      : (graph?.nodes?.[0]?.id ?? null);
    dirty = false;
    syncFieldsFromGraph();
    rememberSelection();
  }

  function addNode(contractItem) {
    if (!graph) startNew();
    if (!graph) return;
    const base =
      contractItem.descriptor.typeId
        .replace(/[^a-z0-9.-]+/g, "-")
        .replace(/^-+|-+$/g, "") || contractItem.descriptor.kind;
    let suffix = 1;
    let nodeId = base;
    while (graph.nodes.some((item) => item.id === nodeId))
      nodeId = `${base}-${++suffix}`;
    const next = addCatalogNode(
      graph,
      contractItem,
      nodeId,
      80 + graph.nodes.length * 180,
      100,
    );
    if (!next) return;
    graph = next;
    invalidateGraphRead();
    selectedNodeId = nodeId;
    dirty = true;
    errors = [];
    outcome = `${humanize(contractItem.descriptor.kind)} node added from the exact server descriptor.`;
    render();
    if (String(contractItem.descriptor.kind).toLowerCase() === "inference")
      void refreshRoutingPreview();
  }

  function removeSelectedNode() {
    if (!selectedNodeId) return;
    const next = removeGraphNode(graph, selectedNodeId);
    if (!next) return;
    graph = next;
    invalidateGraphRead();
    selectedNodeId = graph.nodes[0]?.id ?? null;
    dirty = true;
    errors = [];
    outcome =
      "Node and its incident connectors were removed from the local draft.";
    render();
  }

  function addControlEdge() {
    if (!graph) return;
    const from = elements.connectionFrom.value;
    const to = elements.connectionTo.value;
    const condition = elements.controlCondition.value;
    const next = connectControl(graph, catalog, from, to, condition);
    if (!next) {
      outcome =
        "That control connector is not advertised by the exact server descriptor pair.";
      render();
      return;
    }
    graph = next;
    invalidateGraphRead();
    dirty = true;
    outcome = `Control connector ${from} → ${to} added; server validation remains authoritative.`;
    render();
  }

  function addTypedBinding() {
    if (!graph) return;
    const from = elements.connectionFrom.value;
    const to = elements.connectionTo.value;
    const options = compatibleBindings(graph, from, to);
    const binding = options[Number(elements.bindingChoice.value) || 0];
    const next = connectBinding(graph, from, to, binding);
    if (!next) {
      outcome = "No exact typed port pair is available for that binding.";
      render();
      return;
    }
    graph = next;
    invalidateGraphRead();
    dirty = true;
    outcome = `${humanize(binding.kind)} binding ${from}.${binding.fromPortId} → ${to}.${binding.toPortId} added.`;
    render();
  }

  function updateIdentity() {
    if (!graph || pendingMutation) return;
    const role = selectedRole();
    graph = {
      ...graph,
      graphId: elements.graphId.value.trim(),
      revisionId: elements.revisionId.value.trim(),
      purpose: elements.purpose.value.trim(),
      owningRole: role
        ? {
            identity: { roleId: role.roleId, revision: role.revision },
            contentHash: role.contentHash,
          }
        : null,
      displayMetadata: {
        ...graph.displayMetadata,
        displayName: elements.displayName.value.trim(),
        description: elements.purpose.value.trim(),
      },
    };
    invalidateGraphRead();
    dirty = true;
    renderStatusOnly();
  }

  function selectedRole() {
    return (
      (catalog?.roles?.roles ?? []).find(
        (item) =>
          roleKey(item) === elements.role.value && item.isAdmissionReady,
      ) ?? null
    );
  }

  function selectedProfile() {
    return (
      (catalog?.modelProfiles?.profiles ?? []).find(
        (item) =>
          item.profileId === elements.modelProfile.value &&
          item.availabilityReason === "ready" &&
          item.recommendedExactPolicy,
      ) ?? null
    );
  }

  function selectedFallbackProfileIds(select = elements.fallbackProfiles) {
    if (select === elements.fallbackProfiles) return [...graphFallbackOrder];
    return selectedOptionValues(select);
  }

  function selectedGraphRoutingPolicy() {
    const profile = selectedProfile();
    if (!profile) return null;
    const fallbacks = selectedFallbackProfileIds();
    return elements.modelRoutingMode.value === "inherit"
      ? inheritedRoutingPolicyIntent(
          profile.recommendedExactPolicy,
          [profile.profileId],
          fallbacks,
        )
      : exactRoutingPolicyIntent(
          profile.recommendedExactPolicy,
          profile.profileId,
          fallbacks,
        );
  }

  function selectValues(select, values) {
    const selected = new Set(values ?? []);
    for (const option of select?.children ?? [])
      option.selected = !option.disabled && selected.has(option.value);
  }

  function updateGraphModelProfile() {
    if (!graph || pendingMutation) return;
    const profile = selectedProfile();
    const policy = selectedGraphRoutingPolicy();
    const next = policy ? configureGraphModelRouting(graph, policy) : null;
    if (!next) return;
    graph = next;
    dirty = true;
    errors = [];
    outcome = `${elements.modelRoutingMode.value === "inherit" ? "Configured-default routing bounded to" : "Exact default model profile set to"} ${profile.profileId}${selectedFallbackProfileIds().length ? ` with ${selectedFallbackProfileIds().length} ordered fallback candidate${selectedFallbackProfileIds().length === 1 ? "" : "s"}` : ""}; server admission remains authoritative and #339 executes only the admitted primary.`;
    render();
    void refreshRoutingPreview();
  }

  async function refreshRoutingPreview() {
    const node = graph?.nodes?.find((item) => item.id === selectedNodeId);
    const isInference =
      String(node?.descriptor?.kind ?? "").toLowerCase() === "inference";
    const policy = isInference
      ? (node.modelRoutingPolicy ?? graph?.defaultModelRoutingPolicy)
      : graph?.defaultModelRoutingPolicy;
    const role = selectedRole();
    const generation = ++routingPreviewGeneration;
    if (!policy || !role) {
      routingPreview = null;
      renderInspector();
      return;
    }
    routingPreview = {
      status: "loading",
      reason: "Server is recomputing exact current profile evidence.",
    };
    renderInspector();
    try {
      const response = await requestJson("/api/model-profiles/preview", {
        method: "POST",
        body: JSON.stringify({
          policy,
          roleId: role.roleId,
          nodeTypeId: isInference
            ? node.descriptor.typeId
            : "provider-inference",
          authoredInputDataClasses: isInference
            ? (node.authoredInputDataClasses ?? null)
            : null,
        }),
      });
      if (generation !== routingPreviewGeneration) return;
      routingPreview = response;
    } catch (error) {
      if (generation !== routingPreviewGeneration) return;
      routingPreview = error.payload ?? {
        status: "unavailable",
        reason: "The server-owned routing preview is unavailable.",
      };
    }
    renderInspector();
  }

  function syncFieldsFromGraph() {
    if (!graph) return;
    elements.graphId.value = graph.graphId ?? "";
    elements.revisionId.value = graph.revisionId ?? "";
    elements.displayName.value = graph.displayMetadata?.displayName ?? "";
    elements.purpose.value = graph.purpose ?? "";
    if (graph.owningRole)
      elements.role.value = `${graph.owningRole.identity.roleId}:${graph.owningRole.identity.revision}:${graph.owningRole.contentHash}`;
    const selector = graph.defaultModelRoutingPolicy?.selector;
    elements.modelRoutingMode.value = selector?.kind ?? "exact";
    const selectedId =
      selector?.exactProfileId ?? selector?.permittedInheritedProfileIds?.[0];
    if (selectedId) elements.modelProfile.value = selectedId;
    graphFallbackOrder = [
      ...(graph.defaultModelRoutingPolicy?.fallbackProfileIds ?? []),
    ];
    selectValues(elements.fallbackProfiles, graphFallbackOrder);
  }

  function render() {
    renderRoles();
    renderModelProfiles();
    renderCatalog();
    renderCanvas();
    renderConnections();
    renderInspector();
    renderErrors();
    renderStatusOnly();
    renderInvocation();
  }

  function renderStatusOnly() {
    elements.notice.textContent = outcome;
    elements.notice.className = `governed-graph-notice${errors.length ? " warning" : ""}`;
    const pendingDraft = ["create-draft", "replace-draft"].includes(
      pendingMutation?.kind,
    );
    const pendingPublish = pendingMutation?.kind === "publish";
    const pendingDisable = pendingMutation?.kind === "disable";
    const pendingArchive = pendingMutation?.kind === "archive";
    elements.newButton.disabled =
      inFlight || !catalog || Boolean(pendingMutation);
    elements.loadButton.disabled =
      inFlight || Boolean(pendingMutation) || !elements.graphId.value.trim();
    elements.refreshButton.disabled = inFlight;
    elements.saveButton.textContent = pendingDraft
      ? "Retry exact mutation"
      : "Save draft";
    elements.publishButton.textContent = pendingPublish
      ? "Retry exact publish"
      : "Publish draft";
    elements.disableButton.textContent = pendingDisable
      ? "Retry exact disable"
      : "Disable publication";
    elements.archiveButton.textContent = pendingArchive
      ? "Retry exact archive"
      : "Archive publication";
    elements.saveButton.disabled =
      inFlight || !graph || (pendingMutation ? !pendingDraft : !dirty);
    elements.publishButton.disabled =
      inFlight ||
      (pendingMutation
        ? !pendingPublish
        : dirty || !aggregate?.lifecycle?.draftRevision);
    elements.disableButton.disabled =
      inFlight ||
      (pendingMutation
        ? !pendingDisable
        : aggregate?.lifecycle?.status !== "published" || dirty);
    elements.archiveButton.disabled =
      inFlight ||
      (pendingMutation
        ? !pendingArchive
        : !["published", "disabled"].includes(aggregate?.lifecycle?.status) ||
          dirty);
    for (const field of [
      elements.graphId,
      elements.revisionId,
      elements.displayName,
      elements.purpose,
      elements.role,
      elements.modelRoutingMode,
      elements.modelProfile,
      elements.fallbackProfiles,
    ])
      field.disabled = inFlight || Boolean(pendingMutation);
    for (const button of elements.fallbackOrder.querySelectorAll?.("button") ??
      [])
      button.disabled = inFlight || Boolean(pendingMutation);
    elements.lifecycle.textContent = aggregate?.lifecycle
      ? `${humanize(aggregate.lifecycle.status)} · lifecycle v${aggregate.lifecycle.lifecycleVersion} · ${aggregate.artifacts?.length ?? 0} immutable revision artifact${aggregate.artifacts?.length === 1 ? "" : "s"}`
      : graph
        ? "Local draft · not durable"
        : "No graph loaded";
  }

  function renderInvocation() {
    const selector = publishedInvocationSelector();
    const preparation = invocationPreparation;
    const choices = preparation?.eligibleGrants ?? [];
    const canPrepare = Boolean(selector) && !invocationInFlight && !inFlight;
    const canConfirm =
      Boolean(selector) &&
      !invocationInFlight &&
      !inFlight &&
      ((preparation?.status === "confirmation-required" &&
        preparation.preview?.semanticHash) ||
        (preparation?.status === "ready" && invocationGrantSelection));
    elements.invocationPrompt.disabled =
      !selector || invocationInFlight || inFlight;
    elements.prepareInvokeButton.disabled = !canPrepare;
    elements.prepareInvokeButton.textContent = invocationInFlight
      ? "Preparing invocation"
      : "Prepare invocation";
    elements.confirmInvokeButton.hidden = !preparation || !canConfirm;
    elements.confirmInvokeButton.disabled = !canConfirm;
    elements.confirmInvokeButton.textContent =
      preparation?.status === "confirmation-required"
        ? "Confirm authority and invoke"
        : "Invoke with exact grant";
    elements.grantChoices.replaceChildren();
    elements.grantSelection.replaceChildren();
    elements.grantSelectionField.hidden = true;
    if (!selector) {
      elements.invocationStatus.textContent =
        "Publish the exact current draft with no unsaved changes before preparing a Manual Trigger invocation.";
      return;
    }
    if (!preparation) {
      elements.invocationStatus.textContent =
        invocationOutcome ||
        "Prepare current server authority before invoking. The browser cannot submit actor, workspace, role, profile, publication, eligibility, or effective-authority data.";
      return;
    }
    elements.invocationStatus.textContent =
      preparation.detail ?? "Server preparation completed.";
    if (preparation.status === "confirmation-required") {
      const preview = document.createElement("p");
      preview.textContent =
        "Explicit confirmation creates only the server-derived least-authority grant for this exact publication.";
      elements.grantChoices.append(preview);
    } else if (preparation.status === "ready") {
      const selections = choices.map(visibleGrantSelection);
      const selectedIndex = selections.findIndex((candidate) =>
        sameGrantSelection(candidate, invocationGrantSelection),
      );
      if (selectedIndex < 0) invocationGrantSelection = selections[0] ?? null;
      for (const [index, choice] of choices.entries()) {
        const choiceElement = document.createElement("p");
        choiceElement.textContent = `Eligible exact grant ${index + 1} · ${choice.grant?.grantId ?? "unavailable"} r${choice.grant?.revision ?? "unavailable"}${choice.expiresAtUtc ? ` · expires ${choice.expiresAtUtc}` : ""}`;
        elements.grantChoices.append(choiceElement);
      }
      for (const [index, choice] of choices.entries()) {
        const selection = selections[index];
        if (!selection) continue;
        const option = document.createElement("option");
        option.value = String(index);
        option.textContent = `${selection.grantId} r${selection.revision}${choice.expiresAtUtc ? ` · expires ${choice.expiresAtUtc}` : ""}`;
        elements.grantSelection.append(option);
      }
      const grantOptionCount = elements.grantSelection.children.length;
      elements.grantSelectionField.hidden = !grantOptionCount;
      elements.grantSelection.disabled =
        invocationInFlight || inFlight || !grantOptionCount;
      if (grantOptionCount)
        elements.grantSelection.value = String(
          Math.max(
            0,
            selections.findIndex((candidate) =>
              sameGrantSelection(candidate, invocationGrantSelection),
            ),
          ),
        );
    }
  }

  function visibleGrantSelection(choice) {
    const grant = choice?.grant;
    if (
      typeof grant?.grantId !== "string" ||
      !grant.grantId ||
      !Number.isSafeInteger(grant.revision) ||
      grant.revision < 1 ||
      typeof grant.contentHash !== "string" ||
      !grant.contentHash
    )
      return null;
    return {
      grantId: grant.grantId,
      revision: grant.revision,
      contentHash: grant.contentHash,
    };
  }

  function sameGrantSelection(left, right) {
    return (
      left?.grantId === right?.grantId &&
      left?.revision === right?.revision &&
      left?.contentHash === right?.contentHash
    );
  }

  function renderRoles() {
    const previous = elements.role.value;
    elements.role.replaceChildren();
    for (const role of catalog?.roles?.roles ?? []) {
      const option = document.createElement("option");
      option.value = roleKey(role);
      option.textContent = `${role.displayName} · ${role.roleId} r${role.revision}${role.isAdmissionReady ? "" : " · unavailable"}`;
      option.disabled = !role.isAdmissionReady;
      elements.role.append(option);
    }
    if ([...elements.role.children].some((item) => item.value === previous))
      elements.role.value = previous;
  }

  function renderModelProfiles() {
    const previous =
      graph?.defaultModelRoutingPolicy?.selector?.exactProfileId ??
      graph?.defaultModelRoutingPolicy?.selector
        ?.permittedInheritedProfileIds?.[0] ??
      elements.modelProfile.value;
    const selectableProfileIds = new Set();
    elements.modelProfile.replaceChildren();
    elements.fallbackProfiles.replaceChildren();
    for (const profile of catalog?.modelProfiles?.profiles ?? []) {
      const option = document.createElement("option");
      option.value = profile.profileId;
      option.textContent = `${profile.metadata?.modelId ?? profile.profileId} · ${profile.availabilityReason}${profile.profileId === catalog?.modelProfiles?.defaultProfileId ? " · configured default" : ""}`;
      option.disabled =
        profile.availabilityReason !== "ready" ||
        !profile.recommendedExactPolicy;
      if (!option.disabled) selectableProfileIds.add(profile.profileId);
      elements.modelProfile.append(option);
      const fallback = document.createElement("option");
      fallback.value = profile.profileId;
      fallback.textContent = `${profile.metadata?.modelId ?? profile.profileId} · ${profile.availabilityReason}`;
      fallback.disabled = option.disabled;
      elements.fallbackProfiles.append(fallback);
    }
    if (
      [...elements.modelProfile.children].some(
        (item) => item.value === previous && !item.disabled,
      )
    )
      elements.modelProfile.value = previous;
    for (const option of elements.fallbackProfiles.children)
      option.disabled =
        !selectableProfileIds.has(option.value) ||
        option.value === elements.modelProfile.value;
    selectValues(elements.fallbackProfiles, graphFallbackOrder);
    renderOrderedFallbackList(
      elements.fallbackOrder,
      graphFallbackOrder,
      (profileId, offset) => {
        const moved = moveOrderedProfileSelection(
          graphFallbackOrder,
          profileId,
          offset,
        );
        if (!moved) return;
        graphFallbackOrder = moved;
        selectValues(elements.fallbackProfiles, graphFallbackOrder);
        updateGraphModelProfile();
      },
      document,
    );
  }

  function renderCatalog() {
    elements.catalog.replaceChildren();
    const descriptors = [...(catalog?.nodeDescriptors ?? [])]
      .filter(
        (item) =>
          item.isAdvertised && (item.isExecutable || item.commandAction),
      )
      .sort((left, right) =>
        descriptorKey(left.descriptor).localeCompare(
          descriptorKey(right.descriptor),
        ),
      );
    for (const item of descriptors) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `governed-node-palette-item kind-${item.descriptor.kind}`;
      button.disabled =
        inFlight || Boolean(pendingMutation) || !item.isExecutable;
      button.textContent = item.commandAction
        ? `Command Action · ${item.commandAction.templateId} v${item.commandAction.templateVersion}${item.isExecutable ? "" : ` · ${humanize(item.commandAction.availability)}`}`
        : `${humanize(item.descriptor.kind)} · ${item.descriptor.typeId}`;
      button.title = item.commandAction
        ? `${descriptorKey(item.descriptor)} · ${item.parameters.length} typed parameters · ${item.commandAction.maxExecutionMilliseconds} ms execution · ${item.commandAction.maxOutputBytes} output bytes · ${item.commandAction.maxConcurrency} concurrent`
        : `${descriptorKey(item.descriptor)} · ${item.parameters.length} typed parameters`;
      button.addEventListener("click", () => addNode(item));
      elements.catalog.append(button);
    }
    if (!elements.catalog.children.length)
      elements.catalog.textContent =
        "No executable descriptors are currently provable.";
  }

  function renderCanvas() {
    elements.canvas.replaceChildren();
    for (const item of graph?.nodes ?? []) {
      const metadata = graph.displayMetadata?.nodes?.find(
        (value) => value.nodeId === item.id,
      );
      const card = document.createElement("button");
      card.type = "button";
      card.className = `governed-graph-node kind-${item.descriptor.kind}${item.id === selectedNodeId ? " selected" : ""}`;
      card.dataset.nodeId = item.id;
      card.disabled = inFlight;
      card.style.left = `${metadata?.canvasX ?? 0}px`;
      card.style.top = `${metadata?.canvasY ?? 0}px`;
      card.textContent = `${humanize(item.descriptor.kind)}\n${metadata?.displayName ?? item.id}\n${item.descriptor.typeId}`;
      card.addEventListener("click", () => {
        selectedNodeId = item.id;
        renderCanvas();
        renderInspector();
        if (String(item.descriptor.kind).toLowerCase() === "inference")
          void refreshRoutingPreview();
      });
      elements.canvas.append(card);
    }
    if (!graph?.nodes?.length)
      elements.canvas.textContent =
        "Start a local draft, then add only server-advertised nodes.";
  }

  function renderConnections() {
    const previousFrom = elements.connectionFrom.value;
    const previousTo = elements.connectionTo.value;
    elements.connectionFrom.replaceChildren();
    elements.connectionTo.replaceChildren();
    for (const item of graph?.nodes ?? []) {
      for (const select of [elements.connectionFrom, elements.connectionTo]) {
        const option = document.createElement("option");
        option.value = item.id;
        option.textContent = item.id;
        select.append(option);
      }
    }
    if ((graph?.nodes ?? []).some((item) => item.id === previousFrom))
      elements.connectionFrom.value = previousFrom;
    if ((graph?.nodes ?? []).some((item) => item.id === previousTo))
      elements.connectionTo.value = previousTo;
    const decision = connectorDecision(
      catalog,
      graph,
      elements.connectionFrom.value,
      elements.connectionTo.value,
    );
    elements.controlCondition.replaceChildren();
    for (const condition of decision.conditions ?? []) {
      const option = document.createElement("option");
      option.value = condition;
      option.textContent = humanize(condition);
      elements.controlCondition.append(option);
    }
    const bindings = compatibleBindings(
      graph,
      elements.connectionFrom.value,
      elements.connectionTo.value,
    );
    elements.bindingChoice.replaceChildren();
    bindings.forEach((binding, index) => {
      const option = document.createElement("option");
      option.value = String(index);
      option.textContent = `${humanize(binding.kind)} · ${binding.fromPortId} → ${binding.toPortId}`;
      elements.bindingChoice.append(option);
    });
    elements.addControlButton.disabled =
      inFlight || Boolean(pendingMutation) || !decision.allowed;
    elements.addBindingButton.disabled =
      inFlight || Boolean(pendingMutation) || bindings.length === 0;
    elements.connections.textContent = graph
      ? `${graph.controlEdges.length} control edge${graph.controlEdges.length === 1 ? "" : "s"} · ${graph.bindings.length} typed binding${graph.bindings.length === 1 ? "" : "s"}`
      : "No local graph";
  }

  function renderInspector() {
    elements.inspector.replaceChildren();
    const node = graph?.nodes?.find((item) => item.id === selectedNodeId);
    if (!node) {
      elements.inspector.textContent =
        "Select a governed graph node to inspect its exact descriptor, ports, parameters, and authority ceiling.";
      return;
    }
    const contractItem = catalog?.nodeDescriptors?.find(
      (item) =>
        descriptorKey(item.descriptor) === descriptorKey(node.descriptor),
    );
    const heading = document.createElement("h3");
    heading.textContent = `${node.id} · ${humanize(node.descriptor.kind)}`;
    elements.inspector.append(
      heading,
      fact("Descriptor", descriptorKey(node.descriptor)),
    );
    const role = selectedRole();
    const runtime = runtimeCatalog?.() ?? {};
    const capabilities = node.authorityCeiling?.capabilityIds ?? [];
    const incomingBindings = (graph.bindings ?? []).filter(
      (item) => item.toNodeId === node.id,
    );
    const effectiveRouting =
      node.modelRoutingPolicy ?? graph.defaultModelRoutingPolicy;
    const effectivePrimaryId =
      effectiveRouting?.selector?.exactProfileId ??
      effectiveRouting?.selector?.permittedInheritedProfileIds?.[0];
    const effectiveProfile = (catalog?.modelProfiles?.profiles ?? []).find(
      (profile) => profile.profileId === effectivePrimaryId,
    );
    elements.inspector.append(
      fact(
        "Effective role",
        role ? `${role.roleId} r${role.revision}` : "Unavailable",
      ),
      fact(
        "Model",
        node.modelRoutingPolicy?.selector?.exactProfileId ??
          graph.defaultModelRoutingPolicy?.selector?.exactProfileId ??
          (runtime.runtimeModel
            ? "Legacy display projection only"
            : "Unavailable"),
      ),
      fact(
        "Model profile evidence",
        routingPreview?.status === "eligible" && routingPreview.primary
          ? `Eligible · ${routingPreview.primary.capability.descriptorIdentity.id} · pin ${routingPreview.primary.contentHash} · config ${routingPreview.primary.metadata.configurationHash} · ${effectiveRouting?.selector?.kind ?? "unknown"} selector · ${(routingPreview.fallbacks ?? []).length} currently eligible fallback${(routingPreview.fallbacks ?? []).length === 1 ? "" : "s"}`
          : `${humanize(routingPreview?.status ?? "unavailable")} · ${routingPreview?.reason ?? "Server preview has not proved current eligibility."}`,
      ),
      fact(
        "Model privacy and budget preview",
        routingPreview?.primary?.metadata
          ? `${humanize(routingPreview.primary.metadata.privacy?.locality)} locality · ${humanize(routingPreview.primary.metadata.privacy?.egress)} egress · ${(routingPreview.primary.metadata.privacy?.acceptedDataClasses ?? []).join(", ") || "no accepted data class"} · context ≥ ${routingPreview.requirements?.minimumContextTokens ?? "unknown"} · output ≥ ${routingPreview.requirements?.minimumOutputTokens ?? "unknown"} · capabilities ${(routingPreview.requirements?.requiredCapabilities ?? []).map(humanize).join(", ") || "none"} · ${formatBudget(routingPreview.requirements?.budget)} · usage ${formatUsageSupport(routingPreview.primary.metadata.usageSupport)} · policy ${routingPreview.policyHash} · runtime admission still required`
          : effectiveProfile?.metadata
            ? `${humanize(effectiveProfile.metadata.privacy?.locality)} locality · ${humanize(effectiveProfile.metadata.privacy?.egress)} egress · usage ${formatUsageSupport(effectiveProfile.metadata.usageSupport)} · catalog-only evidence; runtime admission still required`
            : "Unavailable",
      ),
      fact(
        "Ordered model fallback candidates",
        (effectiveRouting?.fallbackProfileIds ?? []).length
          ? effectiveRouting.fallbackProfileIds.join(" → ")
          : "None",
      ),
      fact(
        "Governed tools and capabilities",
        capabilities.join(", ") || "None",
      ),
      fact("Node authority ceiling", capabilities.join(", ") || "None"),
      fact(
        "Role maximum",
        role?.capabilityMaximumIds?.join(", ") || "Unavailable",
      ),
      fact(
        "Context and typed dataflow",
        incomingBindings.length
          ? incomingBindings
              .map(
                (item) =>
                  `${item.fromNodeId}.${item.fromPortId} → ${item.toPortId}`,
              )
              .join(" · ")
          : "No incoming bindings",
      ),
      fact(
        "Ports",
        node.ports
          .map(
            (port) =>
              `${port.direction} ${port.bindingKind} ${port.id}:${port.valueSchemaId}`,
          )
          .join(" · ") || "None",
      ),
      fact(
        "Wait/failure/review posture",
        ["wait", "fail", "human-review", "human-input"].includes(
          String(node.descriptor.kind),
        )
          ? "Durable runtime posture is shown from the canonical run frontier."
          : "Not a gate node",
      ),
      fact(
        "Output",
        graph.outputContract?.outputs
          ?.filter((item) => item.sourceNodeId === node.id)
          .map((item) => item.id)
          .join(", ") || "No declared terminal output",
      ),
    );
    if (contractItem?.commandAction) {
      const command = contractItem.commandAction;
      elements.inspector.append(
        fact(
          "Command template",
          `${command.templateId} v${command.templateVersion} · ${command.templateHash}`,
        ),
        fact(
          "Command availability",
          `${humanize(command.availability)} · credentials ${command.requiresCredentialChannel ? "required but unavailable until the shared one-shot channel exists" : "not required"}`,
        ),
        fact(
          "Command isolation limits",
          `${humanize(command.workingDirectory)} working scope · ${humanize(command.network)} network · ${command.maxExecutionMilliseconds} ms execution · ${command.maxTerminationMilliseconds} ms termination · ${formatBytes(command.maxMemoryBytes)} memory · ${formatBytes(command.maxOutputBytes)} output · ${command.maxConcurrency} concurrent · process tree ${command.requiresProcessTreeTermination ? "must be proved terminal" : "not required"}`,
        ),
      );
    }
    const parameterHeading = document.createElement("h4");
    parameterHeading.textContent = "Typed server parameters";
    elements.inspector.append(parameterHeading);
    for (const parameter of contractItem?.parameters ?? [])
      elements.inspector.append(parameterField(parameter, node));

    if (String(node.descriptor.kind).toLowerCase() === "inference")
      elements.inspector.append(modelRoutingField(node));
    const retryField = retryPolicyField(node);
    if (retryField) elements.inspector.append(retryField);

    const position = graph.displayMetadata?.nodes?.find(
      (item) => item.nodeId === node.id,
    );
    const move = document.createElement("div");
    move.className = "governed-graph-move";
    const x = numericInput("Canvas X", position?.canvasX ?? 0);
    const y = numericInput("Canvas Y", position?.canvasY ?? 0);
    const apply = document.createElement("button");
    apply.type = "button";
    apply.textContent = "Move layout only";
    apply.disabled = inFlight || Boolean(pendingMutation);
    apply.addEventListener("click", () => {
      const next = layoutOnlyMove(
        graph,
        node.id,
        Number(x.input.value),
        Number(y.input.value),
      );
      if (!next) return;
      graph = next;
      invalidateGraphRead();
      dirty = true;
      outcome =
        "Layout metadata moved without changing executable node or connector content.";
      render();
    });
    move.append(x.label, y.label, apply);
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "danger-button";
    remove.textContent = "Remove node";
    remove.disabled = inFlight || Boolean(pendingMutation);
    remove.addEventListener("click", removeSelectedNode);
    elements.inspector.append(move, remove);
  }

  function parameterField(parameter, node) {
    const label = document.createElement("label");
    label.className = "governed-graph-field";
    const title = document.createElement("span");
    title.textContent = `${parameter.id}${parameter.required ? " · required" : ""}`;
    let input;
    if (parameter.allowedValues?.length || parameter.valueKind === "boolean") {
      input = document.createElement("select");
      const values = parameter.allowedValues?.length
        ? parameter.allowedValues
        : ["false", "true"];
      for (const value of values) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = value;
        input.append(option);
      }
    } else {
      input = document.createElement("input");
      input.type = parameter.valueKind === "integer" ? "number" : "text";
      if (parameter.minimumInteger != null)
        input.min = String(parameter.minimumInteger);
      if (parameter.maximumInteger != null)
        input.max = String(parameter.maximumInteger);
      if (parameter.maximumCharacters)
        input.maxLength = parameter.maximumCharacters;
    }
    input.value = node.parameters?.[parameter.id] ?? "";
    input.disabled = inFlight || Boolean(pendingMutation);
    input.addEventListener("input", () => {
      const next = configureNodeParameter(
        graph,
        node.id,
        parameter.id,
        input.value,
        !parameter.required,
      );
      if (!next) return;
      graph = next;
      invalidateGraphRead();
      dirty = true;
      errors = [];
      renderStatusOnly();
    });
    label.append(title, input);
    return label;
  }

  function modelRoutingField(node) {
    const wrapper = document.createElement("div");
    wrapper.className = "governed-graph-field";
    const label = document.createElement("label");
    const title = document.createElement("span");
    title.textContent = "Model routing override";
    const select = document.createElement("select");
    const fallbackLabel = document.createElement("label");
    const fallbackTitle = document.createElement("span");
    fallbackTitle.textContent = "Ordered override fallbacks";
    const fallbacks = document.createElement("select");
    fallbacks.multiple = true;
    const inherited = document.createElement("option");
    inherited.value = "";
    inherited.textContent = "Use graph default";
    select.append(inherited);
    for (const profile of catalog?.modelProfiles?.profiles ?? []) {
      const option = document.createElement("option");
      option.value = profile.profileId;
      option.textContent = `${profile.metadata?.modelId ?? profile.profileId} · ${profile.availabilityReason}`;
      option.disabled =
        profile.availabilityReason !== "ready" ||
        !profile.recommendedExactPolicy;
      select.append(option);
      const fallback = document.createElement("option");
      fallback.value = profile.profileId;
      fallback.textContent = option.textContent;
      fallback.disabled = option.disabled;
      fallbacks.append(fallback);
    }
    select.value = node.modelRoutingPolicy?.selector?.exactProfileId ?? "";
    for (const option of fallbacks.children)
      option.disabled = option.disabled || option.value === select.value;
    let fallbackOrder = [
      ...(node.modelRoutingPolicy?.fallbackProfileIds ?? []),
    ];
    selectValues(fallbacks, fallbackOrder);
    select.disabled = inFlight || Boolean(pendingMutation);
    fallbacks.disabled = select.disabled || !select.value;
    const fallbackOrderList = document.createElement("ol");
    fallbackOrderList.className = "governed-model-fallback-order";
    const update = (nextFallbackOrder = fallbackOrder) => {
      const profile = (catalog?.modelProfiles?.profiles ?? []).find(
        (item) => item.profileId === select.value,
      );
      fallbackOrder =
        updateOrderedProfileSelection(
          fallbackOrder,
          nextFallbackOrder,
          profile?.profileId ?? null,
        ) ?? [];
      selectValues(fallbacks, fallbackOrder);
      const policy = profile
        ? exactRoutingPolicyIntent(
            profile.recommendedExactPolicy,
            profile.profileId,
            fallbackOrder,
          )
        : null;
      const next = configureInferenceModelRouting(graph, node.id, policy);
      if (!next) return;
      graph = next;
      dirty = true;
      errors = [];
      outcome = profile
        ? `Inference node ${node.id} pinned to ${profile.profileId} with ${fallbackOrder.length} ordered fallback candidate${fallbackOrder.length === 1 ? "" : "s"}; #339 still executes only the admitted primary.`
        : `Inference node ${node.id} now uses the graph default model policy.`;
      render();
      void refreshRoutingPreview();
    };
    select.addEventListener("change", () =>
      update(selectedOptionValues(fallbacks)),
    );
    fallbacks.addEventListener("change", () =>
      update(selectedOptionValues(fallbacks)),
    );
    renderOrderedFallbackList(
      fallbackOrderList,
      fallbackOrder,
      (profileId, offset) => {
        const moved = moveOrderedProfileSelection(
          fallbackOrder,
          profileId,
          offset,
        );
        if (moved) update(moved);
      },
      document,
    );
    label.append(title, select);
    fallbackLabel.append(fallbackTitle, fallbacks, fallbackOrderList);
    wrapper.append(label, fallbackLabel);
    return wrapper;
  }

  function retryPolicyField(node) {
    const kind = String(node.descriptor?.kind ?? "").toLowerCase();
    if (
      [
        "trigger",
        "wait",
        "humanreview",
        "human-review",
        "humaninput",
        "human-input",
        "exit",
        "fail",
      ].includes(kind)
    )
      return null;
    const vocabulary = catalog?.retryPolicies;
    if (!vocabulary) return null;
    const policy = node.retryPolicy;
    const section = document.createElement("section");
    section.className = "governed-retry-policy";
    const heading = document.createElement("h4");
    heading.textContent = "Bounded retry policy";
    const status = document.createElement("p");
    status.textContent = policy
      ? `${policy.policyId} · ${policy.maximumAttempts} total attempts · ${humanize(policy.backoffStrategy)} backoff · ${policy.contentHash}`
      : "Off by default. Only an exact server-canonicalized policy can be attached.";
    section.append(heading, status);

    const policyId = textInput(
      "Policy ID",
      policy?.policyId ?? `retry-${node.id}`,
    );
    const failureClasses = document.createElement("select");
    failureClasses.multiple = true;
    for (const value of vocabulary.failureClasses ?? []) {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = humanize(value);
      option.selected = (
        policy?.failureClasses ?? ["retryable-no-effect"]
      ).includes(value);
      failureClasses.append(option);
    }
    const failureLabel = document.createElement("label");
    failureLabel.append(
      labelText("Retry-safe failure classes"),
      failureClasses,
    );
    const serverCodes = textInput(
      "Optional server codes",
      (policy?.serverCodes ?? []).join(","),
    );
    const attempts = boundedInput(
      "Maximum total attempts",
      policy?.maximumAttempts ?? 3,
      2,
      vocabulary.maximumAttempts,
    );
    const timeout = boundedInput(
      "Per-attempt timeout (ms)",
      policy?.perAttemptTimeoutMilliseconds ?? 60_000,
      1,
      vocabulary.maximumPerAttemptTimeoutMilliseconds,
    );
    const elapsed = boundedInput(
      "Maximum elapsed (ms)",
      policy?.maximumElapsedMilliseconds ?? 300_000,
      1,
      vocabulary.maximumElapsedMilliseconds,
    );
    const backoff = choiceInput(
      "Backoff",
      vocabulary.backoffStrategies,
      token(policy?.backoffStrategy) ?? "fixed",
    );
    const initialDelay = boundedInput(
      "Initial delay (ms)",
      policy?.initialDelayMilliseconds ?? 1_000,
      0,
      vocabulary.maximumDelayMilliseconds,
    );
    const maximumDelay = boundedInput(
      "Maximum delay (ms)",
      policy?.maximumDelayMilliseconds ?? 30_000,
      0,
      vocabulary.maximumDelayMilliseconds,
    );
    const jitter = choiceInput(
      "Jitter",
      vocabulary.jitterStrategies,
      token(policy?.jitterStrategy) ?? "deterministic-bounded",
    );
    const maximumJitter = boundedInput(
      "Maximum jitter (ms)",
      policy?.maximumJitterMilliseconds ?? 250,
      0,
      vocabulary.maximumDelayMilliseconds,
    );
    const maximumTokens = optionalBoundedInput(
      "Maximum tokens",
      policy?.maximumTokens,
      vocabulary.maximumTokens,
    );
    const maximumToolCalls = optionalBoundedInput(
      "Maximum tool calls",
      policy?.maximumToolCalls,
      vocabulary.maximumToolCalls,
    );
    const maximumCost = optionalBoundedInput(
      "Maximum cost microunits",
      policy?.maximumCostMicrounits,
      vocabulary.maximumCostMicrounits,
    );
    const costCurrency = textInput(
      "Cost currency",
      policy?.maximumCostCurrency ?? "",
    );
    const maximumResourceUnits = optionalBoundedInput(
      "Maximum resource units",
      policy?.maximumResourceUnits,
      vocabulary.maximumResourceUnits,
    );
    for (const input of [
      policyId.input,
      failureClasses,
      serverCodes.input,
      attempts.input,
      timeout.input,
      elapsed.input,
      backoff.input,
      initialDelay.input,
      maximumDelay.input,
      jitter.input,
      maximumJitter.input,
      maximumTokens.input,
      maximumToolCalls.input,
      maximumCost.input,
      costCurrency.input,
      maximumResourceUnits.input,
    ])
      input.disabled = inFlight || Boolean(pendingMutation);
    section.append(
      policyId.label,
      failureLabel,
      serverCodes.label,
      attempts.label,
      timeout.label,
      elapsed.label,
      backoff.label,
      initialDelay.label,
      maximumDelay.label,
      jitter.label,
      maximumJitter.label,
      maximumTokens.label,
      maximumToolCalls.label,
      maximumCost.label,
      costCurrency.label,
      maximumResourceUnits.label,
    );
    const apply = document.createElement("button");
    apply.type = "button";
    apply.textContent = policy
      ? "Revalidate and update retry"
      : "Preview and enable retry";
    apply.disabled = inFlight || Boolean(pendingMutation);
    apply.addEventListener("click", async () => {
      inFlight = true;
      outcome = `Server is canonicalizing the finite retry policy for ${node.id}…`;
      renderStatusOnly();
      try {
        const response = await requestJson(
          "/api/governed-graphs/retry-preview",
          {
            method: "POST",
            body: JSON.stringify({
              policyId: policyId.input.value.trim(),
              nodeId: node.id,
              failureClasses: selectedOptionValues(failureClasses),
              serverCodes: serverCodes.input.value
                .split(",")
                .map((value) => value.trim())
                .filter(Boolean)
                .sort(),
              maximumAttempts: Number(attempts.input.value),
              perAttemptTimeoutMilliseconds: Number(timeout.input.value),
              maximumElapsedMilliseconds: Number(elapsed.input.value),
              backoffStrategy: backoff.input.value,
              initialDelayMilliseconds: Number(initialDelay.input.value),
              maximumDelayMilliseconds: Number(maximumDelay.input.value),
              jitterStrategy: jitter.input.value,
              maximumJitterMilliseconds: Number(maximumJitter.input.value),
              maximumTokens: optionalNumber(maximumTokens.input.value),
              maximumToolCalls: optionalNumber(maximumToolCalls.input.value),
              maximumCostMicrounits: optionalNumber(maximumCost.input.value),
              maximumCostCurrency: costCurrency.input.value.trim() || null,
              maximumResourceUnits: optionalNumber(
                maximumResourceUnits.input.value,
              ),
            }),
          },
        );
        const next = configureNodeRetryPolicy(graph, node.id, response.policy);
        if (!next)
          throw new Error(
            "The server retry policy did not match the selected node.",
          );
        graph = next;
        retryPolicyPreview = { nodeId: node.id, response };
        dirty = true;
        errors = [];
        outcome = `Retry policy ${response.policy.policyId} is attached with ${response.preview.maximumAttempts} total attempts, at most ${response.preview.maximumReachableElapsedMilliseconds} ms reachable elapsed time, and current runtime admission still required.`;
      } catch (error) {
        retryPolicyPreview = {
          nodeId: node.id,
          response: error.payload ?? null,
        };
        outcome = `Retry policy rejected: ${error.message}`;
      } finally {
        inFlight = false;
        render();
      }
    });
    section.append(apply);
    if (policy) {
      const disable = document.createElement("button");
      disable.type = "button";
      disable.className = "secondary-button";
      disable.textContent = "Disable retry";
      disable.disabled = apply.disabled;
      disable.addEventListener("click", () => {
        const next = configureNodeRetryPolicy(graph, node.id, null);
        if (!next) return;
        graph = next;
        retryPolicyPreview = null;
        dirty = true;
        errors = [];
        outcome = `Retry is disabled for ${node.id}; single-attempt behavior is restored.`;
        render();
      });
      section.append(disable);
    }
    if (
      retryPolicyPreview?.nodeId === node.id &&
      retryPolicyPreview.response?.preview
    )
      section.append(
        fact(
          "Finite preview",
          `${retryPolicyPreview.response.preview.maximumRetries} retries · ${retryPolicyPreview.response.preview.maximumBackoffMilliseconds} ms backoff · ${retryPolicyPreview.response.preview.maximumAttemptExecutionMilliseconds} ms attempt execution · runtime admission still required`,
        ),
      );
    return section;
  }

  function labelText(value) {
    const span = document.createElement("span");
    span.textContent = value;
    return span;
  }

  function textInput(title, value) {
    const label = document.createElement("label");
    const input = document.createElement("input");
    input.type = "text";
    input.value = value ?? "";
    label.append(labelText(title), input);
    return { label, input };
  }

  function boundedInput(title, value, minimum, maximum) {
    const field = numericInput(title, value);
    field.input.min = String(minimum);
    field.input.max = String(maximum);
    return field;
  }

  function optionalBoundedInput(title, value, maximum) {
    const field = boundedInput(title, value ?? "", 0, maximum);
    field.input.placeholder = "Unbounded";
    return field;
  }

  function choiceInput(title, values, selected) {
    const label = document.createElement("label");
    const input = document.createElement("select");
    for (const value of values ?? []) {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = humanize(value);
      input.append(option);
    }
    input.value = selected;
    label.append(labelText(title), input);
    return { label, input };
  }

  function optionalNumber(value) {
    return String(value).trim() === "" ? null : Number(value);
  }

  function token(value) {
    return value == null
      ? null
      : String(value)
          .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
          .toLowerCase();
  }

  function renderErrors() {
    elements.errors.replaceChildren();
    const indexed = indexServerErrors(errors);
    for (const [identity, values] of indexed) {
      const section = document.createElement("section");
      const heading = document.createElement("strong");
      heading.textContent = identity;
      section.append(heading);
      for (const item of values) {
        const detail = document.createElement("div");
        detail.textContent = `${item.code} · ${item.path}${item.message ? ` · ${item.message}` : ""}`;
        section.append(detail);
      }
      elements.errors.append(section);
    }
  }

  function fact(label, value) {
    const item = document.createElement("div");
    item.className = "governed-graph-fact";
    const strong = document.createElement("strong");
    strong.textContent = label;
    const span = document.createElement("span");
    span.textContent = value;
    item.append(strong, span);
    return item;
  }

  function numericInput(title, value) {
    const label = document.createElement("label");
    const text = document.createElement("span");
    text.textContent = title;
    const input = document.createElement("input");
    input.type = "number";
    input.value = String(value);
    input.min = "-100000";
    input.max = "100000";
    input.disabled = inFlight || Boolean(pendingMutation);
    label.append(text, input);
    return { label, input };
  }

  function restorePendingMutation() {
    if (!pendingMutationKey || !storageScope) return null;
    let stored;
    try {
      stored = window.sessionStorage?.getItem(pendingMutationKey);
      if (!stored) return null;
      const payload = JSON.parse(stored);
      if (
        payload?.schemaVersion !== 1 ||
        payload.workspaceScope !== storageScope ||
        !payload.input ||
        typeof payload.input.operationId !== "string" ||
        !payload.input.operationId ||
        typeof payload.input.graphId !== "string" ||
        !payload.input.graphId ||
        typeof payload.input.kind !== "string" ||
        !retryableMutationKinds.has(payload.input.kind)
      ) {
        window.sessionStorage?.removeItem(pendingMutationKey);
        return null;
      }
      return payload.input;
    } catch {
      try {
        window.sessionStorage?.removeItem(pendingMutationKey);
      } catch {
        // A corrupt or unavailable convenience store cannot become mutation truth.
      }
      return null;
    }
  }

  function restorePendingCandidate() {
    invalidateGraphRead();
    elements.graphId.value = pendingMutation.graphId;
    graph = pendingMutation.graphCandidate
      ? structuredClone(pendingMutation.graphCandidate)
      : graph;
    selectedNodeId = graph?.nodes?.[0]?.id ?? null;
    dirty = Boolean(graph);
    errors = [];
    outcome =
      "The exact unresolved graph mutation was restored after reconnect. Retry it before editing or loading another graph.";
    syncFieldsFromGraph();
    rememberSelection();
  }

  function persistPendingMutation(input) {
    try {
      if (!window.sessionStorage || !pendingMutationKey || !storageScope)
        return false;
      window.sessionStorage.setItem(
        pendingMutationKey,
        JSON.stringify({
          schemaVersion: 1,
          workspaceScope: storageScope,
          input,
        }),
      );
      return true;
    } catch {
      return false;
    }
  }

  function clearPendingMutation() {
    try {
      window.sessionStorage?.removeItem(pendingMutationKey);
    } catch {
      // The in-memory identity is still cleared only after a conclusive exact outcome.
    }
    pendingMutation = null;
  }

  function restoreSelection() {
    if (!elements.graphId.value && selectionKey) {
      try {
        elements.graphId.value =
          window.sessionStorage?.getItem(selectionKey) ?? "";
      } catch {
        // Selection persistence is convenience only; durable evidence remains authoritative.
      }
    }
    return elements.graphId.value.trim();
  }

  function rememberSelection() {
    if (!selectionKey) return;
    try {
      window.sessionStorage?.setItem(
        selectionKey,
        elements.graphId.value.trim(),
      );
    } catch {
      // Selection persistence never changes mutation or reload semantics.
    }
  }

  function clearLegacyUnscopedStorage() {
    try {
      window.sessionStorage?.removeItem(selectionKeyPrefix);
      window.sessionStorage?.removeItem(pendingMutationKeyPrefix);
    } catch {
      // Legacy schema-1 convenience data is never migrated into a trusted workspace scope.
    }
  }

  function beginGraphRead() {
    activeGraphReadGeneration = ++graphReadGeneration;
    inFlight = true;
    return activeGraphReadGeneration;
  }

  function ownsGraphRead(readGeneration, graphId) {
    return (
      activeGraphReadGeneration === readGeneration &&
      elements.graphId.value.trim() === graphId &&
      !pendingMutation
    );
  }

  function invalidateGraphRead() {
    graphReadGeneration++;
    invalidateInvocation();
    if (activeGraphReadGeneration === null) return;
    activeGraphReadGeneration = null;
    inFlight = false;
  }

  function invalidateInvocation() {
    invocationGeneration++;
    invocationPreparation = null;
    invocationInFlight = false;
    invocationOutcome = "";
    invocationGrantSelection = null;
    invocationOperationId = null;
    return invocationGeneration;
  }
}

function bindElements(document) {
  return {
    addBindingButton: document.getElementById("governedGraphAddBindingButton"),
    addControlButton: document.getElementById("governedGraphAddControlButton"),
    bindingChoice: document.getElementById("governedGraphBindingChoice"),
    canvas: document.getElementById("governedGraphCanvas"),
    archiveButton: document.getElementById("governedGraphArchiveButton"),
    catalog: document.getElementById("governedGraphCatalog"),
    connectionFrom: document.getElementById("governedGraphConnectionFrom"),
    connectionTo: document.getElementById("governedGraphConnectionTo"),
    connections: document.getElementById("governedGraphConnections"),
    controlCondition: document.getElementById("governedGraphControlCondition"),
    displayName: document.getElementById("governedGraphDisplayName"),
    disableButton: document.getElementById("governedGraphDisableButton"),
    errors: document.getElementById("governedGraphErrors"),
    graphId: document.getElementById("governedGraphId"),
    inspector: document.getElementById("governedGraphInspector"),
    lifecycle: document.getElementById("governedGraphLifecycle"),
    loadButton: document.getElementById("governedGraphLoadButton"),
    newButton: document.getElementById("governedGraphNewButton"),
    notice: document.getElementById("governedGraphNotice"),
    publishButton: document.getElementById("governedGraphPublishButton"),
    purpose: document.getElementById("governedGraphPurpose"),
    modelProfile: document.getElementById("governedGraphModelProfile"),
    modelRoutingMode: document.getElementById("governedGraphModelRoutingMode"),
    fallbackProfiles: document.getElementById("governedGraphFallbackProfiles"),
    fallbackOrder: document.getElementById("governedGraphFallbackOrder"),
    prepareInvokeButton: document.getElementById(
      "governedGraphPrepareInvokeButton",
    ),
    confirmInvokeButton: document.getElementById(
      "governedGraphConfirmInvokeButton",
    ),
    invocationPrompt: document.getElementById("governedGraphInvocationPrompt"),
    invocationStatus: document.getElementById("governedGraphInvocationStatus"),
    grantChoices: document.getElementById("governedGraphGrantChoices"),
    grantSelection: document.getElementById("governedGraphGrantSelection"),
    grantSelectionField: document.getElementById(
      "governedGraphGrantSelectionField",
    ),
    refreshButton: document.getElementById("governedGraphRefreshButton"),
    revisionId: document.getElementById("governedGraphRevisionId"),
    role: document.getElementById("governedGraphRole"),
    saveButton: document.getElementById("governedGraphSaveButton"),
  };
}

function roleKey(role) {
  return `${role.roleId}:${role.revision}:${role.contentHash}`;
}

function humanize(value) {
  return String(value ?? "unknown")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replaceAll("-", " ")
    .replaceAll("_", " ")
    .replace(/^./, (character) => character.toUpperCase());
}

function selectedOptionValues(select) {
  return [...(select?.children ?? [])]
    .filter((option) => option.selected && !option.disabled)
    .map((option) => option.value);
}

function renderOrderedFallbackList(container, values, move, document) {
  container.replaceChildren();
  if (!values.length) {
    const empty = document.createElement("li");
    empty.textContent = "No fallback profile";
    container.append(empty);
    return;
  }
  values.forEach((profileId, index) => {
    const item = document.createElement("li");
    const label = document.createElement("span");
    label.textContent = `${index + 1}. ${profileId}`;
    const earlier = document.createElement("button");
    earlier.type = "button";
    earlier.textContent = "Earlier";
    earlier.disabled = index === 0;
    earlier.addEventListener("click", () => move(profileId, -1));
    const later = document.createElement("button");
    later.type = "button";
    later.textContent = "Later";
    later.disabled = index === values.length - 1;
    later.addEventListener("click", () => move(profileId, 1));
    item.append(label, earlier, later);
    container.append(item);
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

function formatBudget(budget) {
  if (!budget) return "budget unavailable";
  return [
    ["attempt", budget.perAttempt],
    ["node", budget.perNodeSeries],
    ["run", budget.perRun],
  ]
    .map(([scope, ceiling]) => `${scope} ${formatCeiling(ceiling)}`)
    .join(" · ");
}

function formatCeiling(ceiling) {
  if (!ceiling) return "unavailable";
  const token = (name, value) =>
    `${name} ${value?.isBounded ? `≤${value.maximum}` : "unbounded"}`;
  const monetary = ceiling.monetaryCost?.isBounded
    ? `cost ≤${ceiling.monetaryCost.maximumMicros} ${ceiling.monetaryCost.currency}µ`
    : "cost unbounded";
  return [
    token("input", ceiling.inputTokens),
    token("output", ceiling.outputTokens),
    token("cached", ceiling.cachedTokens),
    token("total", ceiling.totalTokens),
    monetary,
  ].join(", ");
}

function formatUsageSupport(support) {
  if (!support) return "support unavailable";
  return [
    "inputTokens",
    "outputTokens",
    "cachedTokens",
    "totalTokens",
    "monetaryCost",
  ]
    .map(
      (dimension) => `${humanize(dimension)} ${humanize(support[dimension])}`,
    )
    .join(", ");
}
