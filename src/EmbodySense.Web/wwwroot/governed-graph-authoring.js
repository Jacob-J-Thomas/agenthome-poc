const identifier = /^[a-z0-9](?:[a-z0-9.-]{0,126}[a-z0-9])?$/;

export function descriptorKey(descriptor) {
  return `${descriptor?.kind ?? "unknown"}:${descriptor?.typeId ?? ""}:${descriptor?.version ?? 0}`;
}

export function executableDescriptors(catalog) {
  return Object.freeze(
    [...(catalog?.nodeDescriptors ?? [])]
      .filter((item) => item.isAdvertised && item.isExecutable)
      .sort((left, right) =>
        descriptorKey(left.descriptor).localeCompare(
          descriptorKey(right.descriptor),
        ),
      ),
  );
}

export function connectorDecision(catalog, graph, fromNodeId, toNodeId) {
  const from = graph?.nodes?.find((item) => item.id === fromNodeId);
  const to = graph?.nodes?.find((item) => item.id === toNodeId);
  if (!from || !to)
    return Object.freeze({ allowed: false, reason: "node-not-found" });
  if (fromNodeId === toNodeId)
    return Object.freeze({ allowed: false, reason: "self-edge-not-admitted" });
  const fromContract = contract(catalog, from.descriptor);
  const toContract = contract(catalog, to.descriptor);
  if (!fromContract?.allowedControlOutcomes?.length)
    return Object.freeze({
      allowed: false,
      reason: "source-has-no-control-output",
    });
  if ((toContract?.minimumIncomingControlEdges ?? 0) < 1)
    return Object.freeze({ allowed: false, reason: "target-is-entry-only" });
  return Object.freeze({
    allowed: true,
    conditions: Object.freeze([...fromContract.allowedControlOutcomes]),
  });
}

export function layoutOnlyMove(graph, nodeId, canvasX, canvasY) {
  if (
    !graph ||
    !Number.isSafeInteger(canvasX) ||
    !Number.isSafeInteger(canvasY)
  )
    return null;
  const metadata = graph.displayMetadata ?? {
    displayName: graph.graphId ?? "Governed graph",
    description: "",
    nodes: [],
  };
  const current = metadata.nodes ?? [];
  if (!graph.nodes?.some((item) => item.id === nodeId)) return null;
  const nextNode = {
    ...(current.find((item) => item.nodeId === nodeId) ?? {
      nodeId,
      displayName: nodeId,
      description: "",
    }),
    canvasX,
    canvasY,
  };
  return {
    ...graph,
    displayMetadata: {
      ...metadata,
      nodes: [
        ...current.filter((item) => item.nodeId !== nodeId),
        nextNode,
      ].sort((left, right) => left.nodeId.localeCompare(right.nodeId)),
    },
  };
}

export function clientShapeErrors(graph) {
  const errors = [];
  if (!identifier.test(graph?.graphId ?? ""))
    errors.push(error("graph-id-invalid", "graph", graph?.graphId, "graphId"));
  if (!identifier.test(graph?.revisionId ?? ""))
    errors.push(
      error("revision-id-invalid", "graph", graph?.graphId, "revisionId"),
    );
  if (
    graph?.defaultModelRoutingPolicy !== undefined &&
    !isRoutingPolicyIntent(graph.defaultModelRoutingPolicy)
  )
    errors.push(
      error(
        "model-routing-default-invalid",
        "graph",
        graph?.graphId,
        "defaultModelRoutingPolicy",
      ),
    );
  const ids = new Set();
  for (const [index, node] of (graph?.nodes ?? []).entries()) {
    if (!identifier.test(node?.id ?? ""))
      errors.push(
        error("node-id-invalid", "node", node?.id, `nodes[${index}].id`),
      );
    else if (ids.has(node.id))
      errors.push(
        error("node-id-duplicate", "node", node.id, `nodes[${index}].id`),
      );
    ids.add(node?.id);
  }
  return Object.freeze(errors);
}

export function indexServerErrors(errors) {
  const indexed = new Map();
  for (const item of errors ?? []) {
    const kind = item.elementKind ?? item.element?.kind ?? "graph";
    const id = item.elementId ?? item.element?.id ?? "";
    const key = `${kind}:${id}`;
    const values = indexed.get(key) ?? [];
    values.push(
      Object.freeze({
        code: item.code,
        path: item.path ?? item.element?.path ?? "graph",
        message: item.message,
      }),
    );
    indexed.set(key, Object.freeze(values));
  }
  return indexed;
}

