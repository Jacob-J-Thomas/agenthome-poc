using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Loops.Models;
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

public sealed class ConversationTranscriptReaderTests
{
    [Fact]
    public async Task ReadCurrentAsync_returns_the_canonical_durable_transcript_without_an_inference_runtime()
    {
        using var workspace = new TestWorkspace();
        var store = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));
        await store.AppendMessageAsync(LlmMessage.User("durable prompt"));
        await store.AppendMessageAsync(LlmMessage.Assistant("durable answer"));

        var transcript = await new ConversationTranscriptReader().ReadCurrentAsync(workspace.RootPath);

        Assert.Collection(
            transcript,
            message =>
            {
                Assert.Equal("User", message.Role);
                Assert.Equal("durable prompt", message.Content);
            },
            message =>
            {
                Assert.Equal("Assistant", message.Role);
                Assert.Equal("durable answer", message.Content);
            });
    }

    [Fact]
    public async Task ReadCurrentAsync_waits_for_the_workspace_turn_lease_before_reading()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);
        await store.AppendMessageAsync(LlmMessage.User("complete turn"));
        using var activeTurn = await new FileConversationWorkspaceLease(paths).AcquireAsync();

        var hydration = new ConversationTranscriptReader().ReadCurrentAsync(workspace.RootPath);
        await Task.Delay(100);

        Assert.False(hydration.IsCompleted);
        activeTurn.Dispose();
        var transcript = await hydration.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("complete turn", Assert.Single(transcript).Content);
    }

    [Fact]
    public async Task ReadCurrentAsync_repairs_observed_output_once_through_the_shared_cli_and_web_hydration_seam()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var memory = new ConversationMemoryStore(paths);
        var turns = new DefaultConversationTurnStore(paths);
        var runs = new LoopRunStore(paths);
        var snapshot = await memory.LoadCurrentConversationSnapshotAsync();
        const string RequestId = "request-reader-recovery";
        var run = LoopRunRecord.Started(DefaultConversationTurnProtocol.CreateRunId(RequestId), BuiltInLoopIds.DefaultConversation, "default-assistant", RuntimeSurfaceId.Web, LoopTrigger.HumanMessage, DateTimeOffset.UtcNow);
        var admittedAtUtc = DateTimeOffset.UtcNow;
        var record = DefaultConversationTurnProtocol.Admit(run, snapshot, LlmMessage.User("durable prompt"), admittedAtUtc, RequestId, TestCapabilityAdmissionFactory.Create(LoopDefinition.CreateDefaultConversation().CapabilityRequirements, admittedAtUtc));
        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await turns.CreateAsync(record)).Status);
        await runs.SaveAsync(run);

        async Task AdvanceAsync(
            DefaultConversationTurnCheckpoint checkpoint,
            DefaultConversationProviderOutcome? outcome = null,
            DefaultConversationTurnMessage? assistant = null)
        {
            var next = record.Advance(checkpoint, DateTimeOffset.UtcNow, checkpoint.ToString(), providerOutcome: outcome, assistantMessage: assistant);
            Assert.Equal(DefaultConversationTurnStoreStatus.Updated, (await turns.UpdateAsync(next, record.LifecycleVersion)).Status);
            record = next;
        }

        await AdvanceAsync(DefaultConversationTurnCheckpoint.RunStarted);
        await AdvanceAsync(DefaultConversationTurnCheckpoint.UserMessageAccepted);
        await AdvanceAsync(DefaultConversationTurnCheckpoint.UserPublicationPrepared);
        var userPublication = await memory.TryPublishMessageAsync(
            record.ConversationId,
            record.ConversationVersion,
            record.BaseTranscript,
            new ConversationMessagePublication(record.UserMessage.MessageId, record.UserPublicationId, record.UserMessage.ToLlmMessage()));
        Assert.Equal(ConversationPublicationAppendStatus.Appended, userPublication.Status);
        await AdvanceAsync(DefaultConversationTurnCheckpoint.UserPublished);
        await AdvanceAsync(DefaultConversationTurnCheckpoint.ProviderDispatchPrepared);
        await AdvanceAsync(DefaultConversationTurnCheckpoint.ProviderDispatchStarted, DefaultConversationProviderOutcome.OutcomeUnknown);
        var assistant = new DefaultConversationTurnMessage(record.TurnId + ":message:assistant", LlmMessageRole.Assistant, "durable answer");
        await AdvanceAsync(DefaultConversationTurnCheckpoint.ProviderOutcomeObserved, DefaultConversationProviderOutcome.Observed, assistant);

        var cliProjection = await new ConversationTranscriptReader().ReadCurrentAsync(workspace.RootPath);
        var webProjection = await new ConversationTranscriptReader().ReadCurrentAsync(workspace.RootPath);
        var recovered = await turns.LoadAsync(record.TurnId);

        Assert.Equal(cliProjection, webProjection);
        Assert.Collection(
            cliProjection,
            message => Assert.Equal(("User", "durable prompt"), (message.Role, message.Content)),
            message => Assert.Equal(("Assistant", "durable answer"), (message.Role, message.Content)));
        Assert.NotNull(recovered);
        Assert.Equal(DefaultConversationTurnCheckpoint.Terminal, recovered.Checkpoint);
        Assert.Equal(LoopRunStatus.Completed, recovered.Run.Status);
        Assert.Equal(2, (await memory.LoadCurrentConversationAsync()).Count);
    }

    [Fact]
    public async Task ReadCurrentAsync_rejects_a_missing_workspace_root()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new ConversationTranscriptReader().ReadCurrentAsync(" "));
    }
}
