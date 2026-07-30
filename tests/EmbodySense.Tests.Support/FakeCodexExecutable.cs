namespace EmbodySense.Tests.Support;

public static class FakeCodexExecutable
{
    public static async Task<string> CreateCompatibleAsync(TestWorkspace workspace, params string[] advertisedModels)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var directory = workspace.File("fake-codex");
        Directory.CreateDirectory(directory);
        var commandPath = Path.Combine(directory, "codex.cmd");
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
                  write({ id: message.id, result: { data: advertisedModels.map((model) => ({ id: model, model })) } });
                  break;
                default:
                  break;
                }
            });
            """);
        await File.WriteAllTextAsync(commandPath, """
            @echo off
            node "%~dp0codex.js" %*
            """);
        return commandPath;
    }
}
