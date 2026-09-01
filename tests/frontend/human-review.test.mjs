import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";

import {
  boundedHumanReviewText,
  createHumanReviewSurface,
  humanReviewActionPath,
  humanReviewOperationIdentity,
  humanReviewOutcomeMessage,
  humanReviewReadMessage,
  normalizeHumanReviewStatus,
  projectHumanReviewPage,
} from "../../src/EmbodySense.Web/wwwroot/human-review.js";

const hash = "a".repeat(64);
const indexSource = fs.readFileSync(
  new URL("../../src/EmbodySense.Web/wwwroot/index.html", import.meta.url),
  "utf8",
);
const loopBuilderSource = fs.readFileSync(
  new URL("../../src/EmbodySense.Web/wwwroot/loop-builder.js", import.meta.url),
  "utf8",
);

test("Human Review exposes stable semantic controls and retires loop-builder approval dispatch", () => {
  assert.match(indexSource, /data-app-view="reviews"/);
  for (const action of ["approve", "reject", "cancel", "request-information"])
    assert.match(
      indexSource,
      new RegExp(`data-testid="human-review-${action}"`),
    );
  assert.match(indexSource, /data-legacy-non-authoritative="true"/);
  assert.doesNotMatch(loopBuilderSource, /DecideApproval/);
  assert.doesNotMatch(loopBuilderSource, /ApprovalsChanged/);
});

function summary(overrides = {}) {
  return {
    runId: "run-review-1",
    requestId: "request-review-1",
    requestHash: hash,
    purpose: "continuation",
    requestedDecisions: ["approve", "reject", "cancel", "request-information"],
    lifecycleStatus: "pending",
    runStatus: "needs-review",
    frontierStatus: "review-blocked",
    lifecycleVersion: 3,
    updatedAtUtc: "2026-09-01T12:00:00Z",
    expiresAtUtc: "2026-09-01T13:00:00Z",
    ...overrides,
  };
}

test("Human Review normalizes the closed action vocabulary and rejects caller-shaped paths", () => {
  assert.equal(
    normalizeHumanReviewStatus("RequestInformation"),
    "request-information",
  );
  assert.deepEqual(
    ["approve", "reject", "cancel", "request-information"].map(
      humanReviewActionPath,
    ),
    ["approve", "reject", "cancel", "request-information"],
  );
  assert.equal(humanReviewActionPath("../approve"), null);
  assert.equal(humanReviewActionPath("DecideApproval"), null);
});

test("Human Review page projection is bounded and fails closed on malformed identities or decisions", () => {
  const page = projectHumanReviewPage({
    status: "ready",
    continuationCursor: "opaque-cursor",
    items: [summary()],
  });
  assert.equal(page.status, "ready");
  assert.equal(page.items.length, 1);
  assert.equal(page.items[0].requestedDecisions.at(-1), "request-information");
  assert.equal(
    projectHumanReviewPage({
      status: "ready",
      items: Array.from({ length: 51 }, () => summary()),
    }).status,
    "invalid",
  );
  assert.equal(
    projectHumanReviewPage({
      status: "ready",
      items: [summary({ runId: "../private" })],
    }).status,
    "invalid",
  );
  assert.equal(
    projectHumanReviewPage({
      status: "ready",
      items: [summary({ requestedDecisions: ["approve", "approve"] })],
    }).status,
    "invalid",
  );
});

test("Human Review operation identities are bounded, lowercase, and free of authority material", () => {
  const operation = humanReviewOperationIdentity("ABC/DEF");
  assert.match(operation, /^web-human-review-[a-z0-9-]+$/);
  assert.ok(operation.length <= 120);
  assert.doesNotMatch(operation, /[A-Z/]/);
  assert.equal(boundedHumanReviewText("x".repeat(20), 8), "xxxxxxx…");
});

test("Human Review outcomes remain safe and explicit across reread, authority, expiry, and availability boundaries", () => {
  assert.match(humanReviewOutcomeMessage("accepted"), /Approval was recorded/);
  assert.match(humanReviewOutcomeMessage("replayed"), /already recorded/);
  assert.match(humanReviewOutcomeMessage("denied"), /not authorized/);
  assert.match(humanReviewOutcomeMessage(null, 409), /conflicted/);
  assert.match(humanReviewOutcomeMessage("expired"), /expired/);
  assert.match(humanReviewOutcomeMessage(null, 503), /temporarily unavailable/);
  assert.match(humanReviewReadMessage("corrupt"), /conflicting/);
  assert.match(humanReviewReadMessage("not-found"), /no longer available/);
});

