using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Loops.Execution;

internal static class DefaultConversationLoopGraphContract
{
    private static readonly string[] RequiredNodeIds =
    [
        DefaultConversationLoopGraphIds.AcceptUserMessage,
        DefaultConversationLoopGraphIds.AssembleContext,
        DefaultConversationLoopGraphIds.DispatchInference,
        DefaultConversationLoopGraphIds.PersistTranscript,
        DefaultConversationLoopGraphIds.CompleteRun
    ];

    public static string? GetExecutionBlocker(LoopDefinition loopDefinition)
    {
        ArgumentNullException.ThrowIfNull(loopDefinition);

        if (loopDefinition.EditMode != LoopEditMode.SystemLocked)
        {
            return $"Default conversation runner only executes system-locked loop definitions; `{loopDefinition.Id}` is `{loopDefinition.EditMode}`.";
        }

        if (loopDefinition.Graph is null)
        {
            return $"Loop `{loopDefinition.Id}` does not include a graph.";
        }

        var graphFailure = loopDefinition.Graph.GetValidationFailure();
        if (graphFailure is not null)
        {
            return $"Loop `{loopDefinition.Id}` graph is invalid: {graphFailure}";
        }

        if (!string.Equals(loopDefinition.Graph.EntryNodeId, DefaultConversationLoopGraphIds.AcceptUserMessage, StringComparison.Ordinal))
        {
            return $"Loop `{loopDefinition.Id}` graph entry node must be `{DefaultConversationLoopGraphIds.AcceptUserMessage}` for the default conversation runner.";
        }

        if (!loopDefinition.Graph.TerminalNodeIds.Contains(DefaultConversationLoopGraphIds.CompleteRun, StringComparer.Ordinal))
        {
            return $"Loop `{loopDefinition.Id}` graph must terminate at `{DefaultConversationLoopGraphIds.CompleteRun}` for the default conversation runner.";
        }

        var nodeIds = loopDefinition.Graph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var requiredNodeId in RequiredNodeIds)
        {
            if (!nodeIds.Contains(requiredNodeId))
            {
                return $"Loop `{loopDefinition.Id}` graph is missing required default node `{requiredNodeId}`.";
            }
        }

        if (nodeIds.Count != RequiredNodeIds.Length)
        {
            return $"Loop `{loopDefinition.Id}` graph contains nodes that the default conversation runner does not execute yet.";
        }

        if (loopDefinition.Graph.Nodes.Any(node => node.EditMode != LoopGraphNodeEditMode.SystemLocked))
        {
            return $"Loop `{loopDefinition.Id}` graph contains editable nodes that require the future generic graph executor.";
        }

        return null;
    }
}
