import assert from "node:assert/strict";
import test from "node:test";

import { createGovernedScheduleAuthoring } from "../../src/EmbodySense.Web/wwwroot/governed-schedule-authoring.js";

const postureUrl =
  "/api/loop-operations/posture?maximumQueueEntries=1&maximumSchedules=50&maximumWakes=1&maximumRuns=1";
const selectedGraph = () => ({
  graphId: "graph-published",
  revisionId: "revision-published",
  lifecycleVersion: 4,
});

test("schedule authoring keeps one confirmed operation through response loss, rereads, bounds input, and renders server text safely", async () => {
  const view = createView();
  const calls = [];
  let createCount = 0;
  let refreshed = 0;
  const authoring = createGovernedScheduleAuthoring({
    document: view.document,
    operationId: (kind) => `operation-${kind}`,
    refreshed: async () => {
      refreshed += 1;
    },
    requestJson: async (url, options) => {
      calls.push({ url, options });
      if (url === "/api/governed-schedules/create") {
        createCount += 1;
        if (createCount === 1)
          return {
            status: "confirmation-required",
            authorityPreviewHash: "a".repeat(64),
          };
        if (createCount === 2)
          throw new Error(
            "The server committed the schedule but the response was lost.",
          );
        return {
          status: "replayed",
          detail: "The prior create is authoritative.",
          schedule: authoredSchedule(
            "schedule-<img src=x onerror=alert(1)>",
            true,
          ),
        };
      }
      if (url.startsWith("/api/governed-schedules/detail"))
        return {
          status: "found",
          schedule: authoredSchedule("schedule-inspected", true),
        };
      throw new Error(`Unexpected request: ${url}`);
    },
    selectedGraph,
  });
  authoring.setInteractive(true);

  view.elements.fixedIntervalSeconds.value = "0";
  await view.elements.submit.click();
  assert.equal(calls.length, 0);
  assert.match(view.elements.result.textContent, /bounded schedule policies/i);

  view.elements.fixedIntervalSeconds.value = "300";
  await view.elements.fixedIntervalSeconds.input();
  await view.elements.submit.click();
  await view.elements.submit.click();
  await view.elements.submit.click();

  const creates = calls.filter(
    (call) => call.url === "/api/governed-schedules/create",
  );
  assert.equal(creates.length, 3);
  assert.deepEqual(
    creates.map((call) => JSON.parse(call.options.body).operationId),
    [
      "operation-schedule-author",
      "operation-schedule-author",
      "operation-schedule-author",
    ],
  );
  assert.deepEqual(Object.keys(JSON.parse(creates[2].options.body)).sort(), [
    "ambiguousLocalTime",
    "catchUpLimit",
    "enabled",
    "expectedAuthorityPreviewHash",
    "expectedGraphLifecycleVersion",
    "firstLocalOccurrence",
    "fixedIntervalSeconds",
    "graphId",
    "invalidLocalTime",
    "misfireKind",
    "operationId",
    "overlap",
    "priority",
    "recurrenceKind",
    "revisionId",
    "timeZoneId",
  ]);
  assert.equal(refreshed, 2);
  assert.match(view.elements.result.textContent, /schedule-<img src=x/i);
  assert.equal(view.elements.result.children.length, 0);

  view.elements.inspectId.value = "schedule-inspected";
  await view.elements.inspect.click();
  assert.equal(
    calls.at(-1).url,
    "/api/governed-schedules/detail?scheduleId=schedule-inspected",
  );
  assert.match(
    view.elements.result.textContent,
    /Inspected schedule-inspected/i,
  );
  assert.match(view.elements.result.textContent, /enabled/i);
  assert.equal(view.elements.result.children.length, 0);
});