export function createGraphCandidate({
  graphId,
  revisionId,
  purpose,
  role,
  displayName,
  defaultModelRoutingPolicy,
}) {
  const graph = {
    schemaVersion: 1,
    graphId,
    revisionId,
    purpose,
    owningRole: role
      ? {
          identity: { roleId: role.roleId, revision: role.revision },
          contentHash: role.contentHash,
        }
      : null,
    entryNodeId: null,
    terminalNodeIds: [],
    authorityCeiling: { capabilityIds: [] },
    valueSchemas: [],
    nodes: [],
    controlEdges: [],
    bindings: [],
    outputContract: {
      summary: "Return declared terminal outputs.",
      outputs: [],
    },
    displayMetadata: {
      displayName: displayName || graphId || "Governed graph",
      description: purpose || "",
      nodes: [],
    },
  };
  const routing = routingPolicyIntent(defaultModelRoutingPolicy);
  if (routing) graph.defaultModelRoutingPolicy = routing;
  return graph;
}

export function candidateFromGraph(graph) {
  if (!graph) return null;
  const candidate = {
    schemaVersion: graph.schemaVersion,
    graphId: graph.graphId,
    revisionId: graph.revisionId,
    purpose: graph.purpose,
    owningRole: clone(graph.owningRole),
    entryNodeId: graph.entryNodeId,
    terminalNodeIds: [...(graph.terminalNodeIds ?? [])],
    authorityCeiling: clone(graph.authorityCeiling),
    valueSchemas: clone(graph.valueSchemas ?? []),
    nodes: clone(graph.nodes ?? []),
    controlEdges: clone(graph.controlEdges ?? []),
    bindings: clone(graph.bindings ?? []),
    outputContract: clone(graph.outputContract),
    displayMetadata: clone(graph.displayMetadata),
  };
  const routing = routingPolicyIntent(graph.defaultModelRoutingPolicy);
  if (routing) candidate.defaultModelRoutingPolicy = routing;
  return candidate;
}

export function exactRoutingPolicyIntent(
  template,
  profileId,
  fallbackProfileIds = [],
) {
  if (!isCapabilityId(profileId) || !isRoutingPolicyIntent(template))
    return null;
  if (!canonicalProfileOrder(fallbackProfileIds, profileId)) return null;
  const policy = routingPolicyIntent(template);
  policy.selector = {
    kind: "exact",
    exactProfileId: profileId,
    permittedInheritedProfileIds: [],
  };
  policy.fallbackProfileIds = [...fallbackProfileIds];
  return policy;
}

export function inheritedRoutingPolicyIntent(
  template,
  permittedProfileIds,
  fallbackProfileIds = [],
) {
  if (
    !isRoutingPolicyIntent(template) ||
    !canonicalProfileSet(permittedProfileIds) ||
    permittedProfileIds.length === 0 ||
    !canonicalProfileOrder(fallbackProfileIds) ||
    fallbackProfileIds.some((id) => permittedProfileIds.includes(id))
  )
    return null;
  const policy = routingPolicyIntent(template);
  policy.selector = {
    kind: "inherit",
    exactProfileId: null,
    permittedInheritedProfileIds: [...permittedProfileIds],
  };
  policy.fallbackProfileIds = [...fallbackProfileIds];
  return policy;
}

export function updateOrderedProfileSelection(
  previousOrder,
  selectedProfileIds,
  excludedProfileId = null,
) {
  if (
    !canonicalProfileOrder(previousOrder) ||
    !canonicalProfileOrder(selectedProfileIds) ||
    (excludedProfileId !== null && !isCapabilityId(excludedProfileId))
  )
    return null;
  const selectedValues = selectedProfileIds.filter(
    (profileId) => profileId !== excludedProfileId,
  );
  const selected = new Set(selectedValues);
  const retained = previousOrder.filter(
    (profileId) => profileId !== excludedProfileId && selected.has(profileId),
  );
  const retainedSet = new Set(retained);
  return [
    ...retained,
    ...selectedValues.filter((profileId) => !retainedSet.has(profileId)),
  ];
}

