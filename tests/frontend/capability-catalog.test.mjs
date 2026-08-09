import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";
import fs from "node:fs";
import test from "node:test";
import vm from "node:vm";

const catalogSource = fs.readFileSync(
  new URL(
    "../../src/EmbodySense.Web/wwwroot/capability-catalog.js",
    import.meta.url,
  ),
  "utf8",
);
const catalogHtml = fs.readFileSync(
  new URL(
    "../../src/EmbodySense.Web/wwwroot/capabilities.html",
    import.meta.url,
  ),
  "utf8",
);
const workspaceScope = "a".repeat(64);

test("catalog boot uses authenticated same-origin reads and renders hostile posture as text", async () => {
  const app = await loadCapabilityCatalog();

  assert.deepEqual(
    app.server.calls.slice(0, 3).map((call) => call.url),
    ["/api/session", "/api/status", "/api/capabilities?maximumCount=50"],
  );
  assert.ok(
    app.server.calls.every(
      (call) => call.options.credentials === "same-origin",
    ),
  );
  assert.ok(app.elements.capabilityTitle.textContent.includes("<SCRIPT>"));
  assert.match(app.elements.capabilityPurpose.textContent, /<img/);
  assert.equal(findByTag(app.elements.capabilityContent, "script").length, 0);
  assert.equal(findByTag(app.elements.capabilityContent, "img").length, 0);
  assert.match(
    app.elements.capabilityFacts.textContent,
    /Local Source.*redacted.*Current Host Compatible/i,
  );
  assert.match(
    app.elements.capabilityFacts.textContent,
    /Contracts.*Schema 1.*sha256:descriptor.*Authority And Egress.*api\.example\.test/i,
  );
  assert.match(
    app.elements.capabilityDependents.textContent,
    /Loop.*default-conversation.*Required.*Assigned Definition/i,
  );
  const affectedLoop = findByTag(app.elements.capabilityDependents, "a")[0];
  assert.equal(affectedLoop.href, "/?view=loops&loopId=default-conversation");
  assert.doesNotMatch(catalogSource, /\.innerHTML\s*=/);
  assert.doesNotMatch(app.storageKey, /workspace/i);
});

test("lifecycle confirmation sends only the exact durable preview identity", async () => {
  const app = await loadCapabilityCatalog();
  app.elements.lifecycleOperation.value = "disable";
  await app.elements.lifecycleOperation.change();

  await app.elements.lifecyclePreviewForm.submit();
  const previewCall = app.server.calls.find(
    (call) =>
      call.method === "POST" &&
      call.url === "/api/capabilities/lifecycle/preview",
  );
  assert.deepEqual(Object.keys(previewCall.body).sort(), [
    "capabilityId",
    "operation",
    "operationId",
    "targetVersion",
  ]);
  assert.equal(previewCall.body.operation, "disable");
  assert.ok(app.localStorage.getItem(app.storageKey));
  assert.match(
    app.elements.lifecyclePreview.textContent,
    new RegExp(`Audit correlation ${previewCall.body.operationId}`),
  );
  assert.match(
    app.elements.lifecyclePreview.textContent,
    /activation 3.*sha256:dependents.*sha256:preview/i,
  );

  const confirmButton = findByTag(app.elements.lifecyclePreview, "button").find(
    (button) => /Confirm Disable/.test(button.textContent),
  );
  await confirmButton.click();
  await flushAsyncWork();

  const confirmation = app.server.calls.find(
    (call) =>
      call.method === "POST" &&
      call.url === "/api/capabilities/lifecycle/confirm",
  );
  assert.equal(confirmation.body.operationId, previewCall.body.operationId);
  assert.equal(confirmation.body.previewHash, "sha256:preview");
  assert.equal(confirmation.body.dependentSetHash, "sha256:dependents");
  assert.equal(confirmation.body.confirmed, true);
  assert.equal(Object.hasOwn(confirmation.body, "targetDescriptor"), false);
  assert.equal(Object.hasOwn(confirmation.body, "artifactDigest"), false);
  assert.match(
    app.window.confirmations[0],
    /exact preview hash sha256:preview/,
  );
  assert.equal(app.localStorage.getItem(app.storageKey), null);
  assert.match(
    app.elements.lifecycleNotice.textContent,
    new RegExp(`Applied.*Operation ${previewCall.body.operationId}`),
  );
});

