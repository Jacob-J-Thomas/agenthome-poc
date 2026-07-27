using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
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
    public async Task Transcript_hydration_waits_for_the_active_turn_and_returns_its_complete_canonical_messages()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace);
        await using var host = CreateHost(workspace.RootPath, codexPath);
        await host.InitializeWorkspaceAsync();
        var deltaObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var send = host.SendMessageAsync("hydrate during turn", async (streamEvent, cancellationToken) =>
        {
            if (streamEvent.Type == "assistant_delta")
            {
                deltaObserved.TrySetResult();
                await releaseDelta.Task.WaitAsync(cancellationToken);
            }
        });
        await deltaObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var hydration = host.GetCurrentTranscriptAsync();
        await Task.Delay(100);
        Assert.False(hydration.IsCompleted);

        releaseDelta.TrySetResult();
        await send;
        var transcript = Assert.IsAssignableFrom<IReadOnlyList<WebTranscriptMessage>>(await hydration);

        Assert.Collection(
            transcript,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("hydrate during turn", message.Content);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("web response: hydrate during turn", message.Content);
            });
    }

    [Fact]
    public async Task Transcript_hydration_does_not_cross_even_a_runtime_independent_turn_boundary()
    {
        using var workspace = new TestWorkspace();
        await using var host = CreateHost(workspace.RootPath);
        await host.InitializeWorkspaceAsync();
        var responseObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var send = host.SendMessageAsync("/help", async (_, cancellationToken) =>
        {
            responseObserved.TrySetResult();
            await releaseResponse.Task.WaitAsync(cancellationToken);
        });
        await responseObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var hydration = host.GetCurrentTranscriptAsync();
        await Task.Delay(100);
        Assert.False(hydration.IsCompleted);

        releaseResponse.TrySetResult();
        await send;
        Assert.Null(await hydration);
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
    public async Task Cancelling_chat_defers_runtime_disposal_until_an_active_custom_loop_remains_controllable()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, turnDelayMilliseconds: 30_000);
        var approvals = new WebApprovalCoordinator();
        approvals.RegisterOwnerConnection("connection-1");
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--codex-path", codexPath]);
        await using var host = new WebAgentRuntimeHost(options, approvals);
        await host.InitializeWorkspaceAsync();
        var definition = await CreateInvocationLoopAsync(workspace);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-during-chat-cancel", "host-dispose-custom-loop");

        var invocation = host.InvokeLoopAsync(input, "connection-1");
        await WaitForMarkerAsync(workspace.File("host-dispose-custom-loop.marker"));
        var running = await WaitForRunAsync(host, input.OperationId);
        var send = host.SendMessageAsync("hello from web", (_, _) => Task.CompletedTask);
        var chatCancelled = false;
        for (var attempt = 0; attempt < 200 && !chatCancelled; attempt++)
        {
            chatCancelled = host.CancelCurrentTurn();
            await Task.Delay(10);
        }

        Assert.True(chatCancelled);
        await send;
        running = Assert.IsType<LoopRunSnapshot>(await host.GetLoopRunAsync(running.Id));
        var cancellation = await host.CancelLoopAsync(new LoopRunControlInput(running.Id, running.LifecycleVersion, "cancel-after-chat-cancel"));
        var completed = await invocation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(cancellation.Status, new[] { "CancelRequested", "Cancelled", "AuditWarning" });
        Assert.NotNull(cancellation.Run);
        Assert.Contains(completed.ExecutionStatus, new[] { "Cancelled", "NeedsReview", "Failed" });
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

    [Fact]
    public async Task ResumeLoopAsync_requires_an_initialized_workspace()
    {
        using var workspace = new TestWorkspace();
        var approvals = new WebApprovalCoordinator();
        approvals.RegisterOwnerConnection("connection-1");
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath]);
        await using var host = new WebAgentRuntimeHost(options, approvals);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ResumeLoopAsync(new LoopRunControlInput("run-one", 1, "resume-uninitialized"), "connection-1"));

        Assert.Contains("Workspace is not initialized", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_cancels_an_active_custom_loop_through_the_host_lifetime()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, turnDelayMilliseconds: 30_000);
        var approvals = new WebApprovalCoordinator();
        approvals.RegisterOwnerConnection("connection-1");
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--codex-path", codexPath]);
        var host = new WebAgentRuntimeHost(options, approvals);
        await host.InitializeWorkspaceAsync();
        var definition = await CreateInvocationLoopAsync(workspace);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-host-dispose", "host-dispose-custom-loop");

        var invocation = host.InvokeLoopAsync(input, "connection-1");
        await WaitForMarkerAsync(workspace.File("host-dispose-custom-loop.marker"));
        var dispose = host.DisposeAsync().AsTask();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        var invocationException = await Record.ExceptionAsync(async () => await invocation);

        Assert.True(invocationException is null or OperationCanceledException, invocationException?.ToString());
        if (invocationException is null)
        {
            var response = await invocation;
            Assert.Contains(response.ExecutionStatus, new[] { "Cancelled", "NeedsReview", "Failed" });
        }
    }

    [Fact]
    public async Task Owner_disconnect_returns_a_zero_execution_tool_rejection_and_the_custom_run_continues()
    {
        using var workspace = new TestWorkspace();
        var codexPath = await CreateFakeCodexExecutableAsync(workspace, turnDelayMilliseconds: -1);
        var approvals = new WebApprovalCoordinator();
        approvals.RegisterOwnerConnection("connection-1");
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--codex-path", codexPath]);
        await using var host = new WebAgentRuntimeHost(options, approvals);
        await host.InitializeWorkspaceAsync();
        await File.WriteAllTextAsync(workspace.File("approval-only-note.txt"), "content-that-must-not-be-returned");
        var definition = await CreateInvocationLoopAsync(workspace, [LoopToolAssignment.Read]);
        var input = new LoopRunInvocationInput(definition.Id, definition.DefinitionVersion, definition.ContentHash, "invoke-owner-disconnect-tool", "request-the-governed-read");

        var invocation = host.InvokeLoopAsync(input, "connection-1");
        await WaitForPendingApprovalAsync(approvals, "connection-1");
        await approvals.DisconnectOwnerAsync("connection-1");
        var response = await invocation;
        var toolResponse = await File.ReadAllTextAsync(workspace.File("owner-disconnected-tool-response.json"));
        var audit = await File.ReadAllTextAsync(workspace.File(".agent", "audit", "events.ndjson"));

        Assert.Equal("Completed", response.ExecutionStatus);
        Assert.Contains("continued after governed tool denial", response.Run!.FinalOutput, StringComparison.Ordinal);
        Assert.Contains("owner_disconnected", toolResponse, StringComparison.Ordinal);
        Assert.Contains("\"success\":false", toolResponse, StringComparison.Ordinal);
        Assert.DoesNotContain("content-that-must-not-be-returned", toolResponse, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"tool.approval.decision\"", audit, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"approval_rejected\"", audit, StringComparison.Ordinal);
        var toolExecution = Assert.Single(audit.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), line => line.Contains("\"action\":\"tool.execute\"", StringComparison.Ordinal));
        Assert.Contains("\"outcome\":\"approval_rejected\"", toolExecution, StringComparison.Ordinal);
        Assert.Contains("\"approved_by_human\":false", toolExecution, StringComparison.Ordinal);
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

    private static async Task<LoopDefinitionSnapshot> CreateInvocationLoopAsync(TestWorkspace workspace, IReadOnlyList<LoopToolAssignment>? toolAssignments = null)
    {
        var facade = new LoopAuthoringFacade(workspace.RootPath);
        var created = Assert.IsType<LoopDefinitionSnapshot>((await facade.CreateAsync("create-host-dispose-loop")).Definition);
        var input = new LoopDefinitionInput(
            "Host disposal loop",
            "Verifies host-lifetime cancellation.",
            new LoopTriggerPolicy(LoopTriggerPromptSource.Invocation, string.Empty, false),
            [new LoopInferenceStep(created.InferenceSteps.Single().Id, "Wait", "Wait for the admitted prompt.", new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null))],
            toolAssignments ?? [],
            new LoopExitPolicy(0, created.ExitPolicy.DecisionInstruction, new LoopNodeContextPolicy(LoopContextPolicyMode.Inherit, null)));
        var updated = await facade.UpdateAsync(created.Id, created.DefinitionVersion, "update-host-dispose-loop", input);
        return Assert.IsType<LoopDefinitionSnapshot>(updated.Definition);
    }

    private static async Task WaitForMarkerAsync(string markerPath)
    {
        for (var attempt = 0; attempt < 100 && !File.Exists(markerPath); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(File.Exists(markerPath), "The custom-loop provider attempt did not start within five seconds.");
    }

    private static async Task<LoopRunSnapshot> WaitForRunAsync(WebAgentRuntimeHost host, string admissionOperationId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var summary = (await host.GetLoopRunsAsync()).SingleOrDefault(run => string.Equals(run.AdmissionOperationId, admissionOperationId, StringComparison.Ordinal));
            if (summary is not null && await host.GetLoopRunAsync(summary.Id) is { } run)
            {
                return run;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Custom run for admission operation `{admissionOperationId}` was not persisted.");
    }

    private static async Task WaitForPendingApprovalAsync(WebApprovalCoordinator approvals, string ownerConnectionId)
    {
        for (var attempt = 0; attempt < 100 && approvals.GetPending(ownerConnectionId).Count == 0; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.Single(approvals.GetPending(ownerConnectionId));
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

                if ($message.id -eq 99) {
                    $toolResponse = $message | ConvertTo-Json -Compress -Depth 20
                    [IO.File]::WriteAllText((Join-Path $PSScriptRoot "owner-disconnected-tool-response.json"), $toolResponse)
                    $text = "continued after governed tool denial"
                    Write-ProtocolJson @{ method = "item/agentMessage/delta"; params = @{ threadId = $threadId; turnId = "turn-test"; delta = $text } }
                    Write-ProtocolJson @{ method = "turn/completed"; params = @{ threadId = $threadId; turnId = "turn-test"; turn = @{ id = "turn-test"; status = "completed"; items = @(@{ type = "agentMessage"; phase = "final_answer"; text = $text }) } } }
                    continue
                }

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
                        if ($turnDelayMilliseconds -eq -1) {
                            Write-ProtocolJson @{ id = 99; method = "item/tool/call"; params = @{ threadId = $threadId; turnId = $turnId; callId = "call-owner-disconnect"; namespace = "embodysense"; tool = "command"; arguments = @{ command = "read"; path = "approval-only-note.txt" } } }
                            break
                        }
                        if ($turnDelayMilliseconds -ge 30000) {
                            [IO.File]::WriteAllText((Join-Path $PSScriptRoot "host-dispose-custom-loop.marker"), "started")
                            Start-Sleep -Milliseconds $turnDelayMilliseconds
                        }
                        elseif ($turnDelayMilliseconds -gt 0 -and $userText.Contains("hello from web")) {
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