test("immutable replacement rereads canonical posture and never enables a successor before its predecessor is disabled", async () => {
  const view = createView();
  const calls = [];
  const predecessor = authoredSchedule("schedule-old", true, 5);
  const successor = authoredSchedule("schedule-new", false, 1);
  const postures = [
    posture(
      schedulePosture("schedule-old", true, 5),
      schedulePosture("schedule-new", false, 1),
    ),
    posture(
      schedulePosture("schedule-old", false, 6),
      schedulePosture("schedule-new", false, 1),
    ),
    posture(
      schedulePosture("schedule-old", false, 6),
      schedulePosture("schedule-new", true, 2),
    ),
  ];
  let createCount = 0;
  const authoring = createGovernedScheduleAuthoring({
    document: view.document,
    operationId: (kind) => `operation-${kind}`,
    selectedGraph,
    requestJson: async (url, options) => {
      calls.push({ url, options });
      if (url === "/api/governed-schedules/detail?scheduleId=schedule-old")
        return { status: "found", schedule: predecessor };
      if (url === "/api/governed-schedules/create") {
        createCount += 1;
        return createCount === 1
          ? {
              status: "confirmation-required",
              authorityPreviewHash: "b".repeat(64),
            }
          : { status: "created", detail: "Created.", schedule: successor };
      }
      if (url === postureUrl) return postures.shift();
      if (url === "/api/loop-operations/control") {
        const request = JSON.parse(options.body);
        if (request.kind === "disable-schedule") {
          predecessor.enabled = false;
          predecessor.stateRevision = 6;
        }
        return { status: "applied" };
      }
      throw new Error(`Unexpected request: ${url}`);
    },
  });
  authoring.setInteractive(true);

  view.elements.inspectId.value = "schedule-old";
  await view.elements.inspect.click();
  await view.elements.prepareEdit.click();
  assert.equal(view.elements.enabled.checked, false);
  view.elements.fixedIntervalSeconds.value = "600";
  await view.elements.fixedIntervalSeconds.input();
  await view.elements.submit.click();
  await view.elements.submit.click();

  const controls = calls.filter(
    (call) => call.url === "/api/loop-operations/control",
  );
  assert.deepEqual(
    controls.map((call) => JSON.parse(call.options.body).kind),
    ["disable-schedule", "enable-schedule"],
  );
  assert.deepEqual(
    controls.map((call) => JSON.parse(call.options.body).operationId),
    [
      "operation-schedule-disable-predecessor",
      "operation-schedule-enable-successor",
    ],
  );
  assert.equal(JSON.parse(controls[0].options.body).expectedRevision, 5);
  assert.equal(JSON.parse(controls[1].options.body).expectedRevision, 1);
  assert.equal(
    JSON.parse(
      calls.find((call) => call.url === "/api/governed-schedules/create")
        .options.body,
    ).fixedIntervalSeconds,
    600,
  );
  assert.equal(
    JSON.parse(
      calls.find((call) => call.url === "/api/governed-schedules/create")
        .options.body,
    ).enabled,
    false,
  );
  assert.match(view.elements.result.textContent, /Replacement complete/i);

  authoring.clear();
  view.elements.inspectId.value = "schedule-old";
  await view.elements.inspect.click();
  assert.match(
    view.elements.result.textContent,
    /schedule-old at state revision 6/i,
  );
  assert.match(view.elements.result.textContent, /disabled/i);
  assert.equal(view.elements.prepareEdit.disabled, false);
});

test("stale or response-lost replacement controls leave the disabled successor recoverable and never issue enable", async () => {
  const view = createView();
  const calls = [];
  const predecessor = authoredSchedule("schedule-old", true, 5);
  const successor = authoredSchedule("schedule-new", false, 1);
  const stale = posture(
    schedulePosture("schedule-old", true, 6),
    schedulePosture("schedule-new", false, 1),
  );
  const authoring = createGovernedScheduleAuthoring({
    document: view.document,
    operationId: (kind) => `operation-${kind}`,
    selectedGraph,
    requestJson: async (url, options) => {
      calls.push({ url, options });
      if (url === "/api/governed-schedules/detail?scheduleId=schedule-old")
        return { status: "found", schedule: predecessor };
      if (url === "/api/governed-schedules/create")
        return calls.filter(
          (call) => call.url === "/api/governed-schedules/create",
        ).length === 1
          ? {
              status: "confirmation-required",
              authorityPreviewHash: "c".repeat(64),
            }
          : { status: "created", detail: "Created.", schedule: successor };
      if (url === postureUrl) return stale;
      if (url === "/api/loop-operations/control")
        throw new Error("The control response was lost.");
      throw new Error(`Unexpected request: ${url}`);
    },
  });
  authoring.setInteractive(true);

  view.elements.inspectId.value = "schedule-old";
  await view.elements.inspect.click();
  await view.elements.prepareEdit.click();
  await view.elements.submit.click();
  await view.elements.submit.click();

  assert.equal(
    calls.some(
      (call) =>
        call.url === "/api/loop-operations/control" &&
        JSON.parse(call.options.body).kind === "enable-schedule",
    ),
    false,
  );
  assert.match(view.elements.result.textContent, /predecessor state is stale/i);
});

