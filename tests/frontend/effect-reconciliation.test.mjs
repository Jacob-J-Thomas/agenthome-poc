import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";

import {
  createEffectReconciliationSurface,
  effectReconciliationOperationIdentity,
  projectEffectReconciliationDetail,
  projectEffectReconciliationPage,
  projectEffectReconciliationProbeCatalog,
  projectEffectReconciliationReference,
} from "../../src/EmbodySense.Web/wwwroot/effect-reconciliation.js";

const hash = "a".repeat(64);
const otherHash = "b".repeat(64);
const reference = {
  caseId: "case-effect-reconciliation-1",
  caseVersion: 1,
  contentHash: hash,
  bindingHash: otherHash,
};
const contract = {
  contractId: "command-action",
  contractVersion: 1,
  contractHash: hash,
  probeContractId: "command-probe",
  probeContractVersion: 1,
  probeContractHash: otherHash,
};
const timestamp = "2026-09-03T12:00:00Z";

const indexSource = fs.readFileSync(
  new URL("../../src/EmbodySense.Web/wwwroot/index.html", import.meta.url),
  "utf8",
);
const appSource = fs.readFileSync(
  new URL("../../src/EmbodySense.Web/wwwroot/app.js", import.meta.url),
  "utf8",
);
const effectSource = fs.readFileSync(
  new URL(
    "../../src/EmbodySense.Web/wwwroot/effect-reconciliation.js",
    import.meta.url,
  ),
  "utf8",
);

function summary(overrides = {}) {
  return { reference, posture: "open", ...overrides };
}

function detail(overrides = {}) {
  return {
    reference,
    posture: "assessed",
    contract,
    evidenceSources: [
      {
        sourceId: "source-registration",
        kind: "authoritative",
        reliabilityPosture: "authoritative",
        contractHash: hash,
        registeredAtUtc: timestamp,
        retiredAtUtc: null,
        contentHash: otherHash,
      },
    ],
    observations: [
      {
        observationId: "observation-1",
        sourceId: "source-registration",
        sourceRegistrationHash: otherHash,
        kind: "evidence",
        reliabilityPosture: "authoritative",
        observedOutcome: "applied-outcome-unknown",
        evidenceReference: "evidence-1",
        evidenceHash: hash,
        observedAtUtc: timestamp,
        recordedAtUtc: timestamp,
        contentHash: hash,
      },
    ],
    assessments: [
      {
        assessmentId: "assessment-1",
        kind: "proved-applied-outcome-unknown",
        observationHashes: [hash],
        assessedAtUtc: timestamp,
        contentHash: otherHash,
      },
    ],
    disposition: null,
    resolution: null,
    receiptHashes: [hash],
    openedAtUtc: timestamp,
    updatedAtUtc: timestamp,
    ...overrides,
  };
}

test("Effect Reconciliation is an authenticated shell view with no browser authority or resolve/open controls", () => {
  for (const id of [
    "effectReconciliationNav",
    "effectReconciliationView",
    "effectReconciliationRefreshButton",
    "effectReconciliationListStatus",
    "effectReconciliationList",
    "effectReconciliationEmpty",
    "effectReconciliationDetailPanel",
    "effectReconciliationDetailRefreshButton",
    "effectReconciliationDetailStatus",
    "effectReconciliationActionStatus",
    "effectReconciliationProbeButton",
    "effectReconciliationAssessButton",
    "effectReconciliationDispositionKind",
    "effectReconciliationDispositionDetail",
    "effectReconciliationDisposeButton",
  ])
    assert.match(indexSource, new RegExp(`id="${id}"`));
  assert.match(indexSource, /data-app-view="effect-reconciliation"/);
  assert.match(appSource, /effectReconciliationView/);
  assert.match(appSource, /effect-reconciliation/);
  assert.doesNotMatch(
    effectSource,
    /localStorage|sessionStorage|SignalR|WebSocket/,
  );
  assert.doesNotMatch(indexSource, /effectReconciliation(Open|Resolve)/);
});

