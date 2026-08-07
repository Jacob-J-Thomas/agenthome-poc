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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Restart_finalizes_a_successful_empty_provider_completion_as_observed_failure_without_abandonment_or_redispatch(string output)
    {
        using var workspace = new TestWorkspace();
        var client = new RecordingInferenceClient(output);
        var fixture = CreateFixture(workspace, new InterruptingFailpoint(DefaultConversationTurnBoundary.ProviderOutcomeObserved), client);
        const string RequestId = "empty-provider-success-restart";

        await Assert.ThrowsAsync<DefaultConversationTurnInterruptedException>(() => fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId)));
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
        Assert.Contains("no usable assistant output", recovered.Run.FailureDetail, StringComparison.Ordinal);
        Assert.False(DefaultConversationTurnProtocol.CanAbandonReview(recovered));
        Assert.Empty(await fixture.Turns.ListNeedsReviewAsync());
        Assert.Equal(1, client.GenerateCount);
        Assert.Equal(0, client.QuarantineCount);

        var replay = await fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId));
        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, replay.Status);
        Assert.Equal(1, client.GenerateCount);
        Assert.Equal(0, client.QuarantineCount);
        Assert.Collection(await fixture.Memory.LoadCurrentConversationAsync(), message => Assert.Equal((LlmMessageRole.User, "hello"), (message.Role, message.Content)));
    }

    [Fact]
    public async Task Restart_finalizes_an_empty_observed_audit_failure_as_conclusive_failure_without_abandonment_or_redispatch()
    {
        using var workspace = new TestWorkspace();
        var response = new LlmInferenceResponse(string.Empty, LlmInferenceSurface.OpenAiCodex, ProviderResponseId: "provider-empty-audit-restart");
        var client = new RecordingInferenceClient("unused")
        {
            Failure = new LlmInferenceObservedResponseException("completion audit failed after provider success", response, new IOException("audit unavailable"))
        };
        var fixture = CreateFixture(workspace, new InterruptingFailpoint(DefaultConversationTurnBoundary.ProviderOutcomeObserved), client);
        const string RequestId = "empty-observed-audit-restart";

        await Assert.ThrowsAsync<DefaultConversationTurnInterruptedException>(() => fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId)));
        var interrupted = fixture.Failpoint!.InterruptedRecord;
        Assert.NotNull(interrupted);
        Assert.Equal(DefaultConversationProviderOutcome.ObservedFailure, interrupted.ProviderOutcome);
        Assert.Equal("provider-empty-audit-restart", interrupted.ProviderResponseId);
        Assert.Null(interrupted.AssistantMessage);

        var result = Assert.Single((await fixture.Recovery.RecoverAsync()).Results);
        var recovered = await fixture.Turns.LoadAsync(result.TurnId);
        Assert.NotNull(recovered);
        Assert.Equal(DefaultConversationTurnRecoveryClassification.ProviderOutcomeObserved, result.Classification);
        Assert.Equal(DefaultConversationTurnCheckpoint.Terminal, recovered.Checkpoint);
        Assert.Equal(LoopRunStatus.Failed, recovered.Run.Status);
        Assert.Contains("no usable assistant output", recovered.Run.FailureDetail, StringComparison.Ordinal);
        Assert.False(DefaultConversationTurnProtocol.CanAbandonReview(recovered));
        Assert.Empty(await fixture.Turns.ListNeedsReviewAsync());
        Assert.Equal(1, client.GenerateCount);
        Assert.Equal(0, client.QuarantineCount);

        var replay = await fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: RequestId));
        Assert.Equal(DefaultConversationLoopTurnStatus.Failed, replay.Status);
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

        var artifactPath = new WorkspacePaths(workspace.RootPath).DefaultConversationTurnsPath + Path.DirectorySeparatorChar + recovered.TurnId + ".json";
        var retainedArtifact = await File.ReadAllTextAsync(artifactPath);
        var reviewService = new DefaultConversationTurnReviewService(fixture.Turns, client, new FileConversationWorkspaceLease(new WorkspacePaths(workspace.RootPath)));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => reviewService.ResolveAsync(recovered.TurnId));

        Assert.Contains(nameof(DefaultConversationTurnReviewClassification.ObservedWithAuditFailure), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.QuarantineCount);
        Assert.Equal(retainedArtifact, await File.ReadAllTextAsync(artifactPath));
        var reread = await fixture.Turns.LoadAsync(recovered.TurnId);
        Assert.NotNull(reread);
        Assert.Equal(recovered.LifecycleVersion, reread.LifecycleVersion);
        Assert.Equal(recovered.ProviderOutcome, reread.ProviderOutcome);
        Assert.Equal(recovered.AssistantMessage, reread.AssistantMessage);
        Assert.Equal(recovered.ReviewDetail, reread.ReviewDetail);
        Assert.Null(reread.ReviewResolution);
        Assert.Single(await fixture.Turns.ListNeedsReviewAsync());
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

        var paths = new WorkspacePaths(workspace.RootPath);
        var artifactPath = Path.Combine(paths.DefaultConversationTurnsPath, recovered.TurnId + ".json");
        var retainedArtifact = await File.ReadAllTextAsync(artifactPath);
        var reviewService = new DefaultConversationTurnReviewService(fixture.Turns, fixture.Client, new FileConversationWorkspaceLease(paths));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => reviewService.ResolveAsync(recovered.TurnId));

        Assert.Contains(nameof(DefaultConversationTurnReviewClassification.TranscriptConflict), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Client.QuarantineCount);
        Assert.Equal(retainedArtifact, await File.ReadAllTextAsync(artifactPath));
        var reread = await fixture.Turns.LoadAsync(recovered.TurnId);
        Assert.NotNull(reread);
        Assert.Equal(recovered.LifecycleVersion, reread.LifecycleVersion);
        Assert.Equal(recovered.ProviderOutcome, reread.ProviderOutcome);
        Assert.Equal(recovered.UserMessage, reread.UserMessage);
        Assert.Equal(recovered.ReviewDetail, reread.ReviewDetail);
        Assert.Null(reread.ReviewResolution);
        Assert.Single(await fixture.Turns.ListNeedsReviewAsync());

        var restartedRecovery = new DefaultConversationTurnRecoveryService(fixture.Turns, fixture.Memory, new LoopRunStore(paths), new FileConversationWorkspaceLease(paths));
        Assert.Empty((await restartedRecovery.RecoverAsync()).Results);
        Assert.Single(await fixture.Turns.ListNeedsReviewAsync());
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
    public async Task Provider_dispatch_transcript_conflict_stays_blocked_without_quarantine_or_abandonment()
    {
        using var workspace = new TestWorkspace();
        var fixture = CreateFixture(workspace, new InterruptingFailpoint(DefaultConversationTurnBoundary.ProviderDispatchStarted));

        await Assert.ThrowsAsync<DefaultConversationTurnInterruptedException>(() => fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("hello", requestId: "dispatch-conflict")));
        await fixture.Memory.AppendMessageAsync(LlmMessage.User("owner edit"));

        var recovered = Assert.Single((await fixture.Recovery.RecoverAsync()).Results);
        var record = await fixture.Turns.LoadAsync(recovered.TurnId);
        Assert.NotNull(record);
        Assert.Equal(DefaultConversationTurnRecoveryClassification.Conflict, recovered.Classification);
        Assert.Equal(DefaultConversationProviderOutcome.OutcomeUnknown, record.ProviderOutcome);
        Assert.Equal(DefaultConversationTurnReviewCause.TranscriptConflict, record.ReviewCause);
        Assert.Equal(DefaultConversationTurnReviewClassification.TranscriptConflict, DefaultConversationTurnProtocol.GetReviewClassification(record));
        var version = record.LifecycleVersion;
        var paths = new WorkspacePaths(workspace.RootPath);
        var reviewService = new DefaultConversationTurnReviewService(fixture.Turns, fixture.Client, new FileConversationWorkspaceLease(paths));

        var forgedAbandonment = (record with { ReviewCause = DefaultConversationTurnReviewCause.OutcomeUnknown }).ResolveReview(DateTimeOffset.UtcNow);
        Assert.Equal(DefaultConversationTurnStoreStatus.Conflict, (await fixture.Turns.UpdateAsync(forgedAbandonment, record.LifecycleVersion)).Status);
        var afterForgedWrite = await fixture.Turns.LoadAsync(record.TurnId);
        Assert.NotNull(afterForgedWrite);
        Assert.Equal(DefaultConversationTurnReviewCause.TranscriptConflict, afterForgedWrite.ReviewCause);
        Assert.Equal(version, afterForgedWrite.LifecycleVersion);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reviewService.ResolveAsync(record.TurnId));
        Assert.Equal(0, fixture.Client.QuarantineCount);
        var blocked = await fixture.Runner.RunTurnAsync(new DefaultConversationLoopTurnRequest("must remain blocked"));
        Assert.Equal(DefaultConversationLoopTurnStatus.NeedsReview, blocked.Status);
        Assert.DoesNotContain("/review resolve", blocked.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Client.GenerateCount);
        var reread = await fixture.Turns.LoadAsync(record.TurnId);
        Assert.NotNull(reread);
        Assert.Equal(version, reread.LifecycleVersion);
        Assert.Null(reread.ReviewResolution);
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

        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task> providerRequestStarting)
        {
            await providerRequestStarting(CancellationToken.None);
            return await GenerateAsync(request, responseChunkHandler, cancellationToken);
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