test("replacement retry enables the exact disabled successor after the predecessor-disable response is lost", async () => {
  const recovered =
    await runResponseLostReplacementRecovery("disable-schedule");

  assert.deepEqual(recovered.controlKinds, [
    "disable-schedule",
    "enable-schedule",
  ]);
  assert.match(recovered.result, /Replacement complete/i);
});

test("replacement retry recognizes canonical completion after the successor-enable response is lost", async () => {
  const recovered = await runResponseLostReplacementRecovery("enable-schedule");

  assert.deepEqual(recovered.controlKinds, [
    "disable-schedule",
    "enable-schedule",
  ]);
  assert.match(recovered.result, /Replacement complete/i);
});

async function runResponseLostReplacementRecovery(lostKind) {
  const view = createView();
  const calls = [];
  const predecessor = authoredSchedule("schedule-old", true, 5);
  const successor = authoredSchedule("schedule-new", false, 1);
  let predecessorEnabled = true;
  let successorEnabled = false;
  let createCount = 0;
  let lossReported = false;
  const authoring = createGovernedScheduleAuthoring({
    document: view.document,
    operationId: (kind) => `operation-${kind}`,
    selectedGraph,
    requestJson: async (url, options) => {
      calls.push({ url, options });
      if (url === "/api/governed-schedules/detail?scheduleId=schedule-old")
        return { status: "found", schedule: predecessor };
      if (url === "/api/governed-schedules/detail?scheduleId=schedule-new")
        return {
          status: "found",
          schedule: {
            ...successor,
            enabled: successorEnabled,
            stateRevision: successorEnabled ? 2 : 1,
          },
        };
      if (url === "/api/governed-schedules/create") {
        createCount += 1;
        return createCount === 1
          ? {
              status: "confirmation-required",
              authorityPreviewHash: "f".repeat(64),
            }
          : { status: "created", detail: "Created.", schedule: successor };
      }
      if (url === postureUrl)
        return posture(
          schedulePosture(
            "schedule-old",
            predecessorEnabled,
            predecessorEnabled ? 5 : 6,
          ),
          schedulePosture(
            "schedule-new",
            successorEnabled,
            successorEnabled ? 2 : 1,
          ),
        );
      if (url === "/api/loop-operations/control") {
        const request = JSON.parse(options.body);
        if (request.kind === "disable-schedule") predecessorEnabled = false;
        if (request.kind === "enable-schedule") successorEnabled = true;
        if (request.kind === lostKind && !lossReported) {
          lossReported = true;
          throw new Error("The control response was lost after commit.");
        }
        return { status: "applied" };
      }
      throw new Error(`Unexpected request: ${url}`);
    },
  });
  authoring.setInteractive(true);

  view.elements.inspectId.value = "schedule-old";
  await view.elements.inspect.click();
  await view.elements.prepareEdit.click();
  await view.elements.submit.click();
  await view.elements.submit.click();
  assert.match(view.elements.result.textContent, /response is unresolved/i);

  await view.elements.submit.click();
  return {
    controlKinds: calls
      .filter((call) => call.url === "/api/loop-operations/control")
      .map((call) => JSON.parse(call.options.body).kind),
    result: view.elements.result.textContent,
  };
}

