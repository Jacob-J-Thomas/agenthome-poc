import assert from "node:assert/strict";
import test from "node:test";

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
  exactRoutingPolicyIntent,
  inheritedRoutingPolicyIntent,
  indexServerErrors,
  layoutOnlyMove,
  mutationInput,
  moveOrderedProfileSelection,
  removeGraphNode,
  selectHydratedNodeId,
  updateOrderedProfileSelection,
} from "../../src/EmbodySense.Web/wwwroot/governed-graph-authoring.js";
import { projectFrontier } from "../../src/EmbodySense.Web/wwwroot/frontier-projection.js";
import {
  controlKinds,
  controlRequest,
  exactControl,
  postureSnapshot,
} from "../../src/EmbodySense.Web/wwwroot/operational-posture.js";

const hash = "a".repeat(64);
const defaultRoutingPolicy = {
  selector: {
    kind: "exact",
    exactProfileId: "org.example/model-a",
    permittedInheritedProfileIds: [],
    contentHash: "b".repeat(64),
  },
  fallbackProfileIds: [],
  requirements: {
    requiredModalities: ["text"],
    contentHash: "c".repeat(64),
  },
  contentHash: "d".repeat(64),
};

test("operational controls require their own exact advertised evidence even when another source is backpressured", () => {
  const response = {
    status: "backpressured",
    snapshot: {
      schemaVersion: 1,
      controlAuthorityEvidenceHash: hash,
      queue: { persistenceBackpressured: true },
    },
  };
  const schedule = {
    eligibleControls: [
      {
        kind: "disable-schedule",
        expectedRevision: 7,
        expectedEvidenceHash: "b".repeat(64),
      },
    ],
  };

  assert.equal(postureSnapshot(response), response.snapshot);
  assert.deepEqual(exactControl(schedule, controlKinds.disableSchedule), {
    kind: "disable-schedule",
    expectedRevision: 7,
    expectedEvidenceHash: "b".repeat(64),
  });
  assert.deepEqual(
    controlRequest({
      operationId: "operation-1",
      targetId: "schedule-1",
      owner: schedule,
      kind: controlKinds.disableSchedule,
      authorityEvidenceHash: hash,
    }),
    {
      operationId: "operation-1",
      kind: "disable-schedule",
      targetId: "schedule-1",
      expectedRevision: 7,
      expectedEvidenceHash: "b".repeat(64),
      expectedAuthorityEvidenceHash: hash,
      maximumBatchItems: 1,
    },
  );
  assert.equal(exactControl(schedule, controlKinds.cancelDelivery), null);
});

test("frontier projection preserves every reached activation and maps waiting, review, skipped, and terminal states", () => {
  const projected = projectFrontier({
    status: "NeedsReview",
    frontier: {
      schemaVersion: 1,
      frontierVersion: 9,
      status: "Waiting",
      contentHash: hash,
      nodes: [
        {
          nodeId: "review",
          kind: "HumanReview",
          typeId: "human-review",
          status: "Blocked",
          activationOrdinal: 3,
          planOrdinal: 3,
        },
        {
          nodeId: "wait",
          kind: "Wait",
          typeId: "timestamp",
          status: "Sleeping",
          activationOrdinal: 2,
          planOrdinal: 2,
        },
        {
          nodeId: "branch",
          kind: "Condition",
          typeId: "condition",
          status: "NotSelected",
          activationOrdinal: 1,
          planOrdinal: 1,
        },
        {
          nodeId: "exit",
          kind: "Exit",
          typeId: "success-exit",
          status: "Completed",
          activationOrdinal: 4,
          planOrdinal: 4,
        },
      ],
    },
  });

  assert.deepEqual(
    projected.nodes.map((item) => item.visualState),
    ["skipped", "waiting", "review-blocked", "terminal"],
  );
});