test("a server-owned current version completes an enable selection that omitted its optional target", async () => {
  const app = await loadCapabilityCatalog();

  await app.elements.lifecyclePreviewForm.submit();

  const previewCall = app.server.calls.find(
    (call) =>
      call.method === "POST" &&
      call.url === "/api/capabilities/lifecycle/preview",
  );
  assert.equal(previewCall.body.operation, "enable");
  assert.equal(previewCall.body.targetVersion, null);
  assert.match(
    app.elements.lifecyclePreview.textContent,
    /Enable.*to 1\.0\.0/i,
  );
});

test("rapid preview submissions admit one operation while its exact response is pending", async () => {
  const server = new FakeCapabilityServer();
  let releasePreview;
  const previewGate = new Promise((resolve) => {
    releasePreview = resolve;
  });
  server.previewHandler = async (call) => {
    await previewGate;
    return server.previewResponse(call);
  };
  const app = await loadCapabilityCatalog({ server });
  app.elements.lifecycleOperation.value = "disable";

  const first = app.elements.lifecyclePreviewForm.submit();
  await app.elements.lifecyclePreviewForm.submit();

  assert.match(
    app.elements.lifecycleNotice.textContent,
    /still being reconciled/i,
  );
  assert.equal(
    server.calls.filter(
      (call) =>
        call.method === "POST" &&
        call.url === "/api/capabilities/lifecycle/preview",
    ).length,
    1,
  );
  releasePreview();
  await first;
  assert.equal(app.elements.lifecyclePreview.hidden, false);
});

test("a mismatched successful response cannot become confirmation authority", async () => {
  const server = new FakeCapabilityServer();
  server.previewHandler = (call) => {
    const response = server.previewResponse(call);
    response.body.preview.capabilityId = "org.example/substituted";
    return response;
  };
  const app = await loadCapabilityCatalog({ server });
  app.elements.lifecycleOperation.value = "disable";

  await app.elements.lifecyclePreviewForm.submit();

  assert.equal(app.elements.lifecyclePreview.hidden, true);
  assert.ok(app.localStorage.getItem(app.storageKey));
  assert.match(
    app.elements.lifecycleNotice.textContent,
    /did not prove the exact requested lifecycle preview identity/i,
  );
});

test("an ambiguous preview transport failure keeps one operation identity for restart replay", async () => {
  const server = new FakeCapabilityServer();
  const localStorage = new FakeStorage();
  let attempts = 0;
  server.previewHandler = (call) => {
    attempts++;
    if (attempts === 1) throw new TypeError("Connection dropped.");
    return server.previewResponse(call);
  };
  const first = await loadCapabilityCatalog({ server, localStorage });
  first.elements.lifecycleOperation.value = "disable";
  await first.elements.lifecyclePreviewForm.submit();
  const retained = JSON.parse(localStorage.getItem(first.storageKey));

  const second = await loadCapabilityCatalog({ server, localStorage });
  const calls = server.calls.filter(
    (call) =>
      call.method === "POST" &&
      call.url === "/api/capabilities/lifecycle/preview",
  );

  assert.equal(calls.length, 2);
  assert.equal(calls[0].body.operationId, retained.selection.operationId);
  assert.equal(calls[1].body.operationId, retained.selection.operationId);
  assert.equal(second.elements.lifecyclePreview.hidden, false);
  assert.match(second.elements.lifecyclePreview.textContent, /Confirm Disable/);
});

