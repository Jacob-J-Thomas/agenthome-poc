import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";

import {
  boundedHumanInputText,
  createHumanInputSurface,
  humanInputOperationIdentity,
  humanInputOutcomeMessage,
  projectHumanInputPage,
  projectHumanInputPosture,
} from "../../src/EmbodySense.Web/wwwroot/human-input.js";

const hash = "a".repeat(64);
const indexSource = fs.readFileSync(
  new URL("../../src/EmbodySense.Web/wwwroot/index.html", import.meta.url),
  "utf8",
);

test("Human Input is a distinct shell surface with bounded semantic controls", () => {
  assert.match(indexSource, /data-app-view="human-input"/);
  for (const testId of [
    "human-input-nav",
    "human-input-refresh",
    "human-input-detail-refresh",
    "human-input-response-submit",
    "human-input-reject",
    "human-input-cancel",
    "human-input-supersede",
  ])
    assert.match(indexSource, new RegExp(`data-testid="${testId}"`));
  assert.match(indexSource, /human-input\.css/);
  assert.match(indexSource, /human-input\.js/);
});

test("Human Input projection keeps only the bounded recipient aggregate and continuation kind", () => {
  assert.match(
    humanInputOperationIdentity("ABC/DEF"),
    /^web-human-input-[a-z0-9-]+$/,
  );
  const projected = projectHumanInputPosture(posture());
  assert.equal(projected.presentation.eligibleRespondentCount, 1);
  assert.equal(
    projected.presentation.continuationPolicyKind,
    "bound-node-and-checkpoint-only",
  );
  assert.equal(
    Object.hasOwn(projected.presentation, "eligibleRespondents"),
    false,
  );
  assert.equal(Object.hasOwn(projected.presentation, "binding"), false);
  assert.equal(
    projectHumanInputPosture(
      posture({
        presentation: {
          ...posture().presentation,
          eligibleRespondentCount: 17,
        },
      }),
    ),
    null,
  );
  assert.equal(
    projectHumanInputPosture(
      posture({
        presentation: {
          ...posture().presentation,
          continuationPolicyKind: "Unknown",
        },
      }),
    ),
    null,
  );
  assert.equal(
    projectHumanInputPage({
      status: "ready",
      requests: [posture()],
      nextCursor: null,
    }).items.length,
    1,
  );
  assert.equal(
    projectHumanInputPage({
      status: "ready",
      requests: Array.from({ length: 51 }, posture),
      nextCursor: null,
    }).status,
    "invalid",
  );
  const conflicted = projectHumanInputPosture(
    posture({
      supersedesRequestId: "request-input-old",
      supersededByRequestId: "request-input-new",
      latestConflict: {
        operationId: "operation-conflict",
        operationFamily: "Response",
        operationKind: "SelectionConflict",
        failureCode: "SelectionConflict",
        recordedAtUtc: "2026-09-01T12:30:00Z",
        value: "must not cross the boundary",
      },
    }),
  );
  assert.equal(conflicted.latestConflict.operationFamily, "response");
  assert.equal(conflicted.latestConflict.operationKind, "selection-conflict");
  assert.equal(conflicted.latestConflict.failureCode, "selection-conflict");
  assert.equal(Object.hasOwn(conflicted.latestConflict, "value"), false);
  assert.equal(
    projectHumanInputPosture(
      posture({
        latestConflict: {
          operationId: "operation-conflict",
          operationFamily: "Response",
          operationKind: "SelectionConflict",
          failureCode: "contains spaces",
          recordedAtUtc: "2026-09-01T12:30:00Z",
        },
      }),
    ),
    null,
  );
  assert.equal(
    projectHumanInputPosture(
      posture({
        presentation: {
          ...posture().presentation,
          responseSchema: {
            kind: "choice",
            choices: [
              { choiceId: "same-choice", displayText: "First" },
              { choiceId: "same-choice", displayText: "Second" },
            ],
          },
        },
      }),
    ),
    null,
  );
  assert.equal(
    projectHumanInputPosture(
      posture({
        presentation: {
          ...posture().presentation,
          responseSchema: {
            kind: "structured",
            structuredFields: [
              {
                fieldId: "same-field",
                kind: "text",
                required: true,
                maxTextCharacters: 100,
              },
              {
                fieldId: "same-field",
                kind: "text",
                required: false,
                maxTextCharacters: 100,
              },
            ],
          },
        },
      }),
    ),
    null,
  );
  assert.equal(
    projectHumanInputPosture(
      posture({
        presentation: {
          ...posture().presentation,
          responsePolicyKind: "first-valid",
          requiredResponseCount: 1,
        },
      }),
    ),
    null,
  );
  assert.equal(
    projectHumanInputPosture(
      posture({
        presentation: {
          ...posture().presentation,
          responsePolicyKind: "quorum",
          requiredResponseCount: null,
        },
      }),
    ),
    null,
  );
  assert.equal(
    projectHumanInputPosture(
      posture({
        presentation: {
          ...posture().presentation,
          eligibleRespondentCount: 2,
          responsePolicyKind: "quorum",
          requiredResponseCount: 2,
        },
      }),
    ).presentation.requiredResponseCount,
    2,
  );
  assert.equal(
    projectHumanInputPosture(
      posture({
        presentation: {
          ...posture().presentation,
          responsePolicyKind: "merge",
          requiredResponseCount: null,
        },
      }),
    ),
    null,
  );
  assert.equal(boundedHumanInputText("x".repeat(2000), 1024).length, 1024);
});

