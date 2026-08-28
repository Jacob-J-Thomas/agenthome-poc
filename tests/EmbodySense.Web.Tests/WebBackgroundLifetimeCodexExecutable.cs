using System.Text.Json;
using EmbodySense.Tests.Support;

namespace EmbodySense.Web.Tests;

internal static class WebBackgroundLifetimeCodexExecutable
{
    internal static async Task<string> CreateAsync(TestWorkspace workspace)
    {
        var directory = workspace.File("background-lifetime-codex");
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "codex.js");
        var commandPath = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.cmd" : "codex");
        await File.WriteAllTextAsync(scriptPath, """
            if (process.argv.slice(2).includes("--version")) {
              process.stdout.write("codex-cli background-lifetime-test\n");
              process.exit(0);
            }

            const fs = require("node:fs");
            const path = require("node:path");
            const readline = require("node:readline");
            const startedPath = path.join(__dirname, "app-server-started.txt");
            const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
            let threadNumber = 0;
            let turnNumber = 0;

            function write(value) {
              process.stdout.write(`${JSON.stringify(value)}\n`);
            }

            input.on("line", (line) => {
              const message = JSON.parse(line);
              switch (message.method) {
                case "initialize":
                  write({ id: message.id, result: {} });
                  break;
                case "model/list":
                  write({ id: message.id, result: { data: [{ id: "gpt-test", model: "gpt-test" }], nextCursor: null } });
                  break;
                case "thread/start": {
                  const threadId = `thread-${++threadNumber}`;
                  const model = String(message.params?.model ?? "");
                  const modelProvider = String(message.params?.modelProvider ?? "");
                  write({ id: message.id, result: { model, modelProvider, thread: { id: threadId, modelProvider } } });
                  break;
                }
                case "turn/start": {
                  const threadId = String(message.params?.threadId ?? "thread-1");
                  const turnId = `turn-${++turnNumber}`;
                  fs.appendFileSync(startedPath, "started\n");
                  write({ id: message.id, result: { turn: { id: turnId } } });
                  write({ method: "item/agentMessage/delta", params: { threadId, turnId, delta: "background host response" } });
                  write({ method: "turn/completed", params: { threadId, turn: { id: turnId, status: "completed", items: [{ type: "agentMessage", phase: "final_answer", text: "background host response" }] } } });
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

    internal static string StartedPath(TestWorkspace workspace) => workspace.File("background-lifetime-codex", "app-server-started.txt");
}
