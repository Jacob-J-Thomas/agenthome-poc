let sessionToken = "";
let status = null;
let configuration = null;
let activeConfigTab = "overview";
let activeAgentMessage = null;
let hub = null;

const elements = {
  approvals: document.getElementById("approvals"),
  approvalCount: document.getElementById("approvalCount"),
  cancelButton: document.getElementById("cancelButton"),
  cliRole: document.getElementById("cliRole"),
  clientRole: document.getElementById("clientRole"),
  clientStatus: document.getElementById("clientStatus"),
  configContent: document.getElementById("configContent"),
  configTabs: Array.from(document.querySelectorAll("[data-config-tab]")),
  initButton: document.getElementById("initButton"),
  messageForm: document.getElementById("messageForm"),
  messageInput: document.getElementById("messageInput"),
  refreshConfigButton: document.getElementById("refreshConfigButton"),
  sendButton: document.getElementById("sendButton"),
  transcript: document.getElementById("transcript"),
  verboseToggle: document.getElementById("verboseToggle"),
  workspaceRoot: document.getElementById("workspaceRoot"),
  workspaceStatus: document.getElementById("workspaceStatus")
};

const recordSeparator = "\u001e";

async function boot() {
  const session = await fetchJson("/api/session");
  sessionToken = session.token;
  await refreshStatus();
  await connectHub();
  await refreshConfiguration();
  hydrateCurrentTranscript();
}

async function fetchJson(url, options = {}) {
  const request = { ...options, headers: { ...(options.headers ?? {}) } };
  const response = await fetch(url, request);
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return await response.json();
}

function createSessionHeaders() {
  return sessionToken ? { "X-EmbodySense-Session": sessionToken } : {};
}

async function refreshStatus() {
  status = await fetchJson("/api/status");
  applyStatus(status);
}

async function refreshConfiguration() {
  if (!sessionToken) {
    return;
  }

  elements.refreshConfigButton.disabled = true;
  renderConfigLoading();
  try {
    configuration = await fetchJson("/api/configuration", { headers: createSessionHeaders() });
    renderConfiguration();
  } catch (error) {
    renderConfigError(error.message);
  } finally {
    elements.refreshConfigButton.disabled = false;
  }
}

function applyStatus(nextStatus) {
  status = nextStatus;
  elements.workspaceRoot.textContent = status.workspaceRoot;
  elements.workspaceStatus.textContent = status.initialized ? "Initialized" : "Needs initialization";
  elements.clientStatus.textContent = hub?.connected ? "Web primary" : "Web reconnecting";
  elements.clientRole.textContent = status.client;
  elements.cliRole.textContent = status.cliRole;
  elements.initButton.disabled = status.initialized || !hub?.connected;
  elements.sendButton.disabled = !status.initialized || !hub?.connected;
  elements.verboseToggle.disabled = !status.initialized || !hub?.connected;
}

async function connectHub() {
  hub = new JsonSignalRConnection(createHubUrl());
  hub.on("StatusChanged", applyStatus);
  hub.on("ApprovalsChanged", renderApprovals);
  hub.on("StreamEvent", handleStreamEvent);
  hub.onclose = scheduleReconnect;
  await hub.start();
  applyStatus(status);
}

function createHubUrl() {
  const url = new URL("/hubs/session", window.location.href);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  url.searchParams.set("access_token", sessionToken);
  return url.toString();
}

function scheduleReconnect() {
  applyDisconnectedState();
  window.setTimeout(async () => {
    try {
      await connectHub();
    } catch (error) {
      appendMessage("error", `Reconnect failed: ${error.message}`);
      scheduleReconnect();
    }
  }, 1000);
}

function applyDisconnectedState() {
  elements.clientStatus.textContent = "Web reconnecting";
  elements.initButton.disabled = true;
  elements.sendButton.disabled = true;
  elements.cancelButton.disabled = true;
  elements.verboseToggle.disabled = true;
}

function renderConfigLoading() {
  elements.configContent.replaceChildren(createState("Loading configuration"));
}

