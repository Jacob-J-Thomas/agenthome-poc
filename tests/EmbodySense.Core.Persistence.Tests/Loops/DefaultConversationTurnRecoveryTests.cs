using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class DefaultConversationTurnRecoveryTests
{
    private const int ProcessLossExitCode = 173;
    private const string ProcessLossWorkspaceVariable = "EMBODYSENSE_TEST_DEFAULT_TURN_PROCESS_LOSS_WORKSPACE";
    private const string ProcessLossBoundaryVariable = "EMBODYSENSE_TEST_DEFAULT_TURN_PROCESS_LOSS_BOUNDARY";
    private const string PublicationWorkspaceVariable = "EMBODYSENSE_TEST_DEFAULT_TURN_PUBLICATION_WORKSPACE";
    private const string PublicationReadyVariable = "EMBODYSENSE_TEST_DEFAULT_TURN_PUBLICATION_READY";
    private const string PublicationReleaseVariable = "EMBODYSENSE_TEST_DEFAULT_TURN_PUBLICATION_RELEASE";
    private const string PublicationResultVariable = "EMBODYSENSE_TEST_DEFAULT_TURN_PUBLICATION_RESULT";

    public static TheoryData<DefaultConversationTurnBoundary, LoopRunStatus, int> DurableBoundaries => new()
    {
        { DefaultConversationTurnBoundary.TurnAdmitted, LoopRunStatus.Failed, 0 },
        { DefaultConversationTurnBoundary.RunStartSaved, LoopRunStatus.Failed, 0 },
        { DefaultConversationTurnBoundary.RunStartCheckpointed, LoopRunStatus.Failed, 0 },
        { DefaultConversationTurnBoundary.UserAccepted, LoopRunStatus.Failed, 1 },
        { DefaultConversationTurnBoundary.UserPublicationPrepared, LoopRunStatus.Failed, 1 },
        { DefaultConversationTurnBoundary.UserTranscriptAppended, LoopRunStatus.Failed, 1 },
        { DefaultConversationTurnBoundary.UserPublished, LoopRunStatus.Failed, 1 },
        { DefaultConversationTurnBoundary.ProviderDispatchPrepared, LoopRunStatus.Failed, 1 },
        { DefaultConversationTurnBoundary.ProviderDispatchStarted, LoopRunStatus.NeedsReview, 1 },
        { DefaultConversationTurnBoundary.ProviderOutcomeObserved, LoopRunStatus.Completed, 2 },
        { DefaultConversationTurnBoundary.AssistantPublicationPrepared, LoopRunStatus.Completed, 2 },
        { DefaultConversationTurnBoundary.AssistantTranscriptAppended, LoopRunStatus.Completed, 2 },
        { DefaultConversationTurnBoundary.AssistantPublished, LoopRunStatus.Completed, 2 },
        { DefaultConversationTurnBoundary.TranscriptSynchronized, LoopRunStatus.Completed, 2 },
        { DefaultConversationTurnBoundary.TerminalPrepared, LoopRunStatus.Completed, 2 },
        { DefaultConversationTurnBoundary.TerminalRunSaved, LoopRunStatus.Completed, 2 },
        { DefaultConversationTurnBoundary.TerminalCommitted, LoopRunStatus.Completed, 2 }
    };

    [Theory]
    [MemberData(nameof(DurableBoundaries))]
    public async Task Restart_reconciles_every_durable_boundary_without_duplicate_messages_or_provider_redispatch(
        DefaultConversationTurnBoundary boundary,
        LoopRunStatus expectedStatus,
        int expectedMessageCount)
    {
        using var workspace = new TestWorkspace();
        var fixture = CreateFixture(workspace, new InterruptingFailpoint(boundary));

        await Assert.ThrowsAsync<DefaultConversationTurnInterruptedException>(() => fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello")));
        var dispatchCount = fixture.Client.GenerateCount;
        var interrupted = fixture.Failpoint!.InterruptedRecord;
        Assert.NotNull(interrupted);
        var turnId = interrupted.TurnId;

        var report = await fixture.Recovery.RecoverAsync();
        var recovered = await fixture.Turns.LoadAsync(turnId);
        Assert.NotNull(recovered);
        var transcript = await fixture.Memory.LoadCurrentConversationAsync();

        Assert.Equal(DefaultConversationTurnCheckpoint.Terminal, recovered.Checkpoint);
        Assert.True(recovered.RunProjectionSynchronized);
        Assert.Equal(expectedStatus, recovered.Run.Status);
        Assert.Equal(expectedMessageCount, transcript.Count);
        Assert.Equal(dispatchCount, fixture.Client.GenerateCount);
        Assert.Equal(transcript.Count, transcript.Select(message => (message.Role, message.Content)).Distinct().Count());
        Assert.Equal(recovered.LifecycleVersion, recovered.Transitions.Count);
        Assert.Equal(recovered.Transitions.Count, recovered.Transitions.Select(transition => transition.TransitionId).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(await fixture.Turns.ListIncompleteAsync());
        Assert.Equal(boundary == DefaultConversationTurnBoundary.TerminalCommitted ? 0 : 1, report.Results.Count);
    }

    [Fact]
    public async Task Restart_parks_an_entered_provider_attempt_for_review_without_calling_the_provider_again()
    {
        using var workspace = new TestWorkspace();
        var client = new RecordingInferenceClient("unused") { InterruptDuringGenerate = true };
        var fixture = CreateFixture(workspace, client: client);

        await Assert.ThrowsAsync<DefaultConversationTurnInterruptedException>(() => fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello")));
        Assert.Equal(1, client.GenerateCount);

        var report = await fixture.Recovery.RecoverAsync();
        var result = Assert.Single(report.Results);
        var recovered = await fixture.Turns.LoadAsync(result.TurnId);
        Assert.NotNull(recovered);

        Assert.Equal(DefaultConversationTurnRecoveryClassification.ProviderOutcomeUnknown, result.Classification);
        Assert.True(report.PreserveCurrentConversation);
        Assert.Equal(LoopRunStatus.NeedsReview, recovered.Run.Status);
        Assert.Contains(recovered.ProviderAttemptId, recovered.Run.FailureDetail, StringComparison.Ordinal);
        Assert.Contains(recovered.ProviderCorrelationId, recovered.Run.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(1, client.GenerateCount);
        Assert.Collection(await fixture.Memory.LoadCurrentConversationAsync(), message => Assert.Equal("hello", message.Content));

        var laterStartup = await fixture.Recovery.RecoverAsync();
        Assert.Empty(laterStartup.Results);
        Assert.True(laterStartup.PreserveCurrentConversation);
        Assert.Single(await fixture.Turns.ListNeedsReviewAsync());

        await fixture.Memory.StartFreshConversationAsync();
        var afterExplicitConversationChange = await fixture.Recovery.RecoverAsync();
        Assert.False(afterExplicitConversationChange.PreserveCurrentConversation);
    }

    [Fact]
    public async Task Restart_recognizes_raw_transcript_commits_and_does_not_duplicate_them()
    {
        using var workspace = new TestWorkspace();
        var fixture = CreateFixture(workspace, new InterruptingFailpoint(DefaultConversationTurnBoundary.AssistantTranscriptAppended));

        await Assert.ThrowsAsync<DefaultConversationTurnInterruptedException>(() => fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello")));
        Assert.Equal(2, (await fixture.Memory.LoadCurrentConversationAsync()).Count);

        var report = await fixture.Recovery.RecoverAsync();
        var result = Assert.Single(report.Results);
        var transcript = await fixture.Memory.LoadCurrentConversationAsync();

        Assert.Equal(DefaultConversationTurnRecoveryClassification.TranscriptPartial, result.Classification);
        Assert.Collection(
            transcript,
            message => Assert.Equal((LlmMessageRole.User, "hello"), (message.Role, message.Content)),
            message => Assert.Equal((LlmMessageRole.Assistant, "answer"), (message.Role, message.Content)));
    }

    [Fact]
    public async Task Recovery_preserves_concurrent_user_content_and_records_an_inspectable_conflict()
    {
        using var workspace = new TestWorkspace();
        var fixture = CreateFixture(workspace, new InterruptingFailpoint(DefaultConversationTurnBoundary.UserPublicationPrepared));

        await Assert.ThrowsAsync<DefaultConversationTurnInterruptedException>(() => fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello")));
        await fixture.Memory.AppendMessageAsync(LlmMessage.User("owner edit"));

        var report = await fixture.Recovery.RecoverAsync();
        var result = Assert.Single(report.Results);
        var recovered = await fixture.Turns.LoadAsync(result.TurnId);
        Assert.NotNull(recovered);

        Assert.Equal(DefaultConversationTurnRecoveryClassification.Conflict, result.Classification);
        Assert.Equal(LoopRunStatus.NeedsReview, recovered.Run.Status);
        Assert.Equal(recovered.TurnId + ":message:user", recovered.UserMessage.MessageId);
        Assert.Equal(recovered.TurnId + ":publication:user", recovered.UserPublicationId);
        Assert.Contains(recovered.UserPublicationId, recovered.Run.FailureDetail, StringComparison.Ordinal);
        Assert.Collection(await fixture.Memory.LoadCurrentConversationAsync(), message => Assert.Equal("owner edit", message.Content));
    }

    [Fact]
    public async Task Recovery_rejects_identical_message_text_when_the_persisted_publication_identity_differs()
    {
        using var workspace = new TestWorkspace();
        var fixture = CreateFixture(workspace, new InterruptingFailpoint(DefaultConversationTurnBoundary.UserPublicationPrepared));

        await Assert.ThrowsAsync<DefaultConversationTurnInterruptedException>(() => fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello")));
        await fixture.Memory.AppendMessageAsync(LlmMessage.User("hello"));

        var result = Assert.Single((await fixture.Recovery.RecoverAsync()).Results);
        var recovered = await fixture.Turns.LoadAsync(result.TurnId);

        Assert.Equal(DefaultConversationTurnRecoveryClassification.Conflict, result.Classification);
        Assert.NotNull(recovered);
        Assert.Equal(LoopRunStatus.NeedsReview, recovered.Run.Status);
        Assert.Collection(await fixture.Memory.LoadCurrentConversationAsync(), message => Assert.Equal("hello", message.Content));
    }

    [Fact]
    public async Task Active_review_blocks_later_dispatch_until_explicit_resolution_quarantines_provider_state()
    {
        using var workspace = new TestWorkspace();
        var fixture = CreateFixture(workspace, new InterruptingFailpoint(DefaultConversationTurnBoundary.ProviderDispatchStarted));
        var originalRequest = new DefaultConversationLoopTurnRequest("hello", requestId: "reviewed-request");

        await Assert.ThrowsAsync<DefaultConversationTurnInterruptedException>(() => fixture.Runner.RunTurnAsync(originalRequest));
        var recovered = Assert.Single((await fixture.Recovery.RecoverAsync()).Results);

        var blocked = await fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("must not dispatch"));
        Assert.Equal(DefaultConversationLoopTurnStatus.NeedsReview, blocked.Status);
        Assert.Contains($"/review resolve {recovered.TurnId}", blocked.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Client.GenerateCount);

        var paths = new WorkspacePaths(workspace.RootPath);
        var reviewService = new DefaultConversationTurnReviewService(fixture.Turns, fixture.Client, new FileConversationWorkspaceLease(paths));
        var resolved = await reviewService.ResolveAsync(recovered.TurnId);

        Assert.NotNull(resolved);
        Assert.Equal(DefaultConversationTurnCheckpoint.ReviewResolved, resolved.Checkpoint);
        Assert.Equal(DefaultConversationTurnReviewDisposition.Abandoned, resolved.ReviewResolution!.Disposition);
        Assert.Equal(1, fixture.Client.QuarantineCount);
        Assert.Empty(await fixture.Turns.ListNeedsReviewAsync());

        var abandonedReplay = await fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: originalRequest.RequestId));
        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, abandonedReplay.Status);
        Assert.Contains("explicitly abandoned", abandonedReplay.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Client.GenerateCount);

        var completed = await fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("new attempt"));
        Assert.Equal(DefaultConversationLoopTurnStatus.Completed, completed.Status);
        Assert.Equal(1, fixture.Client.GenerateCount);
    }

    [Fact]
    public async Task Separate_process_recovers_a_raw_assistant_append_after_abrupt_process_loss()
    {
        using var workspace = new TestWorkspace();
        using var process = StartSelfTest(
            nameof(Process_loss_worker_exits_at_the_requested_durable_boundary),
            new Dictionary<string, string>
            {
                [ProcessLossWorkspaceVariable] = workspace.RootPath,
                [ProcessLossBoundaryVariable] = DefaultConversationTurnBoundary.AssistantTranscriptAppended.ToString()
            });
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode != 0, $"Process-loss worker unexpectedly completed normally. stdout: {output} stderr: {error}");
        Assert.Contains("test host process crashed", error, StringComparison.OrdinalIgnoreCase);

        var fixture = CreateFixture(workspace);
        var recovery = Assert.Single((await fixture.Recovery.RecoverAsync()).Results);
        var turn = await fixture.Turns.LoadAsync(recovery.TurnId);

        Assert.NotNull(turn);
        Assert.Equal(DefaultConversationTurnRecoveryClassification.TranscriptPartial, recovery.Classification);
        Assert.Equal(LoopRunStatus.Completed, turn.Run.Status);
        Assert.Collection(
            await fixture.Memory.LoadCurrentConversationAsync(),
            message => Assert.Equal((LlmMessageRole.User, "hello"), (message.Role, message.Content)),
            message => Assert.Equal((LlmMessageRole.Assistant, "answer"), (message.Role, message.Content)));
    }

    [Fact]
    public async Task Process_loss_worker_exits_at_the_requested_durable_boundary()
    {
        var workspaceRoot = Environment.GetEnvironmentVariable(ProcessLossWorkspaceVariable);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return;
        }

        var boundaryText = Environment.GetEnvironmentVariable(ProcessLossBoundaryVariable) ?? throw new InvalidOperationException("The process-loss boundary is required.");
        var boundary = Enum.Parse<DefaultConversationTurnBoundary>(boundaryText);
        var paths = new WorkspacePaths(workspaceRoot);
        var memory = new ConversationMemoryStore(paths);
        var turns = new DefaultConversationTurnStore(paths);
        var runs = new LoopRunStore(paths);
        var state = new ConversationRuntimeState(workspaceLease: new FileConversationWorkspaceLease(paths));
        var runner = new DefaultConversationLoopRunner(new RecordingInferenceClient("answer"), state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web, turns, new ExitingFailpoint(boundary));

        _ = await runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: "process-loss-request"));
        throw new InvalidOperationException("The process-loss failpoint did not terminate the worker.");
    }

    [Fact]
    public async Task Separate_process_compare_and_publish_allows_only_one_identical_content_identity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var memory = new ConversationMemoryStore(paths);
        var snapshot = await memory.LoadCurrentConversationSnapshotAsync();
        var readyPath = workspace.File("publication-ready");
        var releasePath = workspace.File("publication-release");
        var resultPath = workspace.File("publication-result");
        using var process = StartSelfTest(
            nameof(Cross_process_publication_worker_compares_and_publishes),
            new Dictionary<string, string>
            {
                [PublicationWorkspaceVariable] = workspace.RootPath,
                [PublicationReadyVariable] = readyPath,
                [PublicationReleaseVariable] = releasePath,
                [PublicationResultVariable] = resultPath
            });
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await WaitForFileAsync(readyPath, process, TimeSpan.FromSeconds(20));
            await File.WriteAllTextAsync(releasePath, "release");
            var parentResult = await memory.TryPublishMessageAsync(
                snapshot.ConversationId,
                snapshot.Version,
                snapshot.Messages,
                new ConversationMessagePublication("message-parent", "publication-parent", LlmMessage.User("identical")));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var output = await outputTask;
            var error = await errorTask;
            Assert.True(process.ExitCode == 0, $"Publication worker exited with `{process.ExitCode}`. stdout: {output} stderr: {error}");
            var childStatus = Enum.Parse<ConversationPublicationAppendStatus>(await File.ReadAllTextAsync(resultPath));

            Assert.Equal(
                [ConversationPublicationAppendStatus.Appended, ConversationPublicationAppendStatus.Conflict],
                new[] { parentResult.Status, childStatus }.OrderBy(status => status).ToArray());
            Assert.Collection(await memory.LoadCurrentConversationAsync(), message => Assert.Equal("identical", message.Content));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Cross_process_publication_worker_compares_and_publishes()
    {
        var workspaceRoot = Environment.GetEnvironmentVariable(PublicationWorkspaceVariable);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return;
        }

        var readyPath = Environment.GetEnvironmentVariable(PublicationReadyVariable) ?? throw new InvalidOperationException("The publication ready path is required.");
        var releasePath = Environment.GetEnvironmentVariable(PublicationReleaseVariable) ?? throw new InvalidOperationException("The publication release path is required.");
        var resultPath = Environment.GetEnvironmentVariable(PublicationResultVariable) ?? throw new InvalidOperationException("The publication result path is required.");
        var memory = new ConversationMemoryStore(new WorkspacePaths(workspaceRoot));
        var snapshot = await memory.LoadCurrentConversationSnapshotAsync();
        await File.WriteAllTextAsync(readyPath, "ready");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!File.Exists(releasePath))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(15), cancellation.Token);
        }

        var result = await memory.TryPublishMessageAsync(
            snapshot.ConversationId,
            snapshot.Version,
            snapshot.Messages,
            new ConversationMessagePublication("message-child", "publication-child", LlmMessage.User("identical")),
            cancellation.Token);
        await File.WriteAllTextAsync(resultPath, result.Status.ToString(), cancellation.Token);
    }

    [Fact]
    public async Task Store_replays_exact_updates_and_rejects_mutated_append_only_history()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        var memory = new ConversationMemoryStore(paths);
        var conversation = await memory.LoadCurrentConversationSnapshotAsync();
        const string RequestId = "request-store-test";
        var run = LoopRunRecord.Started(DefaultConversationTurnProtocol.CreateRunId(RequestId), BuiltInLoopIds.DefaultConversation, "default-assistant", RuntimeSurfaceId.Web, LoopTrigger.HumanMessage, DateTimeOffset.UtcNow);
        var admitted = DefaultConversationTurnProtocol.Admit(run, conversation, LlmMessage.User("hello"), DateTimeOffset.UtcNow, RequestId);

        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await turns.CreateAsync(admitted)).Status);
        Assert.Equal(DefaultConversationTurnStoreStatus.Replay, (await turns.CreateAsync(admitted)).Status);
        var advanced = admitted.Advance(DefaultConversationTurnCheckpoint.RunStarted, DateTimeOffset.UtcNow, "Run started.");
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(advanced, admitted.LifecycleVersion)).Status);
        Assert.Equal(DefaultConversationTurnStoreStatus.Replay, (await turns.UpdateAsync(advanced, admitted.LifecycleVersion)).Status);

        var candidate = advanced.Advance(DefaultConversationTurnCheckpoint.UserMessageAccepted, DateTimeOffset.UtcNow, "User accepted.");
        var mutated = candidate with
        {
            Transitions =
            [
                advanced.Transitions[0] with { Detail = "rewritten historical evidence" },
                advanced.Transitions[1],
                candidate.Transitions[2]
            ]
        };
        Assert.Equal(DefaultConversationTurnStoreStatus.Conflict, (await turns.UpdateAsync(mutated, advanced.LifecycleVersion)).Status);
        Assert.Equal("Run started.", (await turns.LoadAsync(admitted.TurnId))!.Transitions[1].Detail);
    }

    [Fact]
    public async Task Store_rejects_mutated_stable_identity_and_observed_outcome_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        var conversation = await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync();
        const string RequestId = "request-evidence-test";
        var run = LoopRunRecord.Started(DefaultConversationTurnProtocol.CreateRunId(RequestId), BuiltInLoopIds.DefaultConversation, "default-assistant", RuntimeSurfaceId.Web, LoopTrigger.HumanMessage, DateTimeOffset.UtcNow, new Dictionary<string, string> { ["stable"] = "value" });
        var record = DefaultConversationTurnProtocol.Admit(run, conversation, LlmMessage.User("hello"), DateTimeOffset.UtcNow, RequestId);
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await turns.CreateAsync(record)).Status);

        foreach (var checkpoint in new[]
        {
            DefaultConversationTurnCheckpoint.RunStarted,
            DefaultConversationTurnCheckpoint.UserMessageAccepted,
            DefaultConversationTurnCheckpoint.UserPublicationPrepared,
            DefaultConversationTurnCheckpoint.UserPublished,
            DefaultConversationTurnCheckpoint.ProviderDispatchPrepared
        })
        {
            var next = record.Advance(checkpoint, DateTimeOffset.UtcNow, checkpoint.ToString());
            Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(next, record.LifecycleVersion)).Status);
            record = next;
        }

        var started = record.Advance(DefaultConversationTurnCheckpoint.ProviderDispatchStarted, DateTimeOffset.UtcNow, "Provider entered.", providerOutcome: DefaultConversationProviderOutcome.OutcomeUnknown);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(started, record.LifecycleVersion)).Status);
        record = started;
        var assistant = new DefaultConversationTurnMessage(record.TurnId + ":message:assistant", LlmMessageRole.Assistant, "answer");
        var observed = record.Advance(DefaultConversationTurnCheckpoint.ProviderOutcomeObserved, DateTimeOffset.UtcNow, "Outcome observed.", providerOutcome: DefaultConversationProviderOutcome.Observed, assistantMessage: assistant, providerResponseId: "response-1");
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(observed, record.LifecycleVersion)).Status);

        var nextCheckpoint = observed.Advance(DefaultConversationTurnCheckpoint.AssistantPublicationPrepared, DateTimeOffset.UtcNow, "Publication prepared.");
        var mutatedAssistant = nextCheckpoint with { AssistantMessage = assistant with { Content = "rewritten answer" } };
        var mutatedRunIdentity = nextCheckpoint with { Run = nextCheckpoint.Run with { Metadata = new Dictionary<string, string> { ["stable"] = "rewritten" } } };

        Assert.Equal(DefaultConversationTurnStoreStatus.Conflict, (await turns.UpdateAsync(mutatedAssistant, observed.LifecycleVersion)).Status);
        Assert.Equal(DefaultConversationTurnStoreStatus.Conflict, (await turns.UpdateAsync(mutatedRunIdentity, observed.LifecycleVersion)).Status);
        Assert.Equal("answer", (await turns.LoadAsync(record.TurnId))!.AssistantMessage!.Content);
    }

    [Fact]
    public async Task Store_rejects_forged_latest_schema_one_artifacts_on_read()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        var conversation = await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync();
        const string RequestId = "request-forged-artifact";
        var startedAtUtc = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var run = LoopRunRecord.Started(DefaultConversationTurnProtocol.CreateRunId(RequestId), BuiltInLoopIds.DefaultConversation, "default-assistant", RuntimeSurfaceId.Web, LoopTrigger.HumanMessage, startedAtUtc);
        var admitted = DefaultConversationTurnProtocol.Admit(run, conversation, LlmMessage.User("hello"), startedAtUtc.AddSeconds(1), RequestId);
        var artifactPath = Path.Combine(paths.DefaultConversationTurnsPath, admitted.TurnId + ".json");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) }
        };

        Directory.CreateDirectory(paths.DefaultConversationTurnsPath);
        await File.WriteAllTextAsync(artifactPath, JsonSerializer.Serialize(admitted with { ProviderAttemptId = "provider-attempt-forged" }, options));
        await Assert.ThrowsAsync<FormatException>(() => turns.LoadAsync(admitted.TurnId));

        var skipped = admitted with
        {
            LifecycleVersion = 2,
            Checkpoint = DefaultConversationTurnCheckpoint.ProviderDispatchStarted,
            ProviderOutcome = DefaultConversationProviderOutcome.OutcomeUnknown,
            Transitions =
            [
                admitted.Transitions[0],
                new DefaultConversationTurnTransition(2, admitted.TurnId + ":2:providerdispatchstarted", DefaultConversationTurnCheckpoint.ProviderDispatchStarted, startedAtUtc.AddSeconds(2), "Forged skipped dispatch.")
            ]
        };
        await File.WriteAllTextAsync(artifactPath, JsonSerializer.Serialize(skipped, options));
        await Assert.ThrowsAsync<FormatException>(() => turns.LoadAsync(admitted.TurnId));
    }

    private static RecoveryFixture CreateFixture(TestWorkspace workspace, InterruptingFailpoint? failpoint = null, RecordingInferenceClient? client = null)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var memory = new ConversationMemoryStore(paths);
        var turns = new DefaultConversationTurnStore(paths);
        var runs = new LoopRunStore(paths);
        var inference = client ?? new RecordingInferenceClient("answer");
        var state = new ConversationRuntimeState(workspaceLease: new FileConversationWorkspaceLease(paths));
        var runner = new DefaultConversationLoopRunner(inference, state, memory, LoopDefinition.CreateDefaultConversation(), runs, RuntimeSurfaceId.Web, turns, failpoint);
        var recovery = new DefaultConversationTurnRecoveryService(turns, memory, runs, new FileConversationWorkspaceLease(paths));
        return new RecoveryFixture(runner, recovery, memory, turns, inference, failpoint);
    }

    private sealed record RecoveryFixture(
        DefaultConversationLoopRunner Runner,
        DefaultConversationTurnRecoveryService Recovery,
        ConversationMemoryStore Memory,
        DefaultConversationTurnStore Turns,
        RecordingInferenceClient Client,
        InterruptingFailpoint? Failpoint);

    private static Process StartSelfTest(string testName, IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(typeof(DefaultConversationTurnRecoveryTests).Assembly.Location);
        startInfo.ArgumentList.Add($"--TestCaseFilter:FullyQualifiedName={typeof(DefaultConversationTurnRecoveryTests).FullName}.{testName}");
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        foreach (var item in environment)
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("The default-conversation test worker did not start.");
    }

    private static async Task WaitForFileAsync(string path, Process process, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The default-conversation test worker exited before signaling readiness with exit code `{process.ExitCode}`.");
            }

            if (elapsed.Elapsed >= timeout)
            {
                throw new TimeoutException("The default-conversation test worker did not signal readiness within the bounded wait.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(15));
        }
    }

    private sealed class RecordingInferenceClient(string output) : ILlmInferenceClient, IQuarantinableInferenceClient
    {
        public int GenerateCount { get; private set; }

        public int QuarantineCount { get; private set; }

        public bool InterruptDuringGenerate { get; init; }

        public async Task<LlmInferenceResponse> GenerateAsync(LlmInferenceRequest request, Func<string, CancellationToken, Task>? responseChunkHandler = null, CancellationToken cancellationToken = default)
        {
            GenerateCount++;
            if (InterruptDuringGenerate)
            {
                throw new DefaultConversationTurnInterruptedException("Simulated process loss inside the provider adapter.");
            }

            if (responseChunkHandler is not null)
            {
                await responseChunkHandler(output, cancellationToken);
            }

            return new LlmInferenceResponse(output, LlmInferenceSurface.OpenAiCodex, "test-model", "provider-response-1");
        }

        public Task QuarantineAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuarantineCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ExitingFailpoint(DefaultConversationTurnBoundary boundary) : IDefaultConversationTurnFailpoint
    {
        public Task AfterBoundaryAsync(DefaultConversationTurnBoundary currentBoundary, DefaultConversationTurnRecord record, CancellationToken cancellationToken = default)
        {
            if (currentBoundary == boundary)
            {
                Environment.Exit(ProcessLossExitCode);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class InterruptingFailpoint(DefaultConversationTurnBoundary boundary) : IDefaultConversationTurnFailpoint
    {
        private int _interrupted;

        public DefaultConversationTurnRecord? InterruptedRecord { get; private set; }

        public Task AfterBoundaryAsync(DefaultConversationTurnBoundary currentBoundary, DefaultConversationTurnRecord record, CancellationToken cancellationToken = default)
        {
            if (currentBoundary == boundary && Interlocked.Exchange(ref _interrupted, 1) == 0)
            {
                InterruptedRecord = record;
                throw new DefaultConversationTurnInterruptedException($"Simulated process loss after `{currentBoundary}`.");
            }

            return Task.CompletedTask;
        }
    }
}
