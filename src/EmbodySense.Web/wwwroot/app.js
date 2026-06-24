let sessionToken = "";
let status = null;
let activeAgentMessage = null;

const elements = {
  approvals: document.getElementById("approvals"),
  approvalCount: document.getElementById("approvalCount"),
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

async function boot() {
  const session = await fetchJson("/api/session");
  sessionToken = session.token;
  await refreshStatus();
  await refreshApprovals();
  window.setInterval(refreshApprovals, 1200);
}

async function fetchJson(url, options = {}) {
  const response = await fetch(url, options);
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return await response.json();
}

function tokenHeaders() {
  return {
    "Content-Type": "application/json",
    "X-EmbodySense-Session": sessionToken
  };
}

async function refreshStatus() {
  status = await fetchJson("/api/status");
  elements.workspaceRoot.textContent = status.workspaceRoot;
  elements.workspaceStatus.textContent = status.initialized ? "Initialized" : "Needs initialization";
  elements.clientStatus.textContent = status.primaryClient ? "Web primary" : "Web client";
  elements.clientRole.textContent = status.client;
  elements.cliRole.textContent = status.cliRole;
  elements.initButton.disabled = status.initialized;
  elements.sendButton.disabled = !status.initialized;
}

async function refreshApprovals() {
  if (!sessionToken) {
    return;
  }

  const approvals = await fetchJson("/api/approvals/pending");
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
  await fetch(`/api/approvals/${encodeURIComponent(requestId)}`, {
    method: "POST",
    headers: tokenHeaders(),
    body: JSON.stringify({ approved })
  });
  await refreshApprovals();
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
  await fetchJson("/api/workspace/init", { method: "POST", headers: tokenHeaders(), body: "{}" });
  await refreshStatus();
});

elements.messageForm.addEventListener("submit", async event => {
  event.preventDefault();
  const message = elements.messageInput.value.trim();
  if (!message || !status?.initialized) {
    return;
  }

  activeAgentMessage = null;
  appendMessage("user", message);
  elements.messageInput.value = "";
  elements.sendButton.disabled = true;

  try {
    const response = await fetch("/api/messages", {
      method: "POST",
      headers: tokenHeaders(),
      body: JSON.stringify({ message })
    });

    if (!response.ok || !response.body) {
      throw new Error(await response.text());
    }

    await readEventStream(response.body);
  } catch (error) {
    appendMessage("error", error.message);
  } finally {
    elements.sendButton.disabled = !status?.initialized;
    await refreshApprovals();
  }
});

async function readEventStream(stream) {
  const reader = stream.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  for (;;) {
    const result = await reader.read();
    if (result.done) {
      break;
    }

    buffer += decoder.decode(result.value, { stream: true });
    const lines = buffer.split(/\r?\n/);
    buffer = lines.pop() ?? "";
    for (const line of lines) {
      handleStreamLine(line);
    }
  }

  if (buffer.trim()) {
    handleStreamLine(buffer);
  }
}

function handleStreamLine(line) {
  if (!line.trim()) {
    return;
  }

  const event = JSON.parse(line);
  if (event.type === "assistant_delta") {
    appendAgentDelta(event.text ?? "");
  } else if (event.type === "assistant_final") {
    finalizeAgentMessage(event.text ?? "");
  } else if (event.type === "error") {
    appendMessage("error", event.error ?? "Request failed.");
  }
}

boot().catch(error => appendMessage("error", error.message));