test("Human Input answer is typed, exact-version-bound, retryable, and free of authority fields", async () => {
  const fixture = createFixture();
  const calls = [];
  const requestJson = async (url, options = {}) => {
    calls.push({ url, options });
    if (url === "/api/human-input?maximumCount=50")
      return { status: "ready", requests: [posture()], nextCursor: null };
    if (url === "/api/human-input/request-input-1") return posture();
    if (options.method === "POST") return { status: "committed" };
    throw new Error("unexpected request");
  };
  const surface = createHumanInputSurface({
    document: fixture.document,
    window: { crypto: { randomUUID: () => "answer-operation" } },
    requestJson,
  });

  await surface.activate();
  const control =
    fixture.elements.humanInputResponseEditor.children[0].children[1];
  control.value = "private response value";
  await clickAndFlush(fixture.elements.humanInputResponseSubmitButton);
  assert.equal(fixture.elements.humanInputResponseSubmitButton.disabled, false);
  assert.match(
    fixture.elements.humanInputResponseStatus.textContent,
    /recorded/i,
  );
  await clickAndFlush(fixture.elements.humanInputResponseSubmitButton);

  const answers = calls.filter((call) => call.url.endsWith("/answer"));
  assert.equal(answers.length, 2);
  const first = JSON.parse(answers[0].options.body);
  const second = JSON.parse(answers[1].options.body);
  assert.equal(first.operationId, second.operationId);
  assert.equal(first.responseId, second.responseId);
  assert.equal(first.expectedLifecycleVersion, 3);
  assert.equal(first.expectedRequest.requestHash, hash);
  assert.equal(first.value.text, "private response value");
  assert.doesNotMatch(first.operationId, /private|response-value/);
  for (const field of [
    "actor",
    "role",
    "workspace",
    "grant",
    "binding",
    "authority",
  ])
    assert.equal(Object.hasOwn(first, field), false);
});