test("Effect Reconciliation projections validate exact refs, bounded pages, and safe catalog values", () => {
  assert.deepEqual(projectEffectReconciliationReference(reference), reference);
  assert.equal(
    projectEffectReconciliationReference({ ...reference, caseVersion: 0 }),
    null,
  );
  assert.equal(
    projectEffectReconciliationPage({
      status: "Ready",
      items: [summary()],
      nextCursor: "opaque.cursor+/==",
    }).items.length,
    1,
  );
  assert.equal(
    projectEffectReconciliationPage({
      status: "Ready",
      items: Array.from({ length: 51 }, () => summary()),
    }).status,
    "invalid",
  );
  assert.equal(
    projectEffectReconciliationPage({
      status: "Ready",
      items: [summary({ posture: "future" })],
    }).status,
    "invalid",
  );
  assert.equal(
    projectEffectReconciliationProbeCatalog({
      status: "Ready",
      contracts: [contract],
      nextCursor: "probe-page",
    }).contracts[0].probeContractId,
    "command-probe",
  );
  assert.equal(
    projectEffectReconciliationDetail({
      status: "Found",
      detail: detail({
        evidenceSources: [
          {
            sourceId: "<script>alert(1)</script>",
            kind: "authoritative",
            reliabilityPosture: "authoritative",
            contractHash: hash,
            registeredAtUtc: timestamp,
            contentHash: otherHash,
          },
        ],
      }),
    }).status,
    "invalid",
  );
});

test("Effect Reconciliation rejects malformed history composition before it reaches the UI", () => {
  const invalidDetails = [
    detail({
      evidenceSources: [
        {
          ...detail().evidenceSources[0],
          sourceId: "source-registration",
        },
        { ...detail().evidenceSources[0], sourceId: "source-registration" },
      ],
    }),
    detail({
      observations: [
        detail().observations[0],
        { ...detail().observations[0], observationId: "observation-2" },
      ],
    }),
    detail({
      observations: [
        { ...detail().observations[0], sourceRegistrationHash: hash },
      ],
    }),
    detail({
      observations: [{ ...detail().observations[0], evidenceReference: null }],
    }),
    detail({
      assessments: [
        { ...detail().assessments[0], observationHashes: [otherHash] },
      ],
    }),
    detail({ posture: "open" }),
    detail({
      posture: "quarantined",
      disposition: {
        dispositionId: "disposition-1",
        kind: "quarantine-unresolved",
        assessmentHash: hash,
        disposedAtUtc: timestamp,
        contentHash: "c".repeat(64),
      },
    }),
  ];

  for (const malformed of invalidDetails)
    assert.equal(
      projectEffectReconciliationDetail({ status: "found", detail: malformed })
        .status,
      "invalid",
    );

  const resolved = detail({
    posture: "resolved",
    observations: [
      {
        ...detail().observations[0],
        observedOutcome: "applied-succeeded",
        evidenceReference: "evidence-1",
        evidenceHash: hash,
      },
    ],
    assessments: [
      {
        ...detail().assessments[0],
        kind: "proved-applied-succeeded",
        observationHashes: [hash],
      },
    ],
    disposition: {
      dispositionId: "disposition-1",
      kind: "accept-proved-applied",
      assessmentHash: otherHash,
      disposedAtUtc: timestamp,
      contentHash: "c".repeat(64),
    },
    resolution: {
      resolutionId: "resolution-1",
      assessmentHash: otherHash,
      dispositionHash: "c".repeat(64),
      outcome: "succeeded",
      outcomeEvidenceId: "evidence-1",
      outcomeEvidenceHash: hash,
      resolvedAtUtc: timestamp,
      contentHash: "d".repeat(64),
    },
  });
  assert.equal(
    projectEffectReconciliationDetail({ status: "found", detail: resolved })
      .status,
    "found",
  );
  assert.equal(
    projectEffectReconciliationDetail({
      status: "found",
      detail: {
        ...resolved,
        resolution: { ...resolved.resolution, dispositionHash: hash },
      },
    }).status,
    "invalid",
  );
  assert.equal(
    projectEffectReconciliationDetail({
      status: "found",
      detail: {
        ...resolved,
        resolution: { ...resolved.resolution, outcomeEvidenceHash: otherHash },
      },
    }).status,
    "invalid",
  );
});

