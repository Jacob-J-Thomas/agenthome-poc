import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";
import vm from "node:vm";

const appSource = fs.readFileSync(new URL("../../src/EmbodySense.Web/wwwroot/app.js", import.meta.url), "utf8");
const indexSource = fs.readFileSync(new URL("../../src/EmbodySense.Web/wwwroot/index.html", import.meta.url), "utf8");
const recordSeparator = "\u001e";

test("history_loaded replaces the transcript using role labels and text content", async () => {
  const app = await loadApp();
  app.elements.transcript.replaceChildren();

  const unsafeContent = "<img src=x onerror=alert(1)>";
  app.socket.serverSendInvocation("StreamEvent", {
    type: "history_loaded",
    messages: [
      { role: "user", content: "restore this" },
      { role: "assistant", content: unsafeContent },
      { role: "unknown", content: "system fallback" }
    ]
  });
  await flushAsyncWork();

  assert.equal(app.elements.transcript.children.length, 3);
  assert.equal(messageRole(app.elements.transcript.children[0]), "User");
  assert.equal(messageRole(app.elements.transcript.children[1]), "Assistant");
  assert.equal(messageContent(app.elements.transcript.children[1]), unsafeContent);
  assert.equal(messageRole(app.elements.transcript.children[2]), "System");
  assert.equal(findByTag(app.elements.transcript, "img").length, 0);
});

test("assistant deltas update one active message and final text resets the active message", async () => {
  const app = await loadApp();
  app.elements.transcript.replaceChildren();

  app.socket.serverSendInvocation("StreamEvent", { type: "assistant_delta", text: "Hel" });
  app.socket.serverSendInvocation("StreamEvent", { type: "assistant_delta", text: "lo" });
  app.socket.serverSendInvocation("StreamEvent", { type: "assistant_final", text: "Hello." });
  app.socket.serverSendInvocation("StreamEvent", { type: "assistant_delta", text: "Next" });
  await flushAsyncWork();

  assert.equal(app.elements.transcript.children.length, 2);
  assert.equal(messageContent(app.elements.transcript.children[0]), "Hello.");
  assert.equal(messageContent(app.elements.transcript.children[1]), "Next");
});

test("configuration tabs render permission details without creating markup from raw JSON", async () => {
  const rawJson = "{\"note\":\"<script>bad()</script>\"}";
  const app = await loadApp({
    configuration: {
      status: { initialized: true },
      runtime: { surface: "web", model: "gpt-test", codexSandbox: "read-only" },
      audit: { path: "audit/events.ndjson", exists: true, events: [], readProblems: [] },
      conversationHistory: { directoryPath: ".agent/memory/conversations", currentPath: "current.ndjson", archivePath: "archive", transcripts: [], readProblems: [] },
      paths: [],
      concepts: [],
      documents: [],
      permissions: {
        exists: true,
        parsed: true,
        version: 1,
        scope: "workspace",
        defaultAccess: "ask",
        readProblems: [],
        approved: [{ path: "workspace/shared/**", requiresApproval: true, effect: "allow", operations: ["read"], detail: "Read requires approval." }],
        denied: [],
        rawJson
      }
    }
  });

  await configTab(app, "permissions").click();

  assert.equal(configTab(app, "permissions").attributes.get("aria-selected"), "true");
  assert.match(app.elements.configContent.textContent, /workspace\/shared\/\*\*/);
  assert.match(app.elements.configContent.textContent, /<script>bad\(\)<\/script>/);
  assert.equal(findByTag(app.elements.configContent, "script").length, 0);
});