test("graph module keeps layout separate, permits only cataloged connectors, and preserves structured server errors", () => {
  const catalog = {
    nodeDescriptors: [
      {
        descriptor: { kind: "trigger", typeId: "manual-trigger", version: 1 },
        isAdvertised: true,
        isExecutable: true,
        allowedControlOutcomes: ["always"],
        minimumIncomingControlEdges: 0,
      },
      {
        descriptor: { kind: "exit", typeId: "success-exit", version: 1 },
        isAdvertised: true,
        isExecutable: true,
        allowedControlOutcomes: [],
        minimumIncomingControlEdges: 1,
      },
    ],
  };
  const graph = {
    graphId: "graph-1",
    revisionId: "revision-1",
    defaultModelRoutingPolicy: defaultRoutingPolicy,
    nodes: [
      { id: "trigger", descriptor: catalog.nodeDescriptors[0].descriptor },
      { id: "exit", descriptor: catalog.nodeDescriptors[1].descriptor },
    ],
    displayMetadata: { displayName: "Graph", description: "", nodes: [] },
  };

  assert.deepEqual(connectorDecision(catalog, graph, "trigger", "exit"), {
    allowed: true,
    conditions: ["always"],
  });
  assert.equal(
    connectorDecision(catalog, graph, "exit", "trigger").allowed,
    false,
  );
  const moved = layoutOnlyMove(graph, "exit", 500, 240);
  assert.equal(moved.nodes, graph.nodes);
  assert.deepEqual(moved.displayMetadata.nodes[0], {
    nodeId: "exit",
    displayName: "exit",
    description: "",
    canvasX: 500,
    canvasY: 240,
  });
  assert.equal(clientShapeErrors(graph).length, 0);
  const indexed = indexServerErrors([
    {
      code: "control-edge.condition.invalid",
      elementKind: "control-edge",
      elementId: "edge-1",
      path: "controlEdges[0].condition",
      message: "The condition is not advertised by the source descriptor.",
    },
    {
      code: "control-edge.target.invalid",
      elementKind: "control-edge",
      elementId: "edge-1",
      path: "controlEdges[0].targetNodeId",
      message: "The target node is unavailable.",
    },
  ]);
  assert.equal(indexed.get("control-edge:edge-1").length, 2);
  assert.equal(
    indexed.get("control-edge:edge-1")[0].path,
    "controlEdges[0].condition",
  );
  assert.equal(
    indexed.get("control-edge:edge-1")[1].path,
    "controlEdges[0].targetNodeId",
  );
});