test("Effect Reconciliation reads exact refs and converges after transport loss with a stable in-memory operation", async () => {
  const fixture = new FakeDocument();
  const calls = [];
  let loseNextProbeResponse = true;
  const requestJson = async (url, options = {}) => {
    calls.push({ url, options });
    if (url === "/api/effect-reconciliation?maximumCount=50")
      return { status: "ready", items: [summary()], nextCursor: null };
    if (url === "/api/effect-reconciliation/probes?maximumCount=50")
      return { status: "ready", contracts: [contract], nextCursor: null };
    if (
      url.startsWith(
        "/api/effect-reconciliation/case-effect-reconciliation-1/resolution?",
      )
    )
      return { status: "not-found", resolution: null };
    if (
      url.startsWith("/api/effect-reconciliation/case-effect-reconciliation-1?")
    )
      return {
        status: "found",
        detail: detail({ posture: "open", observations: [], assessments: [] }),
      };
    if (options.method === "POST") {
      if (loseNextProbeResponse) {
        loseNextProbeResponse = false;
        throw new Error("connection closed after durable write");
      }
      return { status: "replayed", detail: detail({ posture: "assessed" }) };
    }
    throw new Error(`Unexpected URL: ${url}`);
  };
  const surface = createEffectReconciliationSurface({
    document: fixture,
    window: {},
    requestJson,
  });

  await surface.activate();
  assert.equal(
    fixture.elementsObject.effectReconciliationList.children.length,
    1,
  );
  assert.equal(
    fixture.elementsObject.effectReconciliationDetailPanel.hidden,
    false,
  );
  await fixture.elementsObject.effectReconciliationProbeButton.click();
  await flushAsyncWork();
  const firstProbe = calls.find((call) => call.options.method === "POST");
  assert.ok(firstProbe);
  const firstBody = JSON.parse(firstProbe.options.body);
  assert.deepEqual(firstBody.case, reference);
  assert.equal(
    firstBody.operationId,
    effectReconciliationOperationIdentity(reference, "probe"),
  );
  assert.match(
    fixture.elementsObject.effectReconciliationActionStatus.textContent,
    /unavailable|temporarily/i,
  );

  await fixture.elementsObject.effectReconciliationProbeButton.click();
  await flushAsyncWork();
  const probePosts = calls.filter((call) => call.options.method === "POST");
  assert.equal(probePosts.length, 2);
  assert.equal(
    JSON.parse(probePosts[1].options.body).operationId,
    firstBody.operationId,
  );
  assert.ok(
    calls.some((call) =>
      call.url.startsWith(
        "/api/effect-reconciliation/case-effect-reconciliation-1/resolution?caseVersion=1&contentHash=",
      ),
    ),
  );
});

test("Effect Reconciliation fails closed when detail returns a different exact reference", async () => {
  const fixture = new FakeDocument();
  const calls = [];
  const requestJson = async (url) => {
    calls.push(url);
    if (url === "/api/effect-reconciliation?maximumCount=50")
      return { status: "ready", items: [summary()], nextCursor: null };
    if (url === "/api/effect-reconciliation/probes?maximumCount=50")
      return { status: "ready", contracts: [contract], nextCursor: null };
    if (
      url.startsWith("/api/effect-reconciliation/case-effect-reconciliation-1?")
    )
      return {
        status: "found",
        detail: detail({
          reference: { ...reference, caseVersion: reference.caseVersion + 1 },
        }),
      };
    throw new Error(`Unexpected URL: ${url}`);
  };
  const surface = createEffectReconciliationSurface({
    document: fixture,
    window: {},
    requestJson,
  });

  await surface.activate();
  assert.equal(
    fixture.elementsObject.effectReconciliationDetailPanel.hidden,
    true,
  );
  assert.equal(fixture.elementsObject.effectReconciliationEmpty.hidden, false);
  assert.equal(
    fixture.elementsObject.effectReconciliationProbeButton.disabled,
    true,
  );
  assert.equal(
    calls.some((url) => url.includes("/resolution?")),
    false,
  );
});

test("Effect Reconciliation keeps case reads usable when the independent probe catalog is unavailable", async () => {
  const fixture = new FakeDocument();
  const requestJson = async (url) => {
    if (url === "/api/effect-reconciliation?maximumCount=50")
      return { status: "ready", items: [summary()], nextCursor: null };
    if (url === "/api/effect-reconciliation/probes?maximumCount=50")
      throw Object.assign(new Error("probe catalog unavailable"), {
        status: 503,
      });
    if (
      url.startsWith("/api/effect-reconciliation/case-effect-reconciliation-1?")
    )
      return { status: "found", detail: detail() };
    if (
      url.startsWith(
        "/api/effect-reconciliation/case-effect-reconciliation-1/resolution?",
      )
    )
      return { status: "not-found", resolution: null };
    throw new Error(`Unexpected URL: ${url}`);
  };
  const surface = createEffectReconciliationSurface({
    document: fixture,
    window: {},
    requestJson,
  });

  await surface.activate();
  assert.equal(
    fixture.elementsObject.effectReconciliationList.children.length,
    1,
  );
  assert.equal(
    fixture.elementsObject.effectReconciliationDetailPanel.hidden,
    false,
  );
  assert.match(
    fixture.elementsObject.effectReconciliationProbeStatus.textContent,
    /unavailable/i,
  );
});