test("verbose toggle invokes hub and verbose context renders as system text", async () => {
  const app = await loadApp();
  app.elements.transcript.replaceChildren();

  app.elements.verboseToggle.checked = true;
  await app.elements.verboseToggle.change();
  app.socket.serverSendInvocation("StreamEvent", { type: "verbose_context", text: "visible <script>context</script>" });
  await flushAsyncWork();

  assert.deepEqual(app.socket.sentInvocations("SetVerboseMode").map(invocation => invocation.arguments), [[true]]);
  assert.equal(app.elements.transcript.children.length, 1);
  assert.equal(messageRole(app.elements.transcript.children[0]), "System");
  assert.equal(messageContent(app.elements.transcript.children[0]), "visible <script>context</script>");
  assert.equal(findByTag(app.elements.transcript, "script").length, 0);
});

test("approval panel renders pending requests and dispatches approve and reject decisions", async () => {
  const app = await loadApp();

  app.socket.serverSendInvocation("ApprovalsChanged", [{
    requestId: "req-1",
    command: "read",
    operation: "file",
    targetPath: "workspace/shared/note.txt",
    resolvedPath: "C:/workspace/shared/note.txt",
    matchedPath: "workspace/shared/**",
    reason: "Need to inspect the note."
  }]);
  await flushAsyncWork();

  assert.equal(app.elements.approvalCount.textContent, "1 pending");
  assert.match(app.elements.approvals.textContent, /read file/);
  assert.match(app.elements.approvals.textContent, /workspace\/shared\/note\.txt/);

  const buttons = findByTag(app.elements.approvals, "button");
  await buttons.find(button => button.textContent === "Approve").click();
  await buttons.find(button => button.textContent === "Reject").click();

  assert.deepEqual(app.socket.sentInvocations("DecideApproval").map(invocation => invocation.arguments), [
    ["req-1", { approved: true }],
    ["req-1", { approved: false }]
  ]);
});

async function loadApp(overrides = {}) {
  FakeWebSocket.instances = [];
  const document = new FakeDocument(indexSource);
  const context = {
    URL,
    console,
    document,
    fetch: createFetch(overrides),
    setTimeout,
    clearTimeout,
    window: { location: { href: "http://127.0.0.1:4378/" }, setTimeout, clearTimeout },
    WebSocket: FakeWebSocket
  };
  context.globalThis = context;
  vm.runInNewContext(appSource, context, { filename: "app.js" });
  await flushAsyncWork();
  assert.equal(FakeWebSocket.instances.length, 1);
  return { elements: document.elementsObject, configTabs: document.configTabs, socket: FakeWebSocket.instances[0] };
}

function createFetch(overrides) {
  const status = overrides.status ?? { workspaceRoot: "C:/workspace", initialized: true, client: "web", cliRole: "CLI remains available." };
  const configuration = overrides.configuration ?? {
    status: { initialized: true },
    runtime: { surface: "web", model: "configured externally", codexSandbox: "read-only" },
    audit: { path: "audit/events.ndjson", exists: false, events: [], readProblems: [] },
    conversationHistory: { directoryPath: ".agent/memory/conversations", currentPath: "current.ndjson", archivePath: "archive", transcripts: [], readProblems: [] },
    paths: [],
    concepts: [],
    documents: [],
    permissions: { exists: false, parsed: false, version: null, scope: "", defaultAccess: "ask", readProblems: [], approved: [], denied: [], rawJson: "" }
  };
  return async url => {
    if (url === "/api/session") {
      return jsonResponse({ token: "test-token" });
    }

    if (url === "/api/status") {
      return jsonResponse(status);
    }

    if (url === "/api/configuration") {
      return jsonResponse(configuration);
    }

    return { ok: false, text: async () => `Unexpected URL: ${url}` };
  };
}

function jsonResponse(value) {
  return { ok: true, json: async () => value, text: async () => JSON.stringify(value) };
}

async function flushAsyncWork() {
  await new Promise(resolve => setTimeout(resolve, 20));
}

function configTab(app, name) {
  return app.configTabs.find(tab => tab.dataset.configTab === name);
}

function messageRole(message) {
  return message.querySelector(".message-role").textContent;
}

function messageContent(message) {
  return message.querySelector(".message-content").textContent;
}

