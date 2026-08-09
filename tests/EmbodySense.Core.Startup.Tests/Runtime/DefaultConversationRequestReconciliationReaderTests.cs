using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed class DefaultConversationRequestReconciliationReaderTests
{
    private const string RequestId = "chat-11111111-1111-4111-8111-111111111111";
    private const string Message = "do this exactly once";

    [Fact]
    public async Task ReadAsync_returns_not_found_without_creating_turn_evidence()
    {
        using var workspace = new TestWorkspace();

        var result = await new DefaultConversationRequestReconciliationReader(workspace.RootPath).ReadAsync(RequestId, Message);

        Assert.Equal("not-found", result.Status);
        Assert.True(result.RetrySameRequest);
        Assert.False(result.ReleaseRequestIdentity);
        Assert.False(Directory.Exists(new WorkspacePaths(workspace.RootPath).DefaultConversationTurnsPath));
    }

    [Fact]
    public async Task ReadAsync_rejects_a_request_identity_reused_for_different_canonical_content()
    {
        using var workspace = new TestWorkspace();
        _ = await CreateAsync(workspace, DefaultConversationTurnCheckpoint.Admitted);

        var result = await new DefaultConversationRequestReconciliationReader(workspace.RootPath).ReadAsync(RequestId, "different content");

        Assert.Equal("conflict", result.Status);
        Assert.False(result.RetrySameRequest);
        Assert.False(result.ReleaseRequestIdentity);
    }

    [Fact]
    public async Task ReadAsync_reconciles_a_conclusive_pre_dispatch_failure_and_releases_the_identity()
    {
        using var workspace = new TestWorkspace();
        var record = await CreateAsync(workspace, DefaultConversationTurnCheckpoint.ProviderDispatchPrepared);

        var result = await new DefaultConversationRequestReconciliationReader(workspace.RootPath).ReadAsync(RequestId, Message);
        var recovered = await new DefaultConversationTurnStore(new WorkspacePaths(workspace.RootPath)).LoadAsync(record.TurnId);

        Assert.Equal("rejected", result.Status);
        Assert.False(result.RetrySameRequest);
        Assert.True(result.ReleaseRequestIdentity);
        Assert.NotNull(recovered);
        Assert.Equal(DefaultConversationProviderOutcome.DefinitelyNotStarted, recovered.ProviderOutcome);
        Assert.Equal(LoopRunStatus.Failed, recovered.Run.Status);
    }

    [Fact]
    public async Task ReadAsync_parks_an_outcome_unknown_attempt_for_review_without_redispatch()
    {
        using var workspace = new TestWorkspace();
        var record = await CreateAsync(workspace, DefaultConversationTurnCheckpoint.ProviderDispatchStarted);

        var result = await new DefaultConversationRequestReconciliationReader(workspace.RootPath).ReadAsync(RequestId, Message);
        var recovered = await new DefaultConversationTurnStore(new WorkspacePaths(workspace.RootPath)).LoadAsync(record.TurnId);

        Assert.Equal("needs-review", result.Status);
        Assert.False(result.RetrySameRequest);
        Assert.False(result.ReleaseRequestIdentity);
        Assert.NotNull(recovered);
        Assert.Equal(DefaultConversationProviderOutcome.OutcomeUnknown, recovered.ProviderOutcome);
        Assert.Equal(LoopRunStatus.NeedsReview, recovered.Run.Status);
        Assert.Null(recovered.AssistantMessage);
    }

    [Fact]
    public async Task ReadAsync_publishes_an_observed_outcome_once_and_returns_completed()
    {
        using var workspace = new TestWorkspace();
        var record = await CreateAsync(workspace, DefaultConversationTurnCheckpoint.ProviderOutcomeObserved);

        var result = await new DefaultConversationRequestReconciliationReader(workspace.RootPath).ReadAsync(RequestId, Message);
        var transcript = await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).LoadCurrentConversationAsync();

        Assert.Equal("completed", result.Status);
        Assert.False(result.RetrySameRequest);
        Assert.True(result.ReleaseRequestIdentity);
        Assert.Collection(
            transcript,
            message => Assert.Equal((LlmMessageRole.User, Message), (message.Role, message.Content)),
            message => Assert.Equal((LlmMessageRole.Assistant, "observed answer"), (message.Role, message.Content)));
        Assert.Equal(2, transcript.Select(message => (message.Role, message.Content)).Distinct().Count());
        Assert.Equal(record.TurnId, DefaultConversationTurnProtocol.CreateTurnId(RequestId));
    }

    [Theory]
    [InlineData(" request", Message)]
    [InlineData(RequestId, " message ")]
    public async Task ReadAsync_rejects_noncanonical_input(string requestId, string message)
    {
        using var workspace = new TestWorkspace();

        await Assert.ThrowsAsync<ArgumentException>(() => new DefaultConversationRequestReconciliationReader(workspace.RootPath).ReadAsync(requestId, message));
    }

    private static async Task<DefaultConversationTurnRecord> CreateAsync(TestWorkspace workspace, DefaultConversationTurnCheckpoint checkpoint)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var memory = new ConversationMemoryStore(paths);
        var snapshot = await memory.LoadCurrentConversationSnapshotAsync();
        var startedAtUtc = DateTimeOffset.UtcNow;
        var run = LoopRunRecord.Started(DefaultConversationTurnProtocol.CreateRunId(RequestId), BuiltInLoopIds.DefaultConversation, "default-assistant", RuntimeSurfaceId.Web, LoopTrigger.HumanMessage, startedAtUtc);
        var record = DefaultConversationTurnProtocol.Admit(run, snapshot, LlmMessage.User(Message), startedAtUtc, RequestId, TestCapabilityAdmissionFactory.Create(LoopDefinition.CreateDefaultConversation().CapabilityRequirements, startedAtUtc));
        foreach (var next in OperationalCheckpoints().TakeWhile(next => next <= checkpoint))
        {
            record = Advance(record, next);
        }

        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await new DefaultConversationTurnStore(paths).CreateAsync(record)).Status);
        if (checkpoint >= DefaultConversationTurnCheckpoint.UserPublished)
        {
            var publication = new ConversationMessagePublication(record.UserMessage.MessageId, record.UserPublicationId, record.UserMessage.ToLlmMessage());
            Assert.Equal(ConversationPublicationAppendStatus.Appended, (await memory.TryPublishMessageAsync(record.ConversationId, record.ConversationVersion, record.BaseTranscript, publication)).Status);
        }

        return record;
    }

    private static DefaultConversationTurnRecord Advance(DefaultConversationTurnRecord record, DefaultConversationTurnCheckpoint checkpoint)
    {
        return checkpoint switch
        {
            DefaultConversationTurnCheckpoint.ProviderDispatchStarted => record.Advance(checkpoint, DateTimeOffset.UtcNow, checkpoint.ToString(), providerOutcome: DefaultConversationProviderOutcome.OutcomeUnknown),
            DefaultConversationTurnCheckpoint.ProviderOutcomeObserved => record.Advance(
                checkpoint,
                DateTimeOffset.UtcNow,
                checkpoint.ToString(),
                providerOutcome: DefaultConversationProviderOutcome.Observed,
                assistantMessage: new DefaultConversationTurnMessage(record.TurnId + ":message:assistant", LlmMessageRole.Assistant, "observed answer"),
                providerResponseId: "provider-response-1"),
            _ => record.Advance(checkpoint, DateTimeOffset.UtcNow, checkpoint.ToString())
        };
    }

    private static IReadOnlyList<DefaultConversationTurnCheckpoint> OperationalCheckpoints()
    {
        return
        [
            DefaultConversationTurnCheckpoint.RunStarted,
            DefaultConversationTurnCheckpoint.UserMessageAccepted,
            DefaultConversationTurnCheckpoint.UserPublicationPrepared,
            DefaultConversationTurnCheckpoint.UserPublished,
            DefaultConversationTurnCheckpoint.ProviderDispatchPrepared,
            DefaultConversationTurnCheckpoint.ProviderDispatchStarted,
            DefaultConversationTurnCheckpoint.ProviderOutcomeObserved
        ];
    }
}
