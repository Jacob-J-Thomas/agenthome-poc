using System.Text.Json;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    internal static async Task CreateAsync_default_conversation_revalidates_current_authority_before_a_tool_actuation()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(workspace.File(Path.Combine(".agent", "authority.txt")), "server-owned content");
        var executablePath = await CreateToolCallingCodexExecutableAsync(workspace);
        await using var runtime = await CreateAuthorityCoverageRuntimeAsync(workspace, executablePath);

        var result = await runtime.RunTurnAsync("inspect the authority file", requestId: "authority-revalidation-direct");

        Assert.Equal(AgentRuntimeTurnStatus.MessageCompleted, result.Status);
        Assert.Contains("tool result accepted", result.Output, StringComparison.Ordinal);
        Assert.True(result.Output.Contains("server-owned content", StringComparison.Ordinal), $"OUTPUT={result.Output}");
    }

    internal static async Task CreateAsync_default_conversation_denies_tool_actuation_when_definition_authority_narrows()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await File.WriteAllTextAsync(workspace.File(Path.Combine(".agent", "authority.txt")), "server-owned content");
        var turnStartedPath = workspace.File("authority-turn-started.marker");
        var releasePath = workspace.File("authority-turn-release.marker");
        var executablePath = await CreateToolCallingCodexExecutableAsync(workspace, turnStartedPath, releasePath);
        await using var runtime = await CreateAuthorityCoverageRuntimeAsync(workspace, executablePath);

        var turn = runtime.RunTurnAsync("inspect the authority file", requestId: "authority-revalidation-narrowed");
        await WaitForMarkerAsync(turnStartedPath, turn);
        var paths = new WorkspacePaths(workspace.RootPath);
        await WaitForProviderDispatchStartedAsync(paths, "authority-revalidation-narrowed", turn);
        await new LoopDefinitionStore(paths).SaveAsync(LoopDefinition.CreateDefaultConversation() with
        {
            CapabilityIds = [LoopCapabilityIds.ConversationTurn],
        });
        await File.WriteAllTextAsync(releasePath, "release");

        var result = await turn;

        Assert.Equal(AgentRuntimeTurnStatus.MessageCompleted, result.Status);
        Assert.Contains("tool result accepted", result.Output, StringComparison.Ordinal);
        Assert.True(result.Output.Contains("denied", StringComparison.OrdinalIgnoreCase), $"OUTPUT={result.Output}");
        Assert.DoesNotContain("server-owned content", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_schedule_invocation_preparation_accepts_an_exact_wait_node_only_for_schedule_surface()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var role = await CreateScheduleGraphAuthoringRoleAsync(new WorkspacePaths(workspace.RootPath));
        var source = ScheduleBrowserGraphCandidate(new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity(role.Identity.RoleId, role.Identity.Revision),
            role.ContentHash));
        var trigger = Assert.IsType<GovernedLoopNodeDefinition>(source.Nodes?[0]);
        var exit = Assert.IsType<GovernedLoopNodeDefinition>(source.Nodes?[1]);
        var wait = new GovernedLoopNodeDefinition(
            "wait",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Wait, GovernedLoopWaitVocabulary.Timestamp, GovernedLoopWaitVocabulary.DescriptorVersion),
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>
            {
                [GovernedLoopWaitVocabulary.DeadlineUtcParameter] = DateTimeOffset.UtcNow.AddMinutes(5).ToUniversalTime().ToString(GovernedLoopWaitVocabulary.CanonicalUtcTimestampFormat, System.Globalization.CultureInfo.InvariantCulture),
            });
        var candidate = source with
        {
            GraphId = "schedule-invocation-wait-coverage-graph",
            Nodes = [trigger, wait, exit],
            ControlEdges =
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-wait", trigger.Id, wait.Id, GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("wait-to-exit", wait.Id, exit.Id, GovernedLoopControlCondition.Success),
            ],
        };
        var created = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "create-schedule-invocation-wait-coverage-graph",
            GovernedLoopGraphMutationKind.CreateDraft,
            candidate.GraphId!,
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate));
        var draft = Assert.IsType<GovernedLoopGraphReadResponse>(created.Current);
        Assert.True(draft.Lifecycle is not null, $"{created.Status}: {string.Join("; ", created.Errors.Select(error => $"{error.Code}: {error.Message}"))}");
        var draftHead = Assert.IsType<GovernedLoopRevisionLifecycleHead>(draft.Lifecycle);
        var published = await runtime.GovernedLoopGraphAuthoring.MutateAsync(new GovernedLoopGraphMutationInput(
            "publish-schedule-invocation-wait-coverage-graph",
            GovernedLoopGraphMutationKind.Publish,
            candidate.GraphId!,
            draftHead.Status,
            draftHead.LifecycleVersion,
            draftHead.DraftRevision,
            draftHead.PublishedRevision,
            null));
        var scheduled = await runtime.GovernedLoopInvocationPreparation.PrepareScheduleAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var manual = await runtime.GovernedLoopInvocationPreparation.PrepareAsync(new GovernedLoopInvocationPreparationRequest(candidate.GraphId!, candidate.RevisionId!));
        var invalidScheduleConfirmation = await runtime.GovernedLoopInvocationPreparation.ConfirmScheduleAsync(new GovernedLoopInvocationAuthorityConfirmation(
            candidate.GraphId!,
            candidate.RevisionId!,
            new string('a', 64),
            "schedule-invocation-wait-invalid-confirmation"));

        Assert.Equal("committed", created.Status);
        Assert.Equal("committed", published.Status);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.ConfirmationRequired, scheduled.Status);
        Assert.Equal(candidate.GraphId, scheduled.Publication?.Revision.GraphId);
        Assert.Equal(GovernedLoopInvocationPreparationStatus.Ineligible, manual.Status);
        Assert.Equal(GovernedLoopInvocationAuthorityConfirmationStatus.Stale, invalidScheduleConfirmation.Status);
        Assert.Null(invalidScheduleConfirmation.Grant);
    }

    private static async Task<string> CreateToolCallingCodexExecutableAsync(TestWorkspace workspace, string? startedPath = null, string? releasePath = null)
    {
        var scriptPath = workspace.File("tool-calling-codex.js");
        var commandPath = workspace.File(OperatingSystem.IsWindows() ? "tool-calling-codex.cmd" : "tool-calling-codex");
        var serializedStartedPath = JsonSerializer.Serialize(startedPath);
        var serializedReleasePath = JsonSerializer.Serialize(releasePath);
        await File.WriteAllTextAsync(scriptPath, $$"""
            const fs = require("node:fs");
            const readline = require("node:readline");

            if (process.argv.slice(2).includes("--version")) {
              process.stdout.write("codex-cli 999.0.0-test\n");
              process.exit(0);
            }

            const input = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
            const threadId = "thread-authority-test";
            const startedPath = {{serializedStartedPath}};
            const releasePath = {{serializedReleasePath}};

            function write(value) {
              process.stdout.write(`${JSON.stringify(value)}\n`);
            }

            function waitForRelease(callback) {
              if (!releasePath) {
                callback();
                return;
              }

              const poll = () => {
                if (fs.existsSync(releasePath)) {
                  // This is a one-shot release barrier; retaining the marker avoids a
                  // Windows sharing race after the test process has observed it.
                  callback();
                  return;
                }

                setTimeout(poll, 10);
              };
              poll();
            }

            input.on("line", line => {
              const message = JSON.parse(line);
              switch (message.method) {
                case "initialize":
                  write({ id: message.id, result: {} });
                  break;
                case "model/list":
                  write({ id: message.id, result: { data: [{ id: "test-model", model: "test-model" }] } });
                  break;
                case "thread/start":
                  write({ id: message.id, result: { model: "test-model", modelProvider: "openai", thread: { id: threadId, modelProvider: "openai" } } });
                  break;
                case "turn/start": {
                  const turnId = "turn-authority-test";
                  write({ id: message.id, result: { turn: { id: turnId } } });
                  if (startedPath) {
                    fs.writeFileSync(startedPath, "started");
                  }

                  waitForRelease(() => {
                    write({
                      id: "authority-tool-call",
                      method: "item/tool/call",
                      params: {
                        threadId,
                        turnId,
                        itemId: "authority-tool-item",
                        callId: "authority-tool-call",
                        namespace: "embodysense",
                        tool: "command",
                        arguments: { command: "read", path: ".agent/authority.txt" }
                      }
                    });
                  });
                  break;
                }
                default:
                  if (message.id === "authority-tool-call") {
                    const text = `tool result accepted: ${JSON.stringify(message.result ?? message.error ?? {})}`;
                    write({ method: "item/agentMessage/delta", params: { threadId, turnId: "turn-authority-test", delta: text } });
                    write({
                      method: "turn/completed",
                      params: {
                        threadId,
                        turnId: "turn-authority-test",
                        turn: { id: "turn-authority-test", status: "completed", items: [{ type: "agentMessage", phase: "final_answer", text }] }
                      }
                    });
                  }
                  break;
              }
            });
            """);

        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(commandPath, """
                @echo off
                node "%~dp0tool-calling-codex.js" %*
                """);
        }
        else
        {
            await File.WriteAllTextAsync(commandPath, $"#!/bin/sh\nexec node \"{scriptPath}\" \"$@\"\n");
            File.SetUnixFileMode(commandPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return commandPath;
    }

    private static async Task<AgentRuntime> CreateAuthorityCoverageRuntimeAsync(TestWorkspace workspace, string executablePath)
    {
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(new InvocationAuthorityApprovalPrompt(), workspace.ServerStatePath, CreateCompatibleRuntimeStatus(executablePath));
        return await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);
    }

    private static async Task WaitForMarkerAsync(string markerPath, Task<AgentRuntimeTurnResult> turn)
    {
        if (File.Exists(markerPath))
        {
            return;
        }

        var directoryPath = Path.GetDirectoryName(markerPath) ?? throw new Xunit.Sdk.XunitException($"Marker directory is unavailable: {markerPath}");
        var markerName = Path.GetFileName(markerPath);
        var markerTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directoryPath, markerName) { EnableRaisingEvents = true };
        FileSystemEventHandler signalMarker = (_, _) => markerTask.TrySetResult();
        watcher.Created += signalMarker;
        watcher.Changed += signalMarker;
        if (File.Exists(markerPath))
        {
            markerTask.TrySetResult();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
        var completed = await Task.WhenAny(markerTask.Task, turn, timeoutTask);
        if (completed == markerTask.Task || File.Exists(markerPath))
        {
            return;
        }

        if (completed == turn)
        {
            var result = await turn;
            throw new Xunit.Sdk.XunitException($"The tool-calling turn completed before its synchronization marker: {result.Status}; {result.FailureDetail}");
        }

        throw new Xunit.Sdk.XunitException($"The tool-calling turn did not publish its synchronization marker within the bounded wait: {markerPath}");
    }

    private static async Task WaitForProviderDispatchStartedAsync(WorkspacePaths paths, string requestId, Task<AgentRuntimeTurnResult> turn)
    {
        var turnId = DefaultConversationTurnProtocol.CreateTurnId(requestId);
        var store = new DefaultConversationTurnStore(paths);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var record = await store.LoadAsync(turnId, timeout.Token);
                if (record?.Checkpoint == DefaultConversationTurnCheckpoint.ProviderDispatchStarted)
                {
                    return;
                }

                if (record?.Checkpoint > DefaultConversationTurnCheckpoint.ProviderDispatchStarted)
                {
                    throw new Xunit.Sdk.XunitException($"The tool-calling turn advanced past the required provider-dispatch synchronization boundary: {record.Checkpoint}");
                }

                if (turn.IsCompleted)
                {
                    var result = await turn;
                    throw new Xunit.Sdk.XunitException($"The tool-calling turn completed before the durable provider-dispatch checkpoint: {result.Status}; {result.FailureDetail}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }

        throw new Xunit.Sdk.XunitException($"The tool-calling turn did not reach durable checkpoint `{DefaultConversationTurnCheckpoint.ProviderDispatchStarted}` within the bounded wait: {turnId}");
    }
}
