using EmbodySense.Tests.Support;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web.Tests;

public sealed class WebAgentRuntimeHostTests
{
    [Fact]
    public async Task InitializeWorkspaceAsync_initializes_workspace_with_web_audit_actor()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);

        var before = host.GetStatus();
        var after = await host.InitializeWorkspaceAsync();

        Assert.False(before.Initialized);
        Assert.True(after.Initialized);
        Assert.True(File.Exists(workspace.File(".agent", "permissions.json")));
        Assert.Contains("embodysense.web", await File.ReadAllTextAsync(workspace.File(".agent", "audit", "events.ndjson")));
    }

    [Fact]
    public async Task SendMessageAsync_requires_initialized_workspace()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            return host.SendMessageAsync("hello", (_, _) => Task.CompletedTask);
        });

        Assert.Contains("Workspace is not initialized", exception.Message);
    }

    [Fact]
    public async Task GetConfigurationAsync_returns_read_only_workspace_configuration()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);
        await host.InitializeWorkspaceAsync();

        var configuration = await host.GetConfigurationAsync();

        Assert.True(configuration.Status.Initialized);
        Assert.Equal("web", configuration.Runtime.Surface);
        Assert.True(configuration.Permissions.Parsed);
        Assert.Contains(configuration.Paths, path => path.Name == "Agent home" && path.Exists);
        Assert.Contains(configuration.Documents, document => document.Name == "Agent guide" && document.Exists);
    }

    [Fact]
    public async Task SendMessageAsync_handles_help_command_without_initialized_workspace()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);
        var events = new List<WebStreamEvent>();

        await host.SendMessageAsync("/help", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        var streamEvent = Assert.Single(events);
        Assert.Equal("assistant_final", streamEvent.Type);
        Assert.Contains("Harness commands:", streamEvent.Text);
        Assert.Contains("/history, /conversations, /load", streamEvent.Text);
    }

    [Fact]
    public async Task SendMessageAsync_loads_history_selection_before_first_model_turn()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        await WriteCurrentTranscriptAsync(workspace, "web archived prompt", "web archived answer");
        var events = new List<WebStreamEvent>();

        await host.SendMessageAsync("/history", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });
        var historyEvent = Assert.Single(events);
        Assert.Equal("assistant_final", historyEvent.Type);
        Assert.Contains("Stored conversations:", historyEvent.Text);
        Assert.Contains("web archived prompt", historyEvent.Text);
        Assert.Contains("Send conversation number to load", historyEvent.Text);

        events.Clear();
        await host.SendMessageAsync("1", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        var loadedEvent = Assert.Single(events);
        Assert.Equal("assistant_final", loadedEvent.Type);
        Assert.Contains("Loaded conversation `archive/", loadedEvent.Text);
        Assert.Contains("web archived prompt", await File.ReadAllTextAsync(CurrentTranscriptPath(workspace)));
    }

    [Fact]
    public async Task CancelCurrentTurn_returns_false_when_no_turn_is_running()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);

        Assert.False(host.CancelCurrentTurn());
    }

    [Fact]
    public async Task SendMessageAsync_validates_message()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);

        await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            return host.SendMessageAsync(" ", (_, _) => Task.CompletedTask);
        });
    }

    [Fact]
    public async Task SendMessageAsync_validates_event_writer()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            return host.SendMessageAsync("hello", null!);
        });
    }

    private static WebAgentRuntimeHost CreateHost(string rootPath, string? codexPath = null)
    {
        var options = codexPath is null
            ? WebRunOptions.FromArguments(["--workdir", rootPath])
            : WebRunOptions.FromArguments(["--workdir", rootPath, "--codex-path", codexPath]);
        return new WebAgentRuntimeHost(options, new WebApprovalCoordinator());
    }

    private static async Task WriteCurrentTranscriptAsync(TestWorkspace workspace, string prompt, string answer)
    {
        var path = CurrentTranscriptPath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $$"""
            {"schemaVersion":1,"conversationId":"current","sequence":1,"timestampUtc":"2026-06-01T00:01:00+00:00","role":"user","content":"{{prompt}}"}
            {"schemaVersion":1,"conversationId":"current","sequence":2,"timestampUtc":"2026-06-01T00:02:00+00:00","role":"assistant","content":"{{answer}}"}
            """);
    }

    private static string CurrentTranscriptPath(TestWorkspace workspace)
    {
        return workspace.File(".agent", "memory", "conversations", "current.ndjson");
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
                        Write-ProtocolJson @{ id = $message.id; result = @{ thread = @{ id = $threadId } } }
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
}