test("Human Input lifecycle controls and supersede keep operation identity separate from private state", async () => {
  const fixture = createFixture();
  const calls = [];
  const requestJson = async (url, options = {}) => {
    calls.push({ url, options });
    if (url === "/api/human-input?maximumCount=50")
      return { status: "ready", requests: [posture()], nextCursor: null };
    if (url === "/api/human-input/request-input-1") return posture();
    if (url.endsWith("/supersede/prepare"))
      return {
        status: "ready",
        candidateKey: "candidate-opaque",
        expiresAtUtc: "2026-09-01T13:00:00Z",
      };
    if (url.endsWith("/supersede")) return { status: "replayed" };
    if (url.endsWith("/reject")) return { status: "committed" };
    throw new Error("unexpected request");
  };
  const surface = createHumanInputSurface({
    document: fixture.document,
    window: { crypto: { randomUUID: () => "lifecycle-operation" } },
    requestJson,
  });

  await surface.activate();
  await clickAndFlush(fixture.elements.humanInputSupersedeButton);
  assert.match(
    fixture.elements.humanInputSupersedeButton.textContent,
    /commit/i,
  );
  await clickAndFlush(fixture.elements.humanInputSupersedeButton);
  assert.match(
    fixture.elements.humanInputResponseStatus.textContent,
    /already recorded/i,
  );
  await clickAndFlush(fixture.elements.humanInputRejectButton);

  const prepare = calls.find((call) => call.url.endsWith("/supersede/prepare"));
  const commit = calls.find((call) => call.url.endsWith("/supersede"));
  const reject = calls.find((call) => call.url.endsWith("/reject"));
  const prepareBody = JSON.parse(prepare.options.body);
  const commitBody = JSON.parse(commit.options.body);
  const rejectBody = JSON.parse(reject.options.body);
  assert.equal(commitBody.candidateKey, "candidate-opaque");
  assert.equal(commitBody.operationId, prepareBody.operationId);
  assert.equal(rejectBody.reason, "reject");
  assert.equal(prepareBody.operationId, commitBody.operationId);
  assert.deepEqual(prepareBody.successor.responsePolicy, {
    kind: "preserve-canonical",
  });
  assert.equal(Object.hasOwn(prepareBody, "actor"), false);
  assert.match(humanInputOutcomeMessage("replayed"), /already recorded/i);
});

test("Human Input conflict and transport error feedback survive canonical rereads", async () => {
  for (const outcome of ["conflict", "error"]) {
    const fixture = createFixture();
    const current = posture({
      latestConflict: {
        operationId: "operation-conflict",
        operationFamily: "Response",
        operationKind: "Submit",
        failureCode: "SelectionConflict",
        recordedAtUtc: "2026-09-01T12:30:00Z",
      },
    });
    const requestJson = async (url, options = {}) => {
      if (url === "/api/human-input?maximumCount=50")
        return { status: "ready", requests: [current], nextCursor: null };
      if (url === "/api/human-input/request-input-1") return current;
      if (options.method === "POST") {
        if (outcome === "error") throw new Error("transport unavailable");
        return { status: "conflict" };
      }
      throw new Error("unexpected request");
    };
    const surface = createHumanInputSurface({
      document: fixture.document,
      requestJson,
    });
    await surface.activate();
    fixture.elements.humanInputResponseEditor.children[0].children[1].value =
      "bounded response";
    await clickAndFlush(fixture.elements.humanInputResponseSubmitButton);
    assert.match(
      fixture.elements.humanInputResponseStatus.textContent,
      outcome === "conflict"
        ? /changed|conflicted/i
        : /temporarily unavailable/i,
    );
    assert.match(
      fixture.elements.humanInputSummary.textContent,
      /selection conflict/i,
    );
  }
});

test("Human Input supersede remains available for every canonical response policy", async () => {
  for (const [responsePolicyKind, requiredResponseCount] of [
    ["first-valid", null],
    ["quorum", 2],
    ["named-roles", null],
    ["merge", 1],
    ["manual-selection", null],
  ]) {
    const fixture = createFixture();
    const calls = [];
    const current = posture({
      presentation: {
        ...posture().presentation,
        eligibleRespondentCount: 2,
        responsePolicyKind,
        requiredResponseCount,
      },
    });
    const surface = createHumanInputSurface({
      document: fixture.document,
      requestJson: async (url, options = {}) => {
        calls.push({ url, options });
        if (url === "/api/human-input?maximumCount=50")
          return { status: "ready", requests: [current], nextCursor: null };
        if (url === "/api/human-input/request-input-1") return current;
        if (url.endsWith("/supersede/prepare"))
          return {
            status: "ready",
            candidateKey: "candidate-opaque",
            expiresAtUtc: "2026-09-01T13:00:00Z",
          };
        if (url.endsWith("/supersede")) return { status: "committed" };
        throw new Error("unexpected request");
      },
    });
    await surface.activate();
    await clickAndFlush(fixture.elements.humanInputSupersedeButton);
    await clickAndFlush(fixture.elements.humanInputSupersedeButton);
    const prepare = calls.find((call) =>
      call.url.endsWith("/supersede/prepare"),
    );
    const commit = calls.find((call) => call.url.endsWith("/supersede"));
    assert.ok(prepare, responsePolicyKind);
    assert.ok(commit, responsePolicyKind);
    assert.deepEqual(
      JSON.parse(prepare.options.body).successor.responsePolicy,
      { kind: "preserve-canonical" },
      responsePolicyKind,
    );
    assert.equal(
      JSON.parse(commit.options.body).candidateKey,
      "candidate-opaque",
    );
    assert.match(
      fixture.elements.humanInputResponseStatus.textContent,
      /recorded/i,
    );
  }
});