test("cataloged Fail terminals preserve exact Failure routing and omit optional agent-selected parameters until authored", () => {
  const inference = {
    descriptor: { kind: "inference", typeId: "model-inference", version: 1 },
    isAdvertised: true,
    isExecutable: true,
    isLegalEntry: false,
    isLegalTerminal: false,
    allowedControlOutcomes: ["success", "failure"],
    minimumIncomingControlEdges: 1,
    ports: [],
    parameters: [
      { id: "instruction", valueKind: "text", required: true },
      {
        id: "max-iterations",
        valueKind: "integer",
        required: false,
        minimumInteger: 1,
      },
      {
        id: "max-duration-milliseconds",
        valueKind: "integer",
        required: false,
        minimumInteger: 1,
      },
    ],
    requiredCapabilityIds: [],
  };
  const fail = {
    descriptor: { kind: "fail", typeId: "fail-terminal", version: 1 },
    isAdvertised: true,
    isExecutable: true,
    isLegalEntry: false,
    isLegalTerminal: true,
    allowedControlOutcomes: [],
    minimumIncomingControlEdges: 1,
    ports: [],
    parameters: [
      { id: "code", valueKind: "text", required: false, maximumCharacters: 64 },
      {
        id: "explanation",
        valueKind: "text",
        required: false,
        maximumCharacters: 256,
      },
    ],
    requiredCapabilityIds: [],
  };
  const catalog = { nodeDescriptors: [inference, fail] };
  let graph = {
    graphId: "graph-failure",
    revisionId: "revision-failure",
    defaultModelRoutingPolicy: defaultRoutingPolicy,
    nodes: [],
    controlEdges: [],
    bindings: [],
    terminalNodeIds: [],
    authorityCeiling: { capabilityIds: [] },
    valueSchemas: [],
    outputContract: { summary: "Failure graph", outputs: [] },
    displayMetadata: {
      displayName: "Failure graph",
      description: "",
      nodes: [],
    },
  };
  graph = addCatalogNode(graph, inference, "infer", 0, 0);
  graph = addCatalogNode(graph, fail, "fail", 200, 0);

  assert.deepEqual(graph.nodes.find((item) => item.id === "infer").parameters, {
    instruction: "",
  });
  assert.deepEqual(
    graph.nodes.find((item) => item.id === "fail").parameters,
    {},
  );
  assert.deepEqual(connectorDecision(catalog, graph, "infer", "fail"), {
    allowed: true,
    conditions: ["success", "failure"],
  });
  graph = connectControl(graph, catalog, "infer", "fail", "failure");
  assert.equal(graph.controlEdges[0].condition, "failure");

  graph = configureNodeParameter(graph, "fail", "code", "agent-selected", true);
  graph = configureNodeParameter(
    graph,
    "fail",
    "explanation",
    "Stop deliberately.",
    true,
  );
  assert.deepEqual(graph.nodes.find((item) => item.id === "fail").parameters, {
    code: "agent-selected",
    explanation: "Stop deliberately.",
  });
  graph = configureNodeParameter(graph, "fail", "code", "", true);
  assert.equal(
    Object.hasOwn(
      graph.nodes.find((item) => item.id === "fail").parameters,
      "code",
    ),
    false,
  );
});

test("graph mutations use only catalog contracts and lifecycle evidence and never accept trusted identity fields", () => {
  const role = {
    roleId: "researcher",
    revision: 2,
    contentHash: "b".repeat(64),
  };
  const catalog = {
    nodeDescriptors: [
      {
        descriptor: { kind: "trigger", typeId: "manual-trigger", version: 1 },
        isAdvertised: true,
        isExecutable: true,
        isLegalEntry: true,
        isLegalTerminal: false,
        allowedControlOutcomes: ["always"],
        minimumIncomingControlEdges: 0,
        ports: [
          {
            id: "request",
            direction: "output",
            bindingKind: "data",
            allowedValueKinds: ["text"],
            required: true,
          },
        ],
        parameters: [],
        requiredCapabilityIds: [],
      },
      {
        descriptor: { kind: "exit", typeId: "success-exit", version: 1 },
        isAdvertised: true,
        isExecutable: true,
        isLegalEntry: false,
        isLegalTerminal: true,
        allowedControlOutcomes: [],
        minimumIncomingControlEdges: 1,
        ports: [
          {
            id: "result",
            direction: "input",
            bindingKind: "data",
            allowedValueKinds: ["text"],
            required: true,
          },
          {
            id: "published-result",
            direction: "output",
            bindingKind: "data",
            allowedValueKinds: ["text"],
            required: true,
          },
        ],
        parameters: [],
        requiredCapabilityIds: ["org.embodysense/conversation-turn"],
      },
    ],
  };
  let graph = createGraphCandidate({
    graphId: "graph-1",
    revisionId: "revision-1",
    purpose: "Test one graph.",
    role,
    displayName: "Graph",
    defaultModelRoutingPolicy: defaultRoutingPolicy,
  });
  graph = addCatalogNode(graph, catalog.nodeDescriptors[0], "trigger", 0, 0);
  graph = addCatalogNode(graph, catalog.nodeDescriptors[1], "exit", 200, 0);
  graph = connectControl(graph, catalog, "trigger", "exit", "always");
  const bindings = compatibleBindings(graph, "trigger", "exit");
  graph = connectBinding(graph, "trigger", "exit", bindings[0]);

  const lifecycle = {
    graphId: "graph-1",
    status: "draft",
    lifecycleVersion: 3,
    draftRevision: {
      schemaVersion: 1,
      graphId: "graph-1",
      revisionId: "revision-0",
      executableHash: hash,
    },
    publishedRevision: null,
  };
  const input = mutationInput("replace-draft", graph, lifecycle, "operation-1");
  assert.equal(input.expectedLifecycleVersion, 3);
  assert.equal(input.graphCandidate.controlEdges.length, 1);
  assert.equal(input.graphCandidate.bindings.length, 1);
  assert.equal(Object.hasOwn(input, "actorId"), false);
  assert.equal(Object.hasOwn(input, "surfaceId"), false);
  assert.equal(Object.hasOwn(input, "authorityEvidenceHash"), false);
  assert.deepEqual(graph.authorityCeiling.capabilityIds, [
    "org.embodysense/conversation-turn",
  ]);
  assert.deepEqual(graph.outputContract.outputs, [
    {
      id: "result",
      valueSchemaId: "value-text",
      sourceNodeId: "exit",
      sourcePortId: "published-result",
      required: true,
    },
  ]);

  const withoutExit = removeGraphNode(graph, "exit");
  assert.equal(withoutExit.controlEdges.length, 0);
  assert.equal(withoutExit.bindings.length, 0);
  assert.equal(withoutExit.outputContract.outputs.length, 0);
});

