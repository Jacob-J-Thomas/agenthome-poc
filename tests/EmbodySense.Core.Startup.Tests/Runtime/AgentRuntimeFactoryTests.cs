using System.Text;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task CreateAsync_starts_with_fresh_transcript_without_exposing_runtime_internals()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await File.WriteAllTextAsync(paths.CurrentConversationPath, "old transcript" + Environment.NewLine);

        await using var runtime = await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(null, workspace.RootPath, await CreateFakeCodexExecutableAsync(workspace), "read-only");

        Assert.Equal(string.Empty, await File.ReadAllTextAsync(paths.CurrentConversationPath));
        Assert.NotEmpty(Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson"));
    }

    [Fact]
    public async Task SendUserMessageAsync_uses_startup_context_and_streams_response_through_public_runtime()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(Path.Combine(workspace.RootPath, ".agent", "AGENT.md"), "runtime guide");
        await using var runtime = await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(null, workspace.RootPath, await CreateFakeCodexExecutableAsync(workspace), "read-only");
        var chunks = new List<string>();

        var response = await runtime.SendUserMessageAsync("hello", (chunk, _) =>
        {
            chunks.Add(chunk);
            return Task.CompletedTask;
        });

        Assert.Equal("runtime guide observed: hello", response);
        Assert.Equal(["runtime guide observed: hello"], chunks);
    }

    [Fact]
    public async Task RunConsoleLoopAsync_runs_reusable_loop_through_public_runtime()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await using var runtime = await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(null, workspace.RootPath, await CreateFakeCodexExecutableAsync(workspace), "read-only");
        var client = new ScriptedHarnessClient("hello", "/exit");

        var exitCode = await runtime.RunConsoleLoopAsync(client, banner: "banner");

        Assert.Equal(0, exitCode);
        Assert.Contains("banner", client.Output, StringComparison.Ordinal);
        Assert.Contains("runtime guide missing: hello", client.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleHarnessCommandAsync_handles_help_exit_and_unhandled_commands()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await using var runtime = await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(null, workspace.RootPath, await CreateFakeCodexExecutableAsync(workspace), "read-only");

        Assert.True(AgentRuntime.TryHandleStaticHarnessCommand("/help", out var staticResult));
        Assert.Contains("Harness commands:", staticResult.Output, StringComparison.Ordinal);

        var help = await runtime.TryHandleHarnessCommandAsync("/commands");
        var exit = await runtime.TryHandleHarnessCommandAsync("/quit");
        var unhandled = await runtime.TryHandleHarnessCommandAsync("/not-a-command");

        Assert.True(help.Handled);
        Assert.Contains("/new, /new-session", help.Output, StringComparison.Ordinal);
        Assert.True(exit.Handled);
        Assert.True(exit.ExitRequested);
        Assert.False(unhandled.Handled);
    }

    [Fact]
    public async Task TryHandleHarnessCommandAsync_loads_pending_history_selection()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);
        await store.AppendMessageAsync(LlmMessage.User("saved prompt"));
        await store.AppendMessageAsync(LlmMessage.Assistant("saved answer"));
        await using var runtime = await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(null, workspace.RootPath, await CreateFakeCodexExecutableAsync(workspace), "read-only");

        var history = await runtime.TryHandleHarnessCommandAsync("/history");
        var loaded = await runtime.TryHandleHarnessCommandAsync("1");

        Assert.True(history.Handled);
        Assert.Contains("Stored conversations:", history.Output, StringComparison.Ordinal);
        Assert.Contains("saved prompt", history.Output, StringComparison.Ordinal);
        Assert.Contains("Send conversation number to load", history.Output, StringComparison.Ordinal);
        Assert.True(loaded.Handled);
        Assert.Contains("Loaded conversation `archive/", loaded.Output, StringComparison.Ordinal);
        var currentMessages = await store.LoadCurrentConversationAsync();
        Assert.Collection(
            currentMessages,
            message => Assert.Equal("saved prompt", message.Content),
            message => Assert.Equal("saved answer", message.Content));
    }

    [Fact]
    public async Task TryHandleHarnessCommandAsync_handles_pending_history_cancel_and_invalid_selection()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var store = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));
        await store.AppendMessageAsync(LlmMessage.User("saved prompt"));
        await using var runtime = await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(null, workspace.RootPath, await CreateFakeCodexExecutableAsync(workspace), "read-only");

        _ = await runtime.TryHandleHarnessCommandAsync("/history");
        var cancelled = await runtime.TryHandleHarnessCommandAsync("/cancel");
        _ = await runtime.TryHandleHarnessCommandAsync("/history");
        var invalid = await runtime.TryHandleHarnessCommandAsync("99");

        Assert.True(cancelled.Handled);
        Assert.Equal("Conversation load cancelled.", cancelled.Output);
        Assert.True(invalid.Handled);
        Assert.Equal("Invalid conversation selection.", invalid.Output);
    }

    [Fact]
    public async Task TryHandleHarnessCommandAsync_requires_history_before_model_turn_and_new_resets_state()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        await using var runtime = await new AgentRuntimeFactory(new RejectingApprovalPrompt()).CreateAsync(null, workspace.RootPath, await CreateFakeCodexExecutableAsync(workspace), "read-only");

        _ = await runtime.SendUserMessageAsync("hello");
        var historyAfterTurn = await runtime.TryHandleHarnessCommandAsync("/history");
        var fresh = await runtime.TryHandleHarnessCommandAsync("/new");
        var historyAfterNew = await runtime.TryHandleHarnessCommandAsync("/history");

        Assert.Contains("before sending the first prompt", historyAfterTurn.Output, StringComparison.Ordinal);
        Assert.Equal("Started a new conversation.", fresh.Output);
        Assert.Contains("Stored conversations:", historyAfterNew.Output, StringComparison.Ordinal);
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
                        $prefix = if ($developerInstructions.Contains("runtime guide")) { "runtime guide observed" } else { "runtime guide missing" }
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

    private sealed class ScriptedHarnessClient(params string[] inputs) : IAgentRuntimeConsole
    {
        private readonly Queue<string?> _inputs = new(inputs);
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public string? ReadLine()
        {
            return _inputs.Count == 0 ? null : _inputs.Dequeue();
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