test("Human Input selection clears prior request drafts and keeps feedback scoped", async () => {
  const fixture = createFixture();
  const first = posture({
    requestId: "request-input-1",
    supersedesRequestId: "request-input-old",
    latestConflict: {
      operationId: "operation-conflict",
      operationFamily: "Response",
      operationKind: "Submit",
      failureCode: "SelectionConflict",
      recordedAtUtc: "2026-09-01T12:30:00Z",
    },
  });
  const second = posture({
    requestId: "request-input-2",
    presentation: {
      ...posture().presentation,
      requestVersionId: "version-input-2",
      purpose: "Second bounded purpose.",
      prompt: "Second bounded prompt.",
    },
    currentRequest: {
      schemaVersion: 1,
      requestId: "request-input-2",
      requestVersionId: "version-input-2",
      requestHash: hash,
    },
  });
  const requestJson = async (url) => {
    if (url === "/api/human-input?maximumCount=50")
      return { status: "ready", requests: [first, second], nextCursor: null };
    if (url.endsWith("request-input-1")) return first;
    if (url.endsWith("request-input-2")) return second;
    throw new Error("unexpected request");
  };
  const surface = createHumanInputSurface({
    document: fixture.document,
    requestJson,
  });
  await surface.activate();
  assert.match(
    fixture.elements.humanInputSummary.textContent,
    /request-input-old/,
  );
  assert.match(
    fixture.elements.humanInputSummary.textContent,
    /selection conflict/i,
  );
  fixture.elements.humanInputExplanation.value = "private explanation";
  fixture.elements.humanInputSupersedePurpose.value =
    "private successor purpose";
  fixture.elements.humanInputSupersedePrompt.value = "private successor prompt";
  await surface.selectRequest("request-input-2");
  assert.equal(fixture.elements.humanInputExplanation.value, "");
  assert.equal(
    fixture.elements.humanInputSupersedePurpose.value,
    "Second bounded purpose.",
  );
  assert.equal(
    fixture.elements.humanInputSupersedePrompt.value,
    "Second bounded prompt.",
  );
  assert.doesNotMatch(
    fixture.elements.humanInputSummary.textContent,
    /request-input-old/,
  );
  assert.equal(fixture.elements.humanInputResponseStatus.textContent, "");
});

test("Human Input pagination rejects cycles and aggregate overflow", async () => {
  const first = posture({ requestId: "request-input-first" });
  const fixture = createFixture();
  let calls = 0;
  const cycleSurface = createHumanInputSurface({
    document: fixture.document,
    requestJson: async () => {
      calls++;
      return { status: "ready", requests: [first], nextCursor: "Y3Vyc29y" };
    },
  });
  await cycleSurface.activate();
  assert.equal(calls, 2);
  assert.equal(fixture.elements.humanInputList.children.length, 0);
  assert.match(
    fixture.elements.humanInputListStatus.textContent,
    /invalid|canonical/i,
  );

  const overflowFixture = createFixture();
  let page = 0;
  const overflowSurface = createHumanInputSurface({
    document: overflowFixture.document,
    requestJson: async () => {
      page++;
      return {
        status: "ready",
        requests: Array.from({ length: 50 }, (_, index) =>
          posture({ requestId: `request-input-${page}-${index}` }),
        ),
        nextCursor: `cursor-${page}xx`,
      };
    },
  });
  await overflowSurface.activate();
  assert.equal(page, 11);
  assert.equal(overflowFixture.elements.humanInputList.children.length, 0);
});

