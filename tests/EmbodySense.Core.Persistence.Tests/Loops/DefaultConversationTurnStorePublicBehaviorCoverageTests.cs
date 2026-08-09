using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class DefaultConversationTurnStorePublicBehaviorCoverageTests
{
    [Fact]
    public async Task Update_rejects_a_non_positive_lifecycle_version_without_initializing_the_workspace()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var record = await CreateAdmittedRecordAsync(paths, "request-invalid-lifecycle-version");

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new DefaultConversationTurnStore(paths).UpdateAsync(record, 0));

        Assert.Equal("expectedLifecycleVersion", exception.ParamName);
        Assert.False(Directory.Exists(paths.DefaultConversationTurnsPath));
    }

    [Fact]
    public async Task Update_returns_a_null_current_conflict_when_no_active_or_historical_turn_exists()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var record = await CreateAdmittedRecordAsync(paths, "request-missing-update");

        var result = await new DefaultConversationTurnStore(paths).UpdateAsync(record, record.LifecycleVersion);

        Assert.Equal(DefaultConversationTurnStoreStatus.Conflict, result.Status);
        Assert.Null(result.Record);
        Assert.False(File.Exists(Path.Combine(paths.DefaultConversationActiveTurnsPath, record.TurnId + ".json")));
        Assert.False(File.Exists(Path.Combine(paths.DefaultConversationTurnHistoryPath, record.TurnId + ".json")));
    }

    [Fact]
    public async Task Create_replays_only_the_exact_active_turn_intent()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var record = await CreateAdmittedRecordAsync(paths, "request-active-replay");
        var turns = new DefaultConversationTurnStore(paths);

        Assert.Equal(DefaultConversationTurnStoreStatus.Created, (await turns.CreateAsync(record)).Status);
        Assert.Equal(DefaultConversationTurnStoreStatus.Replay, (await turns.CreateAsync(record)).Status);

        var changed = record with { UserMessage = record.UserMessage with { Content = "changed request content" } };
        var conflict = await turns.CreateAsync(changed);

        Assert.Equal(DefaultConversationTurnStoreStatus.Conflict, conflict.Status);
        Assert.NotNull(conflict.Record);
        Assert.Equal(record.TurnId, conflict.Record.TurnId);
        Assert.Equal("hello", conflict.Record.UserMessage.Content);
    }

    [Fact]
    public async Task List_incomplete_rejects_a_null_active_artifact_without_removing_it()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var record = await CreateAdmittedRecordAsync(paths, "request-null-active-artifact");
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, record.TurnId + ".json");
        await File.WriteAllTextAsync(activePath, "null");

        var exception = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());

        Assert.Contains("was empty", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(activePath));
    }

    [Fact]
    public async Task Load_rejects_an_unrecovered_history_stage_without_removing_it()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var record = await CreateAdmittedRecordAsync(paths, "request-unrecovered-history-stage");
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        Directory.CreateDirectory(paths.DefaultConversationTurnHistoryPath);
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{record.TurnId}.json.archive-history.tmp");
        await File.WriteAllTextAsync(pendingHistoryPath, "unrecovered staging evidence");

        var exception = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).LoadAsync(record.TurnId));

        Assert.Contains("interrupted archival staging outside recovery", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(pendingHistoryPath));
    }

    [Theory]
    [InlineData(".json.archive-source")]
    [InlineData(".json.archive-source-proof-publication.tmp")]
    public async Task List_incomplete_rejects_an_interrupted_artifact_without_a_stable_turn_identity(string fileName)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var invalidPath = Path.Combine(paths.DefaultConversationActiveTurnsPath, fileName);
        await File.WriteAllTextAsync(invalidPath, "invalid artifact name");

        var exception = await Assert.ThrowsAsync<FormatException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());

        Assert.Contains("invalid", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(invalidPath));
    }

    private static async Task<DefaultConversationTurnRecord> CreateAdmittedRecordAsync(WorkspacePaths paths, string requestId)
    {
        var conversation = await new ConversationMemoryStore(paths).LoadCurrentConversationSnapshotAsync();
        var admittedAtUtc = DateTimeOffset.UtcNow;
        var run = LoopRunRecord.Started(
            DefaultConversationTurnProtocol.CreateRunId(requestId),
            BuiltInLoopIds.DefaultConversation,
            "default-assistant",
            RuntimeSurfaceId.Web,
            LoopTrigger.HumanMessage,
            admittedAtUtc);
        return DefaultConversationTurnProtocol.Admit(
            run,
            conversation,
            LlmMessage.User("hello"),
            admittedAtUtc.AddSeconds(1),
            requestId,
            TestCapabilityAdmissionFactory.Create(LoopDefinition.CreateDefaultConversation().CapabilityRequirements, admittedAtUtc));
    }
}
