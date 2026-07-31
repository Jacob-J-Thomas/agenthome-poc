using EmbodySense.Core.Startup.Governance;
using System.Text;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Cli.Command.Tests;

public sealed class ConsoleAgentRuntimeHostTests
{
    [Fact]
    public async Task RunAsync_runs_reusable_loop_through_cli_console_adapter()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace);
        var client = new ScriptedRuntimeClient("hello", "/exit");

        var exitCode = await new AgentRuntimeConsoleHost(runtime, client).RunAsync(banner: "banner");

        Assert.Equal(0, exitCode);
        Assert.Contains("banner", client.Output, StringComparison.Ordinal);
        Assert.Contains("User: ", client.Output, StringComparison.Ordinal);
        Assert.Contains("Assistant:", client.Output, StringComparison.Ordinal);
        Assert.Contains("runtime guide missing: hello", client.Output, StringComparison.Ordinal);
    }

    private static async Task<AgentRuntime> CreateRuntimeAsync(TestWorkspace workspace, AgentRuntimeSurface? runtimeSurface = null)
    {
        return await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(
            null,
            workspace.RootPath,
            await CreateFakeCodexExecutableAsync(workspace),
            "read-only",
            runtimeSurface ?? AgentRuntimeSurface.Cli);
    }

    private static async Task<string> CreateFakeCodexExecutableAsync(TestWorkspace workspace)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The fake Codex app-server executable is currently implemented as a Windows command script.");
        }

        var scriptPath = workspace.File("fake-codex.js");
        var commandPath = workspace.File("fake-codex.cmd");
        await File.WriteAllTextAsync(scriptPath, """
            if (process.argv.slice(2).includes("--version")) {
              process.stdout.write("codex-cli 999.0.0-test\n");
              process.exit(0);
            }

            const readline = require("node:readline");
            const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
            const threadId = "thread-test";
            let developerInstructions = "";

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
                  write({ id: message.id, result: { data: [{ id: "test-model", model: "test-model" }, { id: "gpt-test", model: "gpt-test" }], nextCursor: null } });
                  break;
                case "thread/start":
                  developerInstructions = String(message.params?.developerInstructions ?? "");
                  write({ id: message.id, result: { thread: { id: threadId } } });
                  break;
                case "turn/start": {
                  const turnId = "turn-test";
                  const inputText = (message.params?.input ?? []).map((item) => String(item?.text ?? "")).join("\n");
                  const prefix = developerInstructions.includes("runtime guide") || inputText.includes("runtime guide") ? "runtime guide observed" : "runtime guide missing";
                  const currentUserMarker = "Current user message:";
                  const currentUserIndex = inputText.indexOf(currentUserMarker);
                  const userText = currentUserIndex < 0 ? inputText : inputText.slice(currentUserIndex + currentUserMarker.length).trim();
                  const text = `${prefix}: ${userText}`;

                  write({ id: message.id, result: { turn: { id: turnId } } });
                  write({ method: "item/agentMessage/delta", params: { threadId, turnId, delta: text } });
                  write({ method: "turn/completed", params: { threadId, turnId, turn: { id: turnId, status: "completed", items: [{ type: "agentMessage", phase: "final_answer", text }] } } });
                  break;
                }
                default:
                  break;
              }
            });
            """);
        await File.WriteAllTextAsync(commandPath, """
            @echo off
            if "%~1"=="--version" (
                echo codex-cli 999.0.0-test
                exit /b 0
            )
            node "%~dp0fake-codex.js" %*
            """);

        return commandPath;
    }

    private sealed class RejectingApprovalPrompt : IAgentToolApprovalPrompt
    {
        public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult((false, "test", "No approval needed during runtime construction."));
        }
    }

    private sealed class ScriptedRuntimeClient(params string[] inputs) : IAgentRuntimeConsole
    {
        private readonly Queue<string?> _inputs = new(inputs);
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public string? ReadLine()
        {
            return _inputs.Count == 0 ? null : _inputs.Dequeue();
        }

        public void Clear()
        {
        }

        public void Write(string value)
        {
            _output.Append(value);
        }

        public void WriteLine(string value = "")
        {
            _output.AppendLine(value);
        }
    }
}