function renderConfigError(message) {
  elements.configContent.replaceChildren(createState(`Configuration unavailable: ${message}`, "error"));
}

function renderConfiguration() {
  if (!configuration) {
    renderConfigLoading();
    return;
  }

  for (const tab of elements.configTabs) {
    const selected = tab.dataset.configTab === activeConfigTab;
    tab.classList.toggle("active", selected);
    tab.setAttribute("aria-selected", selected ? "true" : "false");
  }

  const renderers = {
    overview: renderOverviewTab,
    permissions: renderPermissionsTab,
    agent: renderAgentTab,
    audit: renderAuditTab,
    history: renderHistoryTab
  };
  elements.configContent.replaceChildren(renderers[activeConfigTab]?.() ?? renderOverviewTab());
}

function renderOverviewTab() {
  const fragment = document.createDocumentFragment();
  fragment.append(renderMetricGrid([
    ["Workspace", configuration.status.initialized ? "Initialized" : "Needs initialization"],
    ["Surface", configuration.runtime.surface],
    ["Model", configuration.runtime.model],
    ["Sandbox", configuration.runtime.codexSandbox],
    ["Audit events", String(configuration.audit.events.length)],
    ["Transcripts", String(configuration.conversationHistory.transcripts.length)]
  ]));
  fragment.append(renderPathGroup(configuration.paths));
  fragment.append(renderConcepts(configuration.concepts));
  return fragment;
}

function renderPermissionsTab() {
  const fragment = document.createDocumentFragment();
  fragment.append(renderMetricGrid([
    ["File", configuration.permissions.exists ? "Present" : "Missing"],
    ["Parsed", configuration.permissions.parsed ? "Yes" : "No"],
    ["Version", configuration.permissions.version ?? "Missing"],
    ["Scope", configuration.permissions.scope || "Missing"],
    ["Default", configuration.permissions.defaultAccess]
  ]));
  fragment.append(renderProblems(configuration.permissions.readProblems));
  fragment.append(renderRuleSection("Approved", configuration.permissions.approved));
  fragment.append(renderRuleSection("Denied", configuration.permissions.denied));
  fragment.append(renderDetails("permissions.json", configuration.permissions.rawJson || "Missing"));
  return fragment;
}

function renderAgentTab() {
  const fragment = document.createDocumentFragment();
  const documents = groupBy(configuration.documents, document => document.category);
  for (const [category, items] of documents) {
    fragment.append(renderSectionHeading(category));
    for (const documentItem of items) {
      fragment.append(renderDocument(documentItem));
    }
  }

  return fragment;
}

function renderAuditTab() {
  const fragment = document.createDocumentFragment();
  fragment.append(renderMetricGrid([
    ["Path", configuration.audit.path],
    ["File", configuration.audit.exists ? "Present" : "Missing"],
    ["Events", String(configuration.audit.events.length)]
  ]));
  fragment.append(renderProblems(configuration.audit.readProblems));
  if (configuration.audit.events.length === 0) {
    fragment.append(createState("No audit events"));
    return fragment;
  }

  for (const event of [...configuration.audit.events].reverse()) {
    fragment.append(renderAuditEvent(event));
  }

  return fragment;
}

function renderHistoryTab() {
  const fragment = document.createDocumentFragment();
  fragment.append(renderMetricGrid([
    ["Directory", configuration.conversationHistory.directoryPath],
    ["Current", configuration.conversationHistory.currentPath],
    ["Archive", configuration.conversationHistory.archivePath],
    ["Transcripts", String(configuration.conversationHistory.transcripts.length)]
  ]));
  fragment.append(renderProblems(configuration.conversationHistory.readProblems));
  for (const transcript of configuration.conversationHistory.transcripts) {
    fragment.append(renderTranscript(transcript));
  }

  return fragment;
}

function renderMetricGrid(items) {
  const grid = document.createElement("dl");
  grid.className = "config-metrics";
  for (const [label, value] of items) {
    const item = document.createElement("div");
    const term = document.createElement("dt");
    term.textContent = label;
    const description = document.createElement("dd");
    description.textContent = value ?? "";
    item.append(term, description);
    grid.append(item);
  }

  return grid;
}

