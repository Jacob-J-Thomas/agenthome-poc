let sessionToken = "";
let status = null;
let activeAgentMessage = null;
let hub = null;

const elements = {
  approvals: document.getElementById("approvals"),
  approvalCount: document.getElementById("approvalCount"),
  cancelButton: document.getElementById("cancelButton"),
  cliRole: document.getElementById("cliRole"),
  clientRole: document.getElementById("clientRole"),
  clientStatus: document.getElementById("clientStatus"),
  initButton: document.getElementById("initButton"),
  messageForm: document.getElementById("messageForm"),
  messageInput: document.getElementById("messageInput"),
  sendButton: document.getElementById("sendButton"),
  transcript: document.getElementById("transcript"),
  workspaceRoot: document.getElementById("workspaceRoot"),
  workspaceStatus: document.getElementById("workspaceStatus")
};

const recordSeparator = "\u001e";

async function boot() {
  const session = await fetchJson("/api/session");
  sessionToken = session.token;
  await refreshStatus();
  await connectHub();
}

async function fetchJson(url, options = {}) {
  const response = await fetch(url, options);
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return await response.json();
}

async function refreshStatus() {
  status = await fetchJson("/api/status");
  applyStatus(status);
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
  const message = document.createElement("div");
  message.className = `message ${kind}`;
  message.textContent = text;
  elements.transcript.append(message);
  elements.transcript.scrollTop = elements.transcript.scrollHeight;
  return message;
}

function appendAgentDelta(text) {
  if (!activeAgentMessage) {
    activeAgentMessage = appendMessage("agent", "");
  }

  activeAgentMessage.textContent += text;
  elements.transcript.scrollTop = elements.transcript.scrollHeight;
}

function finalizeAgentMessage(text) {
  if (activeAgentMessage) {
    activeAgentMessage.textContent = text;
  } else {
    activeAgentMessage = appendMessage("agent", text);
  }

  elements.transcript.scrollTop = elements.transcript.scrollHeight;
  activeAgentMessage = null;
}

elements.initButton.addEventListener("click", async () => {
  elements.initButton.disabled = true;
  await hub.invoke("InitializeWorkspace");
});

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
boot().catch(error => appendMessage("error", error.message));