export function moveOrderedProfileSelection(values, profileId, offset) {
  if (
    !canonicalProfileOrder(values) ||
    !isCapabilityId(profileId) ||
    (offset !== -1 && offset !== 1)
  )
    return null;
  const current = values.indexOf(profileId);
  const target = current + offset;
  if (current < 0 || target < 0 || target >= values.length) return [...values];
  const moved = [...values];
  [moved[current], moved[target]] = [moved[target], moved[current]];
  return moved;
}

export function configureGraphModelRouting(graph, policy) {
  if (!graph || !isRoutingPolicyIntent(policy)) return null;
  const previousProfileIds = allRoutingProfileIds(graph);
  return reconcileRoutingAuthority(
    { ...graph, defaultModelRoutingPolicy: routingPolicyIntent(policy) },
    previousProfileIds,
  );
}

export function configureInferenceModelRouting(
  graph,
  nodeId,
  policy,
  authoredInputDataClasses = undefined,
) {
  const node = graph?.nodes?.find((item) => item.id === nodeId);
  if (
    !node ||
    String(node.descriptor?.kind ?? "").toLowerCase() !== "inference" ||
    (policy !== null && !isRoutingPolicyIntent(policy)) ||
    (authoredInputDataClasses !== undefined &&
      !canonicalDataClasses(authoredInputDataClasses))
  )
    return null;
  const previousProfileIds = allRoutingProfileIds(graph);
  return reconcileRoutingAuthority(
    {
      ...graph,
      nodes: graph.nodes.map((item) =>
        item.id === nodeId
          ? {
              ...item,
              modelRoutingPolicy:
                policy === null ? null : routingPolicyIntent(policy),
              authoredInputDataClasses:
                authoredInputDataClasses === undefined
                  ? clone(item.authoredInputDataClasses ?? null)
                  : clone(authoredInputDataClasses),
            }
          : item,
      ),
    },
    previousProfileIds,
  );
}

export function addCatalogNode(graph, contractItem, nodeId, canvasX, canvasY) {
  if (
    !graph ||
    !identifier.test(nodeId ?? "") ||
    graph.nodes?.some((item) => item.id === nodeId) ||
    !contractItem?.isAdvertised ||
    !contractItem?.isExecutable ||
    !Number.isSafeInteger(canvasX) ||
    !Number.isSafeInteger(canvasY)
  )
    return null;

  const schemas = new Map(
    (graph.valueSchemas ?? []).map((schema) => [schema.id, schema]),
  );
  const ports = (contractItem.ports ?? []).map((port) => {
    const kind = preferredValueKind(port.allowedValueKinds);
    const schemaId = ensureSchema(schemas, kind);
    return {
      id: port.id,
      direction: port.direction,
      bindingKind: port.bindingKind,
      valueSchemaId: schemaId,
      required: port.required,
    };
  });
  const isInference =
    String(contractItem.descriptor.kind).toLowerCase() === "inference";
  const requiredCapabilities = [
    ...(contractItem.requiredCapabilityIds ?? []),
    ...(isInference ? routingProfileIds(graph.defaultModelRoutingPolicy) : []),
  ]
    .filter((value, index, values) => values.indexOf(value) === index)
    .sort();
  const parameters = Object.fromEntries(
    (contractItem.parameters ?? []).map((parameter) => [
      parameter.id,
      defaultParameterValue(parameter),
    ]),
  );
  const node = {
    id: nodeId,
    descriptor: clone(contractItem.descriptor),
    ports,
    authorityCeiling: { capabilityIds: requiredCapabilities },
    parameters,
    ...(isInference
      ? { modelRoutingPolicy: null, authoredInputDataClasses: null }
      : {}),
  };
  const nodes = [...(graph.nodes ?? []), node];
  const terminal = contractItem.isLegalTerminal;
  return {
    ...graph,
    entryNodeId: contractItem.isLegalEntry
      ? (graph.entryNodeId ?? nodeId)
      : graph.entryNodeId,
    terminalNodeIds: terminal
      ? [...new Set([...(graph.terminalNodeIds ?? []), nodeId])]
      : [...(graph.terminalNodeIds ?? [])],
    authorityCeiling: {
      capabilityIds: [
        ...new Set([
          ...(graph.authorityCeiling?.capabilityIds ?? []),
          ...requiredCapabilities,
        ]),
      ].sort(),
    },
    valueSchemas: [...schemas.values()].sort((left, right) =>
      left.id.localeCompare(right.id),
    ),
    nodes,
    outputContract: terminal
      ? outputContractForTerminal(graph.outputContract, node)
      : clone(graph.outputContract),
    displayMetadata: {
      ...(graph.displayMetadata ?? {}),
      nodes: [
        ...(graph.displayMetadata?.nodes ?? []),
        {
          nodeId,
          displayName: nodeId,
          description: `${contractItem.descriptor.kind} · ${contractItem.descriptor.typeId}`,
          canvasX,
          canvasY,
        },
      ],
    },
  };
}