function renderPathGroup(paths) {
  const section = document.createElement("section");
  section.className = "config-group";
  section.append(renderSectionHeading("Paths"));
  for (const path of paths) {
    const item = document.createElement("article");
    item.className = "config-row";
    item.append(renderRowHeader(path.name, path.exists ? "Present" : "Missing"));
    item.append(textLine(path.category, "muted"));
    item.append(textLine(path.path, "path"));
    item.append(textLine(path.description, "muted"));
    section.append(item);
  }

  return section;
}

function renderConcepts(concepts) {
  const section = document.createElement("section");
  section.className = "config-group";
  section.append(renderSectionHeading("Concepts"));
  for (const concept of concepts) {
    const item = document.createElement("article");
    item.className = "config-row";
    item.append(renderRowHeader(concept.name, concept.status));
    item.append(textLine(concept.category, "muted"));
    item.append(textLine(concept.detail, "muted"));
    section.append(item);
  }

  return section;
}

function renderRuleSection(title, rules) {
  const section = document.createElement("section");
  section.className = "config-group";
  section.append(renderSectionHeading(`${title} (${rules.length})`));
  if (rules.length === 0) {
    section.append(createState("No rules"));
    return section;
  }

  for (const rule of rules) {
    const item = document.createElement("article");
    item.className = "config-row permission-rule";
    item.append(renderRowHeader(rule.path, rule.requiresApproval ? "Approval" : rule.effect));
    item.append(renderChipList(rule.operations));
    item.append(textLine(rule.detail, "muted"));
    section.append(item);
  }

  return section;
}

function renderDocument(documentItem) {
  const details = document.createElement("details");
  details.className = "config-document";
  if (documentItem.exists && ["Agent guide", "Context", "Memory", "Models"].includes(documentItem.name)) {
    details.open = true;
  }

  const summary = document.createElement("summary");
  summary.append(renderRowHeader(documentItem.name, documentItem.exists ? "Present" : "Missing"));
  details.append(summary);
  details.append(renderMetricGrid([
    ["Path", documentItem.path],
    ["Size", `${documentItem.sizeBytes} bytes`],
    ["Modified", formatDate(documentItem.lastModifiedUtc)]
  ]));
  details.append(renderCodeBlock(documentItem.content || "Missing"));
  return details;
}

function renderAuditEvent(event) {
  const item = document.createElement("article");
  item.className = "config-row audit-event";
  item.append(renderRowHeader(`${event.sequence}. ${event.action}`, event.outcome));
  item.append(renderMetricGrid([
    ["Time", formatDate(event.timestampUtc)],
    ["Actor", event.actor],
    ["Target", event.target],
    ["Detail", event.detail]
  ]));
  const metadata = Object.entries(event.metadata ?? {});
  if (metadata.length > 0) {
    item.append(renderKeyValueList("Metadata", metadata));
  }

  return item;
}

function renderTranscript(transcript) {
  const details = document.createElement("details");
  details.className = "config-document transcript-detail";
  details.open = transcript.isCurrent;

  const summary = document.createElement("summary");
  summary.append(renderRowHeader(transcript.conversationId, transcript.exists ? `${transcript.messageCount} messages` : "Missing"));
  details.append(summary);
  details.append(renderMetricGrid([
    ["Path", transcript.path],
    ["First", formatDate(transcript.firstTimestampUtc)],
    ["Last", formatDate(transcript.lastTimestampUtc)],
    ["First prompt", transcript.firstPrompt || "None"]
  ]));
  if (transcript.messages.length === 0) {
    details.append(createState("No messages"));
    return details;
  }

  for (const message of transcript.messages) {
    const item = document.createElement("article");
    item.className = "history-message";
    item.append(renderRowHeader(`${message.sequence}. ${message.role}`, formatDate(message.timestampUtc)));
    item.append(textLine(message.content, "content"));
    details.append(item);
  }

  return details;
}