test("same-tab retry reuses an indeterminate preview identity and a ready preview blocks replacement", async () => {
  const server = new FakeCapabilityServer();
  let attempts = 0;
  server.previewHandler = (call) => {
    attempts++;
    if (attempts === 1) throw new TypeError("Connection dropped.");
    return server.previewResponse(call);
  };
  const app = await loadCapabilityCatalog({ server });
  app.elements.lifecycleOperation.value = "disable";
  await app.elements.lifecyclePreviewForm.submit();
  const retained = JSON.parse(app.localStorage.getItem(app.storageKey));

  await app.elements.lifecyclePreviewForm.submit();
  app.elements.lifecycleOperation.value = "rollback";
  await app.elements.lifecyclePreviewForm.submit();

  const calls = server.calls.filter(
    (call) =>
      call.method === "POST" &&
      call.url === "/api/capabilities/lifecycle/preview",
  );
  assert.equal(calls.length, 2);
  assert.equal(calls[0].body.operationId, retained.selection.operationId);
  assert.equal(calls[1].body.operationId, retained.selection.operationId);
  assert.match(app.elements.lifecycleNotice.textContent, /discard or confirm/i);
  assert.match(app.elements.lifecyclePreview.textContent, /Confirm Disable/);
});

test("catalog refresh replays a retained preview with the same operation identity", async () => {
  const app = await loadCapabilityCatalog();
  app.elements.lifecycleOperation.value = "disable";
  await app.elements.lifecyclePreviewForm.submit();
  const firstPreview = app.server.calls.find(
    (call) =>
      call.method === "POST" &&
      call.url === "/api/capabilities/lifecycle/preview",
  );

  await app.elements.refreshCapabilitiesButton.click();
  await flushAsyncWork();

  const previews = app.server.calls.filter(
    (call) =>
      call.method === "POST" &&
      call.url === "/api/capabilities/lifecycle/preview",
  );
  assert.equal(previews.length, 2);
  assert.equal(previews[1].body.operationId, firstPreview.body.operationId);
  assert.equal(app.elements.lifecyclePreview.hidden, false);
  assert.match(app.elements.lifecyclePreview.textContent, /Confirm Disable/);
});

test("a failed catalog read preserves pending operation identity without dispatch", async () => {
  const server = new FakeCapabilityServer();
  const localStorage = new FakeStorage();
  const storageKey = `embodysense.pending-capability-lifecycle.v1.${workspaceScope}`;
  localStorage.setItem(
    storageKey,
    JSON.stringify({
      selection: {
        operationId: "web-capability-catalog-retry",
        operation: "disable",
        capabilityId: "org.example/runtime",
        targetVersion: null,
      },
    }),
  );
  server.catalogHandler = () => ({
    status: 503,
    body: { detail: "Catalog temporarily unavailable." },
  });

  const app = await loadCapabilityCatalog({ server, localStorage });

  assert.ok(localStorage.getItem(storageKey));
  assert.equal(
    server.calls.some((call) => call.method === "POST"),
    false,
  );
  assert.match(
    app.elements.catalogNotice.textContent,
    /temporarily unavailable/i,
  );
});

test("reload restores an off-page pending capability through the exact detail endpoint", async () => {
  const server = new FakeCapabilityServer();
  const localStorage = new FakeStorage();
  const storageKey = `embodysense.pending-capability-lifecycle.v1.${workspaceScope}`;
  const capability = createCatalogResponse().capabilities[0];
  localStorage.setItem(
    storageKey,
    JSON.stringify({
      selection: {
        operationId: "web-capability-off-page",
        operation: "disable",
        capabilityId: capability.id,
        targetVersion: null,
      },
    }),
  );
  server.catalogHandler = () => ({
    status: 200,
    body: { ...createCatalogResponse(), capabilities: [] },
  });
  server.detailHandler = () => ({
    status: 200,
    body: { status: "available", capability, error: null },
  });

  const app = await loadCapabilityCatalog({ server, localStorage });

  assert.equal(app.elements.capabilityTitle.textContent, capability.id);
  assert.equal(app.elements.lifecyclePreview.hidden, false);
  assert.match(
    app.elements.lifecyclePreview.textContent,
    /web-capability-off-page/,
  );
  assert.ok(
    server.calls.some(
      (call) =>
        call.url ===
        `/api/capabilities/detail?capabilityId=${encodeURIComponent(capability.id)}`,
    ),
  );
});

