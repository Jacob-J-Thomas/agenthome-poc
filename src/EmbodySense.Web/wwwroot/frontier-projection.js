const terminalRunStates = new Set([
  "completed",
  "failed",
  "cancelled",
  "needs-review",
]);

export const frontierVisualStates = Object.freeze([
  "skipped",
  "ready",
  "running",
  "completed",
  "waiting",
  "failed",
  "review-blocked",
  "terminal",
]);

export function projectFrontier(run) {
  const frontier = run?.frontier;
  if (
    !frontier ||
    frontier.schemaVersion !== 1 ||
    !Array.isArray(frontier.nodes)
  )
    return null;
  const runState = token(run.status);
  return Object.freeze({
    version: frontier.frontierVersion,
    status: token(frontier.status),
    contentHash: frontier.contentHash,
    updatedAtUtc: frontier.updatedAtUtc,
    nodes: Object.freeze(
      frontier.nodes
        .map((item) => projectNode(item, runState))
        .sort(
          (left, right) =>
            left.activationOrdinal - right.activationOrdinal ||
            left.planOrdinal - right.planOrdinal ||
            left.nodeId.localeCompare(right.nodeId),
        ),
    ),
  });
}

function projectNode(node, runState) {
  const status = token(node?.status);
  let visualState = status;
  if (["pending", "eligible", "ready"].includes(status)) visualState = "ready";
  else if (["active", "executing", "running"].includes(status))
    visualState = "running";
  else if (["suspended", "sleeping", "waiting"].includes(status))
    visualState = "waiting";
  else if (["blocked", "needs-review", "review-blocked"].includes(status))
    visualState = "review-blocked";
  else if (["succeeded", "completed"].includes(status))
    visualState = node?.kind === "Exit" ? "terminal" : "completed";
  else if (["failed", "faulted"].includes(status)) visualState = "failed";
  else if (["skipped", "not-selected"].includes(status))
    visualState = "skipped";
  else if (terminalRunStates.has(runState)) visualState = "terminal";
  else visualState = "ready";

  return Object.freeze({
    nodeId: String(node?.nodeId ?? ""),
    kind: String(node?.kind ?? "Unknown"),
    typeId: String(node?.typeId ?? ""),
    planOrdinal: Number(node?.planOrdinal ?? 0),
    activationOrdinal: Number(node?.activationOrdinal ?? 0),
    visitOrdinal: Number(node?.visitOrdinal ?? 0),
    status,
    visualState,
    attempt: node?.attempt ?? null,
    controlOutcome: node?.controlOutcome ?? null,
    selectedControlEdgeIds: Object.freeze([
      ...(node?.selectedControlEdgeIds ?? []),
    ]),
    skippedControlEdgeIds: Object.freeze([
      ...(node?.skippedControlEdgeIds ?? []),
    ]),
  });
}

function token(value) {
  return String(value ?? "")
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replaceAll("_", "-")
    .toLowerCase();
}