function renderProblems(problems) {
  const section = document.createElement("section");
  section.className = "config-problems";
  if (!problems || problems.length === 0) {
    return section;
  }

  section.append(renderSectionHeading("Read problems"));
  for (const problem of problems) {
    section.append(textLine(problem, "error-text"));
  }

  return section;
}

function renderDetails(title, content) {
  const details = document.createElement("details");
  details.className = "config-document";
  const summary = document.createElement("summary");
  summary.textContent = title;
  details.append(summary, renderCodeBlock(content));
  return details;
}

function renderKeyValueList(title, entries) {
  const section = document.createElement("section");
  section.className = "config-group";
  section.append(renderSectionHeading(title));
  const list = document.createElement("dl");
  list.className = "metadata-list";
  for (const [key, value] of entries) {
    const item = document.createElement("div");
    const term = document.createElement("dt");
    term.textContent = key;
    const description = document.createElement("dd");
    description.textContent = value;
    item.append(term, description);
    list.append(item);
  }

  section.append(list);
  return section;
}

function renderChipList(values) {
  const list = document.createElement("div");
  list.className = "chip-list";
  for (const value of values) {
    const chip = document.createElement("span");
    chip.textContent = value;
    list.append(chip);
  }

  return list;
}

function renderRowHeader(title, statusText) {
  const header = document.createElement("div");
  header.className = "row-header";
  const strong = document.createElement("strong");
  strong.textContent = title;
  const statusBadge = document.createElement("span");
  statusBadge.textContent = statusText;
  header.append(strong, statusBadge);
  return header;
}

function renderSectionHeading(text) {
  const heading = document.createElement("h3");
  heading.textContent = text;
  return heading;
}

function renderCodeBlock(content) {
  const pre = document.createElement("pre");
  pre.textContent = content;
  return pre;
}

function createState(text, kind = "") {
  const state = document.createElement("p");
  state.className = kind ? `empty-state ${kind}` : "empty-state";
  state.textContent = text;
  return state;
}

function textLine(text, className = "") {
  const line = document.createElement("p");
  line.className = className;
  line.textContent = text ?? "";
  return line;
}

function formatDate(value) {
  if (!value) {
    return "None";
  }

  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? String(value) : date.toLocaleString();
}

function groupBy(items, selector) {
  const groups = new Map();
  for (const item of items) {
    const key = selector(item);
    if (!groups.has(key)) {
      groups.set(key, []);
    }

    groups.get(key).push(item);
  }

  return groups;
}

function renderApprovals(approvals) {
  elements.approvalCount.textContent = `${approvals.length} pending`;
  elements.approvals.replaceChildren(...approvals.map(renderApproval));
}

function renderApproval(approval) {
  const item = document.createElement("article");
  item.className = "approval";

  const title = document.createElement("strong");
  title.textContent = `${approval.command} ${approval.operation}`;
  item.append(title);

  for (const text of [
    `Target: ${approval.targetPath}`,
    `Resolved: ${approval.resolvedPath}`,
    `Matched: ${approval.matchedPath}`,
    approval.reason
  ]) {
    const line = document.createElement("p");
    line.textContent = text;
    item.append(line);
  }

  const actions = document.createElement("div");
  actions.className = "approval-actions";

  const reject = document.createElement("button");
  reject.className = "reject";
  reject.type = "button";
  reject.textContent = "Reject";
  reject.addEventListener("click", () => decideApproval(approval.requestId, false));

  const approve = document.createElement("button");
  approve.className = "approve";
  approve.type = "button";
  approve.textContent = "Approve";
  approve.addEventListener("click", () => decideApproval(approval.requestId, true));

  actions.append(reject, approve);
  item.append(actions);
  return item;
}

async function decideApproval(requestId, approved) {
  const result = await hub.invoke("DecideApproval", requestId, { approved });
  if (!result.accepted) {
    appendMessage("error", result.message);
  }
}

function appendMessage(kind, text) {
  const message = createMessage(kind, text);
  elements.transcript.append(message);
  elements.transcript.scrollTop = elements.transcript.scrollHeight;
  return message;
}