export function removeGraphNode(graph, nodeId) {
  if (!graph?.nodes?.some((item) => item.id === nodeId)) return null;
  const previousProfileIds = allRoutingProfileIds(graph);
  const nodes = graph.nodes.filter((item) => item.id !== nodeId);
  const terminalNodeIds = (graph.terminalNodeIds ?? []).filter(
    (item) => item !== nodeId,
  );
  return reconcileRoutingAuthority(
    {
      ...graph,
      entryNodeId: graph.entryNodeId === nodeId ? null : graph.entryNodeId,
      terminalNodeIds,
      nodes,
      controlEdges: (graph.controlEdges ?? []).filter(
        (item) => item.fromNodeId !== nodeId && item.toNodeId !== nodeId,
      ),
      bindings: (graph.bindings ?? []).filter(
        (item) => item.fromNodeId !== nodeId && item.toNodeId !== nodeId,
      ),
      outputContract: {
        ...(graph.outputContract ?? {}),
        outputs: (graph.outputContract?.outputs ?? []).filter(
          (item) => item.sourceNodeId !== nodeId,
        ),
      },
      displayMetadata: {
        ...(graph.displayMetadata ?? {}),
        nodes: (graph.displayMetadata?.nodes ?? []).filter(
          (item) => item.nodeId !== nodeId,
        ),
      },
    },
    previousProfileIds,
  );
}

export function connectControl(
  graph,
  catalog,
  fromNodeId,
  toNodeId,
  condition,
) {
  const decision = connectorDecision(catalog, graph, fromNodeId, toNodeId);
  if (!decision.allowed || !decision.conditions.includes(condition))
    return null;
  const id = uniqueId(
    `${fromNodeId}-to-${toNodeId}-${condition}`,
    graph.controlEdges,
  );
  return {
    ...graph,
    controlEdges: [
      ...(graph.controlEdges ?? []),
      { id, fromNodeId, toNodeId, condition },
    ],
  };
}

export function compatibleBindings(graph, fromNodeId, toNodeId) {
  const from = graph?.nodes?.find((item) => item.id === fromNodeId);
  const to = graph?.nodes?.find((item) => item.id === toNodeId);
  if (!from || !to) return [];
  return Object.freeze(
    (from.ports ?? [])
      .filter((port) => port.direction === "output")
      .flatMap((output) =>
        (to.ports ?? [])
          .filter(
            (input) =>
              input.direction === "input" &&
              input.bindingKind === output.bindingKind &&
              input.valueSchemaId === output.valueSchemaId,
          )
          .map((input) =>
            Object.freeze({
              kind: output.bindingKind,
              fromPortId: output.id,
              toPortId: input.id,
            }),
          ),
      ),
  );
}

export function connectBinding(graph, fromNodeId, toNodeId, binding) {
  const allowed = compatibleBindings(graph, fromNodeId, toNodeId).some(
    (item) =>
      item.kind === binding?.kind &&
      item.fromPortId === binding?.fromPortId &&
      item.toPortId === binding?.toPortId,
  );
  if (!allowed) return null;
  const id = uniqueId(
    `${fromNodeId}-${binding.fromPortId}-to-${toNodeId}-${binding.toPortId}`,
    graph.bindings,
  );
  return {
    ...graph,
    bindings: [
      ...(graph.bindings ?? []),
      {
        id,
        kind: binding.kind,
        fromNodeId,
        fromPortId: binding.fromPortId,
        toNodeId,
        toPortId: binding.toPortId,
      },
    ],
  };
}