function authoredSchedule(scheduleId, enabled, stateRevision = 1) {
  return {
    scheduleId,
    graphId: "graph-published",
    revisionId: "revision-published",
    enabled,
    stateRevision,
    nextOccurrenceAtUtc: "2026-08-30T14:00:00Z",
    recurrenceKind: "fixed-interval",
    firstLocalOccurrence: "2026-08-30T09:00:00",
    fixedIntervalSeconds: 300,
    timeZoneId: "America/Chicago",
    invalidLocalTimePolicy: "reject",
    ambiguousLocalTimePolicy: "earlier",
    misfirePolicy: "skip",
    catchUpLimit: 0,
    overlapPolicy: "skip",
    priority: "normal",
  };
}

function posture(...schedules) {
  return {
    schemaVersion: 1,
    snapshot: {
      schemaVersion: 1,
      controlAuthorityEvidenceHash: "d".repeat(64),
      schedules: { items: schedules },
    },
  };
}

function schedulePosture(scheduleId, enabled, revision) {
  const kind = enabled ? "disable-schedule" : "enable-schedule";
  return {
    scheduleId,
    enabled,
    eligibleControls: [
      {
        kind,
        expectedRevision: revision,
        expectedEvidenceHash: "e".repeat(64),
      },
    ],
  };
}

function createView() {
  const ids = [
    "governedScheduleAmbiguousLocalTime",
    "governedScheduleCatchUpLimit",
    "governedScheduleEnabled",
    "governedScheduleFirstLocalOccurrence",
    "governedScheduleFixedIntervalSeconds",
    "governedScheduleInvalidLocalTime",
    "governedScheduleInspectButton",
    "governedScheduleInspectId",
    "governedScheduleMisfireKind",
    "governedScheduleOverlap",
    "governedSchedulePriority",
    "governedSchedulePrepareEditButton",
    "governedScheduleRecurrenceKind",
    "governedScheduleResult",
    "governedScheduleSubmitButton",
    "governedScheduleTimeZoneId",
  ];
  const elements = Object.fromEntries(ids.map((id) => [id, new FakeElement()]));
  elements.governedScheduleRecurrenceKind.value = "fixed-interval";
  elements.governedScheduleCatchUpLimit.value = "0";
  elements.governedScheduleInvalidLocalTime.value = "reject";
  elements.governedScheduleAmbiguousLocalTime.value = "earlier";
  elements.governedScheduleMisfireKind.value = "skip";
  elements.governedScheduleOverlap.value = "skip";
  elements.governedSchedulePriority.value = "normal";
  return {
    document: { getElementById: (id) => elements[id] },
    elements: {
      ambiguousLocalTime: elements.governedScheduleAmbiguousLocalTime,
      catchUpLimit: elements.governedScheduleCatchUpLimit,
      enabled: elements.governedScheduleEnabled,
      firstLocalOccurrence: elements.governedScheduleFirstLocalOccurrence,
      fixedIntervalSeconds: elements.governedScheduleFixedIntervalSeconds,
      invalidLocalTime: elements.governedScheduleInvalidLocalTime,
      inspect: elements.governedScheduleInspectButton,
      inspectId: elements.governedScheduleInspectId,
      misfireKind: elements.governedScheduleMisfireKind,
      overlap: elements.governedScheduleOverlap,
      priority: elements.governedSchedulePriority,
      prepareEdit: elements.governedSchedulePrepareEditButton,
      recurrenceKind: elements.governedScheduleRecurrenceKind,
      result: elements.governedScheduleResult,
      submit: elements.governedScheduleSubmitButton,
      timeZoneId: elements.governedScheduleTimeZoneId,
    },
  };
}

class FakeElement {
  constructor() {
    this.children = [];
    this.checked = false;
    this.disabled = false;
    this.listeners = new Map();
    this._value = "";
    this._textContent = "";
  }

  addEventListener(name, handler) {
    this.listeners.set(name, handler);
  }

  async click() {
    if (!this.disabled) await this.listeners.get("click")?.({});
  }

  async input() {
    await this.listeners.get("input")?.({});
  }

  get textContent() {
    return this._textContent;
  }

  set textContent(value) {
    this._textContent = String(value ?? "");
    this.children = [];
  }

  get value() {
    return this._value;
  }

  set value(value) {
    this._value = String(value ?? "");
  }
}