test("Human Review surface rereads canonical detail and submits all four visible actions with stable operation identity", async () => {
  const fixture = createFixture();
  const calls = [];
  let operationNumber = 0;
  const requestJson = async (url, options = {}) => {
    calls.push({ url, options });
    if (url === "/api/human-reviews?maximumCount=50")
      return { status: "ready", items: [summary()], continuationCursor: null };
    if (url === "/api/human-reviews/run-review-1")
      return {
        status: "ready",
        detail: {
          summary: summary(),
          previews: [
            {
              kind: "action",
              label: "Action",
              detail: "<script>private</script>",
              detailHash: hash,
            },
          ],
          decisions: [],
          evidence: [],
          runtime: {
            lifecycleStatus: "pending",
            frontierStatus: "review-blocked",
            evidenceCount: 0,
            decisionCount: 0,
          },
          effectEvidence: null,
        },
      };
    if (url === "/api/human-reviews/run-review-1/evidence")
      return { status: "ready", evidence: [], effectEvidence: null };
    if (url === "/api/human-reviews/run-review-1/posture")
      return { status: "ready", posture: { lifecycleStatus: "pending" } };
    if (options.method === "POST") return { status: "accepted" };
    throw Object.assign(new Error("unexpected"), { status: 500 });
  };
  const surface = createHumanReviewSurface({
    document: fixture.document,
    window: { crypto: { randomUUID: () => `operation-${++operationNumber}` } },
    requestJson,
  });

  await surface.activate();
  assert.equal(fixture.elements.humanReviewList.children.length, 1);
  assert.equal(fixture.elements.humanReviewDetailPanel.hidden, false);
  assert.equal(fixture.elements.humanReviewApproveButton.disabled, false);
  assert.match(
    fixture.elements.humanReviewPreviews.textContent,
    /<script>private<\/script>/,
  );
  assert.equal(
    findByTag(fixture.elements.humanReviewPreviews, "script").length,
    0,
  );
  await clickAndFlush(fixture.elements.humanReviewApproveButton);
  await clickAndFlush(fixture.elements.humanReviewApproveButton);
  const approveCalls = calls.filter((call) => call.url.endsWith("/approve"));
  assert.equal(approveCalls.length, 2);
  const firstBody = JSON.parse(approveCalls[0].options.body);
  const secondBody = JSON.parse(approveCalls[1].options.body);
  assert.equal(
    approveCalls[0].options.headers["Content-Type"],
    "application/json",
  );
  assert.equal(firstBody.operationId, secondBody.operationId);
  assert.equal(firstBody.expectedLifecycleVersion, 3);
  assert.equal(Object.hasOwn(firstBody, "actor"), false);
  assert.equal(Object.hasOwn(firstBody, "grant"), false);

  const secondFixture = createFixture();
  const secondSurface = createHumanReviewSurface({
    document: secondFixture.document,
    requestJson,
  });
  await secondSurface.activate();
  await clickAndFlush(secondFixture.elements.humanReviewApproveButton);
  const crossTabApprove = calls.filter((call) => call.url.endsWith("/approve"));
  assert.equal(
    JSON.parse(crossTabApprove.at(-1).options.body).operationId,
    firstBody.operationId,
  );

  await clickAndFlush(fixture.elements.humanReviewRejectButton);
  await clickAndFlush(fixture.elements.humanReviewCancelButton);
  assert.equal(
    calls.some((call) => call.url.endsWith("/reject")),
    true,
  );
  assert.equal(
    calls.some((call) => call.url.endsWith("/cancel")),
    true,
  );

  fixture.elements.humanReviewInformationDetail.value =
    "Need one more bounded fact.";
  await clickAndFlush(fixture.elements.humanReviewRequestInformationButton);
  const infoCall = calls.find((call) =>
    call.url.endsWith("/request-information"),
  );
  const infoBody = JSON.parse(infoCall.options.body);
  assert.equal(infoBody.expectedLifecycleVersion, 3);
  assert.equal(infoBody.detail, "Need one more bounded fact.");
  assert.match(infoBody.operationId, /^web-human-review-[a-f0-9-]+$/);
  assert.notEqual(infoBody.operationId, firstBody.operationId);

  assert.equal(
    calls.filter((call) => call.url === "/api/human-reviews/run-review-1")
      .length >= 3,
    true,
  );
});

test("Human Review fails closed when detail evidence is bound to another request", async () => {
  const fixture = createFixture();
  const requestJson = async (url) => {
    if (url === "/api/human-reviews?maximumCount=50")
      return { status: "ready", items: [summary()], continuationCursor: null };
    if (url.endsWith("/evidence"))
      return { status: "ready", evidence: [], effectEvidence: null };
    if (url.endsWith("/posture"))
      return { status: "ready", posture: { lifecycleStatus: "pending" } };
    return {
      status: "ready",
      detail: { summary: summary({ requestHash: "b".repeat(64) }) },
    };
  };
  const surface = createHumanReviewSurface({
    document: fixture.document,
    requestJson,
  });

  await surface.activate();
  assert.equal(fixture.elements.humanReviewApproveButton.disabled, true);
  assert.match(
    fixture.elements.humanReviewDetailStatus.textContent,
    /canonical review response was invalid/i,
  );
});

function createFixture() {
  const ids = [
    "humanReviewActionSection",
    "humanReviewActionStatus",
    "humanReviewActions",
    "humanReviewApproveButton",
    "humanReviewCancelButton",
    "humanReviewDecisionHistory",
    "humanReviewDetailPanel",
    "humanReviewDetailRefreshButton",
    "humanReviewDetailStatus",
    "humanReviewEffectEvidence",
    "humanReviewEvidence",
    "humanReviewEvidencePosture",
    "humanReviewEmpty",
    "humanReviewInformationDetail",
    "humanReviewInformationField",
    "humanReviewRequestInformationButton",
    "humanReviewList",
    "humanReviewListStatus",
    "humanReviewLifecycleStatus",
    "humanReviewPreviews",
    "humanReviewPurpose",
    "humanReviewRejectButton",
    "humanReviewRefreshButton",
    "humanReviewSummary",
    "humanReviewTitle",
    "humanReviewIdentity",
  ];
  const document = new FakeDocument();
  const elements = Object.fromEntries(ids.map((id) => [id, document.add(id)]));
  elements.humanReviewDetailPanel.hidden = true;
  elements.humanReviewEmpty.hidden = false;
  return { document, elements };
}

async function clickAndFlush(element) {
  await element.click();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0));
}

function findByTag(root, tagName) {
  const matches = [];
  for (const child of root.children) {
    if (child.tagName === tagName.toUpperCase()) matches.push(child);
    matches.push(...findByTag(child, tagName));
  }
  return matches;
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
    for (const handler of this.listeners.get("click") ?? []) await handler();
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  removeAttribute(name) {
    this.attributes.delete(name);
  }

  focus() {}
}