export function configureNodeParameter(graph, nodeId, parameterId, value) {
  if (!graph?.nodes?.some((item) => item.id === nodeId)) return null;
  return {
    ...graph,
    nodes: graph.nodes.map((item) =>
      item.id === nodeId
        ? {
            ...item,
            parameters: { ...(item.parameters ?? {}), [parameterId]: value },
          }
        : item,
    ),
  };
}

export function mutationInput(kind, graph, lifecycle, operationId) {
  const hasCandidate = kind === "create-draft" || kind === "replace-draft";
  return {
    operationId,
    kind,
    graphId: graph?.graphId ?? lifecycle?.graphId ?? "",
    expectedLifecycleStatus: lifecycle?.status ?? "unknown",
    expectedLifecycleVersion: lifecycle?.lifecycleVersion ?? 0,
    expectedDraftRevision: lifecycle?.draftRevision ?? null,
    expectedPublishedRevision: lifecycle?.publishedRevision ?? null,
    graphCandidate: hasCandidate ? candidateFromGraph(graph) : null,
  };
}

export function currentGraph(readResponse) {
  const lifecycle = readResponse?.lifecycle;
  const revision =
    lifecycle?.draftRevision ?? lifecycle?.publishedRevision?.revision;
  if (!revision) return null;
  return (
    readResponse.artifacts?.find(
      (item) =>
        item.graph?.revisionReference?.graphId === revision.graphId &&
        item.graph?.revisionReference?.revisionId === revision.revisionId &&
        item.graph?.revisionReference?.executableHash ===
          revision.executableHash,
    )?.graph ?? null
  );
}

function contract(catalog, descriptor) {
  const key = descriptorKey(descriptor);
  return catalog?.nodeDescriptors?.find(
    (item) => descriptorKey(item.descriptor) === key,
  );
}

function error(code, kind, id, path) {
  return Object.freeze({
    code,
    element: Object.freeze({ kind, id: id ?? null, path }),
    message: code.replaceAll("-", " "),
  });
}

function preferredValueKind(kinds) {
  const preference = [
    "text",
    "object",
    "boolean",
    "integer",
    "number",
    "binary",
    "array",
  ];
  return preference.find((kind) => (kinds ?? []).includes(kind)) ?? "text";
}

function ensureSchema(schemas, kind) {
  if (kind === "array" && !schemas.has("value-text"))
    schemas.set("value-text", {
      id: "value-text",
      kind: "text",
      nullable: false,
      format: null,
      elementSchemaId: null,
    });
  const id = `value-${kind}`;
  if (!schemas.has(id))
    schemas.set(id, {
      id,
      kind,
      nullable: false,
      format: null,
      elementSchemaId: kind === "array" ? "value-text" : null,
    });
  return id;
}

function defaultParameterValue(parameter) {
  if ((parameter.allowedValues ?? []).length) return parameter.allowedValues[0];
  if (parameter.valueKind === "integer")
    return String(parameter.minimumInteger ?? 0);
  if (parameter.valueKind === "boolean") return "false";
  return "";
}

function outputContractForTerminal(current, node) {
  const outputs = (node.ports ?? [])
    .filter((port) => port.direction === "output")
    .map((port) => ({
      id: `${node.id}-${port.id}`,
      valueSchemaId: port.valueSchemaId,
      sourceNodeId: node.id,
      sourcePortId: port.id,
      required: port.required,
    }));
  return {
    summary: current?.summary || "Return declared terminal outputs.",
    outputs: [...(current?.outputs ?? []), ...outputs],
  };
}

function uniqueId(seed, existing) {
  const normalized =
    seed
      .toLowerCase()
      .replace(/[^a-z0-9.-]+/g, "-")
      .replace(/^-+|-+$/g, "")
      .slice(0, 120) || "connection";
  const ids = new Set((existing ?? []).map((item) => item.id));
  if (!ids.has(normalized)) return normalized;
  for (let suffix = 2; suffix < 1000; suffix++) {
    const candidate = `${normalized}-${suffix}`;
    if (!ids.has(candidate)) return candidate;
  }
  return `${normalized}-${Date.now()}`;
}

function clone(value) {
  return value == null ? value : structuredClone(value);
}

