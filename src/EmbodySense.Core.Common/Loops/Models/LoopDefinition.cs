namespace EmbodySense.Core.Common.Loops.Models;

public sealed record LoopDefinition(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string Description,
    string RoleId,
    LoopTrigger Trigger,
    LoopMemoryScope MemoryScope,
    string[] CapabilityIds,
    LoopReviewPolicy ReviewPolicy,
    LoopFailurePolicy FailurePolicy,
    LoopState State)
{
    public const int CurrentSchemaVersion = 1;

    // TODO(loop-graph-definition): This flat seed artifact is not sufficient for user/system-authored graph editing.
    // Revisit when the loop-builder begins storing nodes, edges, system-owned locks, and editable vs system loop boundaries.

    public static LoopDefinition CreateDefaultConversation()
    {
        return new LoopDefinition(
            CurrentSchemaVersion,
            "default-conversation",
            "Default conversation loop",
            "The governed loop behind ordinary chat turns in this workspace.",
            "default-assistant",
            LoopTrigger.HumanMessage,
            LoopMemoryScope.WorkspaceStartupContext,
            [
                "conversation.turn",
                "conversation.history",
                "agent.context",
                "provider.inference",
                "workspace.command",
                "approval.request",
                "audit.write"
            ],
            LoopReviewPolicy.ReviewAtAuthorityBoundaries,
            LoopFailurePolicy.RecordFailureAndSurfaceToUser,
            LoopState.Enabled);
    }
}