test("durable graph hydration follows the exact lifecycle head and strips derived server fields from the next candidate", () => {
  const graph = {
    ...createGraphCandidate({
      graphId: "graph-1",
      revisionId: "revision-1",
      purpose: "Hydrate.",
      role: { roleId: "role-1", revision: 1, contentHash: "c".repeat(64) },
      displayName: "Hydrate",
      defaultModelRoutingPolicy: defaultRoutingPolicy,
    }),
    executableHash: hash,
    revisionReference: {
      schemaVersion: 1,
      graphId: "graph-1",
      revisionId: "revision-1",
      executableHash: hash,
    },
  };
  const read = {
    lifecycle: {
      draftRevision: graph.revisionReference,
      publishedRevision: null,
    },
    artifacts: [{ graph }],
  };
  const hydrated = currentGraph(read);
  const candidate = candidateFromGraph(hydrated);
  assert.equal(hydrated, graph);
  assert.equal(Object.hasOwn(candidate, "executableHash"), false);
  assert.equal(Object.hasOwn(candidate, "revisionReference"), false);
});

test("authoritative graph hydration preserves a still-valid node selection", () => {
  const nodes = [{ id: "fail-terminal" }, { id: "provider-inference" }];

  assert.equal(
    selectHydratedNodeId(nodes, "provider-inference"),
    "provider-inference",
  );
  assert.equal(selectHydratedNodeId(nodes, "removed-node"), "fail-terminal");
  assert.equal(selectHydratedNodeId([], "provider-inference"), null);
});