test("Effect Reconciliation rejects cursor cycles and duplicate page identities independently", async () => {
  const fixture = new FakeDocument();
  const calls = [];
  const requestJson = async (url) => {
    calls.push(url);
    if (url === "/api/effect-reconciliation?maximumCount=50")
      return { status: "ready", items: [summary()], nextCursor: "case-next" };
    if (url === "/api/effect-reconciliation?maximumCount=50&cursor=case-next")
      return { status: "ready", items: [summary()], nextCursor: "case-next" };
    if (url === "/api/effect-reconciliation/probes?maximumCount=50")
      return {
        status: "ready",
        contracts: [contract],
        nextCursor: "probe-next",
      };
    if (
      url ===
      "/api/effect-reconciliation/probes?maximumCount=50&cursor=probe-next"
    )
      return { status: "ready", contracts: [contract], nextCursor: null };
    throw new Error(`Unexpected URL: ${url}`);
  };
  const surface = createEffectReconciliationSurface({
    document: fixture,
    window: {},
    requestJson,
  });

  await surface.activate();
  assert.equal(
    fixture.elementsObject.effectReconciliationList.children.length,
    1,
  );
  assert.equal(
    fixture.elementsObject.effectReconciliationProbeCatalog.children.length,
    1,
  );
  assert.match(
    fixture.elementsObject.effectReconciliationList.textContent,
    /invalid|unavailable/i,
  );
  assert.match(
    fixture.elementsObject.effectReconciliationProbeStatus.textContent,
    /invalid|unavailable/i,
  );
  assert.equal(
    fixture.elementsObject.effectReconciliationList.attributes.has("role"),
    false,
  );
  assert.deepEqual(
    [...calls].sort(),
    [
      "/api/effect-reconciliation?maximumCount=50",
      "/api/effect-reconciliation?maximumCount=50&cursor=case-next",
      "/api/effect-reconciliation/probes?maximumCount=50",
      "/api/effect-reconciliation/probes?maximumCount=50&cursor=probe-next",
    ].sort(),
  );
});

test("Effect Reconciliation listbox supports bounded Arrow, Home, and End navigation", async () => {
  const fixture = new FakeDocument();
  const secondReference = {
    ...reference,
    caseId: "case-effect-reconciliation-2",
  };
  const requestJson = async (url) => {
    if (url === "/api/effect-reconciliation?maximumCount=50")
      return {
        status: "ready",
        items: [summary(), summary({ reference: secondReference })],
        nextCursor: null,
      };
    if (url === "/api/effect-reconciliation/probes?maximumCount=50")
      return { status: "ready", contracts: [contract], nextCursor: null };
    if (
      url.startsWith("/api/effect-reconciliation/case-effect-reconciliation-1?")
    )
      return {
        status: "found",
        detail: detail({ posture: "open", observations: [], assessments: [] }),
      };
    if (
      url.startsWith(
        "/api/effect-reconciliation/case-effect-reconciliation-1/resolution?",
      )
    )
      return { status: "not-found", resolution: null };
    throw new Error(`Unexpected URL: ${url}`);
  };
  const surface = createEffectReconciliationSurface({
    document: fixture,
    window: {},
    requestJson,
  });

  await surface.activate();
  const options = fixture.elementsObject.effectReconciliationList.children;
  assert.equal(options.length, 2);
  const preventDefault = () => {};
  options[0].listeners.get("keydown")({ key: "ArrowDown", preventDefault });
  assert.equal(fixture.activeElement, options[1]);
  options[1].listeners.get("keydown")({ key: "Home", preventDefault });
  assert.equal(fixture.activeElement, options[0]);
  options[0].listeners.get("keydown")({ key: "End", preventDefault });
  assert.equal(fixture.activeElement, options[1]);
});

