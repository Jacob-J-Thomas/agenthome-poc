using System.Text.Json;
using EmbodySense.Tests.Support;

namespace EmbodySense.Web.Tests;

internal static class WebShutdownDeadlineCodexExecutable
{
    internal static async Task<string> CreateRuntimeCompositionStallAsync(TestWorkspace workspace)
    {
        var directory = workspace.File("shutdown-deadline-codex");
        Directory.CreateDirectory(directory);
        var scriptPath = Path.Combine(directory, "codex.js");
        var commandPath = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.cmd" : "codex");
        var readyPath = RuntimeCompositionReadyPath(workspace);
        var releasePath = RuntimeCompositionReleasePath(workspace);
        await File.WriteAllTextAsync(scriptPath, $$"""
            if (process.argv.slice(2).includes("--version")) {
              process.stdout.write("codex-cli shutdown-deadline-test\n");
              process.exit(0);
            }

            const fs = require("node:fs");
            const readline = require("node:readline");
            const readyPath = {{JsonSerializer.Serialize(readyPath)}};
            const releasePath = {{JsonSerializer.Serialize(releasePath)}};
            const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });

            function write(value) {
              process.stdout.write(`${JSON.stringify(value)}\n`);
            }

            async function waitForRelease() {
              while (!fs.existsSync(releasePath)) {
                await new Promise(resolve => setTimeout(resolve, 25));
              }
            }

            input.on("line", async line => {
              const message = JSON.parse(line);
              switch (message.method) {
                case "initialize":
                  fs.writeFileSync(readyPath, "runtime composition entered");
                  await waitForRelease();
                  write({ id: message.id, result: {} });
                  break;
                case "model/list":
                  write({ id: message.id, result: { data: [{ id: "gpt-test", model: "gpt-test" }], nextCursor: null } });
                  break;
                case "thread/start": {
                  const model = String(message.params?.model ?? "");
                  const modelProvider = String(message.params?.modelProvider ?? "");
                  write({ id: message.id, result: { model, modelProvider, thread: { id: "thread-shutdown-deadline", modelProvider } } });
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

    internal static async Task WaitForRuntimeCompositionAsync(TestWorkspace workspace)
    {
        var readyPath = RuntimeCompositionReadyPath(workspace);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (File.Exists(readyPath))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The runtime-composition stall did not begin.");
    }

    internal static Task ReleaseRuntimeCompositionAsync(TestWorkspace workspace)
        => File.WriteAllTextAsync(RuntimeCompositionReleasePath(workspace), "release");

    private static string RuntimeCompositionReadyPath(TestWorkspace workspace)
        => workspace.File("runtime-composition.ready");

    private static string RuntimeCompositionReleasePath(TestWorkspace workspace)
        => workspace.File("runtime-composition.release");
}
