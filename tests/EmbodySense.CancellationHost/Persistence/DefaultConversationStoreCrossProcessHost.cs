using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Models;
using EmbodySense.Core.Persistence.Memory;

namespace EmbodySense.CancellationHost.Persistence;

internal static class DefaultConversationStoreCrossProcessHost
{
    private const int ProcessLossExitCode = 173;

    internal static async Task<int> RunArchiveProcessLossAsync(string workspaceRoot, string phaseText)
    {
        if (!Enum.TryParse<DefaultConversationTurnArchivePhase>(phaseText, out var phase))
        {
            return 2;
        }

        var coordination = new DefaultConversationExitingArchiveCoordination(phase, ProcessLossExitCode);
        _ = await new DefaultConversationTurnStore(new WorkspacePaths(workspaceRoot), coordination).ListIncompleteAsync();
        return 4;
    }

    internal static async Task<int> RunPublicationAsync(string workspaceRoot, string readyPath, string releasePath, string resultPath)
    {
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
        return 0;
    }

    internal static async Task<int> RunActiveSetLeaseAsync(string workspaceRoot, string readyPath, string releasePath)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        Directory.CreateDirectory(paths.DefaultConversationActiveTurnsPath);
        var turns = new DefaultConversationTurnStore(paths, new DefaultConversationFileBlockingCoordination(readyPath, releasePath));
        _ = await turns.ListIncompleteAsync();
        return 0;
    }

    internal static int RunHistoryStageSubstitution(string stagePath, string displacedPath, string replacementPayload)
    {
        byte[] replacement;
        try
        {
            replacement = Convert.FromBase64String(replacementPayload);
        }
        catch (FormatException)
        {
            return 2;
        }

        File.Move(stagePath, displacedPath);
        File.WriteAllBytes(stagePath, replacement);
        return 0;
    }
}
