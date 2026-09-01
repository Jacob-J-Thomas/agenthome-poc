import {
  controlKinds,
  controlRequest,
  exactControl,
  postureSnapshot,
} from "./operational-posture.js";

const validSelector = (value) =>
  value &&
  typeof value.graphId === "string" &&
  value.graphId &&
  typeof value.revisionId === "string" &&
  value.revisionId &&
  Number.isSafeInteger(value.lifecycleVersion) &&
  value.lifecycleVersion > 0;

const maximumScheduleReconciliationPages = 100;

export function createGovernedScheduleAuthoring({
  document,
  requestJson,
  operationId,
  selectedGraph,
  refreshed,
}) {
  const elements = {
    ambiguousLocalTime: document.getElementById(
      "governedScheduleAmbiguousLocalTime",
    ),
    catchUpLimit: document.getElementById("governedScheduleCatchUpLimit"),
    enabled: document.getElementById("governedScheduleEnabled"),
    firstLocalOccurrence: document.getElementById(
      "governedScheduleFirstLocalOccurrence",
    ),
    fixedIntervalSeconds: document.getElementById(
      "governedScheduleFixedIntervalSeconds",
    ),
    invalidLocalTime: document.getElementById(
      "governedScheduleInvalidLocalTime",
    ),
    inspect: document.getElementById("governedScheduleInspectButton"),
    inspectId: document.getElementById("governedScheduleInspectId"),
    misfireKind: document.getElementById("governedScheduleMisfireKind"),
    overlap: document.getElementById("governedScheduleOverlap"),
    priority: document.getElementById("governedSchedulePriority"),
    prepareEdit: document.getElementById("governedSchedulePrepareEditButton"),
    recurrenceKind: document.getElementById("governedScheduleRecurrenceKind"),
    result: document.getElementById("governedScheduleResult"),
    submit: document.getElementById("governedScheduleSubmitButton"),
    timeZoneId: document.getElementById("governedScheduleTimeZoneId"),
  };
  let interactive = false;
  let inFlight = false;
  let pendingPreviewHash = null;
  let stableOperationId = null;
  let inspectedSchedule = null;
  let replacement = null;
  let supportedTimeZoneIds = null;
  let timeZoneLoadPromise = null;
  let timeZoneSelectionRequired = false;

  initializeDefaults();
  bind();
  render();

  return Object.freeze({
    setInteractive(enabled) {
      interactive = Boolean(enabled);
      render();
    },
    clear() {
      pendingPreviewHash = null;
      stableOperationId = null;
      inspectedSchedule = null;
      replacement = null;
      elements.result.textContent = "";
      render();
    },
  });

  function bind() {
    elements.submit.addEventListener("click", submit);
    elements.inspect.addEventListener("click", inspect);
    elements.prepareEdit.addEventListener("click", prepareEdit);
    for (const item of [
      elements.ambiguousLocalTime,
      elements.catchUpLimit,
      elements.enabled,
      elements.firstLocalOccurrence,
      elements.fixedIntervalSeconds,
      elements.invalidLocalTime,
      elements.misfireKind,
      elements.overlap,
      elements.priority,
      elements.recurrenceKind,
      elements.timeZoneId,
    ]) {
      item.addEventListener("input", resetConfirmation);
      item.addEventListener("change", resetConfirmation);
    }
    elements.timeZoneId.addEventListener(
      "change",
      acknowledgeTimeZoneSelection,
    );
  }

  function initializeDefaults() {
    elements.timeZoneId.value = "UTC";
    const later = new Date(Date.now() + 60 * 60 * 1000);
    const local = new Date(later.getTime() - later.getTimezoneOffset() * 60000);
    elements.firstLocalOccurrence.value = local.toISOString().slice(0, 16);
  }

  function resetConfirmation() {
    pendingPreviewHash = null;
    stableOperationId = null;
    render();
  }

  function acknowledgeTimeZoneSelection() {
    timeZoneSelectionRequired = false;
    resetConfirmation();
  }

  async function inspect() {
    const scheduleId = elements.inspectId.value.trim();
    if (!scheduleId || inFlight) return;
    inFlight = true;
    render();
    try {
      const response = await requestJson(
        `/api/governed-schedules/detail?scheduleId=${encodeURIComponent(scheduleId)}`,
      );
      inspectedSchedule = response?.schedule ?? null;
      replacement = null;
      if (!inspectedSchedule) {
        elements.result.textContent =
          response?.detail ??
          "The canonical schedule has no visible authoring projection.";
      } else {
        elements.result.textContent = `Inspected ${inspectedSchedule.scheduleId} at state revision ${inspectedSchedule.stateRevision}; ${inspectedSchedule.enabled ? "enabled" : "disabled"}. Use “Prepare immutable successor edit” to change it.`;
      }
    } catch (error) {
      inspectedSchedule = null;
      replacement = null;
      elements.result.textContent = `Schedule inspection failed: ${error.message}`;
    } finally {
      inFlight = false;
      render();
    }
  }

  function prepareEdit() {
    if (!inspectedSchedule || inFlight) {
      elements.result.textContent =
        "Inspect one canonical schedule before preparing an immutable successor edit.";
      return;
    }
    prefill(inspectedSchedule);
    elements.enabled.checked = false;
    replacement = Object.freeze({
      ...inspectedSchedule,
      disableOperationId: operationId("schedule-disable-predecessor"),
      enableOperationId: operationId("schedule-enable-successor"),
    });
    pendingPreviewHash = null;
    stableOperationId = null;
    elements.result.textContent = `Successor prepared for ${replacement.scheduleId}. It will be created disabled; after its canonical reread, this flow disables the exact predecessor before enabling the successor.`;
    render();
  }

  async function submit() {
    if (!interactive || inFlight) return;
    if (replacement?.successorScheduleId) {
      await resumeReplacement();
      return;
    }
    const selector = selectedGraph();
    if (!validSelector(selector)) {
      elements.result.textContent =
        "Publish and refresh one exact graph revision before authoring a schedule.";
      return;
    }
    const input = createInput(selector);
    if (!input) {
      elements.result.textContent =
        "Enter a local occurrence, supported time zone, and bounded schedule policies.";
      return;
    }

    inFlight = true;
    render();
    if (!(await ensureServerTimeZones())) {
      inFlight = false;
      render();
      return;
    }
    const serverBoundInput = createInput(selector);
    if (!serverBoundInput) {
      elements.result.textContent =
        "Select one exact time zone from the server-owned rules snapshot.";
      inFlight = false;
      render();
      return;
    }
    try {
      const response = await requestJson("/api/governed-schedules/create", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(serverBoundInput),
      });
      pendingPreviewHash =
        response.status === "confirmation-required" &&
        typeof response.authorityPreviewHash === "string"
          ? response.authorityPreviewHash
          : null;
      if (response.status !== "confirmation-required") stableOperationId = null;
      renderResponse(response);
      if (
        replacement &&
        (response.status === "created" || response.status === "replayed") &&
        response.schedule?.scheduleId
      )
        replacement = {
          ...replacement,
          successorScheduleId: response.schedule.scheduleId,
        };
      if (response.schedule)
        await commitReplacement(replacement, response.schedule);
      await refreshed?.();
    } catch (error) {
      elements.result.textContent = `Schedule authoring failed: ${error.message}`;
    } finally {
      inFlight = false;
      render();
    }
  }

  function createInput(selector) {
    const firstLocalOccurrence = elements.firstLocalOccurrence.value;
    const fixedIntervalSeconds = nullablePositiveInteger(
      elements.fixedIntervalSeconds.value,
    );
    const catchUpLimit = integer(elements.catchUpLimit.value);
    if (
      !firstLocalOccurrence ||
      !elements.timeZoneId.value ||
      catchUpLimit === null ||
      (elements.recurrenceKind.value === "fixed-interval" &&
        fixedIntervalSeconds === null)
    )
      return null;
    stableOperationId ??= operationId("schedule-author");
    return {
      operationId: stableOperationId,
      graphId: selector.graphId,
      revisionId: selector.revisionId,
      expectedGraphLifecycleVersion: selector.lifecycleVersion,
      expectedAuthorityPreviewHash: pendingPreviewHash,
      recurrenceKind: elements.recurrenceKind.value,
      firstLocalOccurrence,
      fixedIntervalSeconds:
        elements.recurrenceKind.value === "fixed-interval"
          ? fixedIntervalSeconds
          : null,
      timeZoneId: elements.timeZoneId.value,
      invalidLocalTime: elements.invalidLocalTime.value,
      ambiguousLocalTime: elements.ambiguousLocalTime.value,
      misfireKind: elements.misfireKind.value,
      catchUpLimit,
      overlap: elements.overlap.value,
      priority: elements.priority.value,
      enabled: replacement ? false : elements.enabled.checked,
    };
  }

  async function ensureServerTimeZones() {
    if (supportedTimeZoneIds) {
      if (
        !timeZoneSelectionRequired &&
        supportedTimeZoneIds.includes(elements.timeZoneId.value)
      )
        return true;
      elements.result.textContent =
        "Select one exact time zone from the server-owned rules snapshot before submitting.";
      return false;
    }
    timeZoneLoadPromise ??= requestJson("/api/governed-schedules/time-zones")
      .then((response) => {
        const ids = [...(response?.timeZones ?? [])].map((item) => item?.id);
        if (
          response?.status !== "available" ||
          ids.length === 0 ||
          ids.length > 1024 ||
          ids.some(
            (id) =>
              typeof id !== "string" || id.length === 0 || id.length > 128,
          ) ||
          new Set(ids).size !== ids.length
        )
          throw new Error(
            response?.detail ??
              "The server-owned time-zone catalog is unavailable.",
          );
        const selectedTimeZoneId = elements.timeZoneId.value;
        supportedTimeZoneIds = Object.freeze(ids);
        timeZoneSelectionRequired =
          !supportedTimeZoneIds.includes(selectedTimeZoneId);
        populateServerTimeZones(supportedTimeZoneIds);
        if (timeZoneSelectionRequired) {
          elements.result.textContent =
            "The server-owned time-zone choices are loaded. Select one before submitting.";
          return false;
        }
        return true;
      })
      .catch((error) => {
        timeZoneLoadPromise = null;
        elements.result.textContent = `Time-zone choices are unavailable: ${error.message}`;
        return false;
      });
    return timeZoneLoadPromise;
  }

  function populateServerTimeZones(ids) {
    if (
      !elements.timeZoneId.options ||
      typeof elements.timeZoneId.add !== "function"
    )
      return;
    const selectedTimeZoneId = elements.timeZoneId.value;
    while (elements.timeZoneId.options.length > 0)
      elements.timeZoneId.remove(0);
    if (timeZoneSelectionRequired) {
      const placeholder = document.createElement("option");
      placeholder.value = "";
      placeholder.textContent = "Select a server time zone";
      placeholder.disabled = true;
      elements.timeZoneId.add(placeholder);
    }
    for (const id of ids) {
      const option = document.createElement("option");
      option.value = id;
      option.textContent = id;
      elements.timeZoneId.add(option);
    }
    elements.timeZoneId.value = ids.includes(selectedTimeZoneId)
      ? selectedTimeZoneId
      : "";
  }

  function renderResponse(response) {
    const schedule = response?.schedule;
    const status = String(response?.status ?? "unknown");
    if (status === "confirmation-required") {
      elements.result.textContent =
        "The server derived least-authority terms. Confirm to create this exact schedule; the browser cannot select a grant.";
      return;
    }
    if (!schedule) {
      elements.result.textContent =
        response?.detail ?? "No canonical schedule state is available.";
      return;
    }
    elements.inspectId.value = schedule.scheduleId;
    elements.result.textContent = `${response.detail} Schedule ${schedule.scheduleId} targets ${schedule.graphId}/${schedule.revisionId}; state revision ${schedule.stateRevision}; ${schedule.enabled ? "enabled" : "disabled"}.`;
  }

  async function resumeReplacement() {
    inFlight = true;
    render();
    try {
      const response = await requestJson(
        `/api/governed-schedules/detail?scheduleId=${encodeURIComponent(replacement.successorScheduleId)}`,
      );
      if (!response?.schedule) {
        elements.result.textContent =
          "Replacement remains unresolved: the successor cannot be reread. Do not retry creation; inspect canonical schedule state first.";
        return;
      }
      await commitReplacement(replacement, response.schedule);
      await refreshed?.();
    } catch (error) {
      elements.result.textContent = `Replacement reread failed: ${error.message}. Retry only after canonical state becomes available.`;
    } finally {
      inFlight = false;
      render();
    }
  }

  async function commitReplacement(predecessor, successor) {
    if (!predecessor) return;
    elements.result.textContent =
      "Rereading canonical operational posture before continuing the replacement…";
    const initial = await readOperationalSnapshot([
      predecessor.scheduleId,
      successor.scheduleId,
    ]);
    const currentPredecessor = findSchedule(initial, predecessor.scheduleId);
    const currentSuccessor = findSchedule(initial, successor.scheduleId);
    if (!currentPredecessor || !currentSuccessor) {
      elements.result.textContent =
        "Replacement halted: canonical posture does not contain both exact schedules. Inspect both schedules before applying any further control.";
      return;
    }
    if (!currentPredecessor.enabled && currentSuccessor.enabled) {
      finishReplacement(predecessor, currentSuccessor);
      return;
    }
    if (currentPredecessor.enabled && currentSuccessor.enabled) {
      elements.result.textContent =
        "Replacement halted: canonical posture shows both schedules enabled. Do not apply another control until their exact state is inspected.";
      return;
    }
    if (!currentPredecessor.enabled) {
      await enableSuccessor(predecessor, initial, currentSuccessor);
      return;
    }
    if (
      !sameStateRevision(currentPredecessor, predecessor) ||
      currentSuccessor.enabled
    ) {
      elements.result.textContent =
        "Replacement halted: predecessor state is stale or successor is not safely disabled. Inspect both canonical schedules and retry only the required exact control.";
      return;
    }
    const disabled = await exactScheduleControl(
      initial,
      currentPredecessor,
      controlKinds.disableSchedule,
      predecessor.disableOperationId,
    );
    if (!disabled) return;

    elements.result.textContent =
      "Predecessor control returned; rereading canonical posture before enabling the successor…";
    const afterDisable = await readOperationalSnapshot([
      predecessor.scheduleId,
      successor.scheduleId,
    ]);
    const disabledPredecessor = findSchedule(
      afterDisable,
      predecessor.scheduleId,
    );
    const disabledSuccessor = findSchedule(afterDisable, successor.scheduleId);
    if (
      !disabledPredecessor ||
      disabledPredecessor.enabled ||
      !disabledSuccessor ||
      disabledSuccessor.enabled
    ) {
      elements.result.textContent =
        "Replacement remains recoverable: canonical reread cannot prove that only the disabled successor remains. Do not enable it until the predecessor is visibly disabled.";
      return;
    }
    await enableSuccessor(predecessor, afterDisable, disabledSuccessor);
  }

  async function enableSuccessor(predecessor, snapshot, successor) {
    const enabled = await exactScheduleControl(
      snapshot,
      successor,
      controlKinds.enableSchedule,
      predecessor.enableOperationId,
    );
    if (!enabled) return;

    const complete = await readOperationalSnapshot([
      predecessor.scheduleId,
      successor.scheduleId,
    ]);
    const finalPredecessor = findSchedule(complete, predecessor.scheduleId);
    const finalSuccessor = findSchedule(complete, successor.scheduleId);
    if (
      !finalPredecessor ||
      finalPredecessor.enabled ||
      !finalSuccessor ||
      !finalSuccessor.enabled
    ) {
      elements.result.textContent =
        "Replacement outcome is unresolved. Reread canonical schedules before applying any further exact control.";
      return;
    }
    finishReplacement(predecessor, finalSuccessor);
  }

  function finishReplacement(predecessor, successor) {
    replacement = null;
    inspectedSchedule = null;
    elements.inspectId.value = successor.scheduleId;
    elements.result.textContent = `Replacement complete: ${predecessor.scheduleId} is disabled and ${successor.scheduleId} is enabled after canonical reconciliation. Inspect the successor before preparing another edit.`;
  }

  async function exactScheduleControl(snapshot, schedule, kind, operation) {
    const request = controlRequest({
      operationId: operation,
      targetId: schedule.scheduleId,
      owner: schedule,
      kind,
      authorityEvidenceHash: snapshot.controlAuthorityEvidenceHash,
    });
    if (!request) {
      elements.result.textContent =
        "Replacement halted: the current posture does not advertise exact control evidence for this schedule.";
      return false;
    }
    try {
      await requestJson("/api/loop-operations/control", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(request),
      });
      return true;
    } catch (error) {
      elements.result.textContent = `Exact ${kind} response is unresolved or stale: ${error.message}. Reread canonical posture before retrying the same operation.`;
      return false;
    }
  }

  async function readOperationalSnapshot(requiredScheduleIds = []) {
    const required = new Set(
      requiredScheduleIds.filter(
        (scheduleId) => typeof scheduleId === "string" && scheduleId,
      ),
    );
    let afterScheduleId = null;
    let merged = null;
    for (let page = 0; page < maximumScheduleReconciliationPages; page++) {
      const response = await requestJson(
        `/api/loop-operations/posture?maximumQueueEntries=1&maximumSchedules=50&maximumWakes=1&maximumRuns=1${afterScheduleId ? `&afterScheduleId=${encodeURIComponent(afterScheduleId)}` : ""}`,
      );
      const snapshot = postureSnapshot(response);
      if (!snapshot)
        throw new Error("Canonical schedule posture is unavailable.");
      if (
        merged &&
        merged.controlAuthorityEvidenceHash !==
          snapshot.controlAuthorityEvidenceHash
      )
        throw new Error(
          "Canonical schedule posture changed while its exact pages were being reconciled.",
        );
      const schedules = snapshot.schedules;
      if (!Array.isArray(schedules?.items))
        throw new Error("Canonical schedule posture pagination is invalid.");
      merged = merged
        ? {
            ...merged,
            schedules: {
              ...merged.schedules,
              ...schedules,
              items: [...merged.schedules.items, ...schedules.items],
            },
          }
        : snapshot;
      if (
        required.size === 0 ||
        [...required].every((scheduleId) => findSchedule(merged, scheduleId))
      )
        return merged;
      if (!schedules.hasMore) return merged;
      const nextCursor = schedules.continuationCursor;
      if (
        typeof nextCursor !== "string" ||
        !nextCursor ||
        nextCursor === afterScheduleId
      )
        throw new Error("Canonical schedule posture pagination is invalid.");
      afterScheduleId = nextCursor;
    }
    throw new Error(
      "Canonical schedule posture exceeded the bounded reconciliation page limit.",
    );
  }

  function findSchedule(snapshot, scheduleId) {
    const matches = (snapshot?.schedules?.items ?? []).filter(
      (candidate) => candidate?.scheduleId === scheduleId,
    );
    return matches.length === 1 ? matches[0] : null;
  }

  function sameStateRevision(postureSchedule, snapshotSchedule) {
    return (
      postureSchedule &&
      exactControl(postureSchedule, controlKinds.disableSchedule)
        ?.expectedRevision === snapshotSchedule.stateRevision
    );
  }

  function prefill(schedule) {
    elements.recurrenceKind.value = schedule.recurrenceKind;
    elements.firstLocalOccurrence.value = String(
      schedule.firstLocalOccurrence,
    ).slice(0, 16);
    elements.fixedIntervalSeconds.value = schedule.fixedIntervalSeconds ?? "";
    elements.timeZoneId.value = schedule.timeZoneId;
    elements.invalidLocalTime.value = schedule.invalidLocalTimePolicy;
    elements.ambiguousLocalTime.value = schedule.ambiguousLocalTimePolicy;
    elements.misfireKind.value = schedule.misfirePolicy;
    elements.catchUpLimit.value = String(schedule.catchUpLimit);
    elements.overlap.value = schedule.overlapPolicy;
    elements.priority.value = schedule.priority;
  }

  function render() {
    const disabled = !interactive || inFlight;
    const successorRetained = Boolean(replacement?.successorScheduleId);
    for (const item of Object.values(elements)) {
      if (item && "disabled" in item) item.disabled = disabled;
    }
    for (const item of [
      elements.ambiguousLocalTime,
      elements.catchUpLimit,
      elements.enabled,
      elements.firstLocalOccurrence,
      elements.fixedIntervalSeconds,
      elements.invalidLocalTime,
      elements.misfireKind,
      elements.overlap,
      elements.priority,
      elements.recurrenceKind,
      elements.timeZoneId,
    ])
      item.disabled = disabled || successorRetained;
    elements.enabled.disabled = disabled || Boolean(replacement);
    elements.submit.textContent = pendingPreviewHash
      ? "Confirm authority and create schedule"
      : replacement
        ? "Create disabled successor and replace"
        : "Create schedule";
    elements.prepareEdit.disabled = disabled || !inspectedSchedule;
  }
}

function integer(value) {
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= 0 ? parsed : null;
}

function nullablePositiveInteger(value) {
  if (!String(value).trim()) return null;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}
