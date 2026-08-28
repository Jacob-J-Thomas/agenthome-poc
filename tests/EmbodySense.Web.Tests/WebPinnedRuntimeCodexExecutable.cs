using System.Text.Json;
using EmbodySense.Tests.Support;

namespace EmbodySense.Web.Tests;

internal static class WebPinnedRuntimeCodexExecutable
{
    internal static async Task<string> CreateAsync(TestWorkspace workspace)
    {
        var directory = workspace.File("pinned-runtime-codex");
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "codex.js");
        var commandPath = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.cmd" : "codex");
        var instancePath = workspace.File("pinned-runtime-instances.txt");
        var turnsPath = workspace.File("pinned-runtime-turns.txt");
        await File.WriteAllTextAsync(scriptPath, $$"""
            if (process.argv.slice(2).includes("--version")) {
              process.stdout.write("codex-cli pinned-runtime-test\n");
              process.exit(0);
            }

            const crypto = require("node:crypto");
            const fs = require("node:fs");
            const path = require("node:path");
            const readline = require("node:readline");
            const instanceId = crypto.randomUUID().replaceAll("-", "");
            const threadId = `thread-${instanceId}`;
            const instancePath = {{JsonSerializer.Serialize(instancePath)}};
            const turnsPath = {{JsonSerializer.Serialize(turnsPath)}};
            const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
            if (!process.cwd().includes("embodysense-codex-probe")) {
              fs.appendFileSync(instancePath, `${instanceId}\n`);
            }

            function write(value) {
              process.stdout.write(`${JSON.stringify(value)}\n`);
            }

            function userText(message) {
              const inputText = (message.params?.input ?? [])
                .map((item) => String(item?.text ?? ""))
                .join("\n");
              const marker = "Current user message:";
              const markerIndex = inputText.indexOf(marker);
              return markerIndex < 0 ? inputText : inputText.slice(markerIndex + marker.length).trim();
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
                case "thread/start":
                  write({ id: message.id, result: { model: "gpt-test", modelProvider: "openai", thread: { id: threadId, modelProvider: "openai" } } });
                  break;
                case "turn/start": {
                  const prompt = userText(message);
                  const turnId = `turn-${instanceId}`;
                  fs.appendFileSync(turnsPath, `${instanceId}:${prompt}\n`);
                  write({ id: message.id, result: { turn: { id: turnId } } });
                  write({ method: "item/agentMessage/delta", params: { threadId, turnId, delta: `pinned response: ${prompt}` } });
                  write({ method: "turn/completed", params: { threadId, turn: { id: turnId, status: "completed", items: [{ type: "agentMessage", phase: "final_answer", text: `pinned response: ${prompt}` }] } } });
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