function posture(overrides = {}) {
  const requestId = overrides.requestId ?? "request-input-1";
  const presentation = {
    requestVersionId: "version-input-1",
    requestHash: hash,
    purpose: "Collect a bounded data value.",
    prompt: "Provide one display-safe value.",
    responseSchema: { kind: "text", maxTextCharacters: 4000 },
    privacyClass: "sensitive",
    timing: {
      requestedAtUtc: "2026-09-01T12:00:00Z",
      expiresAtUtc: "2026-09-01T13:00:00Z",
    },
    responsePolicyKind: "first-valid",
    requiredResponseCount: null,
    eligibleRespondentCount: 1,
    continuationPolicyKind: "bound-node-and-checkpoint-only",
    ...overrides.presentation,
  };
  return {
    schemaVersion: 1,
    requestId,
    lifecycleVersion: 3,
    status: "pending",
    currentRequest: {
      schemaVersion: 1,
      requestId,
      requestVersionId: "version-input-1",
      requestHash: hash,
    },
    presentation,
    reminderCount: 0,
    supersedesRequestId: null,
    supersededByRequestId: null,
    updatedAtUtc: "2026-09-01T12:00:00Z",
    acceptedResponseCount: 0,
    activeResponseCount: 0,
    withdrawnResponseCount: 0,
    isAnswered: false,
    ...overrides,
  };
}

function createFixture() {
  const ids = [
    "humanInputActionsSection",
    "humanInputCancelButton",
    "humanInputDetailPanel",
    "humanInputDetailRefreshButton",
    "humanInputDetailStatus",
    "humanInputEmpty",
    "humanInputExplanation",
    "humanInputIdentity",
    "humanInputLifecycleStatus",
    "humanInputList",
    "humanInputListStatus",
    "humanInputPrivacySummary",
    "humanInputPrompt",
    "humanInputPurpose",
    "humanInputRefreshButton",
    "humanInputRejectButton",
    "humanInputResponseEditor",
    "humanInputResponseForm",
    "humanInputResponseSection",
    "humanInputResponseStatus",
    "humanInputResponseSchema",
    "humanInputResponseSubmitButton",
    "humanInputSummary",
    "humanInputSupersedeButton",
    "humanInputSupersedePrompt",
    "humanInputSupersedePurpose",
    "humanInputSupersedeSection",
    "humanInputSupersedeStatus",
    "humanInputTitle",
  ];
  const document = new FakeDocument();
  const elements = Object.fromEntries(ids.map((id) => [id, document.add(id)]));
  elements.humanInputDetailPanel.hidden = true;
  elements.humanInputEmpty.hidden = false;
  elements.humanInputSupersedeSection.hidden = true;
  return { document, elements };
}

async function clickAndFlush(element) {
  await element.click();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0));
}

class FakeDocument {
  constructor() {
    this.elements = new Map();
  }

  add(id) {
    const element = new FakeElement("div", this);
    element.id = id;
    this.elements.set(id, element);
    return element;
  }

  getElementById(id) {
    return this.elements.get(id);
  }

  createElement(tagName) {
    return new FakeElement(tagName, this);
  }
}

class FakeElement {
  constructor(tagName, ownerDocument) {
    this.tagName = tagName.toUpperCase();
    this.ownerDocument = ownerDocument;
    this.children = [];
    this.dataset = {};
    this.listeners = new Map();
    this.attributes = new Map();
    this.className = "";
    this.classList = {
      add: (...names) =>
        names.forEach((name) => this.attributes.set(`class:${name}`, true)),
      remove: (...names) =>
        names.forEach((name) => this.attributes.delete(`class:${name}`)),
    };
    this.hidden = false;
    this.disabled = false;
    this.value = "";
    this.checked = false;
    this._textContent = "";
  }

  get textContent() {
    return [
      this._textContent,
      ...this.children.map((child) => child.textContent),
    ]
      .filter(Boolean)
      .join(" ");
  }

  set textContent(value) {
    this._textContent = String(value ?? "");
    this.children = [];
  }

  append(...nodes) {
    this.children.push(...nodes);
  }

  replaceChildren(...nodes) {
    this.children = nodes;
  }

  addEventListener(type, handler) {
    const handlers = this.listeners.get(type) ?? [];
    handlers.push(handler);
    this.listeners.set(type, handlers);
  }

  async click() {
    if (this.disabled) return;
    const event = { preventDefault() {} };
    for (const handler of this.listeners.get("click") ?? [])
      await handler(event);
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  removeAttribute(name) {
    this.attributes.delete(name);
  }

  focus() {}
}