test("graph model routing keeps only typed authoring intent and scopes node overrides to Inference", () => {
  const graph = createGraphCandidate({
    graphId: "graph-1",
    revisionId: "revision-1",
    purpose: "Route one inference.",
    role: { roleId: "role-1", revision: 1, contentHash: hash },
    displayName: "Routing",
    defaultModelRoutingPolicy: defaultRoutingPolicy,
  });
  assert.equal(
    graph.defaultModelRoutingPolicy.selector.exactProfileId,
    "org.example/model-a",
  );
  assert.equal(
    Object.hasOwn(graph.defaultModelRoutingPolicy, "contentHash"),
    false,
  );
  assert.equal(
    Object.hasOwn(graph.defaultModelRoutingPolicy.requirements, "contentHash"),
    false,
  );

  const exact = exactRoutingPolicyIntent(
    defaultRoutingPolicy,
    "org.example/model-b",
    ["org.example/model-c"],
  );
  const inherited = inheritedRoutingPolicyIntent(
    defaultRoutingPolicy,
    ["org.example/model-a", "org.example/model-b"],
    ["org.example/model-c"],
  );
  assert.deepEqual(exact.selector, {
    kind: "exact",
    exactProfileId: "org.example/model-b",
    permittedInheritedProfileIds: [],
  });
  assert.equal(inherited.selector.kind, "inherit");
  assert.equal(
    inheritedRoutingPolicyIntent(defaultRoutingPolicy, [
      "org.example/model-b",
      "org.example/model-a",
    ]),
    null,
  );
  assert.equal(
    exactRoutingPolicyIntent(defaultRoutingPolicy, "org.example/model-b", [
      "org.example/model-b",
    ]),
    null,
  );

  const routed = configureGraphModelRouting(graph, exact);
  assert.deepEqual(routed.authorityCeiling.capabilityIds, [
    "org.example/model-b",
    "org.example/model-c",
  ]);
  const inference = {
    id: "infer",
    descriptor: { kind: "inference", typeId: "provider-inference", version: 1 },
    ports: [],
    authorityCeiling: { capabilityIds: ["org.example/model-b"] },
    parameters: {},
    modelRoutingPolicy: null,
    authoredInputDataClasses: null,
  };
  const withInference = { ...routed, nodes: [inference] };
  const overridden = configureInferenceModelRouting(
    withInference,
    "infer",
    inherited,
    ["public"],
  );
  assert.equal(overridden.nodes[0].modelRoutingPolicy.selector.kind, "inherit");
  assert.deepEqual(overridden.nodes[0].authoredInputDataClasses, ["public"]);
  assert.deepEqual(overridden.authorityCeiling.capabilityIds, [
    "org.example/model-a",
    "org.example/model-b",
    "org.example/model-c",
  ]);
  assert.deepEqual(overridden.nodes[0].authorityCeiling.capabilityIds, [
    "org.example/model-a",
    "org.example/model-b",
    "org.example/model-c",
  ]);
  assert.equal(
    configureInferenceModelRouting(
      {
        ...routed,
        nodes: [
          {
            ...inference,
            descriptor: { ...inference.descriptor, kind: "transform" },
          },
        ],
      },
      "infer",
      inherited,
    ),
    null,
  );
  assert.equal(clientShapeErrors(graph).length, 0);
  assert.ok(
    clientShapeErrors({ ...graph, defaultModelRoutingPolicy: null }).some(
      (item) => item.code === "model-routing-default-invalid",
    ),
  );
});

test("retry authoring accepts only exact server-authenticated policies on fallible nodes", () => {
  const policy = {
    schemaVersion: 1,
    policyId: "retry-infer",
    nodeId: "infer",
    failureClasses: ["RetryableNoEffect"],
    serverCodes: [],
    maximumAttempts: 3,
    perAttemptTimeoutMilliseconds: 1000,
    maximumElapsedMilliseconds: 10000,
    backoffStrategy: "Fixed",
    initialDelayMilliseconds: 250,
    maximumDelayMilliseconds: 250,
    jitterStrategy: "None",
    maximumJitterMilliseconds: 0,
    maximumTokens: null,
    maximumToolCalls: null,
    maximumCostMicrounits: null,
    maximumCostCurrency: null,
    maximumResourceUnits: null,
    contentHash: hash,
  };
  const graph = {
    graphId: "graph-1",
    revisionId: "revision-1",
    nodes: [
      {
        id: "trigger",
        descriptor: { kind: "trigger", typeId: "manual-trigger", version: 1 },
      },
      {
        id: "infer",
        descriptor: {
          kind: "inference",
          typeId: "provider-inference",
          version: 1,
        },
      },
    ],
  };

  const configured = configureNodeRetryPolicy(graph, "infer", policy);
  assert.deepEqual(configured.nodes[1].retryPolicy, policy);
  assert.notEqual(configured.nodes[1].retryPolicy, policy);
  assert.equal(
    configureNodeRetryPolicy(graph, "trigger", {
      ...policy,
      nodeId: "trigger",
    }),
    null,
  );
  assert.equal(
    configureNodeRetryPolicy(graph, "infer", {
      ...policy,
      contentHash: "caller-authored",
    }),
    null,
  );
  assert.equal(clientShapeErrors(configured).length, 0);
  assert.equal(
    clientShapeErrors({
      ...configured,
      nodes: [
        { ...configured.nodes[1], retryPolicy: { ...policy, nodeId: "other" } },
      ],
    }).some((item) => item.code === "node-retry-policy-invalid"),
    true,
  );
  assert.equal(
    configureNodeRetryPolicy(configured, "infer", null).nodes[1].retryPolicy,
    null,
  );
});

