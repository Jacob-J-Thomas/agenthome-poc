namespace EmbodySense.Core.Common.Loops.Models;

public sealed record LoopGraphDefinition(
    string EntryNodeId,
    string[] TerminalNodeIds,
    LoopGraphNodeDefinition[] Nodes,
    LoopGraphEdgeDefinition[] Edges)
{
    public static LoopGraphDefinition CreateDefaultConversation()
    {
        return new LoopGraphDefinition(
            DefaultConversationLoopGraphIds.AcceptUserMessage,
            [DefaultConversationLoopGraphIds.CompleteRun],
            [
                new LoopGraphNodeDefinition(
                    DefaultConversationLoopGraphIds.AcceptUserMessage,
                    "Accept user message",
                    "Receives the current human message as the trigger for the governed default conversation turn.",
                    LoopGraphNodeKind.Trigger,
                    LoopGraphNodeEditMode.SystemLocked,
                    [LoopCapabilityIds.ConversationTurn]),
                new LoopGraphNodeDefinition(
                    DefaultConversationLoopGraphIds.AssembleContext,
                    "Assemble runtime context",
                    "Combines startup context, restored/session transcript context, and current turn input before provider dispatch.",
                    LoopGraphNodeKind.ContextAssembly,
                    LoopGraphNodeEditMode.SystemLocked,
                    [LoopCapabilityIds.AgentContext, LoopCapabilityIds.ConversationHistory]),
                new LoopGraphNodeDefinition(
                    DefaultConversationLoopGraphIds.DispatchInference,
                    "Dispatch provider inference",
                    "Sends the assembled turn request to the configured inference adapter.",
                    LoopGraphNodeKind.ModelInference,
                    LoopGraphNodeEditMode.SystemLocked,
                    [LoopCapabilityIds.ProviderInference]),
                new LoopGraphNodeDefinition(
                    DefaultConversationLoopGraphIds.PersistTranscript,
                    "Persist transcript",
                    "Persists accepted user and assistant messages into runtime state and conversation memory.",
                    LoopGraphNodeKind.TranscriptPersistence,
                    LoopGraphNodeEditMode.SystemLocked,
                    [LoopCapabilityIds.ConversationTurn, LoopCapabilityIds.ConversationHistory]),
                new LoopGraphNodeDefinition(
                    DefaultConversationLoopGraphIds.CompleteRun,
                    "Complete loop run",
                    "Records the terminal loop run status and returns the typed runtime turn result to the active surface.",
                    LoopGraphNodeKind.RunFinalization,
                    LoopGraphNodeEditMode.SystemLocked,
                    [LoopCapabilityIds.AuditWrite])
            ],
            [
                new LoopGraphEdgeDefinition(
                    "accept-message-to-context",
                    DefaultConversationLoopGraphIds.AcceptUserMessage,
                    DefaultConversationLoopGraphIds.AssembleContext,
                    LoopGraphEdgeCondition.Always,
                    "Accepted user input flows into context assembly."),
                new LoopGraphEdgeDefinition(
                    "context-to-inference",
                    DefaultConversationLoopGraphIds.AssembleContext,
                    DefaultConversationLoopGraphIds.DispatchInference,
                    LoopGraphEdgeCondition.Success,
                    "Context assembly must succeed before provider inference."),
                new LoopGraphEdgeDefinition(
                    "inference-to-transcript",
                    DefaultConversationLoopGraphIds.DispatchInference,
                    DefaultConversationLoopGraphIds.PersistTranscript,
                    LoopGraphEdgeCondition.Success,
                    "A completed inference response is persisted into the transcript."),
                new LoopGraphEdgeDefinition(
                    "transcript-to-complete-run",
                    DefaultConversationLoopGraphIds.PersistTranscript,
                    DefaultConversationLoopGraphIds.CompleteRun,
                    LoopGraphEdgeCondition.Success,
                    "Persisted transcript state completes the run.")
            ]);
    }

    public string? GetValidationFailure()
    {
        if (string.IsNullOrWhiteSpace(EntryNodeId))
        {
            return "Loop graph must specify an entry node id.";
        }

        if (TerminalNodeIds is null || TerminalNodeIds.Length == 0 || TerminalNodeIds.Any(string.IsNullOrWhiteSpace))
        {
            return "Loop graph must specify at least one terminal node id.";
        }

        if (Nodes is null || Nodes.Length == 0)
        {
            return "Loop graph must include at least one node.";
        }

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            var failure = ValidateNode(node);
            if (failure is not null)
            {
                return failure;
            }

            if (!nodeIds.Add(node.Id))
            {
                return $"Loop graph contains duplicate node id `{node.Id}`.";
            }
        }

        if (!nodeIds.Contains(EntryNodeId))
        {
            return $"Loop graph entry node `{EntryNodeId}` does not exist.";
        }

        foreach (var terminalNodeId in TerminalNodeIds)
        {
            if (!nodeIds.Contains(terminalNodeId))
            {
                return $"Loop graph terminal node `{terminalNodeId}` does not exist.";
            }
        }

        if (Edges is null)
        {
            return "Loop graph edge list cannot be null.";
        }

        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in Edges)
        {
            var failure = ValidateEdge(edge, nodeIds);
            if (failure is not null)
            {
                return failure;
            }

            if (!edgeIds.Add(edge.Id))
            {
                return $"Loop graph contains duplicate edge id `{edge.Id}`.";
            }
        }

        return null;
    }

    private static string? ValidateNode(LoopGraphNodeDefinition? node)
    {
        if (node is null)
        {
            return "Loop graph contains a null node.";
        }

        if (string.IsNullOrWhiteSpace(node.Id))
        {
            return "Loop graph nodes must include ids.";
        }

        if (string.IsNullOrWhiteSpace(node.DisplayName))
        {
            return $"Loop graph node `{node.Id}` must include a display name.";
        }

        if (string.IsNullOrWhiteSpace(node.Description))
        {
            return $"Loop graph node `{node.Id}` must include a description.";
        }

        if (!Enum.IsDefined(node.Kind) || node.Kind == LoopGraphNodeKind.Unknown)
        {
            return $"Loop graph node `{node.Id}` has unsupported kind `{node.Kind}`.";
        }

        if (!Enum.IsDefined(node.EditMode) || node.EditMode == LoopGraphNodeEditMode.Unknown)
        {
            return $"Loop graph node `{node.Id}` has unsupported edit mode `{node.EditMode}`.";
        }

        if (node.CapabilityIds is null || node.CapabilityIds.Any(string.IsNullOrWhiteSpace))
        {
            return $"Loop graph node `{node.Id}` capability ids cannot be null or blank.";
        }

        return null;
    }

    private static string? ValidateEdge(LoopGraphEdgeDefinition? edge, IReadOnlySet<string> nodeIds)
    {
        if (edge is null)
        {
            return "Loop graph contains a null edge.";
        }

        if (string.IsNullOrWhiteSpace(edge.Id))
        {
            return "Loop graph edges must include ids.";
        }

        if (!nodeIds.Contains(edge.FromNodeId))
        {
            return $"Loop graph edge `{edge.Id}` references missing from node `{edge.FromNodeId}`.";
        }

        if (!nodeIds.Contains(edge.ToNodeId))
        {
            return $"Loop graph edge `{edge.Id}` references missing to node `{edge.ToNodeId}`.";
        }

        if (!Enum.IsDefined(edge.Condition) || edge.Condition == LoopGraphEdgeCondition.Unknown)
        {
            return $"Loop graph edge `{edge.Id}` has unsupported condition `{edge.Condition}`.";
        }

        if (string.IsNullOrWhiteSpace(edge.Description))
        {
            return $"Loop graph edge `{edge.Id}` must include a description.";
        }

        return null;
    }
}
