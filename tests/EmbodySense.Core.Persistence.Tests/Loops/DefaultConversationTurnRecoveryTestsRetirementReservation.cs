using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;
using static EmbodySense.Core.Persistence.Tests.Loops.DefaultConversationTurnRecoveryTests;

namespace EmbodySense.Core.Persistence.Tests.Loops;

public sealed class DefaultConversationTurnRecoveryTestsRetirementReservation
{
    [Fact]
    public async Task Active_archive_reserves_one_retirement_pair_before_claiming_another_source()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var terminal = await CreateTerminalRecordAsync(paths, "request-retirement-evidence-capacity-reservation", needsReview: false);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var activePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, terminal.TurnId + ".json");
        var pendingSourcePath = Path.Combine(paths.DefaultConversationActiveTurnsPath, $".{terminal.TurnId}.json.archive-source");
        var pendingHistoryPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-history.tmp");
        var historyPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, terminal.TurnId + ".json");
        var sourceProofPath = Path.Combine(paths.DefaultConversationTurnHistoryPath, $".{terminal.TurnId}.json.archive-source-proof");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(terminal, CreateTurnJsonOptions()));
        await File.WriteAllBytesAsync(activePath, bytes);
        await CreateHistoryStageRetirementEvidencePairsAsync(paths, terminal.TurnId, 128);

        var exception = await Assert.ThrowsAsync<IOException>(() => new DefaultConversationTurnStore(paths).ListIncompleteAsync());

        Assert.Contains("retirement-evidence capacity", exception.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(activePath));
        Assert.False(File.Exists(pendingSourcePath));
        Assert.False(File.Exists(pendingHistoryPath));
        Assert.False(File.Exists(historyPath));
        Assert.False(File.Exists(sourceProofPath));
        Assert.Equal(128, GetHistoryStageRetirementEvidencePaths(paths, terminal.TurnId, ".retirement-intent").Count);
        Assert.Equal(128, GetHistoryStageRetirementEvidencePaths(paths, terminal.TurnId, ".retired").Count);
    }
}