test("a definitive stale preview conflict clears retained confirmation authority", async () => {
  const server = new FakeCapabilityServer();
  server.confirmHandler = () => ({
    status: 409,
    body: { status: "conflict", detail: "The preview is stale." },
  });
  const app = await loadCapabilityCatalog({ server });
  app.elements.lifecycleOperation.value = "disable";
  await app.elements.lifecyclePreviewForm.submit();
  const confirmButton = findByTag(app.elements.lifecyclePreview, "button").find(
    (button) => /Confirm Disable/.test(button.textContent),
  );

  await confirmButton.click();

  assert.equal(app.localStorage.getItem(app.storageKey), null);
  assert.equal(app.elements.lifecyclePreview.hidden, true);
  assert.match(app.elements.lifecycleNotice.textContent, /stale/i);
});

test("an indeterminate server failure retains the exact confirmation for replay", async () => {
  const server = new FakeCapabilityServer();
  server.confirmHandler = () => ({
    status: 500,
    body: { detail: "The response was lost after the durable boundary." },
  });
  const app = await loadCapabilityCatalog({ server });
  app.elements.lifecycleOperation.value = "disable";
  await app.elements.lifecyclePreviewForm.submit();
  const retainedBefore = app.localStorage.getItem(app.storageKey);
  const confirmButton = findByTag(app.elements.lifecyclePreview, "button").find(
    (button) => /Confirm Disable/.test(button.textContent),
  );

  await confirmButton.click();

  assert.equal(app.localStorage.getItem(app.storageKey), retainedBefore);
  assert.equal(app.elements.lifecyclePreview.hidden, false);
  assert.match(app.elements.lifecycleNotice.textContent, /durable boundary/i);
});

test("malformed retained browser state is discarded without automatic lifecycle dispatch", async () => {
  const server = new FakeCapabilityServer();
  const localStorage = new FakeStorage();
  const storageKey = `embodysense.pending-capability-lifecycle.v1.${workspaceScope}`;
  localStorage.setItem(
    storageKey,
    JSON.stringify({ selection: { operation: 17 } }),
  );

  await loadCapabilityCatalog({ server, localStorage });

  assert.equal(localStorage.getItem(storageKey), null);
  assert.equal(
    server.calls.some((call) => call.method === "POST"),
    false,
  );
});

test("retained browser state is reconstructed without forged trusted fields", async () => {
  const server = new FakeCapabilityServer();
  const localStorage = new FakeStorage();
  const storageKey = `embodysense.pending-capability-lifecycle.v1.${workspaceScope}`;
  localStorage.setItem(
    storageKey,
    JSON.stringify({
      selection: {
        operationId: "web-capability-retained",
        operation: "disable",
        capabilityId: "org.example/<SCRIPT>alert(1)</SCRIPT>",
        targetVersion: null,
        targetDescriptor: { privateConfiguration: "forged" },
        artifactDigest: "sha256:forged",
      },
    }),
  );

  await loadCapabilityCatalog({ server, localStorage });

  const replay = server.calls.find(
    (call) =>
      call.method === "POST" &&
      call.url === "/api/capabilities/lifecycle/preview",
  );
  assert.deepEqual(Object.keys(replay.body).sort(), [
    "capabilityId",
    "operation",
    "operationId",
    "targetVersion",
  ]);
  assert.equal(replay.body.operationId, "web-capability-retained");
});

