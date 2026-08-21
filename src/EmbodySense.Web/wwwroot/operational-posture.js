const sha256 = /^[0-9a-f]{64}$/;

export const controlKinds = Object.freeze({
  pauseRun: "pause-run",
  cancelRun: "cancel-run",
  resumeRun: "resume-run",
  disableSchedule: "disable-schedule",
  enableSchedule: "enable-schedule",
  cancelDelivery: "cancel-delivery",
  cancelPendingDeliveries: "cancel-pending-deliveries",
});

export function postureSnapshot(response) {
  if (!response || response.schemaVersion === 0) return null;
  const snapshot = response.snapshot;
  if (
    !snapshot ||
    snapshot.schemaVersion !== 1 ||
    !sha256.test(snapshot.controlAuthorityEvidenceHash ?? "")
  )
    return null;
  return snapshot;
}

export function exactControl(owner, kind) {
  if (!owner || !Array.isArray(owner.eligibleControls)) return null;
  const normalized = normalizeToken(kind);
  const matches = owner.eligibleControls.filter(
    (candidate) => normalizeToken(candidate?.kind) === normalized,
  );
  if (matches.length !== 1) return null;
  const control = matches[0];
  if (
    !Number.isSafeInteger(control.expectedRevision) ||
    control.expectedRevision < 0 ||
    !sha256.test(control.expectedEvidenceHash ?? "")
  )
    return null;
  return Object.freeze({
    kind: normalized,
    expectedRevision: control.expectedRevision,
    expectedEvidenceHash: control.expectedEvidenceHash,
  });
}

export function controlRequest({
  operationId,
  targetId,
  owner,
  kind,
  authorityEvidenceHash,
  maximumBatchItems = 1,
}) {
  const control = exactControl(owner, kind);
  if (
    !control ||
    typeof operationId !== "string" ||
    operationId.length === 0 ||
    typeof targetId !== "string" ||
    targetId.length === 0 ||
    !sha256.test(authorityEvidenceHash ?? "") ||
    !Number.isSafeInteger(maximumBatchItems) ||
    maximumBatchItems < 1
  )
    return null;
  return Object.freeze({
    operationId,
    kind: control.kind,
    targetId,
    expectedRevision: control.expectedRevision,
    expectedEvidenceHash: control.expectedEvidenceHash,
    expectedAuthorityEvidenceHash: authorityEvidenceHash,
    maximumBatchItems,
  });
}

export function controlsForRun(snapshot, runId) {
  if (!snapshot || !runId) return [];
  const run = snapshot.runs?.items?.find((item) => item.runId === runId);
  return run?.eligibleControls ?? [];
}

function normalizeToken(value) {
  return String(value ?? "")
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replaceAll("_", "-")
    .toLowerCase();
}
