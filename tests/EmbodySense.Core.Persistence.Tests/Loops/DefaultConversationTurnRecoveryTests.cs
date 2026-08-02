using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Protocol;
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
using EmbodySense.Core.Persistence.Loops.Models;
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
    private const string TurnLeaseWorkspaceVariable = "EMBODYSENSE_TEST_DEFAULT_TURN_LEASE_WORKSPACE";
    private const string TurnLeaseReadyVariable = "EMBODYSENSE_TEST_DEFAULT_TURN_LEASE_READY";
    private const string TurnLeaseReleaseVariable = "EMBODYSENSE_TEST_DEFAULT_TURN_LEASE_RELEASE";
    private const string RetirementStageVariable = "EMBODYSENSE_TEST_DEFAULT_TURN_RETIREMENT_STAGE";
    private const string RetirementDisplacedVariable = "EMBODYSENSE_TEST_DEFAULT_TURN_RETIREMENT_DISPLACED";
    private const string RetirementReplacementVariable = "EMBODYSENSE_TEST_DEFAULT_TURN_RETIREMENT_REPLACEMENT";

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
    public async Task Restart_finalizes_a_durably_observed_terminal_provider_failure_without_quarantine_or_redispatch()
    {
        using var workspace = new TestWorkspace();
        var client = new RecordingInferenceClient("unused") { Failure = new LlmInferenceTerminalFailureException("provider rejected the turn", "provider-turn-1") };
        var fixture = CreateFixture(workspace, new InterruptingFailpoint(DefaultConversationTurnBoundary.ProviderOutcomeObserved), client);

        await Assert.ThrowsAsync<DefaultConversationTurnInterruptedException>(() => fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello")));
        var interrupted = fixture.Failpoint!.InterruptedRecord;
        Assert.NotNull(interrupted);
        Assert.Equal(DefaultConversationProviderOutcome.ObservedFailure, interrupted.ProviderOutcome);
        Assert.Null(interrupted.AssistantMessage);

        var result = Assert.Single((await fixture.Recovery.RecoverAsync()).Results);
        var recovered = await fixture.Turns.LoadAsync(result.TurnId);

        Assert.NotNull(recovered);
        Assert.Equal(DefaultConversationTurnRecoveryClassification.ProviderOutcomeObserved, result.Classification);
        Assert.Equal(DefaultConversationTurnCheckpoint.Terminal, recovered.Checkpoint);
        Assert.Equal(LoopRunStatus.Failed, recovered.Run.Status);
        Assert.Contains("provider rejected the turn", recovered.Run.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(1, client.GenerateCount);
        Assert.Equal(0, client.QuarantineCount);
        Assert.Collection(await fixture.Memory.LoadCurrentConversationAsync(), message => Assert.Equal((LlmMessageRole.User, "hello"), (message.Role, message.Content)));
    }

    [Fact]
    public async Task Restart_preserves_concurrent_transcript_content_but_still_closes_a_conclusive_provider_failure()
    {
        using var workspace = new TestWorkspace();
        var client = new RecordingInferenceClient("unused") { Failure = new LlmInferenceTerminalFailureException("provider rejected the turn", "provider-turn-1") };
        var fixture = CreateFixture(workspace, new InterruptingFailpoint(DefaultConversationTurnBoundary.ProviderOutcomeObserved), client);

        await Assert.ThrowsAsync<DefaultConversationTurnInterruptedException>(() => fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello")));
        await fixture.Memory.AppendMessageAsync(LlmMessage.User("owner edit"));

        var result = Assert.Single((await fixture.Recovery.RecoverAsync()).Results);
        var recovered = await fixture.Turns.LoadAsync(result.TurnId);

        Assert.NotNull(recovered);
        Assert.Equal(DefaultConversationTurnRecoveryClassification.ProviderOutcomeObserved, result.Classification);
        Assert.Contains("divergent transcript content was preserved", result.Detail, StringComparison.Ordinal);
        Assert.Equal(DefaultConversationTurnCheckpoint.Terminal, recovered.Checkpoint);
        Assert.Equal(LoopRunStatus.Failed, recovered.Run.Status);
        Assert.Contains("provider rejected the turn", recovered.Run.FailureDetail, StringComparison.Ordinal);
        Assert.Contains("divergent transcript content", recovered.Run.FailureDetail, StringComparison.Ordinal);
        Assert.Empty(await fixture.Turns.ListNeedsReviewAsync());
        Assert.Equal(1, client.GenerateCount);
        Assert.Equal(0, client.QuarantineCount);
        Assert.Collection(
            await fixture.Memory.LoadCurrentConversationAsync(),
            message => Assert.Equal((LlmMessageRole.User, "hello"), (message.Role, message.Content)),
            message => Assert.Equal((LlmMessageRole.User, "owner edit"), (message.Role, message.Content)));
    }

    [Fact]
    public async Task Restart_retains_observed_output_for_review_when_completion_audit_failed()
    {
        using var workspace = new TestWorkspace();
        var response = new LlmInferenceResponse("observed answer", LlmInferenceSurface.OpenAiCodex, ProviderResponseId: "provider-turn-2");
        var client = new RecordingInferenceClient("unused")
        {
            Failure = new LlmInferenceObservedResponseException("completion audit failed after provider success", response, new IOException("audit unavailable"))
        };
        var fixture = CreateFixture(workspace, new InterruptingFailpoint(DefaultConversationTurnBoundary.ProviderOutcomeObserved), client);

        await Assert.ThrowsAsync<DefaultConversationTurnInterruptedException>(() => fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello")));
        Assert.Equal(DefaultConversationProviderOutcome.ObservedWithAuditFailure, fixture.Failpoint!.InterruptedRecord!.ProviderOutcome);

        var result = Assert.Single((await fixture.Recovery.RecoverAsync()).Results);
        var recovered = await fixture.Turns.LoadAsync(result.TurnId);

        Assert.NotNull(recovered);
        Assert.Equal(DefaultConversationTurnRecoveryClassification.ProviderOutcomeObserved, result.Classification);
        Assert.Equal(DefaultConversationTurnCheckpoint.Terminal, recovered.Checkpoint);
        Assert.Equal(LoopRunStatus.NeedsReview, recovered.Run.Status);
        Assert.Contains("completion audit failed", recovered.Run.FailureDetail, StringComparison.Ordinal);
        Assert.Equal("observed answer", recovered.AssistantMessage!.Content);
        Assert.Equal(0, client.QuarantineCount);
        Assert.Collection(await fixture.Memory.LoadCurrentConversationAsync(), message => Assert.Equal((LlmMessageRole.User, "hello"), (message.Role, message.Content)));
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
    public async Task Cross_process_active_set_lease_contention_remains_cancellation_aware()
    {
        using var workspace = new TestWorkspace();
        var readyPath = workspace.File("turn-lease-ready");
        var releasePath = workspace.File("turn-lease-release");
        using var process = StartSelfTest(
            nameof(Cross_process_active_set_lease_worker_holds_until_released),
            new Dictionary<string, string>
            {
                [TurnLeaseWorkspaceVariable] = workspace.RootPath,
                [TurnLeaseReadyVariable] = readyPath,
                [TurnLeaseReleaseVariable] = releasePath
            });
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await WaitForFileAsync(readyPath, process, TimeSpan.FromSeconds(20));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var turns = new DefaultConversationTurnStore(new WorkspacePaths(workspace.RootPath));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turns.ListIncompleteAsync(cancellation.Token));

            await File.WriteAllTextAsync(releasePath, "release");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var output = await outputTask;
            var error = await errorTask;
            Assert.True(process.ExitCode == 0, $"Turn-lease worker exited with `{process.ExitCode}`. stdout: {output} stderr: {error}");
        }
        finally
        {
            if (!File.Exists(releasePath))
            {
                await File.WriteAllTextAsync(releasePath, "release");
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Cross_process_active_set_lease_worker_holds_until_released()
    {
        var workspaceRoot = Environment.GetEnvironmentVariable(TurnLeaseWorkspaceVariable);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return;
        }

        var readyPath = Environment.GetEnvironmentVariable(TurnLeaseReadyVariable) ?? throw new InvalidOperationException("The turn-lease ready path is required.");
        var releasePath = Environment.GetEnvironmentVariable(TurnLeaseReleaseVariable) ?? throw new InvalidOperationException("The turn-lease release path is required.");
        var coordination = new FileBlockingTurnStoreCoordination(readyPath, releasePath);
        var paths = new WorkspacePaths(workspaceRoot);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var turns = new DefaultConversationTurnStore(paths, coordination);

        _ = await turns.ListIncompleteAsync();
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
        var artifactPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var options = CreateTurnJsonOptions();

        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
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

    [Fact]
    public async Task Store_rejects_legacy_flat_artifacts_before_reading_active_or_history_storage()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        Directory.CreateDirectory(paths.DefaultConversationTurnsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.DefaultConversationTurnsPath, "legacy.json"), "{}");

        var exception = await Assert.ThrowsAsync<FormatException>(() => turns.LoadAsync("legacy"));

        Assert.Contains("predates bounded active-turn discovery", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Active_discovery_fails_closed_before_materializing_corrupt_or_oversized_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        await File.WriteAllTextAsync(Path.Combine(paths.DefaultConversationActiveTurnsPath, "corrupt.json"), "{}");
        await Assert.ThrowsAsync<FormatException>(() => turns.ListIncompleteAsync());

        File.Delete(Path.Combine(paths.DefaultConversationActiveTurnsPath, "corrupt.json"));
        await File.WriteAllBytesAsync(Path.Combine(paths.DefaultConversationActiveTurnsPath, "oversized.json"), new byte[1024 * 1024 + 1]);
        await Assert.ThrowsAsync<FormatException>(() => turns.ListIncompleteAsync());
    }

    [Fact]
    public async Task Active_discovery_rejects_count_and_aggregate_bounds_before_json_deserialization()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        for (var index = 0; index < 129; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(paths.DefaultConversationActiveTurnsPath, $"turn-{index:D3}.json"), "{}");
        }
        await Assert.ThrowsAsync<IOException>(() => turns.ListIncompleteAsync());
    }

    [Fact]
    public async Task Active_discovery_bounds_all_entries_and_fails_closed_on_interrupted_or_unrecognized_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var interruptedPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, ".turn.json.0123456789abcdef0123456789abcdef.tmp");
        await File.WriteAllTextAsync(interruptedPath, "staged");

        await Assert.ThrowsAsync<FormatException>(() => turns.ListIncompleteAsync());
        Assert.Equal("staged", await File.ReadAllTextAsync(interruptedPath));

        File.Delete(interruptedPath);
        for (var index = 0; index < 129; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(paths.DefaultConversationActiveTurnsPath, $"unexpected-{index:D3}.tmp"), "staged");
        }

        await Assert.ThrowsAsync<IOException>(() => turns.ListIncompleteAsync());
    }

    [Fact]
    public async Task Active_discovery_enforces_aggregate_bytes_from_the_opened_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        var conversation = await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync();
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        for (var index = 0; index < 9; index++)
        {
            var requestId = $"request-aggregate-{index:D2}";
            var startedAtUtc = new DateTimeOffset(2026, 8, 1, 12, index, 0, TimeSpan.Zero);
            var run = LoopRunRecord.Started(DefaultConversationTurnProtocol.CreateRunId(requestId), BuiltInLoopIds.DefaultConversation, "default-assistant", RuntimeSurfaceId.Web, LoopTrigger.HumanMessage, startedAtUtc);
            var record = DefaultConversationTurnProtocol.Admit(run, conversation, LlmMessage.User("hello"), startedAtUtc.AddSeconds(1), requestId);
            var json = JsonSerializer.Serialize(record, CreateTurnJsonOptions());
            var padding = 1024 * 1024 - System.Text.Encoding.UTF8.GetByteCount(json);
            await File.WriteAllTextAsync(Path.Combine(paths.DefaultConversationActiveTurnsPath, record.TurnId + ".json"), json + new string(' ', padding));
        }

        await Assert.ThrowsAsync<FormatException>(() => turns.ListIncompleteAsync());
    }

    [Fact]
    public async Task Active_discovery_does_not_materialize_unrelated_terminal_history()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        for (var index = 0; index < 256; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(paths.DefaultConversationTurnHistoryPath, $"terminal-{index:D3}.json"), "{}");
        }

        var record = await CreateAdmittedRecordAsync(paths, "request-bounded-history");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await turns.CreateAsync(record)).Status);

        Assert.Equal(record.TurnId, Assert.Single(await turns.ListIncompleteAsync()).TurnId);
    }

    [Fact]
    public async Task Normal_archival_and_arbitrary_loads_leave_only_the_single_active_set_lease_artifact()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        for (var index = 0; index < 140; index++)
        {
            var admitted = await CreateAdmittedRecordAsync(paths, $"request-normal-history-{index:D3}");
            var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
            var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
            Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await turns.CreateAsync(admitted)).Status);
            Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
            Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(terminal, prepared.LifecycleVersion)).Status);
            Assert.Null(await turns.LoadAsync($"missing-{index:D3}"));
        }

        Assert.Equal(140, Directory.EnumerateFiles(paths.DefaultConversationTurnHistoryPath, "*.json").Count());
        Assert.Equal(140, Directory.EnumerateFiles(paths.DefaultConversationTurnHistoryPath, "*.archive-source-proof").Count());
        Assert.Equal(280, Directory.EnumerateFiles(paths.DefaultConversationTurnHistoryPath).Count());
        Assert.Empty(Directory.EnumerateFiles(paths.DefaultConversationActiveTurnsPath, "*.json"));
        Assert.Collection(Directory.EnumerateFiles(paths.DefaultConversationActiveTurnsPath).Select(Path.GetFileName).Order(StringComparer.Ordinal), file => Assert.Equal(".active-set.lock", file));
    }

    [Fact]
    public async Task Review_resolution_archives_replays_conflicts_and_releases_active_capacity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-review-resolution", needsReview: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, terminal.TurnId + ".json");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        await File.WriteAllTextAsync(activePath, JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()) + Environment.NewLine);
        for (var index = 0; index < 127; index++)
        {
            var filler = await CreateAdmittedRecordAsync(paths, $"request-review-capacity-{index:D3}");
            await File.WriteAllTextAsync(Path.Combine(paths.DefaultConversationActiveTurnsPath, filler.TurnId + ".json"), JsonSerializer.Serialize(filler, CreateTurnJsonOptions()) + Environment.NewLine);
        }

        var resolved = terminal.ResolveReview(terminal.Transitions[^1].OccurredAtUtc.AddSeconds(1));
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(resolved, terminal.LifecycleVersion)).Status);
        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(historyPath));
        Assert.Equal(DefaultConversationTurnStoreStatus.Replay, (await turns.CreateAsync(resolved)).Status);
        Assert.Equal(DefaultConversationTurnStoreStatus.Conflict, (await turns.CreateAsync(resolved with { UserMessage = resolved.UserMessage with { Content = "changed" } })).Status);

        var replacement = await CreateAdmittedRecordAsync(paths, "request-after-review-resolution");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await turns.CreateAsync(replacement)).Status);
        Assert.Equal(128, Directory.EnumerateFiles(paths.DefaultConversationActiveTurnsPath, "*.json").Count());
    }

    [Fact]
    public async Task Store_enforces_serialized_artifact_and_aggregate_byte_limits_before_commit()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        for (var index = 0; index < 8; index++)
        {
            var record = WithSerializedSize(await CreateAdmittedRecordAsync(paths, $"request-byte-boundary-{index:D2}"), 1024 * 1024);
            Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await turns.CreateAsync(record)).Status);
        }

        var aggregateOverflow = await CreateAdmittedRecordAsync(paths, "request-aggregate-overflow");
        await Assert.ThrowsAsync<FormatException>(() => turns.CreateAsync(aggregateOverflow));
        Assert.Equal(8, Directory.EnumerateFiles(paths.DefaultConversationActiveTurnsPath, "*.json").Count());

        using var oversizeWorkspace = new TestWorkspace();
        var oversizePaths = new WorkspacePaths(oversizeWorkspace.RootPath);
        var oversizeTurns = new DefaultConversationTurnStore(oversizePaths);
        var admitted = await CreateAdmittedRecordAsync(oversizePaths, "request-artifact-oversize");
        var oversized = WithSerializedSize(admitted, 1024 * 1024 + 1);
        await Assert.ThrowsAsync<FormatException>(() => oversizeTurns.CreateAsync(oversized));
        Assert.False(Directory.Exists(oversizePaths.DefaultConversationActiveTurnsPath));

        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await oversizeTurns.CreateAsync(admitted)).Status);
        var oversizedUpdate = admitted.Advance(DefaultConversationTurnCheckpoint.RunStarted, DateTimeOffset.UtcNow, new string('x', 1024 * 1024));
        await Assert.ThrowsAsync<FormatException>(() => oversizeTurns.UpdateAsync(oversizedUpdate, admitted.LifecycleVersion));
        var unchanged = await oversizeTurns.LoadAsync(admitted.TurnId);
        Assert.NotNull(unchanged);
        Assert.Equal(admitted.LifecycleVersion, unchanged.LifecycleVersion);
        Assert.Equal(admitted.Checkpoint, unchanged.Checkpoint);
        Assert.Equal(admitted.UserMessage, unchanged.UserMessage);
    }

    [Fact]
    public async Task Archiving_update_rejects_transient_aggregate_overflow_before_mutating_active_or_history()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-archival-overflow", needsReview: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, terminal.TurnId + ".json");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        var terminalJson = JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()) + Environment.NewLine;
        await File.WriteAllTextAsync(activePath, terminalJson);
        var remainingBytes = 8 * 1024 * 1024 - Encoding.UTF8.GetByteCount(terminalJson);
        for (var index = 0; index < 7; index++)
        {
            var filler = WithSerializedSize(await CreateAdmittedRecordAsync(paths, $"request-archival-overflow-{index:D2}"), 1024 * 1024);
            await File.WriteAllTextAsync(Path.Combine(paths.DefaultConversationActiveTurnsPath, filler.TurnId + ".json"), JsonSerializer.Serialize(filler, CreateTurnJsonOptions()) + Environment.NewLine);
            remainingBytes -= 1024 * 1024;
        }

        var finalFiller = WithSerializedSize(await CreateAdmittedRecordAsync(paths, "request-archival-overflow-final"), remainingBytes);
        await File.WriteAllTextAsync(Path.Combine(paths.DefaultConversationActiveTurnsPath, finalFiller.TurnId + ".json"), JsonSerializer.Serialize(finalFiller, CreateTurnJsonOptions()) + Environment.NewLine);
        var originalActive = await File.ReadAllBytesAsync(activePath);
        var resolved = terminal.ResolveReview(terminal.Transitions[^1].OccurredAtUtc.AddSeconds(1));

        await Assert.ThrowsAsync<FormatException>(() => turns.UpdateAsync(resolved, terminal.LifecycleVersion));

        Assert.Equal(originalActive, await File.ReadAllBytesAsync(activePath));
        Assert.False(File.Exists(historyPath));
        var reloaded = await turns.LoadAsync(terminal.TurnId);
        Assert.NotNull(reloaded);
        Assert.Equal(terminal.LifecycleVersion, reloaded.LifecycleVersion);
        Assert.Equal(DefaultConversationTurnCheckpoint.Terminal, reloaded.Checkpoint);
        Assert.Null(reloaded.ReviewResolution);
        Assert.Equal(terminal.TurnId, Assert.Single(await turns.ListNeedsReviewAsync()).TurnId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Store_rejects_unknown_or_mis_cased_fields_in_active_and_history_artifacts(bool history, bool misCased)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        var record = await CreateAdmittedRecordAsync(paths, $"request-strict-json-{history}-{misCased}");
        var directory = history ? paths.DefaultConversationTurnHistoryPath : paths.DefaultConversationActiveTurnsPath;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(record, CreateTurnJsonOptions());
        json = misCased ? json.Replace("\"schemaVersion\"", "\"SchemaVersion\"", StringComparison.Ordinal) : json.Insert(json.LastIndexOf('}'), ",\"unknownField\":true");
        await File.WriteAllTextAsync(Path.Combine(directory, record.TurnId + ".json"), json);

        await Assert.ThrowsAsync<FormatException>(() => turns.LoadAsync(record.TurnId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_rejects_duplicate_properties_in_active_and_history_artifacts(bool history)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        var record = await CreateAdmittedRecordAsync(paths, $"request-duplicate-json-{history}");
        var directory = history ? paths.DefaultConversationTurnHistoryPath : paths.DefaultConversationActiveTurnsPath;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(record, CreateTurnJsonOptions());
        json = json.Insert(1, "\"schemaVersion\":2,");
        await File.WriteAllTextAsync(Path.Combine(directory, record.TurnId + ".json"), json);

        await Assert.ThrowsAsync<FormatException>(() => turns.LoadAsync(record.TurnId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_rejects_unix_fifo_artifacts_and_leases_without_blocking(bool lease)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var fifoPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, lease ? ".active-set.lock" : "malicious.json");
        Assert.Equal(0, mkfifo(fifoPath, 0x180));

        var operation = turns.ListIncompleteAsync();
        var completed = await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(operation, completed);
        var exception = await Assert.ThrowsAsync<IOException>(() => operation);
        Assert.Contains("not a regular file", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_refuses_symbolic_links_for_active_artifacts_and_leases(bool lease)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var targetPath = workspace.File("symbolic-link-target");
        await File.WriteAllTextAsync(targetPath, "do not follow");
        var linkPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, lease ? ".active-set.lock" : "malicious.json");
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        Task operation = lease ? turns.ListIncompleteAsync() : turns.LoadAsync("malicious");

        await Assert.ThrowsAsync<IOException>(() => operation);
        Assert.Equal("do not follow", await File.ReadAllTextAsync(targetPath));
    }

    [Fact]
    public async Task Concurrent_admission_at_active_capacity_allows_only_one_new_turn()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var coordination = new BlockingTurnStoreCoordination(DefaultConversationTurnStoreOperation.Create);
        var turns = new DefaultConversationTurnStore(paths, coordination);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        for (var index = 0; index < 127; index++)
        {
            var record = await CreateAdmittedRecordAsync(paths, $"request-capacity-{index:D3}");
            await File.WriteAllTextAsync(Path.Combine(paths.DefaultConversationActiveTurnsPath, record.TurnId + ".json"), JsonSerializer.Serialize(record, CreateTurnJsonOptions()));
        }

        var first = await CreateAdmittedRecordAsync(paths, "request-capacity-first");
        var second = await CreateAdmittedRecordAsync(paths, "request-capacity-second");
        var firstCreate = CreateAtCapacityAsync(turns, first);
        await coordination.WaitUntilBlockedAsync();
        var secondCreate = CreateAtCapacityAsync(turns, second);
        await Task.Yield();
        coordination.Release();
        var outcomes = await Task.WhenAll(firstCreate, secondCreate);

        Assert.Single(outcomes, outcome => outcome == DefaultConversationTurnStoreStatus.Created);
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Equal(128, Directory.EnumerateFiles(paths.DefaultConversationActiveTurnsPath, "*.json").Count());
    }

    [Fact]
    public async Task Full_active_set_replays_or_conflicts_the_requested_turn_before_rejecting_new_admission()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var record = await CreateAdmittedRecordAsync(paths, "request-full-set-target");
        await File.WriteAllTextAsync(Path.Combine(paths.DefaultConversationActiveTurnsPath, record.TurnId + ".json"), JsonSerializer.Serialize(record, CreateTurnJsonOptions()));
        for (var index = 0; index < 127; index++)
        {
            var filler = await CreateAdmittedRecordAsync(paths, $"request-full-set-{index:D3}");
            await File.WriteAllTextAsync(Path.Combine(paths.DefaultConversationActiveTurnsPath, filler.TurnId + ".json"), JsonSerializer.Serialize(filler, CreateTurnJsonOptions()));
        }

        var changedIntent = record with { UserMessage = record.UserMessage with { Content = "changed" } };

        Assert.Equal(DefaultConversationTurnStoreStatus.Replay, (await turns.CreateAsync(record)).Status);
        Assert.Equal(DefaultConversationTurnStoreStatus.Conflict, (await turns.CreateAsync(changedIntent)).Status);
        Assert.Equal(128, Directory.EnumerateFiles(paths.DefaultConversationActiveTurnsPath, "*.json").Count());
    }

    [Fact]
    public async Task Concurrent_list_and_terminal_archive_observe_one_active_set_snapshot()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var record = await CreateAdmittedRecordAsync(paths, "request-concurrent-terminal-archive");
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, record.TurnId + ".json");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, record.TurnId + ".json");
        var coordination = new BlockingTurnStoreCoordination(DefaultConversationTurnStoreOperation.Update);
        var updatingStore = new DefaultConversationTurnStore(paths, coordination);
        var listingStore = new DefaultConversationTurnStore(paths);
        var preparingStore = new DefaultConversationTurnStore(paths);
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(record)).Status);
        var prepared = CreateTerminalPreparedRecord(record, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, record.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);

        var updating = updatingStore.UpdateAsync(terminal, prepared.LifecycleVersion);
        await coordination.WaitUntilBlockedAsync();
        var listing = listingStore.ListIncompleteAsync();
        await Task.Yield();
        coordination.Release();
        await Task.WhenAll(updating, listing);

        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await updating).Status);
        Assert.Empty(await listing);
        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(historyPath));
    }

    [Fact]
    public async Task Matching_active_and_history_collisions_fail_closed_without_mutating_artifacts_across_store_operations()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        var record = await CreateAdmittedRecordAsync(paths, "request-matching-collision");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await turns.CreateAsync(record)).Status);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, record.TurnId + ".json");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, record.TurnId + ".json");
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        File.Copy(activePath, historyPath);
        var originalActive = await File.ReadAllBytesAsync(activePath);
        var originalHistory = await File.ReadAllBytesAsync(historyPath);
        var advanced = record.Advance(DefaultConversationTurnCheckpoint.RunStarted, DateTimeOffset.UtcNow, "Run started.");

        await Assert.ThrowsAsync<FormatException>(() => turns.LoadAsync(record.TurnId));
        Assert.Equal(originalActive, await File.ReadAllBytesAsync(activePath));
        Assert.Equal(originalHistory, await File.ReadAllBytesAsync(historyPath));
        await Assert.ThrowsAsync<FormatException>(() => turns.CreateAsync(record));
        Assert.Equal(originalActive, await File.ReadAllBytesAsync(activePath));
        Assert.Equal(originalHistory, await File.ReadAllBytesAsync(historyPath));
        await Assert.ThrowsAsync<FormatException>(() => turns.UpdateAsync(advanced, record.LifecycleVersion));
        Assert.Equal(originalActive, await File.ReadAllBytesAsync(activePath));
        Assert.Equal(originalHistory, await File.ReadAllBytesAsync(historyPath));
        await Assert.ThrowsAsync<FormatException>(() => turns.ListIncompleteAsync());
        Assert.Equal(originalActive, await File.ReadAllBytesAsync(activePath));
        Assert.Equal(originalHistory, await File.ReadAllBytesAsync(historyPath));
    }

    [Fact]
    public async Task Substituted_history_collisions_fail_closed_without_mutating_artifacts_across_store_operations()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var turns = new DefaultConversationTurnStore(paths);
        var record = await CreateAdmittedRecordAsync(paths, "request-substituted-collision-a");
        var substituted = await CreateAdmittedRecordAsync(paths, "request-substituted-collision-b");
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, record.TurnId + ".json");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, record.TurnId + ".json");
        await File.WriteAllTextAsync(activePath, JsonSerializer.Serialize(record, CreateTurnJsonOptions()));
        await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(substituted, CreateTurnJsonOptions()));
        var originalActive = await File.ReadAllBytesAsync(activePath);
        var originalHistory = await File.ReadAllBytesAsync(historyPath);
        var advanced = record.Advance(DefaultConversationTurnCheckpoint.RunStarted, DateTimeOffset.UtcNow, "Run started.");

        await Assert.ThrowsAsync<FormatException>(() => turns.LoadAsync(record.TurnId));
        await Assert.ThrowsAsync<FormatException>(() => turns.CreateAsync(record));
        await Assert.ThrowsAsync<FormatException>(() => turns.UpdateAsync(advanced, record.LifecycleVersion));
        await Assert.ThrowsAsync<FormatException>(() => turns.ListIncompleteAsync());
        Assert.Equal(originalActive, await File.ReadAllBytesAsync(activePath));
        Assert.Equal(originalHistory, await File.ReadAllBytesAsync(historyPath));
    }

    [Fact]
    public async Task Terminal_update_rejects_same_content_path_substitution_after_write_publication_and_preserves_the_replacement()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-terminal-identity-substitution");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var displacedPath = workspace.File("displaced-terminal.json");
        byte[]? replacementBytes = null;
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.Update, DefaultConversationTurnArchivePhase.AfterTerminalWritePublication, async _ =>
        {
            replacementBytes = await File.ReadAllBytesAsync(activePath);
            File.Move(activePath, displacedPath);
            await File.WriteAllBytesAsync(activePath, replacementBytes);
        });

        var exception = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion));

        Assert.Contains("pathname was substituted", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(replacementBytes);
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(activePath));
        Assert.True(File.Exists(displacedPath));
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(displacedPath));
        Assert.False(File.Exists(Path.Combine(paths.DefaultConversationTurnHistoryPath, admitted.TurnId + ".json")));
        Assert.False(File.Exists(Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-source-proof")));
        Assert.Empty(Directory.EnumerateFiles(paths.DefaultConversationActiveTurnsPath, "*.archive-source"));
        Assert.Empty(Directory.EnumerateFiles(paths.DefaultConversationActiveTurnsPath, "*.tmp"));
    }

    [Fact]
    public async Task Restart_scavenging_rejects_malformed_path_substitution_without_archiving_or_deleting_it()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-list-path-substitution", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, terminal.TurnId + ".json");
        var displacedPath = workspace.File("displaced-list-terminal.json");
        await File.WriteAllTextAsync(activePath, JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.List, DefaultConversationTurnArchivePhase.BeforeSourceClaim, async _ =>
        {
            File.Move(activePath, displacedPath);
            await File.WriteAllTextAsync(activePath, "{}");
        });

        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths, coordination).ListIncompleteAsync());

        Assert.Equal("{}", await File.ReadAllTextAsync(activePath));
        Assert.True(File.Exists(displacedPath));
        Assert.False(File.Exists(Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json")));
    }

    [Fact]
    public async Task Restart_scavenging_fails_closed_when_the_proved_source_disappears()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-list-source-disappeared", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, terminal.TurnId + ".json");
        var displacedPath = workspace.File("disappeared-list-terminal.json");
        await File.WriteAllTextAsync(activePath, JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.List, DefaultConversationTurnArchivePhase.BeforeSourceClaim, _ =>
        {
            File.Move(activePath, displacedPath);
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<FileNotFoundException>(() => new DefaultConversationTurnStore(paths, coordination).ListIncompleteAsync());

        Assert.True(File.Exists(displacedPath));
        Assert.False(File.Exists(activePath));
        Assert.False(File.Exists(Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json")));
    }

    [Fact]
    public async Task Terminal_update_preserves_the_claimed_source_and_stage_when_no_replace_publication_loses_a_race()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-terminal-history-race");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-history.tmp");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, admitted.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-source-proof");
        var immutableBytes = Encoding.UTF8.GetBytes("immutable external history");
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.Update, DefaultConversationTurnArchivePhase.BeforeSourceClaim, async _ =>
        {
            Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
            await File.WriteAllBytesAsync(historyPath, immutableBytes);
        });

        await Assert.ThrowsAsync<IOException>(() => new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion));

        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(pendingPath));
        Assert.True(File.Exists(pendingHistoryPath));
        Assert.Equal(immutableBytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(await File.ReadAllBytesAsync(pendingPath), await File.ReadAllBytesAsync(pendingHistoryPath));
        Assert.False(File.Exists(sourceProofPath));

        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).LoadAsync(admitted.TurnId));

        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(pendingPath));
        Assert.True(File.Exists(pendingHistoryPath));
        Assert.Equal(immutableBytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(await File.ReadAllBytesAsync(pendingPath), await File.ReadAllBytesAsync(pendingHistoryPath));
        Assert.False(File.Exists(sourceProofPath));
    }

    [Fact]
    public async Task Terminal_update_rejects_a_same_content_history_stage_substitution_before_publication()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-history-stage-substitution");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-history.tmp");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, admitted.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-source-proof");
        var displacedPath = workspace.File("displaced-history-stage.json");
        byte[]? replacementBytes = null;
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.Update, DefaultConversationTurnArchivePhase.BeforeHistoryPublication, async _ =>
        {
            replacementBytes = await File.ReadAllBytesAsync(pendingHistoryPath);
            File.Move(pendingHistoryPath, displacedPath);
            await File.WriteAllBytesAsync(pendingHistoryPath, replacementBytes);
        });

        var exception = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion));

        Assert.Contains("canonical history was substituted", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(replacementBytes);
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(activePath));
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(displacedPath));
        Assert.False(File.Exists(pendingHistoryPath));
        Assert.False(File.Exists(sourceProofPath));
        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).LoadAsync(admitted.TurnId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_update_keeps_a_complete_history_stage_recoverable_when_publication_is_interrupted(bool cancelPublication)
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, $"request-history-publication-{cancelPublication}");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-history.tmp");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, admitted.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-source-proof");
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.Update, DefaultConversationTurnArchivePhase.BeforeHistoryPublication, _ =>
        {
            if (cancelPublication)
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            }

            return Task.FromException(new IOException("Injected history publication failure."));
        });
        var operation = new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion, cancellation.Token);

        if (cancelPublication)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => operation);
        }

        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(pendingPath));
        Assert.True(File.Exists(pendingHistoryPath));
        Assert.False(File.Exists(historyPath));
        Assert.False(File.Exists(sourceProofPath));
        var stageBytes = await File.ReadAllBytesAsync(pendingHistoryPath);
        Assert.Equal(stageBytes, await File.ReadAllBytesAsync(pendingPath));

        var loaded = await new DefaultConversationTurnStore(paths).LoadAsync(admitted.TurnId);

        Assert.NotNull(loaded);
        Assert.Equal(terminal.TurnId, loaded.TurnId);
        Assert.Equal(terminal.LifecycleVersion, loaded.LifecycleVersion);
        Assert.False(File.Exists(activePath));
        Assert.False(File.Exists(pendingPath));
        Assert.False(File.Exists(pendingHistoryPath));
        Assert.Equal(stageBytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(stageBytes, await File.ReadAllBytesAsync(sourceProofPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_update_retires_its_exact_partial_history_stage_before_restoring_source(bool cancelStaging)
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, $"request-partial-history-stage-{cancelStaging}");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-history.tmp");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, admitted.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-source-proof");
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.Update, DefaultConversationTurnArchivePhase.AfterPartialHistoryStageWrite, _ =>
        {
            if (cancelStaging)
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            }

            return Task.FromException(new IOException("Injected partial history staging failure."));
        });
        var operation = new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion, cancellation.Token);

        if (cancelStaging)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => operation);
        }

        Assert.True(File.Exists(activePath));
        Assert.False(File.Exists(pendingPath));
        Assert.False(File.Exists(pendingHistoryPath));
        Assert.False(File.Exists(historyPath));
        Assert.False(File.Exists(sourceProofPath));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retirement-intent"));
        Assert.Single(await File.ReadAllBytesAsync(Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retired"))));

        Assert.Empty(await new DefaultConversationTurnStore(paths).ListIncompleteAsync());

        Assert.False(File.Exists(activePath));
        Assert.False(File.Exists(pendingPath));
        Assert.False(File.Exists(pendingHistoryPath));
        var historyBytes = await File.ReadAllBytesAsync(historyPath);
        Assert.Equal(historyBytes, await File.ReadAllBytesAsync(sourceProofPath));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retirement-intent"));
        Assert.Single(await File.ReadAllBytesAsync(Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retired"))));
    }

    [Fact]
    public async Task Sequential_incomplete_history_stage_retirements_preserve_each_attempt_and_allow_a_later_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-sequential-history-stage-retirement");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");

        var updateCoordination = new SubstitutingTurnStoreCoordination(
            DefaultConversationTurnStoreOperation.Update,
            DefaultConversationTurnArchivePhase.AfterPartialHistoryStageWrite,
            _ => Task.FromException(new IOException("Injected first incomplete history stage failure.")));
        await Assert.ThrowsAsync<IOException>(() => new DefaultConversationTurnStore(paths, updateCoordination).UpdateAsync(terminal, prepared.LifecycleVersion));
        Assert.True(File.Exists(activePath));

        var retryCoordination = new SubstitutingTurnStoreCoordination(
            DefaultConversationTurnStoreOperation.List,
            DefaultConversationTurnArchivePhase.AfterPartialHistoryStageWrite,
            _ => Task.FromException(new IOException("Injected retry incomplete history stage failure.")));
        await Assert.ThrowsAsync<IOException>(() => new DefaultConversationTurnStore(paths, retryCoordination).ListIncompleteAsync());
        Assert.True(File.Exists(activePath));
        Assert.Empty(Directory.EnumerateFiles(paths.DefaultConversationActiveTurnsPath, ".*.archive-source"));

        Assert.Equal(2, GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retirement-intent").Count);
        Assert.Equal(2, GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retired").Count);

        Assert.Empty(await new DefaultConversationTurnStore(paths).ListIncompleteAsync());
        Assert.False(File.Exists(activePath));
        Assert.Empty(await new DefaultConversationTurnStore(paths).ListIncompleteAsync());
        Assert.Equal(2, GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retirement-intent").Count);
        Assert.Equal(2, GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retired").Count);
    }

    [Fact]
    public async Task Restart_completes_a_later_source_claim_after_a_prior_complete_history_stage_retirement()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-retired-stage-prior-source-claim");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingSourcePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, admitted.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-source-proof");
        var coordination = new SubstitutingTurnStoreCoordination(
            DefaultConversationTurnStoreOperation.Update,
            DefaultConversationTurnArchivePhase.AfterPartialHistoryStageWrite,
            _ => Task.FromException(new IOException("Injected first incomplete history stage failure.")));

        await Assert.ThrowsAsync<IOException>(() => new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion));
        Assert.True(File.Exists(activePath));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retirement-intent"));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retired"));

        File.Move(activePath, pendingSourcePath);

        Assert.Empty(await new DefaultConversationTurnStore(paths).ListIncompleteAsync());
        Assert.False(File.Exists(activePath));
        Assert.False(File.Exists(pendingSourcePath));
        Assert.True(File.Exists(historyPath));
        Assert.True(File.Exists(sourceProofPath));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retirement-intent"));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retired"));
    }

    [Fact]
    public async Task Restart_fails_closed_on_an_unrecognized_history_stage_entry_after_a_prior_complete_retirement()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-unrecognized-history-stage-entry");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingSourcePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var unexpectedPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-history.unrecognized");
        var coordination = new SubstitutingTurnStoreCoordination(
            DefaultConversationTurnStoreOperation.Update,
            DefaultConversationTurnArchivePhase.AfterPartialHistoryStageWrite,
            _ => Task.FromException(new IOException("Injected incomplete history stage failure.")));

        await Assert.ThrowsAsync<IOException>(() => new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion));
        File.Move(activePath, pendingSourcePath);
        await File.WriteAllTextAsync(unexpectedPath, "evidence-like but unrecognized");

        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());

        Assert.True(File.Exists(pendingSourcePath));
        Assert.True(File.Exists(unexpectedPath));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retirement-intent"));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retired"));
    }

    [Fact]
    public async Task Restart_fails_closed_on_a_hard_linked_completed_retired_history_stage()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-hard-linked-retired-history-stage");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingSourcePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var coordination = new SubstitutingTurnStoreCoordination(
            DefaultConversationTurnStoreOperation.Update,
            DefaultConversationTurnArchivePhase.AfterPartialHistoryStageWrite,
            _ => Task.FromException(new IOException("Injected incomplete history stage failure.")));

        await Assert.ThrowsAsync<IOException>(() => new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion));
        var retiredPath = Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retired"));
        var aliasPath = workspace.File("retired-history-stage-alias");
        UnixHardLink.Create(aliasPath, retiredPath);
        File.Move(activePath, pendingSourcePath);

        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());

        Assert.True(File.Exists(pendingSourcePath));
        Assert.True(File.Exists(retiredPath));
        Assert.True(File.Exists(aliasPath));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retirement-intent"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Terminal_update_preserves_a_replacement_raced_against_identity_bound_stage_retirement(bool cancelStaging, bool sameBytes)
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, $"request-stage-retirement-race-{cancelStaging}-{sameBytes}");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingSourcePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-history.tmp");
        var displacedStagePath = workspace.File($"displaced-retirement-stage-{cancelStaging}-{sameBytes}.json");
        byte[] originalPartialBytes = [(byte)'{'];
        var replacementBytes = sameBytes ? originalPartialBytes : Encoding.UTF8.GetBytes("attacker replacement");
        var coordination = new FailingHistoryStageRetirementCoordination(async token =>
        {
            File.Move(pendingHistoryPath, displacedStagePath);
            await File.WriteAllBytesAsync(pendingHistoryPath, replacementBytes, token);
        }, cancellation, cancelStaging);
        var operation = new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion, cancellation.Token);

        if (cancelStaging)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => operation);
        }

        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(pendingSourcePath));
        Assert.False(File.Exists(pendingHistoryPath));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retirement-intent"));
        var retiredStagePath = Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retired"));
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(retiredStagePath));
        Assert.Equal(originalPartialBytes, await File.ReadAllBytesAsync(displacedStagePath));
        using (File.Open(retiredStagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
        }

        var firstRestart = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());
        var secondRestart = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());
        Assert.Contains("retirement evidence", firstRestart.Message, StringComparison.Ordinal);
        Assert.Equal(firstRestart.Message, secondRestart.Message);
        Assert.True(File.Exists(pendingSourcePath));
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(retiredStagePath));
        Assert.Equal(originalPartialBytes, await File.ReadAllBytesAsync(displacedStagePath));
    }

    [Fact]
    public async Task Terminal_update_preserves_cross_process_stage_substitution_and_restart_fails_closed()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-cross-process-stage-retirement");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingSourcePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-history.tmp");
        var displacedStagePath = workspace.File("displaced-cross-process-retirement-stage.json");
        byte[] originalPartialBytes = [(byte)'{'];
        var replacementBytes = Encoding.UTF8.GetBytes("cross-process replacement");
        var coordination = new FailingHistoryStageRetirementCoordination(async _ =>
        {
            using var process = StartSelfTest(nameof(History_stage_retirement_substitution_worker), new Dictionary<string, string>
            {
                [RetirementStageVariable] = pendingHistoryPath,
                [RetirementDisplacedVariable] = displacedStagePath,
                [RetirementReplacementVariable] = Convert.ToBase64String(replacementBytes)
            });
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"Cross-process substitution failed. stdout: {await output} stderr: {await error}");
        }, cancellation, cancelStaging: false);

        await Assert.ThrowsAsync<IOException>(() => new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion));

        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(pendingSourcePath));
        Assert.False(File.Exists(pendingHistoryPath));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retirement-intent"));
        var retiredStagePath = Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retired"));
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(retiredStagePath));
        Assert.Equal(originalPartialBytes, await File.ReadAllBytesAsync(displacedStagePath));
        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(retiredStagePath));
    }

    [Fact]
    public void History_stage_retirement_substitution_worker()
    {
        var stagePath = Environment.GetEnvironmentVariable(RetirementStageVariable);
        if (string.IsNullOrEmpty(stagePath))
        {
            return;
        }

        var displacedPath = Environment.GetEnvironmentVariable(RetirementDisplacedVariable) ?? throw new InvalidOperationException("The displaced stage path is required.");
        var replacement = Environment.GetEnvironmentVariable(RetirementReplacementVariable) ?? throw new InvalidOperationException("The replacement bytes are required.");
        File.Move(stagePath, displacedPath);
        File.WriteAllBytes(stagePath, Convert.FromBase64String(replacement));
    }

    [Fact]
    public async Task Terminal_update_preserves_a_history_stage_that_disappears_before_retirement()
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-disappeared-stage-retirement");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingSourcePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-history.tmp");
        var displacedStagePath = workspace.File("disappeared-retirement-stage.json");
        byte[] originalPartialBytes = [(byte)'{'];
        var coordination = new FailingHistoryStageRetirementCoordination(_ =>
        {
            File.Move(pendingHistoryPath, displacedStagePath);
            return Task.CompletedTask;
        }, cancellation, cancelStaging: false);

        await Assert.ThrowsAsync<IOException>(() => new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion));

        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(pendingSourcePath));
        Assert.False(File.Exists(pendingHistoryPath));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retirement-intent"));
        Assert.Equal(originalPartialBytes, await File.ReadAllBytesAsync(displacedStagePath));
        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());
        Assert.True(File.Exists(pendingSourcePath));
        Assert.Equal(originalPartialBytes, await File.ReadAllBytesAsync(displacedStagePath));
    }

    [Fact]
    public async Task Terminal_update_preserves_a_unix_special_file_substituted_before_retirement()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-special-stage-retirement");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingSourcePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-history.tmp");
        var displacedStagePath = workspace.File("displaced-special-retirement-stage.json");
        byte[] originalPartialBytes = [(byte)'{'];
        var coordination = new FailingHistoryStageRetirementCoordination(_ =>
        {
            File.Move(pendingHistoryPath, displacedStagePath);
            Assert.Equal(0, mkfifo(pendingHistoryPath, 0x180));
            return Task.CompletedTask;
        }, cancellation, cancelStaging: false);

        await Assert.ThrowsAsync<IOException>(() => new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion));

        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(pendingSourcePath));
        Assert.False(File.Exists(pendingHistoryPath));
        Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retirement-intent"));
        var retiredStagePath = Assert.Single(GetHistoryStageRetirementEvidencePaths(paths, admitted.TurnId, ".retired"));
        Assert.Contains(retiredStagePath, Directory.EnumerateFileSystemEntries(paths.DefaultConversationTurnHistoryPath), StringComparer.Ordinal);
        Assert.Equal(originalPartialBytes, await File.ReadAllBytesAsync(displacedStagePath));
        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());
        Assert.Contains(retiredStagePath, Directory.EnumerateFileSystemEntries(paths.DefaultConversationTurnHistoryPath), StringComparer.Ordinal);
        Assert.Equal(originalPartialBytes, await File.ReadAllBytesAsync(displacedStagePath));
    }

    [Fact]
    public async Task Terminal_update_preserves_a_substituted_partial_history_stage_with_its_pending_source()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-partial-history-stage-substitution");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-history.tmp");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, admitted.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-source-proof");
        var displacedPath = workspace.File("displaced-partial-history-stage.json");
        const string Replacement = "{\"schemaVersion\":";
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.Update, DefaultConversationTurnArchivePhase.AfterPartialHistoryStageWrite, async _ =>
        {
            File.Move(pendingHistoryPath, displacedPath);
            await File.WriteAllTextAsync(pendingHistoryPath, Replacement);
        });

        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion));

        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(pendingPath));
        Assert.Equal(Replacement, await File.ReadAllTextAsync(pendingHistoryPath));
        Assert.True(File.Exists(displacedPath));
        Assert.Equal(await File.ReadAllBytesAsync(pendingPath), await File.ReadAllBytesAsync(displacedPath));
        Assert.False(File.Exists(historyPath));
        Assert.False(File.Exists(sourceProofPath));

        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).LoadAsync(admitted.TurnId));

        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(pendingPath));
        Assert.Equal(Replacement, await File.ReadAllTextAsync(pendingHistoryPath));
        Assert.True(File.Exists(displacedPath));
        Assert.Equal(await File.ReadAllBytesAsync(pendingPath), await File.ReadAllBytesAsync(displacedPath));
        Assert.False(File.Exists(historyPath));
        Assert.False(File.Exists(sourceProofPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_update_keeps_the_pending_source_recoverable_when_initial_history_revalidation_fails(bool cancelRevalidation)
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, $"request-initial-history-revalidation-{cancelRevalidation}");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, admitted.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-source-proof");
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.Update, DefaultConversationTurnArchivePhase.BeforeInitialHistoryRevalidation, _ =>
        {
            if (cancelRevalidation)
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }

            return Task.FromException(new IOException("Injected initial history revalidation failure."));
        });
        var operation = new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion, cancellation.Token);

        if (cancelRevalidation)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => operation);
        }

        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(pendingPath));
        Assert.True(File.Exists(historyPath));
        Assert.False(File.Exists(sourceProofPath));
        var historyBytes = await File.ReadAllBytesAsync(historyPath);
        Assert.Equal(historyBytes, await File.ReadAllBytesAsync(pendingPath));

        var loaded = await new DefaultConversationTurnStore(paths).LoadAsync(admitted.TurnId);

        Assert.NotNull(loaded);
        Assert.Equal(terminal.TurnId, loaded.TurnId);
        Assert.Equal(terminal.LifecycleVersion, loaded.LifecycleVersion);
        Assert.False(File.Exists(activePath));
        Assert.False(File.Exists(pendingPath));
        Assert.Equal(historyBytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(historyBytes, await File.ReadAllBytesAsync(sourceProofPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_update_keeps_the_completed_proof_recoverable_when_final_history_revalidation_fails(bool cancelRevalidation)
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, $"request-final-history-revalidation-{cancelRevalidation}");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, admitted.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-source-proof");
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.Update, DefaultConversationTurnArchivePhase.BeforeFinalHistoryRevalidation, _ =>
        {
            if (cancelRevalidation)
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }

            return Task.FromException(new IOException("Injected final history revalidation failure."));
        });
        var operation = new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion, cancellation.Token);

        if (cancelRevalidation)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => operation);
        }

        Assert.False(File.Exists(activePath));
        Assert.False(File.Exists(pendingPath));
        Assert.True(File.Exists(historyPath));
        Assert.True(File.Exists(sourceProofPath));
        var historyBytes = await File.ReadAllBytesAsync(historyPath);
        Assert.Equal(historyBytes, await File.ReadAllBytesAsync(sourceProofPath));

        var loaded = await new DefaultConversationTurnStore(paths).LoadAsync(admitted.TurnId);

        Assert.NotNull(loaded);
        Assert.Equal(terminal.TurnId, loaded.TurnId);
        Assert.Equal(terminal.LifecycleVersion, loaded.LifecycleVersion);
        Assert.False(File.Exists(activePath));
        Assert.False(File.Exists(pendingPath));
        Assert.Equal(historyBytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(historyBytes, await File.ReadAllBytesAsync(sourceProofPath));
    }

    [Fact]
    public async Task Terminal_update_preserves_a_pending_source_replacement_after_history_publication()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-pending-source-substitution");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{admitted.TurnId}.json.archive-source");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, admitted.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-source-proof");
        var displacedPath = workspace.File("displaced-pending-source.json");
        byte[]? replacementBytes = null;
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.Update, DefaultConversationTurnArchivePhase.AfterHistoryPublication, async _ =>
        {
            replacementBytes = await File.ReadAllBytesAsync(pendingPath);
            File.Move(pendingPath, displacedPath);
            await File.WriteAllBytesAsync(pendingPath, replacementBytes);
        });

        var exception = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion));

        Assert.Contains("source proof was substituted", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(replacementBytes);
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(activePath));
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(historyPath));
        Assert.True(File.Exists(displacedPath));
        Assert.False(File.Exists(pendingPath));
        Assert.False(File.Exists(sourceProofPath));
        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).LoadAsync(admitted.TurnId));
    }

    [Fact]
    public async Task Terminal_update_preserves_a_source_proof_replacement_before_revalidation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var preparingStore = new DefaultConversationTurnStore(paths);
        var admitted = await CreateAdmittedRecordAsync(paths, "request-source-proof-substitution");
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await preparingStore.CreateAsync(admitted)).Status);
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview: false);
        Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await preparingStore.UpdateAsync(prepared, admitted.LifecycleVersion)).Status);
        var terminal = prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, admitted.TurnId + ".json");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, admitted.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{admitted.TurnId}.json.archive-source-proof");
        var displacedPath = workspace.File("displaced-source-proof.json");
        byte[]? replacementBytes = null;
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.Update, DefaultConversationTurnArchivePhase.AfterSourceProofPublication, async _ =>
        {
            replacementBytes = await File.ReadAllBytesAsync(sourceProofPath);
            File.Move(sourceProofPath, displacedPath);
            await File.WriteAllBytesAsync(sourceProofPath, replacementBytes);
        });

        var exception = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths, coordination).UpdateAsync(terminal, prepared.LifecycleVersion));

        Assert.Contains("source proof was substituted", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(replacementBytes);
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(activePath));
        Assert.Equal(replacementBytes, await File.ReadAllBytesAsync(historyPath));
        Assert.True(File.Exists(displacedPath));
        Assert.False(File.Exists(sourceProofPath));
        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).LoadAsync(admitted.TurnId));
    }

    [Fact]
    public async Task History_without_its_exact_source_proof_fails_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-history-missing-proof", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));

        var exception = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).LoadAsync(terminal.TurnId));

        Assert.Contains("incomplete immutable archival evidence", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(historyPath));
    }

    [Fact]
    public async Task History_with_a_byte_different_source_proof_fails_closed_without_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-history-conflicting-proof", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-source-proof");
        var historyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        byte[] proofBytes = [.. historyBytes, (byte)' '];
        await File.WriteAllBytesAsync(historyPath, historyBytes);
        await File.WriteAllBytesAsync(sourceProofPath, proofBytes);

        var exception = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).LoadAsync(terminal.TurnId));

        Assert.Contains("conflicting immutable archival evidence", exception.Message, StringComparison.Ordinal);
        Assert.Equal(historyBytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(proofBytes, await File.ReadAllBytesAsync(sourceProofPath));
    }

    [Fact]
    public async Task Restart_restores_an_interrupted_source_claim_then_completes_identity_bound_archival()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-source-claim-restart", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, terminal.TurnId + ".json");
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{terminal.TurnId}.json.archive-source");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-source-proof");
        await File.WriteAllTextAsync(activePath, JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        File.Move(activePath, pendingPath);

        Assert.Empty(await new DefaultConversationTurnStore(paths).ListIncompleteAsync());

        Assert.False(File.Exists(activePath));
        Assert.False(File.Exists(pendingPath));
        Assert.True(File.Exists(historyPath));
        Assert.Equal(await File.ReadAllBytesAsync(historyPath), await File.ReadAllBytesAsync(sourceProofPath));
    }

    [Fact]
    public async Task Restart_finishes_exact_interrupted_history_publication_without_reintroducing_the_active_source()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-history-publication-restart", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{terminal.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-history.tmp");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-source-proof");
        await File.WriteAllBytesAsync(pendingPath, bytes);
        await File.WriteAllBytesAsync(pendingHistoryPath, bytes);

        Assert.Empty(await new DefaultConversationTurnStore(paths).ListIncompleteAsync());

        Assert.False(File.Exists(pendingPath));
        Assert.False(File.Exists(pendingHistoryPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sourceProofPath));
    }

    [Theory]
    [InlineData(DefaultConversationTurnArchivePhase.BeforeInitialHistoryRevalidation, false)]
    [InlineData(DefaultConversationTurnArchivePhase.BeforeInitialHistoryRevalidation, true)]
    [InlineData(DefaultConversationTurnArchivePhase.BeforeFinalHistoryRevalidation, false)]
    [InlineData(DefaultConversationTurnArchivePhase.BeforeFinalHistoryRevalidation, true)]
    public async Task Restart_keeps_post_publication_evidence_recoverable_when_history_revalidation_fails(DefaultConversationTurnArchivePhase phase, bool cancelRevalidation)
    {
        using var workspace = new TestWorkspace();
        using var cancellation = new CancellationTokenSource();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, $"request-restart-revalidation-{phase}-{cancelRevalidation}", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, terminal.TurnId + ".json");
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{terminal.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-history.tmp");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-source-proof");
        await File.WriteAllBytesAsync(pendingPath, bytes);
        await File.WriteAllBytesAsync(pendingHistoryPath, bytes);
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.List, phase, _ =>
        {
            if (cancelRevalidation)
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }

            return Task.FromException(new IOException("Injected restart history revalidation failure."));
        });
        var operation = new DefaultConversationTurnStore(paths, coordination).ListIncompleteAsync(cancellation.Token);

        if (cancelRevalidation)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => operation);
        }

        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(historyPath));
        Assert.False(File.Exists(pendingHistoryPath));
        Assert.Equal(phase == DefaultConversationTurnArchivePhase.BeforeInitialHistoryRevalidation, File.Exists(pendingPath));
        Assert.Equal(phase == DefaultConversationTurnArchivePhase.BeforeFinalHistoryRevalidation, File.Exists(sourceProofPath));

        var loaded = await new DefaultConversationTurnStore(paths).LoadAsync(terminal.TurnId);

        Assert.NotNull(loaded);
        Assert.Equal(terminal.TurnId, loaded.TurnId);
        Assert.False(File.Exists(activePath));
        Assert.False(File.Exists(pendingPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sourceProofPath));
    }

    [Fact]
    public async Task Restart_rejects_a_same_content_history_stage_substitution_before_publication()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-restart-history-stage-substitution", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, terminal.TurnId + ".json");
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{terminal.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-history.tmp");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-source-proof");
        var displacedPath = workspace.File("displaced-restart-history-stage.json");
        await File.WriteAllBytesAsync(pendingPath, bytes);
        await File.WriteAllBytesAsync(pendingHistoryPath, bytes);
        var coordination = new SubstitutingTurnStoreCoordination(DefaultConversationTurnStoreOperation.List, DefaultConversationTurnArchivePhase.BeforeHistoryPublication, async _ =>
        {
            var replacementBytes = await File.ReadAllBytesAsync(pendingHistoryPath);
            File.Move(pendingHistoryPath, displacedPath);
            await File.WriteAllBytesAsync(pendingHistoryPath, replacementBytes);
        });

        var exception = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths, coordination).ListIncompleteAsync());

        Assert.Contains("canonical history was substituted", exception.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(activePath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(displacedPath));
        Assert.False(File.Exists(pendingPath));
        Assert.False(File.Exists(pendingHistoryPath));
        Assert.False(File.Exists(sourceProofPath));
        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).LoadAsync(terminal.TurnId));
    }

    [Fact]
    public async Task Restart_accepts_an_exact_completed_history_and_source_proof_boundary()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-source-proof-boundary-restart", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-source-proof");
        await File.WriteAllBytesAsync(historyPath, bytes);
        await File.WriteAllBytesAsync(sourceProofPath, bytes);

        var loaded = await new DefaultConversationTurnStore(paths).LoadAsync(terminal.TurnId);

        Assert.NotNull(loaded);
        Assert.Equal(terminal.TurnId, loaded.TurnId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sourceProofPath));
    }

    [Fact]
    public async Task Restart_rejects_duplicate_pending_and_completed_source_proof_states()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-duplicate-proof-restart", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{terminal.TurnId}.json.archive-source");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-source-proof");
        await File.WriteAllBytesAsync(pendingPath, bytes);
        await File.WriteAllBytesAsync(historyPath, bytes);
        await File.WriteAllBytesAsync(sourceProofPath, bytes);

        var exception = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());

        Assert.Contains("duplicate interrupted archival evidence", exception.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(pendingPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sourceProofPath));
    }

    [Fact]
    public async Task Restart_fails_closed_and_preserves_a_partial_history_stage_with_its_exact_claimed_source()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-partial-history-restart", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{terminal.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-history.tmp");
        await File.WriteAllBytesAsync(pendingPath, bytes);
        await File.WriteAllTextAsync(pendingHistoryPath, "{\"schemaVersion\":");

        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());

        Assert.Equal(bytes, await File.ReadAllBytesAsync(pendingPath));
        Assert.Equal("{\"schemaVersion\":", await File.ReadAllTextAsync(pendingHistoryPath));
    }

    [Fact]
    public async Task Restart_rejects_conflicting_interrupted_history_and_preserves_both_artifacts()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-conflicting-publication-restart", needsReview: false);
        var conflicting = await CreateTerminalRecordAsync(paths, "request-conflicting-publication-other", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        var pendingPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{terminal.TurnId}.json.archive-source");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        await File.WriteAllTextAsync(pendingPath, JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(conflicting, CreateTurnJsonOptions()));
        var pendingBytes = await File.ReadAllBytesAsync(pendingPath);
        var historyBytes = await File.ReadAllBytesAsync(historyPath);

        await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());

        Assert.Equal(pendingBytes, await File.ReadAllBytesAsync(pendingPath));
        Assert.Equal(historyBytes, await File.ReadAllBytesAsync(historyPath));
    }

    [Fact]
    public async Task Restart_archives_a_terminal_write_left_active_but_retains_an_unresolved_needs_review_turn()
    {
        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var record = await CreateTerminalRecordAsync(paths, "request-terminal-restart", needsReview: false);
            var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, record.TurnId + ".json");
            var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, record.TurnId + ".json");
            Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
            await File.WriteAllTextAsync(activePath, JsonSerializer.Serialize(record, CreateTurnJsonOptions()));

            var restarted = new DefaultConversationTurnStore(paths);
            Assert.Empty(await restarted.ListIncompleteAsync());
            Assert.False(File.Exists(activePath));
            Assert.True(File.Exists(historyPath));
            Assert.Equal(DefaultConversationTurnCheckpoint.Terminal, (await restarted.LoadAsync(record.TurnId))!.Checkpoint);
        }

        using (var workspace = new TestWorkspace())
        {
            var paths = new WorkspacePaths(workspace.RootPath);
            var record = await CreateTerminalRecordAsync(paths, "request-review-restart", needsReview: true);
            var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, record.TurnId + ".json");
            var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, record.TurnId + ".json");
            Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
            await File.WriteAllTextAsync(activePath, JsonSerializer.Serialize(record, CreateTurnJsonOptions()));

            var restarted = new DefaultConversationTurnStore(paths);
            Assert.Single(await restarted.ListNeedsReviewAsync());
            Assert.True(File.Exists(activePath));
            Assert.False(File.Exists(historyPath));
        }
    }

    private static async Task<DefaultConversationTurnRecord> CreateAdmittedRecordAsync(WorkspacePaths paths, string requestId)
    {
        var conversation = await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var run = LoopRunRecord.Started(DefaultConversationTurnProtocol.CreateRunId(requestId), BuiltInLoopIds.DefaultConversation, "default-assistant", RuntimeSurfaceId.Web, LoopTrigger.HumanMessage, now);
        return DefaultConversationTurnProtocol.Admit(run, conversation, LlmMessage.User("hello"), now.AddSeconds(1), requestId);
    }

    private static async Task<DefaultConversationTurnStoreStatus?> CreateAtCapacityAsync(DefaultConversationTurnStore turns, DefaultConversationTurnRecord record)
    {
        try
        {
            return (await turns.CreateAsync(record)).Status;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<DefaultConversationTurnRecord> CreateTerminalRecordAsync(WorkspacePaths paths, string requestId, bool needsReview)
    {
        var admitted = await CreateAdmittedRecordAsync(paths, requestId);
        return CreateTerminalRecord(admitted, needsReview);
    }

    private static DefaultConversationTurnRecord CreateTerminalRecord(DefaultConversationTurnRecord admitted, bool needsReview)
    {
        var prepared = CreateTerminalPreparedRecord(admitted, needsReview);
        return prepared.Advance(DefaultConversationTurnCheckpoint.Terminal, prepared.Transitions[^1].OccurredAtUtc, "Terminal evidence.", run: prepared.Run, runProjectionSynchronized: true);
    }

    private static DefaultConversationTurnRecord CreateTerminalPreparedRecord(DefaultConversationTurnRecord admitted, bool needsReview)
    {
        var terminalTime = admitted.Transitions[^1].OccurredAtUtc.AddSeconds(1);
        const string Detail = "Terminal evidence.";
        var run = needsReview ? admitted.Run.NeedsReview(terminalTime, Detail) : admitted.Run.Fail(terminalTime, Detail);
        return admitted.Advance(DefaultConversationTurnCheckpoint.TerminalPrepared, terminalTime, Detail, run: run, reviewDetail: needsReview ? Detail : null);
    }

    private static DefaultConversationTurnRecord WithSerializedSize(DefaultConversationTurnRecord record, int targetBytes)
    {
        var empty = record with { UserMessage = record.UserMessage with { Content = string.Empty } };
        var fixedBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(empty, CreateTurnJsonOptions()) + Environment.NewLine);
        var contentLength = targetBytes - fixedBytes;
        Assert.True(contentLength > 0);
        var candidate = record with { UserMessage = record.UserMessage with { Content = new string('x', contentLength) } };
        Assert.Equal(targetBytes, Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(candidate, CreateTurnJsonOptions()) + Environment.NewLine));
        return candidate;
    }

    private static JsonSerializerOptions CreateTurnJsonOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) }
        };
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

    private static IReadOnlyList<string> GetHistoryStageRetirementEvidencePaths(WorkspacePaths paths, string turnId, string suffix)
    {
        return Directory.EnumerateFileSystemEntries(paths.DefaultConversationTurnHistoryPath, $".{turnId}.json.archive-history.*{suffix}", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int mkfifo(string path, int mode);

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

        public Exception? Failure { get; init; }

        public async Task<LlmInferenceResponse> GenerateAsync(LlmInferenceRequest request, Func<string, CancellationToken, Task>? responseChunkHandler = null, CancellationToken cancellationToken = default)
        {
            GenerateCount++;
            if (Failure is not null)
            {
                throw Failure;
            }

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

    private sealed class FileBlockingTurnStoreCoordination(string readyPath, string releasePath) : IDefaultConversationTurnStoreCoordination
    {
        public async Task BeforeActiveSetOperationAsync(DefaultConversationTurnStoreOperation operation, CancellationToken cancellationToken = default)
        {
            await File.WriteAllTextAsync(readyPath, operation.ToString(), cancellationToken);
            while (!File.Exists(releasePath))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken);
            }
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