test("degraded catalog and required blocked impact remain explicit and fail closed", async () => {
  const server = new FakeCapabilityServer();
  const catalog = createCatalogResponse();
  catalog.capabilities[0].state = "unavailable";
  catalog.capabilities[0].health = "degraded";
  catalog.capabilities[0].areDependentsAvailable = false;
  server.catalogHandler = () => ({ status: 200, body: catalog });
  server.previewHandler = (call) => {
    const response = server.previewResponse(call);
    response.body.preview.isBlocked = true;
    response.body.preview.impacts = [
      {
        dependentKind: "loop",
        dependentIdentity: "default-conversation",
        dependentRevision: "definition-v1",
        requirementKind: "required",
        compatibleVersionRange: "*",
        isCompatible: false,
        authorityPosture: "assigned-definition",
        outcome: "blocked",
      },
    ];
    return response;
  };
  const app = await loadCapabilityCatalog({ server });

  assert.match(
    app.elements.capabilityBadges.textContent,
    /Unavailable.*Degraded/i,
  );
  assert.match(
    app.elements.capabilityDependents.textContent,
    /complete dependent set is unavailable/i,
  );
  app.elements.lifecycleOperation.value = "disable";
  await app.elements.lifecyclePreviewForm.submit();
  const confirm = findByTag(app.elements.lifecyclePreview, "button").find(
    (button) => /Confirm Disable/.test(button.textContent),
  );
  assert.equal(confirm.disabled, true);
  assert.match(
    app.elements.lifecyclePreview.textContent,
    /Required dependents block.*Required.*Blocked/i,
  );
});

test("unavailable browser storage prevents preview dispatch before durable admission", async () => {
  const server = new FakeCapabilityServer();
  const localStorage = new FakeStorage();
  localStorage.failWrites = true;
  const app = await loadCapabilityCatalog({ server, localStorage });
  app.elements.lifecycleOperation.value = "disable";

  await app.elements.lifecyclePreviewForm.submit();

  assert.equal(
    server.calls.some((call) => call.method === "POST"),
    false,
  );
  assert.match(
    app.elements.lifecycleNotice.textContent,
    /no lifecycle preview/i,
  );
});

test("an authoritative mutation outcome survives browser cleanup failure", async () => {
  const app = await loadCapabilityCatalog();
  app.elements.lifecycleOperation.value = "disable";
  await app.elements.lifecyclePreviewForm.submit();
  app.localStorage.failRemovals = true;
  const confirmButton = findByTag(app.elements.lifecyclePreview, "button").find(
    (button) => /Confirm Disable/.test(button.textContent),
  );

  await confirmButton.click();
  await flushAsyncWork();

  assert.match(app.elements.lifecycleNotice.textContent, /Applied/);
  assert.match(
    app.elements.lifecycleNotice.textContent,
    /reconciliation state could not be cleared/i,
  );
  assert.equal(
    app.server.calls.filter(
      (call) =>
        call.method === "POST" &&
        call.url === "/api/capabilities/lifecycle/confirm",
    ).length,
    1,
  );
});

async function loadCapabilityCatalog(options = {}) {
  const document = new FakeDocument(catalogHtml);
  document.elementsObject.capabilityContent.hidden = true;
  document.elementsObject.lifecyclePreview.hidden = true;
  document.elementsObject.loadMoreCapabilitiesButton.hidden = true;
  document.elementsObject.lifecycleOperation.value = "enable";
  const server = options.server ?? new FakeCapabilityServer();
  const localStorage = options.localStorage ?? new FakeStorage();
  const window = {
    confirmations: [],
    localStorage,
    confirm(message) {
      this.confirmations.push(message);
      return true;
    },
  };
  let operation = 0;
  const context = {
    console,
    crypto: {
      subtle: webcrypto.subtle,
      randomUUID: () =>
        `00000000-0000-4000-8000-${String(++operation).padStart(12, "0")}`,
    },
    document,
    fetch: server.fetch.bind(server),
    localStorage,
    setTimeout,
    clearTimeout,
    URLSearchParams,
    window,
  };
  context.globalThis = context;
  vm.runInNewContext(catalogSource, context, {
    filename: "capability-catalog.js",
  });
  await flushAsyncWork();
  const storageKey = `embodysense.pending-capability-lifecycle.v1.${workspaceScope}`;
  return {
    context,
    document,
    elements: document.elementsObject,
    localStorage,
    server,
    storageKey,
    window,
  };
}

