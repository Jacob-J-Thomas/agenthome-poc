using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;
using static EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnRecoveryTests;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class DefaultConversationTurnRecoveryTestsRetirementEvidenceBounds
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_excludes_the_pending_history_stage_before_applying_the_bounded_retirement_evidence_count(bool overCapacity)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, $"request-bounded-retirement-evidence-{overCapacity}", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, terminal.TurnId + ".json");
        var pendingSourcePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{terminal.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-history.tmp");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-source-proof");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        await File.WriteAllBytesAsync(activePath, bytes);
        await CreateHistoryStageRetirementEvidencePairsAsync(paths, terminal.TurnId, 128);
        File.Move(activePath, pendingSourcePath, overwrite: false);
        await File.WriteAllBytesAsync(pendingHistoryPath, bytes);
        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(pendingSourcePath));
        Assert.True(File.Exists(pendingHistoryPath));

        if (overCapacity)
        {
            await File.WriteAllBytesAsync(Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-history.unexpected"), [1]);
        }

        var store = new DefaultConversationTurnStore(paths);
        if (overCapacity)
        {
            await Assert.ThrowsAsync<FormatException>(() => store.ListIncompleteAsync());
            Assert.True(File.Exists(pendingSourcePath));
            Assert.True(File.Exists(pendingHistoryPath));
            Assert.False(File.Exists(historyPath));
            Assert.False(File.Exists(sourceProofPath));
            return;
        }

        Assert.Empty(await store.ListIncompleteAsync());
        Assert.False(File.Exists(pendingSourcePath));
        Assert.False(File.Exists(pendingHistoryPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(historyPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(sourceProofPath));
    }
}
