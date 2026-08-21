namespace EmbodySense.Tests.Support;

public static class FakeCodexExecutable
{
    public static async Task<string> CreateCompatibleAsync(TestWorkspace workspace, params string[] advertisedModels)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var directory = workspace.File("fake-codex");
        Directory.CreateDirectory(directory);
        var commandPath = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.cmd" : "codex");
        var scriptPath = Path.Combine(directory, "codex.js");
        var modelsJson = System.Text.Json.JsonSerializer.Serialize(advertisedModels);
        await File.WriteAllTextAsync(scriptPath, $$"""
            if (process.argv.slice(2).includes("--version")) {
              process.stdout.write("codex-cli compatible-test\n");
              process.exit(0);
            }

            const readline = require("node:readline");
            const advertisedModels = {{modelsJson}};
            const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
            let threadNumber = 0;
            let turnNumber = 0;
            let pendingToolTurn = null;

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
                  write({ id: message.id, result: {} });
                  break;
                case "model/list":
                  write({ id: message.id, result: { data: advertisedModels.map((model) => ({ id: model, model })), nextCursor: null } });
                  break;
                case "thread/start": {
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
