import {
  addCatalogNode,
  candidateFromGraph,
  clientShapeErrors,
  compatibleBindings,
  configureNodeParameter,
  connectBinding,
  connectControl,
  connectorDecision,
  createGraphCandidate,
  currentGraph,
  descriptorKey,
  executableDescriptors,
  indexServerErrors,
  layoutOnlyMove,
  mutationInput,
  removeGraphNode,
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
      pendingMutation = restorePendingMutation();
      elements.graphId.value = "";
      restoreSelection();
      if (active && pendingMutation) restorePendingCandidate();
      if (active) render();
    },
    async activate() {
      active = true;
      if (!catalog) await refreshCatalog();
      restoreSelection();
      if (pendingMutation) restorePendingCandidate();
      render();
      if (elements.graphId.value && !aggregate && !graph && !pendingMutation)
        await readGraph(true);
    },
    deactivate() {
      active = false;
    },
    async refresh() {
      if (!active) return;
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
    elements.graphId.addEventListener("input", render);
    elements.revisionId.addEventListener("input", updateIdentity);
    elements.displayName.addEventListener("input", updateIdentity);
    elements.purpose.addEventListener("input", updateIdentity);
    elements.role.addEventListener("change", updateIdentity);
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
    });
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
    inFlight = true;
    if (!silent) outcome = "Reading immutable graph history…";
    render();
    try {
      const read = await requestJson(
        `/api/governed-graphs/detail?graphId=${encodeURIComponent(graphId)}`,
      );
      aggregate = read;
      graph = candidateFromGraph(currentGraph(read));
      selectedNodeId = graph?.nodes?.[0]?.id ?? null;
      errors = [];
      dirty = false;
      outcome = `Loaded durable ${read.lifecycle?.status ?? "unknown"} lifecycle version ${read.lifecycle?.lifecycleVersion ?? 0}.`;
      syncFieldsFromGraph();
      rememberSelection();
    } catch (error) {
      aggregate = null;
      graph = null;
      selectedNodeId = null;
      errors = error.payload?.errors ?? [];
      outcome =
        error.status === 404
          ? "No durable governed graph has this ID. Start a new local draft to create it."
          : `Graph read unavailable: ${error.message}`;
    } finally {
      inFlight = false;
      render();
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
      applyCurrentAggregate(response.current);
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
    selectedNodeId = nodeId;
    dirty = true;
    errors = [];
    outcome = `${humanize(contractItem.descriptor.kind)} node added from the exact server descriptor.`;
    render();
  }

  function removeSelectedNode() {
    if (!selectedNodeId) return;
    const next = removeGraphNode(graph, selectedNodeId);
    if (!next) return;
    graph = next;
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

  function syncFieldsFromGraph() {
    if (!graph) return;
    elements.graphId.value = graph.graphId ?? "";
    elements.revisionId.value = graph.revisionId ?? "";
    elements.displayName.value = graph.displayMetadata?.displayName ?? "";
    elements.purpose.value = graph.purpose ?? "";
    if (graph.owningRole)
      elements.role.value = `${graph.owningRole.identity.roleId}:${graph.owningRole.identity.revision}:${graph.owningRole.contentHash}`;
  }

  function render() {
    renderRoles();
    renderCatalog();
    renderCanvas();
    renderConnections();
    renderInspector();
    renderErrors();
    renderStatusOnly();
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
    ])
      field.disabled = inFlight || Boolean(pendingMutation);
    elements.lifecycle.textContent = aggregate?.lifecycle
      ? `${humanize(aggregate.lifecycle.status)} · lifecycle v${aggregate.lifecycle.lifecycleVersion} · ${aggregate.artifacts?.length ?? 0} immutable revision artifact${aggregate.artifacts?.length === 1 ? "" : "s"}`
      : graph
        ? "Local draft · not durable"
        : "No graph loaded";
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

  function renderCatalog() {
    elements.catalog.replaceChildren();
    for (const item of executableDescriptors(catalog)) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = `governed-node-palette-item kind-${item.descriptor.kind}`;
      button.disabled = inFlight || Boolean(pendingMutation);
      button.textContent = `${humanize(item.descriptor.kind)} · ${item.descriptor.typeId}`;
      button.title = `${descriptorKey(item.descriptor)} · ${item.parameters.length} typed parameters`;
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
    elements.inspector.append(
      fact(
        "Effective role",
        role ? `${role.roleId} r${role.revision}` : "Unavailable",
      ),
      fact(
        "Model",
        runtime.runtimeModel
          ? `${runtime.runtimeModel.provider} · ${runtime.runtimeModel.model || "provider default"}`
          : "Resolved at admission",
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
    const parameterHeading = document.createElement("h4");
    parameterHeading.textContent = "Typed server parameters";
    elements.inspector.append(parameterHeading);
    for (const parameter of contractItem?.parameters ?? [])
      elements.inspector.append(parameterField(parameter, node));

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
      );
      if (!next) return;
      graph = next;
      dirty = true;
      errors = [];
      renderStatusOnly();
    });
    label.append(title, input);
    return label;
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
    if (elements.graphId.value || !selectionKey) return;
    try {
      elements.graphId.value =
        window.sessionStorage?.getItem(selectionKey) ?? "";
    } catch {
      // Selection persistence is convenience only; durable evidence remains authoritative.
    }
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