function findByTag(root, tagName) {
  const matches = [];
  for (const child of root.children) {
    if (child.tagName === tagName.toUpperCase()) {
      matches.push(child);
    }

    matches.push(...findByTag(child, tagName));
  }

  return matches;
}

class FakeWebSocket {
  static OPEN = 1;
  static instances = [];

  constructor(url) {
    this.url = url;
    this.readyState = FakeWebSocket.OPEN;
    this.sent = [];
    FakeWebSocket.instances.push(this);
    setTimeout(() => this.onopen?.(), 0);
  }

  send(message) {
    this.sent.push(message);
    const payload = parseFrame(message);
    if (!payload.type) {
      setTimeout(() => this.serverSend({}), 0);
      return;
    }

    if (payload.type === 1 && payload.invocationId !== undefined) {
      setTimeout(() => this.serverSend({ type: 3, invocationId: payload.invocationId, result: payload.target === "DecideApproval" ? { accepted: true } : true }), 0);
    }
  }

  serverSendInvocation(target, ...args) {
    this.serverSend({ type: 1, target, arguments: args });
  }

  serverSend(message) {
    this.onmessage?.({ data: `${JSON.stringify(message)}${recordSeparator}` });
  }

  sentInvocations(target) {
    return this.sent
      .map(parseFrame)
      .filter(message => message.type === 1 && message.target === target);
  }
}

function parseFrame(message) {
  return JSON.parse(String(message).replace(recordSeparator, ""));
}

class FakeDocument {
  constructor(html) {
    this.elements = new Map();
    this.elementsObject = {};
    this.configTabs = [...html.matchAll(/<([a-z0-9]+)[^>]*\sdata-config-tab="([^"]+)"/gi)].map(match => {
      const element = new FakeElement("button");
      element.dataset.configTab = match[2];
      return element;
    });

    for (const match of html.matchAll(/<([a-z0-9]+)[^>]*\sid="([^"]+)"/gi)) {
      const element = new FakeElement(match[1]);
      this.elements.set(match[2], element);
      this.elementsObject[match[2]] = element;
    }
  }

  getElementById(id) {
    return this.elements.get(id);
  }

  querySelectorAll(selector) {
    return selector === "[data-config-tab]" ? this.configTabs : [];
  }

  createElement(tagName) {
    return new FakeElement(tagName);
  }

  createDocumentFragment() {
    return new FakeElement("#fragment");
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
    this.checked = false;
    this.open = false;
    this.scrollHeight = 0;
    this.scrollTop = 0;
    this.value = "";
    this._textContent = "";
    this.classList = {
      toggle: (name, force) => {
        const values = new Set(this.className.split(/\s+/).filter(Boolean));
        if (force) {
          values.add(name);
        } else {
          values.delete(name);
        }

        this.className = [...values].join(" ");
      }
    };
  }

  append(...nodes) {
    for (const node of nodes) {
      if (node.tagName === "#FRAGMENT") {
        this.children.push(...node.children);
      } else {
        this.children.push(node);
      }
    }

    this.scrollHeight = this.children.length;
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

  click() {
    return this.listeners.get("click")?.({ preventDefault() { } });
  }

  change() {
    return this.listeners.get("change")?.({ preventDefault() { } });
  }

  querySelector(selector) {
    if (!selector.startsWith(".")) {
      return null;
    }

    const className = selector.slice(1);
    return findFirst(this, child => child.className.split(/\s+/).includes(className));
  }

  set textContent(value) {
    this._textContent = String(value ?? "");
    this.children = [];
  }

  get textContent() {
    return this._textContent + this.children.map(child => child.textContent).join("");
  }
}

function findFirst(root, predicate) {
  for (const child of root.children) {
    if (predicate(child)) {
      return child;
    }

    const nested = findFirst(child, predicate);
    if (nested) {
      return nested;
    }
  }

  return null;
}