function createMessage(kind, text) {
  const message = document.createElement("div");
  message.className = `message ${kind}`;
  const role = document.createElement("strong");
  role.className = "message-role";
  role.textContent = messageRoleLabel(kind);
  const content = document.createElement("p");
  content.className = "message-content";
  content.textContent = text;
  message.append(role, content);
  return message;
}

function messageRoleLabel(kind) {
  if (kind === "user") {
    return "User";
  }

  if (kind === "agent") {
    return "Assistant";
  }

  if (kind === "tool") {
    return "Tool";
  }

  if (kind === "system") {
    return "System";
  }

  return "Error";
}

function getMessageContent(message) {
  return message.querySelector(".message-content") ?? message;
}

function replaceTranscript(messages) {
  activeAgentMessage = null;
  const renderedMessages = (messages ?? []).map(message => createMessage(messageKind(message.role), message.content ?? ""));
  elements.transcript.replaceChildren(...renderedMessages);
  elements.transcript.scrollTop = elements.transcript.scrollHeight;
}

function hydrateCurrentTranscript() {
  const currentTranscript = configuration?.conversationHistory?.transcripts?.find(transcript => transcript.isCurrent);
  if (currentTranscript) {
    replaceTranscript(currentTranscript.messages);
  }
}

function messageKind(role) {
  const normalizedRole = String(role ?? "").toLowerCase();
  if (normalizedRole === "user") {
    return "user";
  }

  if (normalizedRole === "assistant") {
    return "agent";
  }

  if (normalizedRole === "tool") {
    return "tool";
  }

  return "system";
}

function appendAgentDelta(text) {
  if (!activeAgentMessage) {
    activeAgentMessage = appendMessage("agent", "");
  }

  getMessageContent(activeAgentMessage).textContent += text;
  elements.transcript.scrollTop = elements.transcript.scrollHeight;
}

function finalizeAgentMessage(text) {
  if (activeAgentMessage) {
    getMessageContent(activeAgentMessage).textContent = text;
  } else {
    activeAgentMessage = appendMessage("agent", text);
  }

  elements.transcript.scrollTop = elements.transcript.scrollHeight;
  activeAgentMessage = null;
}

elements.initButton.addEventListener("click", async () => {
  elements.initButton.disabled = true;
  const nextStatus = await hub.invoke("InitializeWorkspace");
  applyStatus(nextStatus);
  await refreshConfiguration();
});

elements.refreshConfigButton.addEventListener("click", refreshConfiguration);

elements.verboseToggle.addEventListener("change", async () => {
  const enabled = elements.verboseToggle.checked;
  elements.verboseToggle.disabled = true;
  try {
    await hub.invoke("SetVerboseMode", enabled);
  } catch (error) {
    elements.verboseToggle.checked = !enabled;
    appendMessage("error", error.message);
  } finally {
    applyStatus(status);
  }
});

for (const tab of elements.configTabs) {
  tab.addEventListener("click", () => {
    activeConfigTab = tab.dataset.configTab ?? "overview";
    renderConfiguration();
  });
}

elements.cancelButton.addEventListener("click", async () => {
  elements.cancelButton.disabled = true;
  const cancelled = await hub.invoke("CancelCurrentTurn");
  if (!cancelled) {
    appendMessage("error", "No active agent turn is running.");
  }
});

elements.messageForm.addEventListener("submit", async event => {
  event.preventDefault();
  const message = elements.messageInput.value.trim();
  if (!message || !status?.initialized || !hub?.connected) {
    return;
  }

  activeAgentMessage = null;
  appendMessage("user", message);
  elements.messageInput.value = "";
  elements.sendButton.disabled = true;
  elements.cancelButton.disabled = false;

  try {
    await hub.invoke("SendMessage", message);
  } catch (error) {
    appendMessage("error", error.message);
  } finally {
    elements.cancelButton.disabled = true;
    elements.sendButton.disabled = !status?.initialized || !hub?.connected;
  }
});