test("new graph routing remains optional and profile authority follows effective exact and fallback policies", () => {
  let graph = createGraphCandidate({
    graphId: "graph-1",
    revisionId: "revision-1",
    purpose: "Route one inference.",
    role: { roleId: "role-1", revision: 1, contentHash: hash },
    displayName: "Routing",
  });
  assert.equal(Object.hasOwn(graph, "defaultModelRoutingPolicy"), false);
  assert.deepEqual(clientShapeErrors(graph), []);

  const inferenceContract = {
    descriptor: {
      kind: "inference",
      typeId: "provider-inference",
      version: 1,
    },
    isAdvertised: true,
    isExecutable: true,
    isLegalEntry: false,
    isLegalTerminal: false,
    ports: [],
    parameters: [],
    requiredCapabilityIds: ["org.embodysense/model-inference"],
  };
  graph = configureGraphModelRouting(graph, defaultRoutingPolicy);
  graph = addCatalogNode(graph, inferenceContract, "infer", 0, 0);
  assert.deepEqual(graph.nodes[0].authorityCeiling.capabilityIds, [
    "org.embodysense/model-inference",
    "org.example/model-a",
  ]);
  assert.deepEqual(graph.authorityCeiling.capabilityIds, [
    "org.embodysense/model-inference",
    "org.example/model-a",
  ]);

  const override = exactRoutingPolicyIntent(
    defaultRoutingPolicy,
    "org.example/model-b",
    ["org.example/model-c"],
  );
  graph = configureInferenceModelRouting(graph, "infer", override);
  assert.deepEqual(graph.nodes[0].authorityCeiling.capabilityIds, [
    "org.embodysense/model-inference",
    "org.example/model-b",
    "org.example/model-c",
  ]);
  assert.deepEqual(graph.authorityCeiling.capabilityIds, [
    "org.embodysense/model-inference",
    "org.example/model-a",
    "org.example/model-b",
    "org.example/model-c",
  ]);

  graph = configureInferenceModelRouting(graph, "infer", null);
  assert.deepEqual(graph.nodes[0].authorityCeiling.capabilityIds, [
    "org.embodysense/model-inference",
    "org.example/model-a",
  ]);
  assert.deepEqual(graph.authorityCeiling.capabilityIds, [
    "org.embodysense/model-inference",
    "org.example/model-a",
  ]);
});

test("fallback selection preserves authored order and supports explicit bounded reordering", () => {
  const original = ["org.example/model-c", "org.example/model-b"];
  assert.deepEqual(
    updateOrderedProfileSelection(original, [
      "org.example/model-a",
      "org.example/model-b",
      "org.example/model-c",
    ]),
    ["org.example/model-c", "org.example/model-b", "org.example/model-a"],
  );
  assert.deepEqual(
    updateOrderedProfileSelection(original, [
      "org.example/model-a",
      "org.example/model-c",
    ]),
    ["org.example/model-c", "org.example/model-a"],
  );
  assert.deepEqual(
    moveOrderedProfileSelection(original, "org.example/model-b", -1),
    ["org.example/model-b", "org.example/model-c"],
  );
  assert.deepEqual(
    moveOrderedProfileSelection(original, "org.example/model-c", -1),
    original,
  );
  assert.deepEqual(
    updateOrderedProfileSelection(
      original,
      ["org.example/model-c"],
      "org.example/model-c",
    ),
    [],
  );
});
