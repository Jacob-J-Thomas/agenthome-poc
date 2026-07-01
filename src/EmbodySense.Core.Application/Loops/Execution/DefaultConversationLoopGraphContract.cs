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

    private static readonly RequiredNode[] RequiredNodes =
    [
        new(DefaultConversationLoopGraphIds.AcceptUserMessage, LoopGraphNodeKind.Trigger),
        new(DefaultConversationLoopGraphIds.AssembleContext, LoopGraphNodeKind.ContextAssembly),
        new(DefaultConversationLoopGraphIds.DispatchInference, LoopGraphNodeKind.ModelInference),
        new(DefaultConversationLoopGraphIds.PersistTranscript, LoopGraphNodeKind.TranscriptPersistence),
        new(DefaultConversationLoopGraphIds.CompleteRun, LoopGraphNodeKind.RunFinalization)
    ];

    private static readonly RequiredEdge[] RequiredEdges =
    [
        new("accept-message-to-context", DefaultConversationLoopGraphIds.AcceptUserMessage, DefaultConversationLoopGraphIds.AssembleContext, LoopGraphEdgeCondition.Always),
        new("context-to-inference", DefaultConversationLoopGraphIds.AssembleContext, DefaultConversationLoopGraphIds.DispatchInference, LoopGraphEdgeCondition.Success),
        new("inference-to-transcript", DefaultConversationLoopGraphIds.DispatchInference, DefaultConversationLoopGraphIds.PersistTranscript, LoopGraphEdgeCondition.Success),
        new("transcript-to-complete-run", DefaultConversationLoopGraphIds.PersistTranscript, DefaultConversationLoopGraphIds.CompleteRun, LoopGraphEdgeCondition.Success)
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

        foreach (var requiredNode in RequiredNodes)
        {
            var node = loopDefinition.Graph.Nodes.Single(item => string.Equals(item.Id, requiredNode.Id, StringComparison.Ordinal));
            if (node.Kind != requiredNode.Kind)
            {
                return $"Loop `{loopDefinition.Id}` graph node `{node.Id}` must be `{requiredNode.Kind}` for the default conversation runner.";
            }
        }

        if (loopDefinition.Graph.Edges.Length != RequiredEdges.Length)
        {
            return $"Loop `{loopDefinition.Id}` graph contains edges that the default conversation runner does not execute yet.";
        }

        foreach (var requiredEdge in RequiredEdges)
        {
            if (!loopDefinition.Graph.Edges.Any(edge => requiredEdge.Matches(edge)))
            {
                return $"Loop `{loopDefinition.Id}` graph is missing required default edge `{requiredEdge.Id}`.";
            }
        }

        return null;
    }

    private sealed record RequiredNode(string Id, LoopGraphNodeKind Kind);

    private sealed record RequiredEdge(string Id, string FromNodeId, string ToNodeId, LoopGraphEdgeCondition Condition)
    {
        public bool Matches(LoopGraphEdgeDefinition edge)
        {
            return string.Equals(edge.Id, Id, StringComparison.Ordinal)
                && string.Equals(edge.FromNodeId, FromNodeId, StringComparison.Ordinal)
                && string.Equals(edge.ToNodeId, ToNodeId, StringComparison.Ordinal)
                && edge.Condition == Condition;
        }
    }
}