class FakeCapabilityServer {
  constructor() {
    this.calls = [];
    this.catalogHandler = () => ({
      status: 200,
      body: createCatalogResponse(),
    });
    this.detailHandler = () => ({
      status: 404,
      body: { detail: "Capability not found." },
    });
    this.previewResponse = (call) => {
      const response = createPreviewResponse();
      response.preview.operationId = call.body.operationId;
      response.preview.operation = call.body.operation;
      response.preview.capabilityId = call.body.capabilityId;
      response.preview.targetVersion =
        call.body.operation === "enable" && call.body.targetVersion === null
          ? "1.0.0"
          : call.body.targetVersion;
      return { status: 200, body: response };
    };
    this.previewHandler = (call) => this.previewResponse(call);
    this.confirmHandler = () => ({
      status: 200,
      body: {
        status: "applied",
        isCommitted: true,
        replayedOutcome: null,
        state: {
          capabilityId: "org.example/<SCRIPT>alert(1)</SCRIPT>",
          version: "1.0.0",
          isEnabled: false,
          isRemoved: false,
          revision: 3,
          updatedAtUtc: "2026-08-09T12:00:00Z",
        },
        lifecycleRevision: 3,
        outcomeAuditPending: false,
        detail: "Applied.",
      },
    });
  }

  async fetch(url, options = {}) {
    const method = options.method ?? "GET";
    const call = {
      url,
      method,
      body: options.body ? JSON.parse(options.body) : null,
      options: { ...options, headers: { ...(options.headers ?? {}) } },
    };
    this.calls.push(call);
    if (method === "GET" && url === "/api/session")
      return responseFrom({
        status: 200,
        body: { generationId: "test", chatRequestScope: workspaceScope },
      });
    if (method === "GET" && url === "/api/status")
      return responseFrom({
        status: 200,
        body: { workspaceRoot: "C:/workspace", initialized: true },
      });
    if (method === "GET" && url.startsWith("/api/capabilities?"))
      return responseFrom(await this.catalogHandler(call));
    if (method === "GET" && url.startsWith("/api/capabilities/detail?"))
      return responseFrom(await this.detailHandler(call));
    if (method === "POST" && url === "/api/capabilities/lifecycle/preview")
      return responseFrom(await this.previewHandler(call));
    if (method === "POST" && url === "/api/capabilities/lifecycle/confirm")
      return responseFrom(await this.confirmHandler(call));
    return responseFrom({
      status: 500,
      body: { detail: `Unexpected request: ${method} ${url}` },
    });
  }
}

function createCatalogResponse() {
  return {
    status: "available",
    catalogRevision: 8,
    capabilities: [
      {
        id: "org.example/<SCRIPT>alert(1)</SCRIPT>",
        version: "1.0.0",
        descriptorHash: "sha256:descriptor",
        kind: "skill",
        purpose: "Inspect <img src=x onerror=alert(1)> safely.",
        providerId: "org.example",
        implementationId: "runtime-lifecycle",
        provenanceKind: "local-source",
        sourceUri: "file:///[redacted]/runtime-lifecycle",
        sourceRevision: "rev-1",
        integrity: "sha256:artifact",
        hostVersionRange: "*",
        supportedPlatforms: ["current-host"],
        isCurrentHostCompatible: true,
        sideEffectClass: "none",
        dataClasses: [],
        egressMode: "none",
        egressDestinations: ["api.example.test"],
        secretRequirements: ["credential-reference-name"],
        state: "available",
        declaration: "declared",
        installation: "installed",
        enablement: "enabled",
        health: "healthy",
        retirement: "active",
        trust: "verified",
        isLifecycleEnabled: true,
        isRemoved: false,
        entryRevision: 8,
        lifecycleRevision: 2,
        isRecovered: false,
        dependents: [
          {
            kind: "loop",
            identity: "default-conversation",
            revision: "definition-v1",
            requirementKind: "required",
            compatibleVersionRange: "*",
            authorityPosture: "assigned-definition",
          },
        ],
        areDependentsAvailable: true,
        dependentsTruncated: false,
      },
    ],
    nextCursor: null,
    error: null,
  };
}

