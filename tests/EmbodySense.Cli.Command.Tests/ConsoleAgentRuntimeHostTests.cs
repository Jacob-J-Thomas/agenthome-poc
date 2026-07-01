using System.Text;
using EmbodySense.Core.Startup.Governance;
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

        var scriptPath = workspace.File("fake-codex.ps1");
        var commandPath = workspace.File("fake-codex.cmd");
        await File.WriteAllTextAsync(scriptPath, """
            $threadId = "thread-test"
            $developerInstructions = ""

            function Write-ProtocolJson($value) {
                $value | ConvertTo-Json -Compress -Depth 20
                [Console]::Out.Flush()
            }

            while (($line = [Console]::In.ReadLine()) -ne $null) {
                $message = $line | ConvertFrom-Json

                switch ($message.method) {
                    "initialize" {
                        Write-ProtocolJson @{ id = $message.id; result = @{} }
                    }

                    "initialized" {
                    }

                    "thread/start" {
                        $developerInstructions = [string]$message.params.developerInstructions
                        Write-ProtocolJson @{ id = $message.id; result = @{ thread = @{ id = $threadId } } }
                    }

                    "turn/start" {
                        $turnId = "turn-test"
                        $userText = [string]$message.params.input[0].text
                        $prefix = if ($developerInstructions.Contains("runtime guide") -or $userText.Contains("runtime guide")) { "runtime guide observed" } else { "runtime guide missing" }
                        $currentUserMarker = "Current user message:"
                        $currentUserIndex = $userText.IndexOf($currentUserMarker)
                        if ($currentUserIndex -ge 0) {
                            $userText = $userText.Substring($currentUserIndex + $currentUserMarker.Length).Trim()
                        }
                        $text = "${prefix}: $userText"

                        Write-ProtocolJson @{ id = $message.id; result = @{ turn = @{ id = $turnId } } }
                        Write-ProtocolJson @{ method = "item/agentMessage/delta"; params = @{ threadId = $threadId; turnId = $turnId; delta = $text } }
                        Write-ProtocolJson @{ method = "turn/completed"; params = @{ threadId = $threadId; turnId = $turnId; turn = @{ id = $turnId; status = "completed"; items = @(@{ type = "agentMessage"; phase = "final_answer"; text = $text }) } } }
                    }
                }
            }
            """);
        await File.WriteAllTextAsync(commandPath, """
            @echo off
            powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fake-codex.ps1" %*
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
