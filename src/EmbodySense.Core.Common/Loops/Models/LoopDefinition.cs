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

    public LoopEditMode EditMode { get; init; } = LoopEditMode.SystemLocked;

    public LoopGraphDefinition Graph { get; init; } = LoopGraphDefinition.CreateDefaultConversation();

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
                LoopCapabilityIds.ConversationTurn,
                LoopCapabilityIds.ConversationHistory,
                LoopCapabilityIds.AgentContext,
                LoopCapabilityIds.ProviderInference,
                LoopCapabilityIds.WorkspaceCommand,
                LoopCapabilityIds.ApprovalRequest,
                LoopCapabilityIds.AuditWrite
            ],
            LoopReviewPolicy.ReviewAtAuthorityBoundaries,
            LoopFailurePolicy.RecordFailureAndSurfaceToUser,
            LoopState.Enabled)
        {
            EditMode = LoopEditMode.SystemLocked,
            Graph = LoopGraphDefinition.CreateDefaultConversation()
        };
    }
}
