import assert from "node:assert/strict";
import test from "node:test";

import {
  addCatalogNode,
  candidateFromGraph,
  clientShapeErrors,
  compatibleBindings,
  connectBinding,
  connectControl,
  connectorDecision,
  createGraphCandidate,
  currentGraph,
  indexServerErrors,
  layoutOnlyMove,
  mutationInput,
  removeGraphNode,
} from "../../src/EmbodySense.Web/wwwroot/governed-graph-authoring.js";
import { projectFrontier } from "../../src/EmbodySense.Web/wwwroot/frontier-projection.js";
import {
  controlKinds,
  controlRequest,
  exactControl,
  postureSnapshot,
} from "../../src/EmbodySense.Web/wwwroot/operational-posture.js";

const hash = "a".repeat(64);

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
            id: "published",
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
