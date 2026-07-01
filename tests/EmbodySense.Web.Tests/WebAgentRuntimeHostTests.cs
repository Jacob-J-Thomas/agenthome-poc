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
        Assert.Contains("Runtime commands:", streamEvent.Text);
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

        Assert.Collection(
            events,
            loadedEvent =>
            {
                Assert.Equal("history_loaded", loadedEvent.Type);
                Assert.Collection(
                    loadedEvent.Messages,
                    message =>
                    {
                        Assert.Equal("user", message.Role);
                        Assert.Equal("web archived prompt", message.Content);
                    },
                    message =>
                    {
                        Assert.Equal("assistant", message.Role);
                        Assert.Equal("web archived answer", message.Content);
                    });
            },
            confirmationEvent =>
            {
                Assert.Equal("assistant_final", confirmationEvent.Type);
                Assert.Contains("Loaded conversation `archive/", confirmationEvent.Text);
            });
        Assert.Contains("web archived prompt", await File.ReadAllTextAsync(CurrentTranscriptPath(workspace)));
    }

    [Fact]
    public async Task SendMessageAsync_emits_verbose_context_when_web_verbose_mode_is_enabled()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        var events = new List<WebStreamEvent>();

        await host.SetVerboseModeAsync(true, (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });
        await host.SendMessageAsync("hello from web", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        Assert.Collection(
            events,
            statusEvent =>
            {
                Assert.Equal("system", statusEvent.Type);
                Assert.Contains("Verbose mode enabled", statusEvent.Text);
            },
            contextEvent =>
            {
                Assert.Equal("verbose_context", contextEvent.Type);
                Assert.Contains("[verbose] Visible inference context follows.", contextEvent.Text);
                Assert.Contains("This is not private model reasoning", contextEvent.Text);
                Assert.Contains("loop_id: default-conversation", contextEvent.Text);
                Assert.Contains("source=current-turn-input", contextEvent.Text);
                Assert.Contains("compaction:", contextEvent.Text);
                Assert.Contains("workspace_commands_allowed_by_loop:", contextEvent.Text);
                Assert.Contains("hello from web", contextEvent.Text);
            },
            deltaEvent =>
            {
                Assert.Equal("assistant_delta", deltaEvent.Type);
                Assert.Contains("web response: hello from web", deltaEvent.Text);
            },
            finalEvent =>
            {
                Assert.Equal("assistant_final", finalEvent.Type);
                Assert.Contains("web response: hello from web", finalEvent.Text);
            });
    }

    [Fact]
    public async Task SendMessageAsync_surfaces_loop_failure_as_error_event()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, "provider down");
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        var events = new List<WebStreamEvent>();

        await host.SendMessageAsync("hello from web", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        var streamEvent = Assert.Single(events);
        Assert.Equal("error", streamEvent.Type);
        Assert.Equal("Codex app-server turn failed: provider down", streamEvent.Error);
    }

    [Fact]
    public async Task SendMessageAsync_surfaces_disabled_loop_as_error_event()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        var definitionPath = workspace.File(".agent", "loops", "definitions", "default-conversation.json");
        var definitionJson = await File.ReadAllTextAsync(definitionPath);
        await File.WriteAllTextAsync(definitionPath, definitionJson.Replace("\"state\": \"enabled\"", "\"state\": \"disabled\"", StringComparison.Ordinal));
        var events = new List<WebStreamEvent>();

        await host.SendMessageAsync("hello from web", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        var streamEvent = Assert.Single(events);
        Assert.Equal("error", streamEvent.Type);
        Assert.Equal("Loop `default-conversation` is not enabled.", streamEvent.Error);
    }

    [Fact]
    public async Task SendMessageAsync_emits_cancelled_event_after_active_turn_is_cancelled()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, turnDelayMilliseconds: 5000);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        var events = new List<WebStreamEvent>();

        var sendTask = host.SendMessageAsync("hello from web", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });
        var cancelled = false;
        for (var attempt = 0; attempt < 200 && !cancelled; attempt++)
        {
            cancelled = host.CancelCurrentTurn();
            await Task.Delay(10);
        }

        Assert.True(cancelled);
        await sendTask;

        var streamEvent = Assert.Single(events);
        Assert.Equal("cancelled", streamEvent.Type);
        Assert.Equal("Message cancelled.", streamEvent.Text);

        events.Clear();
        await host.SendMessageAsync("after cancel", (streamEvent, _) =>
        {
            events.Add(streamEvent);
            return Task.CompletedTask;
        });

        Assert.Collection(
            events,
            deltaEvent =>
            {
                Assert.Equal("assistant_delta", deltaEvent.Type);
                Assert.Equal("web response: after cancel", deltaEvent.Text);
            },
            finalEvent =>
            {
                Assert.Equal("assistant_final", finalEvent.Type);
                Assert.Equal("web response: after cancel", finalEvent.Text);
            });
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

    private static async Task<string> CreateFakeCodexExecutableAsync(TestWorkspace workspace, string? turnFailureMessage = null, int turnDelayMilliseconds = 0)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The fake Codex app-server executable is currently implemented as a Windows command script.");
        }

        var scriptPath = workspace.File("fake-codex.ps1");
        var commandPath = workspace.File("fake-codex.cmd");
        await File.WriteAllTextAsync(scriptPath, $$"""
            $threadId = "thread-test"
            $turnFailureMessage = {{FormatPowerShellStringLiteral(turnFailureMessage)}}
            $turnDelayMilliseconds = {{turnDelayMilliseconds}}
            $turnNumber = 0

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

                    "turn/start" {
                        $turnNumber++
                        $turnId = "turn-test"
                        $userText = [string]$message.params.input[0].text
                        $currentUserMarker = "Current user message:"
                        $currentUserIndex = $userText.IndexOf($currentUserMarker)
                        if ($currentUserIndex -ge 0) {
                            $userText = $userText.Substring($currentUserIndex + $currentUserMarker.Length).Trim()
                        }

                        $text = "web response: $userText"
                        Write-ProtocolJson @{ id = $message.id; result = @{ turn = @{ id = $turnId } } }
                        if ($turnDelayMilliseconds -gt 0 -and $userText.Contains("hello from web")) {
                            Start-Sleep -Milliseconds $turnDelayMilliseconds
                        }

                        if ($turnFailureMessage) {
                            Write-ProtocolJson @{ method = "turn/completed"; params = @{ threadId = $threadId; turnId = $turnId; turn = @{ id = $turnId; status = "failed"; error = @{ message = $turnFailureMessage }; items = @() } } }
                            break
                        }

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

    private static string FormatPowerShellStringLiteral(string? value)
    {
        return value is null ? "$null" : "'" + value.Replace("'", "''") + "'";
    }
}
