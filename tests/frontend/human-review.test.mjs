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
  projectHumanReviewEvidence,
  projectHumanReviewPage,
  projectHumanReviewSummary,
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
    continuationCursor: "Y3Vyc29y",
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
  assert.equal(
    projectHumanReviewPage({
      status: "ready",
      continuationCursor: "invalid cursor",
      items: [summary()],
    }).status,
    "invalid",
  );
  assert.equal(
    projectHumanReviewPage({
      status: "ready",
      continuationCursor: "x".repeat(1025),
      items: [summary()],
    }).status,
    "invalid",
  );
  for (const field of ["lifecycleStatus", "runStatus", "frontierStatus"])
    assert.equal(
      projectHumanReviewSummary(summary({ [field]: "future" })),
      null,
    );
});

test("Human Review evidence projection fails closed for non-ready and malformed canonical reads", () => {
  assert.equal(
    projectHumanReviewEvidence({ status: "unavailable" }).status,
    "unavailable",
  );
  assert.equal(
    projectHumanReviewEvidence({ status: "conflict" }).status,
    "conflict",
  );
  assert.equal(
    projectHumanReviewEvidence({ status: "ready", evidence: [] }).status,
    "invalid",
  );
  assert.equal(
    projectHumanReviewEvidence({
      status: "ready",
      evidence: [],
      effectEvidence: { status: "future", certainty: "unknown" },
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

test("Human Review replays a committed response after the POST result is lost without using the reread lifecycle", async () => {
  const fixture = createFixture();
  const information =
    "Private reviewer detail that must not enter the operation identity.";
  const calls = [];
  let canonical = summary();
  let postCount = 0;
  const requestJson = async (url, options = {}) => {
    calls.push({ url, options });
    if (url === "/api/human-reviews?maximumCount=50")
      return { status: "ready", items: [canonical], continuationCursor: null };
    if (url === "/api/human-reviews/run-review-1")
      return {
        status: "ready",
        detail: {
          summary: canonical,
          previews: [],
          decisions: [],
          evidence: [],
          runtime: { lifecycleStatus: canonical.lifecycleStatus },
          effectEvidence: null,
        },
      };
    if (url === "/api/human-reviews/run-review-1/evidence")
      return { status: "ready", evidence: [], effectEvidence: null };
    if (url === "/api/human-reviews/run-review-1/posture")
      return {
        status: "ready",
        posture: { lifecycleStatus: canonical.lifecycleStatus },
      };
    if (options.method === "POST") {
      const body = JSON.parse(options.body);
      assert.equal(body.detail, information);
      postCount++;
      if (postCount === 1) {
        canonical = summary({
          lifecycleStatus: "awaiting-information",
          lifecycleVersion: 4,
          updatedAtUtc: "2026-09-01T12:01:00Z",
        });
        throw new Error("simulated committed response loss");
      }
      return { status: "replayed" };
    }
    throw Object.assign(new Error("unexpected"), { status: 500 });
  };
  const surface = createHumanReviewSurface({
    document: fixture.document,
    requestJson,
  });

  await surface.activate();
  fixture.elements.humanReviewInformationDetail.value = information;
  await clickAndFlush(fixture.elements.humanReviewRequestInformationButton);
  assert.match(
    fixture.elements.humanReviewLifecycleStatus.textContent,
    /Awaiting Information/,
  );
  assert.match(
    fixture.elements.humanReviewSummary.textContent,
    /Lifecycle version 4/,
  );

  await clickAndFlush(fixture.elements.humanReviewRequestInformationButton);
  const posts = calls.filter((call) => call.options.method === "POST");
  assert.equal(posts.length, 2);
  const firstBody = JSON.parse(posts[0].options.body);
  const secondBody = JSON.parse(posts[1].options.body);
  assert.equal(firstBody.expectedLifecycleVersion, 3);
  assert.equal(secondBody.expectedLifecycleVersion, 3);
  assert.equal(secondBody.operationId, firstBody.operationId);
  assert.match(firstBody.operationId, /^web-human-review-[a-f0-9-]+$/);
  assert.doesNotMatch(firstBody.operationId, /private|reviewer|detail/i);
  assert.equal(secondBody.detail, information);
  assert.match(
    fixture.elements.humanReviewActionStatus.textContent,
    /already recorded/i,
  );
});

test("Human Review decision operation identity is deterministic across surfaces while retaining only bounded intent state", async () => {
  const firstFixture = createFixture();
  const secondFixture = createFixture();
  const information = "Same exact bounded request-information intent.";
  const requests = [];
  const makeRequestJson =
    () =>
    async (url, options = {}) => {
      requests.push({ url, options });
      if (url === "/api/human-reviews?maximumCount=50")
        return {
          status: "ready",
          items: [summary()],
          continuationCursor: null,
        };
      if (url.endsWith("/evidence"))
        return { status: "ready", evidence: [], effectEvidence: null };
      if (url.endsWith("/posture"))
        return { status: "ready", posture: { lifecycleStatus: "pending" } };
      if (options.method === "POST") return { status: "replayed" };
      return {
        status: "ready",
        detail: {
          summary: summary(),
          previews: [],
          decisions: [],
          evidence: [],
          runtime: { lifecycleStatus: "pending" },
          effectEvidence: null,
        },
      };
    };
  const firstSurface = createHumanReviewSurface({
    document: firstFixture.document,
    requestJson: makeRequestJson(),
  });
  const secondSurface = createHumanReviewSurface({
    document: secondFixture.document,
    requestJson: makeRequestJson(),
  });

  await firstSurface.activate();
  firstFixture.elements.humanReviewInformationDetail.value = information;
  await clickAndFlush(
    firstFixture.elements.humanReviewRequestInformationButton,
  );
  await secondSurface.activate();
  secondFixture.elements.humanReviewInformationDetail.value = information;
  await clickAndFlush(
    secondFixture.elements.humanReviewRequestInformationButton,
  );

  const posts = requests.filter((call) => call.options.method === "POST");
  assert.equal(posts.length, 2);
  const firstBody = JSON.parse(posts[0].options.body);
  const secondBody = JSON.parse(posts[1].options.body);
  assert.equal(firstBody.operationId, secondBody.operationId);
  assert.equal(
    firstBody.expectedLifecycleVersion,
    secondBody.expectedLifecycleVersion,
  );
  assert.equal(firstBody.detail, information);
  assert.equal(secondBody.detail, information);
  assert.ok(requests.length < 32, "surface refresh state must remain bounded");
});

test("Human Review recovers a response-lost operation after a hard reload through same-profile storage", async () => {
  const storage = new FakeStorage();
  const firstFixture = createFixture();
  const secondFixture = createFixture();
  const information = "Private detail must never be persisted or hashed.";
  const calls = [];
  let canonical = summary();
  let postCount = 0;
  const requestJson = async (url, options = {}) => {
    calls.push({ url, options });
    if (url === "/api/human-reviews?maximumCount=50")
      return { status: "ready", items: [canonical], continuationCursor: null };
    if (url.endsWith("/evidence"))
      return { status: "ready", evidence: [], effectEvidence: null };
    if (url.endsWith("/posture"))
      return {
        status: "ready",
        posture: { lifecycleStatus: canonical.lifecycleStatus },
      };
    if (options.method === "POST") {
      postCount++;
      if (postCount === 1) {
        canonical = summary({
          lifecycleStatus: "awaiting-information",
          lifecycleVersion: 4,
        });
        throw new Error("simulated lost response after commit");
      }
      return { status: "replayed" };
    }
    return {
      status: "ready",
      detail: {
        summary: canonical,
        previews: [],
        decisions: [],
        evidence: [],
        runtime: { lifecycleStatus: canonical.lifecycleStatus },
        effectEvidence: null,
      },
    };
  };
  const firstSurface = createHumanReviewSurface({
    document: firstFixture.document,
    window: { localStorage: storage },
    requestJson,
  });
  await firstSurface.activate();
  firstFixture.elements.humanReviewInformationDetail.value = information;
  await clickAndFlush(
    firstFixture.elements.humanReviewRequestInformationButton,
  );
  const storedBeforeReload = storage.value;
  assert.equal(typeof storedBeforeReload, "string");
  assert.doesNotMatch(storedBeforeReload, /Private|detail|hashed/i);
  const storedEntry = JSON.parse(storedBeforeReload).entries[0];
  assert.deepEqual(Object.keys(storedEntry).sort(), [
    "action",
    "expectedLifecycleVersion",
    "operationId",
    "requestHash",
    "requestId",
    "runId",
  ]);
  assert.equal(storedEntry.expectedLifecycleVersion, 3);

  const secondSurface = createHumanReviewSurface({
    document: secondFixture.document,
    window: { localStorage: storage },
    requestJson,
  });
  await secondSurface.activate();
  secondFixture.elements.humanReviewInformationDetail.value = information;
  await clickAndFlush(
    secondFixture.elements.humanReviewRequestInformationButton,
  );
  const posts = calls.filter((call) => call.options.method === "POST");
  assert.equal(posts.length, 2);
  const firstBody = JSON.parse(posts[0].options.body);
  const secondBody = JSON.parse(posts[1].options.body);
  assert.equal(secondBody.operationId, firstBody.operationId);
  assert.equal(secondBody.expectedLifecycleVersion, 3);
  assert.equal(storage.value, null);
});

test("Human Review keeps changed response-lost detail on the same public operation for server conflict", async () => {
  const storage = new FakeStorage();
  const fixture = createFixture();
  const original = "Original bounded reviewer request.";
  const changed = "Changed bounded reviewer request.";
  const calls = [];
  let canonical = summary();
  let postCount = 0;
  const requestJson = async (url, options = {}) => {
    calls.push({ url, options });
    if (url === "/api/human-reviews?maximumCount=50")
      return { status: "ready", items: [canonical], continuationCursor: null };
    if (url.endsWith("/evidence"))
      return { status: "ready", evidence: [], effectEvidence: null };
    if (url.endsWith("/posture"))
      return {
        status: "ready",
        posture: { lifecycleStatus: canonical.lifecycleStatus },
      };
    if (options.method === "POST") {
      const body = JSON.parse(options.body);
      postCount++;
      if (postCount === 1) {
        assert.equal(body.detail, original);
        canonical = summary({ lifecycleVersion: 4 });
        throw new Error("response lost after durable commit");
      }
      assert.equal(body.detail, changed);
      throw Object.assign(new Error("receipt detail conflict"), {
        status: 409,
      });
    }
    return {
      status: "ready",
      detail: {
        summary: canonical,
        previews: [],
        decisions: [],
        evidence: [],
        runtime: { lifecycleStatus: canonical.lifecycleStatus },
        effectEvidence: null,
      },
    };
  };
  const surface = createHumanReviewSurface({
    document: fixture.document,
    window: { localStorage: storage },
    requestJson,
  });
  await surface.activate();
  fixture.elements.humanReviewInformationDetail.value = original;
  await clickAndFlush(fixture.elements.humanReviewRequestInformationButton);
  fixture.elements.humanReviewInformationDetail.value = changed;
  await clickAndFlush(fixture.elements.humanReviewRequestInformationButton);
  const posts = calls.filter((call) => call.options.method === "POST");
  assert.equal(posts.length, 2);
  const firstBody = JSON.parse(posts[0].options.body);
  const secondBody = JSON.parse(posts[1].options.body);
  assert.equal(secondBody.operationId, firstBody.operationId);
  assert.equal(secondBody.expectedLifecycleVersion, 3);
  assert.equal(secondBody.detail, changed);
  assert.equal(storage.value, null);
});

test("Human Review fails closed before dispatch when operation storage is malformed, over-capacity, or unavailable", async () => {
  const invalidValues = [
    JSON.stringify({ schemaVersion: 1, entries: [], extra: true }),
    '{"schemaVersion":1,"entries":[],"entries":[]}',
    JSON.stringify({
      schemaVersion: 1,
      entries: Array.from({ length: 129 }, () => null),
    }),
  ];
  for (const value of invalidValues) {
    const fixture = createFixture();
    const storage = new FakeStorage(value);
    let postCount = 0;
    const requestJson = async (url, options = {}) => {
      if (options.method === "POST") postCount++;
      if (url === "/api/human-reviews?maximumCount=50")
        return {
          status: "ready",
          items: [summary()],
          continuationCursor: null,
        };
      if (url.endsWith("/evidence"))
        return { status: "ready", evidence: [], effectEvidence: null };
      if (url.endsWith("/posture"))
        return { status: "ready", posture: { lifecycleStatus: "pending" } };
      return {
        status: "ready",
        detail: {
          summary: summary(),
          previews: [],
          decisions: [],
          evidence: [],
          runtime: { lifecycleStatus: "pending" },
          effectEvidence: null,
        },
      };
    };
    const surface = createHumanReviewSurface({
      document: fixture.document,
      window: { localStorage: storage },
      requestJson,
    });
    await surface.activate();
    await clickAndFlush(fixture.elements.humanReviewApproveButton);
    assert.equal(postCount, 0);
    assert.match(
      fixture.elements.humanReviewActionStatus.textContent,
      /operation recovery is unavailable/i,
    );
  }
  const fixture = createFixture();
  let postCount = 0;
  const unavailableStorage = {
    getItem() {
      throw new Error("storage unavailable");
    },
    setItem() {
      throw new Error("storage unavailable");
    },
    removeItem() {
      throw new Error("storage unavailable");
    },
  };
  const requestJson = async (url, options = {}) => {
    if (options.method === "POST") postCount++;
    if (url === "/api/human-reviews?maximumCount=50")
      return { status: "ready", items: [summary()], continuationCursor: null };
    if (url.endsWith("/evidence"))
      return { status: "ready", evidence: [], effectEvidence: null };
    if (url.endsWith("/posture"))
      return { status: "ready", posture: { lifecycleStatus: "pending" } };
    return {
      status: "ready",
      detail: {
        summary: summary(),
        previews: [],
        decisions: [],
        evidence: [],
        runtime: { lifecycleStatus: "pending" },
        effectEvidence: null,
      },
    };
  };
  const surface = createHumanReviewSurface({
    document: fixture.document,
    window: { localStorage: unavailableStorage },
    requestJson,
  });
  await surface.activate();
  await clickAndFlush(fixture.elements.humanReviewApproveButton);
  assert.equal(postCount, 0);
});

test("Human Review operation storage separates distinct public requests and profile stores", async () => {
  const sharedStorage = new FakeStorage();
  const separateStorage = new FakeStorage();
  const firstFixture = createFixture();
  const secondFixture = createFixture();
  const separateFixture = createFixture();
  const first = summary();
  const second = summary({
    runId: "run-review-2",
    requestId: "request-review-2",
    requestHash: "b".repeat(64),
  });
  const requestJson = async (url, options = {}) => {
    if (url === "/api/human-reviews?maximumCount=50")
      return {
        status: "ready",
        items: [first, second],
        continuationCursor: null,
      };
    if (url.endsWith("/evidence"))
      return { status: "ready", evidence: [], effectEvidence: null };
    if (url.endsWith("/posture"))
      return { status: "ready", posture: { lifecycleStatus: "pending" } };
    if (options.method === "POST") throw new Error("transport lost");
    const selected = url.includes("run-review-2") ? second : first;
    return {
      status: "ready",
      detail: {
        summary: selected,
        previews: [],
        decisions: [],
        evidence: [],
        runtime: { lifecycleStatus: "pending" },
        effectEvidence: null,
      },
    };
  };
  const firstSurface = createHumanReviewSurface({
    document: firstFixture.document,
    window: { localStorage: sharedStorage },
    requestJson,
  });
  await firstSurface.activate();
  await clickAndFlush(firstFixture.elements.humanReviewApproveButton);
  const firstEntry = JSON.parse(sharedStorage.value).entries[0];
  const secondSurface = createHumanReviewSurface({
    document: secondFixture.document,
    window: { localStorage: sharedStorage },
    requestJson,
  });
  await secondSurface.activate();
  await secondSurface.selectReview(second.runId);
  await clickAndFlush(secondFixture.elements.humanReviewApproveButton);
  const sharedEntries = JSON.parse(sharedStorage.value).entries;
  const secondEntry = sharedEntries.find(
    (entry) => entry.requestHash === second.requestHash,
  );
  assert.notEqual(firstEntry.operationId, secondEntry.operationId);
  assert.notEqual(firstEntry.requestHash, secondEntry.requestHash);
  assert.equal(sharedEntries.length, 2);

  const separateSurface = createHumanReviewSurface({
    document: separateFixture.document,
    window: { localStorage: separateStorage },
    requestJson: async (url, options = {}) => {
      if (url === "/api/human-reviews?maximumCount=50")
        return { status: "ready", items: [first], continuationCursor: null };
      if (url.endsWith("/evidence"))
        return { status: "ready", evidence: [], effectEvidence: null };
      if (url.endsWith("/posture"))
        return { status: "ready", posture: { lifecycleStatus: "pending" } };
      if (options.method === "POST") throw new Error("transport lost");
      return {
        status: "ready",
        detail: {
          summary: first,
          previews: [],
          decisions: [],
          evidence: [],
          runtime: { lifecycleStatus: "pending" },
          effectEvidence: null,
        },
      };
    },
  });
  await separateSurface.activate();
  await clickAndFlush(separateFixture.elements.humanReviewApproveButton);
  assert.equal(JSON.parse(separateStorage.value).entries.length, 1);
});

test("Human Review operation storage evicts the oldest unresolved entries at its bounded cap", async () => {
  const storage = new FakeStorage();
  for (let index = 0; index < 130; index++) {
    const item = summary({
      runId: `run-bounded-${index}`,
      requestId: `request-bounded-${index}`,
      requestHash: index.toString(16).padStart(64, "0"),
    });
    const fixture = createFixture();
    const requestJson = async (url, options = {}) => {
      if (options.method === "POST") throw new Error("transport lost");
      if (url === "/api/human-reviews?maximumCount=50")
        return { status: "ready", items: [item], continuationCursor: null };
      if (url.endsWith("/evidence"))
        return { status: "ready", evidence: [], effectEvidence: null };
      if (url.endsWith("/posture"))
        return { status: "ready", posture: { lifecycleStatus: "pending" } };
      return {
        status: "ready",
        detail: {
          summary: item,
          previews: [],
          decisions: [],
          evidence: [],
          runtime: { lifecycleStatus: "pending" },
          effectEvidence: null,
        },
      };
    };
    const surface = createHumanReviewSurface({
      document: fixture.document,
      window: { localStorage: storage },
      requestJson,
    });
    await surface.activate();
    await clickAndFlush(fixture.elements.humanReviewApproveButton);
  }
  const entries = JSON.parse(storage.value).entries;
  assert.equal(entries.length, 128);
  assert.equal(
    entries.some((entry) => entry.runId === "run-bounded-0"),
    false,
  );
  assert.equal(
    entries.some((entry) => entry.runId === "run-bounded-129"),
    true,
  );
});

test("Human Review starts a new operation identity after a definitive conflict and reread lifecycle", async () => {
  const fixture = createFixture();
  const information = "A bounded information request.";
  const calls = [];
  let canonical = summary();
  let postCount = 0;
  const requestJson = async (url, options = {}) => {
    calls.push({ url, options });
    if (url === "/api/human-reviews?maximumCount=50")
      return { status: "ready", items: [canonical], continuationCursor: null };
    if (url.endsWith("/evidence"))
      return { status: "ready", evidence: [], effectEvidence: null };
    if (url.endsWith("/posture"))
      return {
        status: "ready",
        posture: { lifecycleStatus: canonical.lifecycleStatus },
      };
    if (options.method === "POST") {
      postCount++;
      if (postCount === 1) {
        canonical = summary({
          lifecycleVersion: 4,
          lifecycleStatus: "awaiting-information",
        });
        throw Object.assign(new Error("stale review"), { status: 409 });
      }
      return { status: "information-requested" };
    }
    return {
      status: "ready",
      detail: {
        summary: canonical,
        previews: [],
        decisions: [],
        evidence: [],
        runtime: { lifecycleStatus: canonical.lifecycleStatus },
        effectEvidence: null,
      },
    };
  };
  const surface = createHumanReviewSurface({
    document: fixture.document,
    requestJson,
  });

  await surface.activate();
  fixture.elements.humanReviewInformationDetail.value = information;
  await clickAndFlush(fixture.elements.humanReviewRequestInformationButton);
  fixture.elements.humanReviewInformationDetail.value = information;
  await clickAndFlush(fixture.elements.humanReviewRequestInformationButton);
  const posts = calls.filter((call) => call.options.method === "POST");
  const firstBody = JSON.parse(posts[0].options.body);
  const secondBody = JSON.parse(posts[1].options.body);
  assert.notEqual(firstBody.operationId, secondBody.operationId);
  assert.equal(firstBody.expectedLifecycleVersion, 3);
  assert.equal(secondBody.expectedLifecycleVersion, 4);
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

  const versionFixture = createFixture();
  const versionSurface = createHumanReviewSurface({
    document: versionFixture.document,
    requestJson: async (url) => {
      if (url === "/api/human-reviews?maximumCount=50")
        return {
          status: "ready",
          items: [summary()],
          continuationCursor: null,
        };
      if (url.endsWith("/evidence"))
        return { status: "ready", evidence: [], effectEvidence: null };
      if (url.endsWith("/posture"))
        return { status: "ready", posture: { lifecycleStatus: "pending" } };
      return {
        status: "ready",
        detail: { summary: summary({ lifecycleVersion: 4 }) },
      };
    },
  });

  await versionSurface.activate();
  assert.equal(versionFixture.elements.humanReviewApproveButton.disabled, true);
});

test("Human Review does not fall back to detail effect evidence when canonical evidence is unavailable", async () => {
  for (const evidenceFailure of ["unavailable", "conflict"]) {
    const fixture = createFixture();
    const requestJson = async (url) => {
      if (url === "/api/human-reviews?maximumCount=50")
        return {
          status: "ready",
          items: [summary()],
          continuationCursor: null,
        };
      if (url.endsWith("/evidence")) {
        if (evidenceFailure === "unavailable")
          throw Object.assign(new Error("evidence unavailable"), {
            status: 503,
          });
        return { status: evidenceFailure };
      }
      if (url.endsWith("/posture"))
        return { status: "ready", posture: { lifecycleStatus: "pending" } };
      return {
        status: "ready",
        detail: {
          summary: summary(),
          previews: [],
          decisions: [],
          evidence: [],
          runtime: { lifecycleStatus: "pending" },
          effectEvidence: {
            status: "exact-not-started",
            certainty: "not-started",
          },
        },
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
      /evidence posture is not ready/i,
    );
    assert.match(
      fixture.elements.humanReviewActionStatus.textContent,
      /evidence is not ready/i,
    );
  }
});

test("Human Review paginates bounded canonical pages and rejects cursor cycles and page overflow", async () => {
  const first = summary({
    runId: "run-review-first",
    requestId: "request-review-first",
  });
  const second = summary({
    runId: "run-review-second",
    requestId: "request-review-second",
  });
  const cursor = "Y3Vyc29y";
  const pagedFixture = createFixture();
  const pagedCalls = [];
  const pagedRequestJson = async (url) => {
    pagedCalls.push(url);
    if (url === "/api/human-reviews?maximumCount=50")
      return { status: "ready", items: [first], continuationCursor: cursor };
    if (url === `/api/human-reviews?maximumCount=50&cursor=${cursor}`)
      return { status: "ready", items: [second], continuationCursor: null };
    if (url.endsWith("/evidence"))
      return { status: "ready", evidence: [], effectEvidence: null };
    if (url.endsWith("/posture"))
      return { status: "ready", posture: { lifecycleStatus: "pending" } };
    return {
      status: "ready",
      detail: {
        summary: first,
        previews: [],
        decisions: [],
        evidence: [],
        runtime: {},
      },
    };
  };
  const pagedSurface = createHumanReviewSurface({
    document: pagedFixture.document,
    requestJson: pagedRequestJson,
  });
  await pagedSurface.activate();
  assert.equal(pagedFixture.elements.humanReviewList.children.length, 2);
  assert.ok(
    pagedCalls.includes(`/api/human-reviews?maximumCount=50&cursor=${cursor}`),
  );

  const cycleFixture = createFixture();
  let cycleCalls = 0;
  const cycleSurface = createHumanReviewSurface({
    document: cycleFixture.document,
    requestJson: async () => {
      cycleCalls++;
      return { status: "ready", items: [first], continuationCursor: cursor };
    },
  });
  await cycleSurface.activate();
  assert.equal(cycleFixture.elements.humanReviewList.children.length, 0);
  assert.equal(cycleCalls, 2);

  const overflowFixture = createFixture();
  let overflowCalls = 0;
  const overflowSurface = createHumanReviewSurface({
    document: overflowFixture.document,
    requestJson: async () => {
      overflowCalls++;
      return {
        status: "ready",
        items: [
          summary({
            runId: `run-overflow-${overflowCalls}`,
            requestId: `request-overflow-${overflowCalls}`,
          }),
        ],
        continuationCursor: `cursor--${String(overflowCalls).padStart(2, "0")}`,
      };
    },
  });
  await overflowSurface.activate();
  assert.equal(overflowFixture.elements.humanReviewList.children.length, 0);
  assert.equal(overflowCalls, 20);

  const aggregateFixture = createFixture();
  let aggregateCalls = 0;
  const aggregateSurface = createHumanReviewSurface({
    document: aggregateFixture.document,
    requestJson: async () => {
      aggregateCalls++;
      const items = Array.from(
        { length: aggregateCalls === 11 ? 1 : 50 },
        (_, index) =>
          summary({
            runId: `run-aggregate-${aggregateCalls}-${index}`,
            requestId: `request-aggregate-${aggregateCalls}-${index}`,
          }),
      );
      return {
        status: "ready",
        items,
        continuationCursor: `aggregate-${aggregateCalls}`,
      };
    },
  });
  await aggregateSurface.activate();
  assert.equal(aggregateFixture.elements.humanReviewList.children.length, 0);
  assert.equal(aggregateCalls, 11);
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

class FakeStorage {
  constructor(value = null) {
    this.value = value;
  }

  getItem() {
    return this.value;
  }

  setItem(_key, value) {
    this.value = value;
  }

  removeItem() {
    this.value = null;
  }
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
