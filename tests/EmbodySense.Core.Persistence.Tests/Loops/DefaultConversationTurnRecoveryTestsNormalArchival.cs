using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;
using static EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnRecoveryTests;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class DefaultConversationTurnRecoveryTestsNormalArchival
{
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
        Assert.Equal(140, Directory.EnumerateFiles(paths.DefaultConversationTurnHistoryPath, "*.archive-source-proof-publication.*.completed").Count());
        Assert.Equal(420, Directory.EnumerateFiles(paths.DefaultConversationTurnHistoryPath).Count());
        Assert.Empty(Directory.EnumerateFiles(paths.DefaultConversationActiveTurnsPath, "*.json"));
        Assert.Collection(Directory.EnumerateFiles(paths.DefaultConversationActiveTurnsPath).Select(Path.GetFileName).Order(StringComparer.Ordinal), file => Assert.Equal(".active-set.lock", file));
    }
}
