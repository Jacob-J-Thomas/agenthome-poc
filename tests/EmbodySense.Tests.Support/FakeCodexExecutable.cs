namespace EmbodySense.Tests.Support;

public static class FakeCodexExecutable
{
    public static string ProtocolTracePath(TestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.File("fake-codex", "protocol-events.ndjson");
    }

    public static async Task<string> CreateBrowserApprovalAsync(TestWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var directory = workspace.File("fake-codex");
        Directory.CreateDirectory(directory);
        var configuration = new
        {
            version = "codex-cli compatible-test",
            advertisedModels = new[] { "gpt-test" },
            responsePrefix = "browser response: ",
            turnFailureMessage = "controlled browser provider failure",
            turnFailurePromptMarker = "browser-provider-failure",
            waitForTurnRelease = false,
            requestGovernedTool = true,
            turnReadyMarkerPath = (string?)null,
            turnReleaseMarkerPath = (string?)null,
            toolResponsePath = workspace.File("fake-codex", "tool-response.json"),
            governedToolPromptMarker = "browser-approval",
            governedToolPath = "approval-note.txt",
            protocolTracePath = ProtocolTracePath(workspace)
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, "browser-config.json"),
            System.Text.Json.JsonSerializer.Serialize(configuration, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        return await CancellationHostExecutable.CreateAsync(workspace, "fake-codex", "codex-conversation-probe", "browser-config.json", "codex");
    }

    public static async Task<string> CreateCompatibleAsync(TestWorkspace workspace, params string[] advertisedModels)
    {
        return await CreateCompatibleCoreAsync(workspace, null, advertisedModels);
    }

    public static async Task<string> CreateCompatibleWithMilestoneTraceAsync(TestWorkspace workspace, string milestoneTracePath, params string[] advertisedModels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(milestoneTracePath);
        return await CreateCompatibleCoreAsync(workspace, milestoneTracePath, advertisedModels);
    }

    private static async Task<string> CreateCompatibleCoreAsync(TestWorkspace workspace, string? milestoneTracePath, string[] advertisedModels)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var directory = workspace.File("fake-codex");
        Directory.CreateDirectory(directory);
        if (!string.IsNullOrWhiteSpace(milestoneTracePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(milestoneTracePath)!);
        }

        var commandPath = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.cmd" : "codex");
        var scriptPath = Path.Combine(directory, "codex.js");
        var modelsJson = System.Text.Json.JsonSerializer.Serialize(advertisedModels);
        var milestoneTracePathJson = System.Text.Json.JsonSerializer.Serialize(milestoneTracePath);
        await File.WriteAllTextAsync(scriptPath, $$"""
            const milestoneTracePath = {{milestoneTracePathJson}};
            const fileSystem = milestoneTracePath ? require("node:fs") : null;

            function traceMilestone(milestone, detail = {}) {
              if (!milestoneTracePath) {
                return;
              }

              fileSystem.appendFileSync(milestoneTracePath, `${JSON.stringify({ timestampUtc: new Date().toISOString(), processId: process.pid, milestone, detail })}\n`);
            }

            traceMilestone("process-start", { arguments: process.argv.slice(2) });
            if (process.argv.slice(2).includes("--version")) {
              traceMilestone("version-request");
              process.stdout.write("codex-cli compatible-test\n");
              traceMilestone("version-response", { version: "codex-cli compatible-test" });
              process.exit(0);
            }

            const readline = require("node:readline");
            const advertisedModels = {{modelsJson}};
            const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
            let threadNumber = 0;
            let turnNumber = 0;
            let pendingToolTurn = null;
            let visibleCycleExhaustionAttempts = 0;

            function write(value) {
              process.stdout.write(`${JSON.stringify(value)}\n`);
            }

            function completeTurn(threadId, turnId, text) {
              write({ method: "item/agentMessage/delta", params: { threadId, turnId, delta: text } });
              write({
                method: "thread/tokenUsage/updated",
                params: {
                  threadId,
                  turnId,
                  tokenUsage: {
                    last: {
                      inputTokens: 1,
                      cachedInputTokens: 0,
                      outputTokens: 1,
                      reasoningOutputTokens: 0,
                      totalTokens: 2
                    },
                    total: {
                      inputTokens: 1,
                      cachedInputTokens: 0,
                      outputTokens: 1,
                      reasoningOutputTokens: 0,
                      totalTokens: 2
                    }
                  }
                }
              });
              write({
                method: "turn/completed",
                params: {
                  threadId,
                  turnId,
                  turn: {
                    id: turnId,
                    status: "completed",
                    items: [{ type: "agentMessage", phase: "final_answer", text }]
                  }
                }
              });
            }

            function turnInput(message) {
              return (message.params?.input ?? [])
                .map((item) => String(item?.text ?? ""))
                .join("\n");
            }

            function userText(message) {
              const inputText = turnInput(message);
              const marker = "Current user message:";
              const markerIndex = inputText.indexOf(marker);
              return markerIndex < 0 ? inputText : inputText.slice(markerIndex + marker.length).trim();
            }

            input.on("line", (line) => {
              const message = JSON.parse(line);
              if (message.id === 99 && pendingToolTurn) {
                const completed = pendingToolTurn;
                pendingToolTurn = null;
                const toolText = (message.result?.contentItems ?? [])
                  .map((item) => String(item?.text ?? ""))
                  .join("\n");
                const approved = message.result?.success === true && toolText.includes("approved browser evidence");
                const outcome = approved
                  ? `browser governed tool approved: ${toolText}`
                  : `browser governed tool rejected: ${toolText}`;
                completeTurn(completed.threadId, completed.turnId, `${outcome}; prompt: ${completed.prompt}`);
                return;
              }

              switch (message.method) {
                case "initialize":
                  traceMilestone("initialize-request", { id: message.id });
                  write({ id: message.id, result: {} });
                  traceMilestone("initialize-response", { id: message.id });
                  break;
                case "model/list":
                  traceMilestone("model/list-request", { id: message.id });
                  write({ id: message.id, result: { data: advertisedModels.map((model) => ({ id: model, model })), nextCursor: null } });
                  traceMilestone("model/list-response", { id: message.id, advertisedModels });
                  break;
                case "thread/start": {
                  traceMilestone("thread/start-request", { id: message.id });
                  const threadId = `thread-browser-${++threadNumber}`;
                  const model = String(message.params?.model ?? "");
                  const modelProvider = String(message.params?.modelProvider ?? "");
                  write({
                    id: message.id,
                    result: {
                      model,
                      modelProvider,
                      thread: { id: threadId, modelProvider }
                    }
                  });
                  traceMilestone("thread/start-response", { id: message.id, threadId, model, modelProvider });
                  break;
                }
                case "turn/start": {
                  const threadId = String(message.params?.threadId ?? `thread-browser-${threadNumber}`);
                  const turnId = `turn-browser-${++turnNumber}`;
                  const inputText = turnInput(message);
                  const prompt = userText(message);
                  write({ id: message.id, result: { turn: { id: turnId } } });
                  if (inputText.includes("browser-explicit-fail")) {
                    completeTurn(threadId, turnId, "select-fail");
                    break;
                  }

                  if (inputText.includes("visible-cycle-marker")) {
                    if (inputText.includes("visible-cycle-success")) {
                      completeTurn(threadId, turnId, "terminal");
                      break;
                    }

                    if (inputText.includes("visible-cycle-exhaustion")) {
                      visibleCycleExhaustionAttempts += 1;
                      completeTurn(threadId, turnId, visibleCycleExhaustionAttempts < 3 ? "retry" : "terminal");
                      break;
                    }
                  }

                  if (inputText.includes("browser-provider-failure")) {
                    write({
                      method: "turn/completed",
                      params: {
                        threadId,
                        turnId,
                        turn: {
                          id: turnId,
                          status: "failed",
                          error: { message: "controlled browser provider failure" },
                          items: []
                        }
                      }
                    });
                    break;
                  }

                  if (inputText.includes("browser-approval")) {
                    pendingToolTurn = { threadId, turnId, prompt };
                    write({
                      id: 99,
                      method: "item/tool/call",
                      params: {
                        threadId,
                        turnId,
                        callId: `call-browser-${turnNumber}`,
                        namespace: "embodysense",
                        tool: "command",
                        arguments: { command: "read", path: "approval-note.txt" }
                      }
                    });
                    break;
                  }

                  completeTurn(threadId, turnId, `browser response: ${prompt}`);
                  break;
                }
                default:
                  break;
                }
            });
            """);
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(commandPath, """
                @echo off
                node "%~dp0codex.js" %*
                """);
        }
        else
        {
            await File.WriteAllTextAsync(commandPath, """
                #!/bin/sh
                exec node "$(dirname "$0")/codex.js" "$@"
                """);
            File.SetUnixFileMode(commandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return commandPath;
    }
}