test("Effect Reconciliation ignores disabled disposition clicks until the case is assessed", async () => {
  const fixture = new FakeDocument();
  const calls = [];
  const requestJson = async (url, options = {}) => {
    calls.push({ url, options });
    if (url === "/api/effect-reconciliation?maximumCount=50")
      return { status: "ready", items: [summary()], nextCursor: null };
    if (url === "/api/effect-reconciliation/probes?maximumCount=50")
      return { status: "ready", contracts: [contract], nextCursor: null };
    if (
      url.startsWith("/api/effect-reconciliation/case-effect-reconciliation-1?")
    )
      return {
        status: "found",
        detail: detail({ posture: "open", observations: [], assessments: [] }),
      };
    if (
      url.startsWith(
        "/api/effect-reconciliation/case-effect-reconciliation-1/resolution?",
      )
    )
      return { status: "not-found", resolution: null };
    throw new Error(`Unexpected URL: ${url}`);
  };
  const surface = createEffectReconciliationSurface({
    document: fixture,
    window: {},
    requestJson,
  });

  await surface.activate();
  assert.equal(
    fixture.elementsObject.effectReconciliationDisposeButton.disabled,
    true,
  );
  await fixture.elementsObject.effectReconciliationDisposeButton.click();
  assert.equal(
    calls.some((call) => call.options.method === "POST"),
    false,
  );
});

test("Effect Reconciliation surfaces derive the same stable operation identity without shared browser storage", async () => {
  const calls = [];
  const requestJson = async (url, options = {}) => {
    calls.push({ url, options });
    if (url === "/api/effect-reconciliation?maximumCount=50")
      return { status: "ready", items: [summary()], nextCursor: null };
    if (url === "/api/effect-reconciliation/probes?maximumCount=50")
      return { status: "ready", contracts: [contract], nextCursor: null };
    if (
      url.startsWith(
        "/api/effect-reconciliation/case-effect-reconciliation-1/resolution?",
      )
    )
      return { status: "not-found", resolution: null };
    if (
      url.startsWith("/api/effect-reconciliation/case-effect-reconciliation-1?")
    )
      return { status: "found", detail: detail() };
    if (options.method === "POST")
      return { status: "replayed", detail: detail() };
    throw new Error(`Unexpected URL: ${url}`);
  };
  const first = new FakeDocument();
  const second = new FakeDocument();
  const firstSurface = createEffectReconciliationSurface({
    document: first,
    window: {},
    requestJson,
  });
  const secondSurface = createEffectReconciliationSurface({
    document: second,
    window: {},
    requestJson,
  });
  await firstSurface.activate();
  await secondSurface.activate();
  await first.elementsObject.effectReconciliationProbeButton.click();
  await second.elementsObject.effectReconciliationProbeButton.click();
  await flushAsyncWork();
  const posts = calls.filter((call) => call.options.method === "POST");
  assert.equal(posts.length, 2);
  assert.equal(
    JSON.parse(posts[0].options.body).operationId,
    JSON.parse(posts[1].options.body).operationId,
  );
  assert.equal(
    JSON.parse(posts[0].options.body).operationId,
    effectReconciliationOperationIdentity(reference, "probe"),
  );
});

test("Effect Reconciliation replays assess and dispose with one exact in-memory identity each", async () => {
  const fixture = new FakeDocument();
  const calls = [];
  const lost = new Set();
  const requestJson = async (url, options = {}) => {
    calls.push({ url, options });
    if (url === "/api/effect-reconciliation?maximumCount=50")
      return {
        status: "ready",
        items: [summary({ posture: "assessed" })],
        nextCursor: null,
      };
    if (url === "/api/effect-reconciliation/probes?maximumCount=50")
      return { status: "ready", contracts: [contract], nextCursor: null };
    if (
      url.startsWith(
        "/api/effect-reconciliation/case-effect-reconciliation-1/resolution?",
      )
    )
      return { status: "not-found", resolution: null };
    if (
      url.startsWith("/api/effect-reconciliation/case-effect-reconciliation-1?")
    )
      return { status: "found", detail: detail() };
    if (options.method === "POST") {
      const action = url.includes("/assess?") ? "assess" : "dispose";
      if (!lost.has(action)) {
        lost.add(action);
        throw new Error(`${action} response was lost`);
      }
      return { status: "replayed", detail: detail() };
    }
    throw new Error(`Unexpected URL: ${url}`);
  };
  const surface = createEffectReconciliationSurface({
    document: fixture,
    window: {},
    requestJson,
  });

  await surface.activate();
  await fixture.elementsObject.effectReconciliationAssessButton.click();
  await flushAsyncWork();
  await fixture.elementsObject.effectReconciliationAssessButton.click();
  await flushAsyncWork();
  fixture.elementsObject.effectReconciliationDispositionKind.value =
    "quarantine-unresolved";
  await fixture.elementsObject.effectReconciliationDisposeButton.click();
  await flushAsyncWork();
  await fixture.elementsObject.effectReconciliationDisposeButton.click();
  await flushAsyncWork();

  const posts = calls.filter((call) => call.options.method === "POST");
  assert.equal(posts.length, 4);
  const assessBodies = posts
    .filter((call) => call.url.includes("/assess?"))
    .map((call) => JSON.parse(call.options.body));
  assert.equal(assessBodies.length, 2);
  assert.equal(assessBodies[0].operationId, assessBodies[1].operationId);
  assert.deepEqual(assessBodies[0].case, reference);
  assert.equal(
    assessBodies[0].operationId,
    effectReconciliationOperationIdentity(reference, "assess"),
  );
  const disposeBodies = posts
    .filter((call) => call.url.includes("/dispose?"))
    .map((call) => JSON.parse(call.options.body));
  assert.equal(disposeBodies.length, 2);
  assert.equal(disposeBodies[0].operationId, disposeBodies[1].operationId);
  assert.deepEqual(disposeBodies[0].case, reference);
  assert.equal(disposeBodies[0].dispositionKind, "quarantine-unresolved");
  assert.equal(
    disposeBodies[0].operationId,
    effectReconciliationOperationIdentity(
      reference,
      "dispose",
      "quarantine-unresolved",
    ),
  );
});