function createPreviewResponse() {
  return {
    status: "ready",
    preview: {
      operationId: "replaced-by-server-call",
      operation: "disable",
      capabilityId: "org.example/<SCRIPT>alert(1)</SCRIPT>",
      targetVersion: null,
      baselineCatalogRevision: 8,
      baselineActivationRevision: 3,
      lifecycleRevision: 2,
      dependentSetRevision: 5,
      dependentSetHash: "sha256:dependents",
      previewHash: "sha256:preview",
      isBlocked: false,
      hasDegradation: false,
      impacts: [],
      detail: "Ready.",
    },
    error: null,
  };
}

function responseFrom({ status, body }) {
  const text = body === null || body === undefined ? "" : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    text: async () => text,
  };
}

class FakeStorage {
  constructor() {
    this.values = new Map();
    this.failRemovals = false;
    this.failWrites = false;
  }

  getItem(key) {
    return this.values.has(key) ? this.values.get(key) : null;
  }

  removeItem(key) {
    if (this.failRemovals) throw new Error("Storage removal failed.");
    this.values.delete(key);
  }

  setItem(key, value) {
    if (this.failWrites) throw new Error("Storage write failed.");
    this.values.set(key, String(value));
  }
}

class FakeDocument {
  constructor(html) {
    this.elements = new Map();
    this.elementsObject = {};
    for (const match of html.matchAll(/<([a-z0-9]+)[^>]*\sid="([^"]+)"/gi)) {
      const element = new FakeElement(match[1]);
      this.elements.set(match[2], element);
      this.elementsObject[match[2]] = element;
    }
  }

  getElementById(id) {
    return this.elements.get(id);
  }

  createDocumentFragment() {
    return new FakeElement("fragment");
  }

  createElement(tagName) {
    return new FakeElement(tagName);
  }
}

class FakeElement {
  constructor(tagName) {
    this.tagName = tagName.toUpperCase();
    this.attributes = new Map();
    this.children = [];
    this.dataset = {};
    this.listeners = new Map();
    this.className = "";
    this.disabled = false;
    this.hidden = false;
    this.required = false;
    this._value = "";
    this._textContent = "";
    this.classList = {
      toggle: (name, force) => {
        const values = new Set(this.className.split(/\s+/).filter(Boolean));
        const add = force === undefined ? !values.has(name) : force;
        if (add) values.add(name);
        else values.delete(name);
        this.className = [...values].join(" ");
      },
    };
  }

  append(...nodes) {
    for (const node of nodes) {
      if (node.tagName === "FRAGMENT") this.children.push(...node.children);
      else this.children.push(node);
    }
  }

  replaceChildren(...nodes) {
    this.children = [];
    this._textContent = "";
    this.append(...nodes);
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  addEventListener(name, handler) {
    this.listeners.set(name, handler);
  }

  async dispatch(name) {
    return this.listeners.get(name)?.({
      target: this,
      preventDefault() {},
    });
  }

  async click() {
    if (!this.disabled) return this.dispatch("click");
  }

  async change() {
    return this.dispatch("change");
  }

  async submit() {
    return this.dispatch("submit");
  }

  focus() {}

  set value(value) {
    this._value = String(value ?? "");
  }

  get value() {
    return this._value;
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

function findByTag(root, tagName) {
  return findAll(root, (child) => child.tagName === tagName.toUpperCase());
}

function findAll(root, predicate) {
  const matches = [];
  for (const child of root.children ?? []) {
    if (predicate(child)) matches.push(child);
    matches.push(...findAll(child, predicate));
  }
  return matches;
}

async function flushAsyncWork() {
  await new Promise((resolve) => setTimeout(resolve, 25));
}
