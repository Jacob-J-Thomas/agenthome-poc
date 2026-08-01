using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Capabilities.Models;
namespace EmbodySense.Core.Common.Loops;

/// <summary>
/// Defines the persisted metadata, authority policy, and executable graph for a governed loop.
/// </summary>
/// <param name="SchemaVersion">The persisted schema version.</param>
/// <param name="Id">The stable artifact identifier.</param>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="Description">The human-readable description.</param>
/// <param name="RoleId">The workspace role identifier.</param>
/// <param name="Trigger">The trigger.</param>
/// <param name="MemoryScope">The memory scope.</param>
/// <param name="CapabilityIds">The capability IDs.</param>
/// <param name="ReviewPolicy">The review policy.</param>
/// <param name="FailurePolicy">The failure policy.</param>
/// <param name="State">The state.</param>
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
    /// <summary>
    /// Schema version required by the current built-in loop-definition contract.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Gets the loop edit mode.
    /// </summary>
    /// <value>The loop edit mode.</value>
    public LoopEditMode EditMode { get; init; }

    /// <summary>
    /// Gets the loop graph definition.
    /// </summary>
    /// <value>The loop graph definition.</value>
    public LoopGraphDefinition Graph { get; init; } = null!;

    /// <summary>Gets the bounded capability requirements declared by this loop.</summary>
    public CapabilityDependencyManifest CapabilityRequirements { get; init; } = null!;

    /// <summary>
    /// Creates the system-locked built-in conversation-loop definition.
    /// </summary>
    /// <returns>A version-1 enabled definition with the default assistant role, workspace startup memory, governed capabilities, authority-boundary review policy, failure recording, and canonical graph.</returns>
    public static LoopDefinition CreateDefaultConversation()
    {
        return new LoopDefinition(
            CurrentSchemaVersion,
            BuiltInLoopIds.DefaultConversation,
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
            Graph = LoopGraphDefinition.CreateDefaultConversation(),
            CapabilityRequirements = LoopCapabilityRequirements.CreateDefaultConversationManifest()
        };
    }
}