function routingPolicyIntent(value) {
  if (!isRoutingPolicyIntent(value)) return null;
  return stripDerivedHashes(clone(value));
}

function stripDerivedHashes(value) {
  if (Array.isArray(value)) return value.map(stripDerivedHashes);
  if (!value || typeof value !== "object") return value;
  return Object.fromEntries(
    Object.entries(value)
      .filter(([key]) => key !== "contentHash")
      .map(([key, item]) => [key, stripDerivedHashes(item)]),
  );
}

function isRoutingPolicyIntent(value) {
  if (!value || typeof value !== "object" || !value.requirements) return false;
  const kind = String(value.selector?.kind ?? "").toLowerCase();
  const exactProfileId = value.selector?.exactProfileId ?? null;
  const permitted = value.selector?.permittedInheritedProfileIds ?? [];
  const fallbacks = value.fallbackProfileIds ?? [];
  return kind === "exact"
    ? isCapabilityId(exactProfileId) &&
        Array.isArray(permitted) &&
        permitted.length === 0 &&
        canonicalProfileOrder(fallbacks, exactProfileId)
    : kind === "inherit" &&
        exactProfileId === null &&
        Array.isArray(permitted) &&
        permitted.length > 0 &&
        canonicalProfileOrder(permitted) &&
        canonicalProfileOrder(fallbacks) &&
        fallbacks.every((id) => !permitted.includes(id));
}

function canonicalProfileOrder(values, excluded = null) {
  if (!Array.isArray(values) || values.some((value) => !isCapabilityId(value)))
    return false;
  if (excluded !== null && values.includes(excluded)) return false;
  return new Set(values).size === values.length;
}

function canonicalProfileSet(values) {
  return (
    canonicalProfileOrder(values) &&
    values.every((value, index) => index === 0 || values[index - 1] < value)
  );
}

function isCapabilityId(value) {
  return (
    typeof value === "string" &&
    value.length > 2 &&
    value.length <= 256 &&
    /^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?\/[a-z0-9](?:[a-z0-9._/-]*[a-z0-9])?$/.test(
      value,
    )
  );
}

function canonicalDataClasses(values) {
  return (
    values === null ||
    (Array.isArray(values) &&
      values.every(
        (value) =>
          typeof value === "string" &&
          /^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$/.test(value),
      ) &&
      values.every((value, index) => index === 0 || values[index - 1] < value))
  );
}

function routingProfileIds(policy) {
  if (!isRoutingPolicyIntent(policy)) return [];
  const selectorIds =
    policy.selector.kind === "exact"
      ? [policy.selector.exactProfileId]
      : policy.selector.permittedInheritedProfileIds;
  return [...new Set([...selectorIds, ...policy.fallbackProfileIds])].sort();
}

function allRoutingProfileIds(graph) {
  return new Set([
    ...routingProfileIds(graph?.defaultModelRoutingPolicy),
    ...(graph?.nodes ?? []).flatMap((node) =>
      routingProfileIds(node?.modelRoutingPolicy),
    ),
  ]);
}

function reconcileRoutingAuthority(graph, previousProfileIds) {
  const currentProfileIds = [...allRoutingProfileIds(graph)].sort();
  const graphCapabilities = [
    ...(graph.authorityCeiling?.capabilityIds ?? []).filter(
      (id) => !previousProfileIds.has(id),
    ),
    ...currentProfileIds,
  ];
  return {
    ...graph,
    authorityCeiling: {
      ...(graph.authorityCeiling ?? {}),
      capabilityIds: [...new Set(graphCapabilities)].sort(),
    },
    nodes: (graph.nodes ?? []).map((node) => {
      if (String(node.descriptor?.kind ?? "").toLowerCase() !== "inference")
        return node;
      const effectivePolicy =
        node.modelRoutingPolicy ?? graph.defaultModelRoutingPolicy;
      const capabilities = [
        ...(node.authorityCeiling?.capabilityIds ?? []).filter(
          (id) => !previousProfileIds.has(id),
        ),
        ...routingProfileIds(effectivePolicy),
      ];
      return {
        ...node,
        authorityCeiling: {
          ...(node.authorityCeiling ?? {}),
          capabilityIds: [...new Set(capabilities)].sort(),
        },
      };
    }),
  };
}