function handleStreamEvent(event) {
  if (event.type === "assistant_delta") {
    appendAgentDelta(event.text ?? "");
  } else if (event.type === "assistant_final") {
    finalizeAgentMessage(event.text ?? "");
  } else if (event.type === "history_loaded") {
    replaceTranscript(event.messages ?? []);
  } else if (event.type === "verbose_context" || event.type === "system") {
    appendMessage("system", event.text ?? "");
  } else if (event.type === "cancelled") {
    appendMessage("error", event.text ?? "Message cancelled.");
    activeAgentMessage = null;
  } else if (event.type === "error") {
    appendMessage("error", event.error ?? "Request failed.");
  }
}

class JsonSignalRConnection {
  constructor(url) {
    this.url = url;
    this.socket = null;
    this.handlers = new Map();
    this.invocations = new Map();
    this.nextInvocationId = 0;
    this.buffer = "";
    this.connected = false;
    this.closedByClient = false;
    this.isClosed = true;
    this.handshake = null;
    this.handshakeReject = null;
    this.handshakeResolve = null;
    this.onclose = null;
  }

  on(target, handler) {
    this.handlers.set(target, handler);
  }

  async start() {
    this.closedByClient = false;
    this.isClosed = false;
    this.socket = new WebSocket(this.url);
    this.socket.onmessage = event => this.receive(event.data);
    this.socket.onclose = () => this.handleClose();

    await new Promise((resolve, reject) => {
      this.socket.onopen = () => {
        resolve();
      };
      this.socket.onerror = () => reject(new Error("SignalR connection failed."));
    });

    this.handshake = new Promise((resolve, reject) => {
      this.handshakeResolve = resolve;
      this.handshakeReject = reject;
      window.setTimeout(() => this.handshakeReject?.(new Error("SignalR handshake timed out.")), 5000);
    });
    this.socket.onerror = () => this.handleClose();
    this.sendRaw({ protocol: "json", version: 1 });
    await this.handshake;
    this.connected = true;
  }

  async invoke(target, ...args) {
    if (!this.connected || !this.socket || this.socket.readyState !== WebSocket.OPEN) {
      throw new Error("SignalR connection is not available.");
    }

    const invocationId = String(this.nextInvocationId++);
    const completion = new Promise((resolve, reject) => {
      this.invocations.set(invocationId, { resolve, reject });
    });
    this.sendRaw({ type: 1, invocationId, target, arguments: args });
    return await completion;
  }

  sendRaw(message) {
    this.socket.send(`${JSON.stringify(message)}${recordSeparator}`);
  }

  async receive(data) {
    const text = typeof data === "string" ? data : await data.text();
    this.buffer += text;
    const messages = this.buffer.split(recordSeparator);
    this.buffer = messages.pop() ?? "";

    for (const messageText of messages) {
      if (!messageText) {
        continue;
      }

      const message = JSON.parse(messageText);
      if (!message.type) {
        if (message.error) {
          this.handshakeReject?.(new Error(message.error));
        } else {
          this.handshakeResolve?.();
        }

        continue;
      }

      this.handleMessage(message);
    }
  }

  handleMessage(message) {
    if (message.type === 1) {
      const handler = this.handlers.get(message.target);
      if (handler) {
        handler(...(message.arguments ?? []));
      }
    } else if (message.type === 3) {
      const invocation = this.invocations.get(message.invocationId);
      if (!invocation) {
        return;
      }

      this.invocations.delete(message.invocationId);
      if (message.error) {
        invocation.reject(new Error(message.error));
      } else {
        invocation.resolve(message.result);
      }
    } else if (message.type === 7) {
      this.handleClose();
    }
  }

  handleClose() {
    if (this.isClosed) {
      return;
    }

    this.isClosed = true;
    this.connected = false;
    this.handshakeReject?.(new Error("SignalR connection closed."));
    for (const invocation of this.invocations.values()) {
      invocation.reject(new Error("SignalR connection closed."));
    }

    this.invocations.clear();
    this.socket = null;
    if (!this.closedByClient && this.onclose) {
      this.onclose();
    }
  }
}

elements.cancelButton.disabled = true;
elements.refreshConfigButton.disabled = true;
renderConfigLoading();
boot().catch(error => appendMessage("error", error.message));