class FakeDocument {
  constructor() {
    this.elements = new Map();
    this.elementsObject = {};
    this.activeElement = null;
    for (const id of [
      "effectReconciliationActionStatus",
      "effectReconciliationAssessButton",
      "effectReconciliationDetailPanel",
      "effectReconciliationDetailRefreshButton",
      "effectReconciliationDetailStatus",
      "effectReconciliationDispositionDetail",
      "effectReconciliationDispositionKind",
      "effectReconciliationDisposeButton",
      "effectReconciliationEmpty",
      "effectReconciliationEvidence",
      "effectReconciliationEvidenceSources",
      "effectReconciliationIdentity",
      "effectReconciliationList",
      "effectReconciliationListStatus",
      "effectReconciliationPosture",
      "effectReconciliationProbeButton",
      "effectReconciliationProbeCatalog",
      "effectReconciliationProbeStatus",
      "effectReconciliationRefreshButton",
      "effectReconciliationResolution",
      "effectReconciliationSummary",
      "effectReconciliationTitle",
      "effectReconciliationContract",
    ]) {
      const element = new FakeElement(id, this);
      this.elements.set(id, element);
      this.elementsObject[id] = element;
    }
    this.elements.get("effectReconciliationDetailPanel").hidden = true;
    this.elements.get("effectReconciliationEmpty").hidden = false;
    this.elements.get("effectReconciliationDispositionKind").value = "";
  }

  getElementById(id) {
    return this.elements.get(id);
  }

  createElement(tagName) {
    return new FakeElement(tagName, this);
  }
}

class FakeElement {
  constructor(tagName, ownerDocument = null) {
    this.tagName = String(tagName).toUpperCase();
    this.ownerDocument = ownerDocument;
    this.children = [];
    this.attributes = new Map();
    this.listeners = new Map();
    this.className = "";
    this.hidden = false;
    this.disabled = false;
    this.value = "";
    this._textContent = "";
  }

  append(...nodes) {
    this.children.push(...nodes);
  }

  replaceChildren(...nodes) {
    this.children = [];
    this._textContent = "";
    this.append(...nodes);
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  removeAttribute(name) {
    this.attributes.delete(name);
  }

  addEventListener(name, handler) {
    this.listeners.set(name, handler);
  }

  click() {
    if (this.disabled) return;
    return this.listeners.get("click")?.({ preventDefault() {} });
  }

  focus() {
    if (this.ownerDocument) this.ownerDocument.activeElement = this;
  }

  querySelectorAll(selector) {
    const matches = [];
    const visit = (element) => {
      for (const child of element.children) {
        if (
          selector === '[role="option"]' &&
          child.attributes?.get("role") === "option"
        )
          matches.push(child);
        visit(child);
      }
    };
    visit(this);
    return matches;
  }

  set textContent(value) {
    this._textContent = String(value ?? "");
    this.children = [];
  }

  get textContent() {
    return (
      this._textContent +
      this.children.map((child) => child.textContent).join("")
    );
  }
}

async function flushAsyncWork() {
  await new Promise((resolve) => setTimeout(resolve, 20));
}
