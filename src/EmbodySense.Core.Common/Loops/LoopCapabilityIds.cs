using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Loops;

/// <summary>
/// Defines canonical loop capability IDs.
/// </summary>
public static class LoopCapabilityIds
{
    // TODO(loop-capability-registry): Raw capability ids are enough for the default workspace-command loop gate, but skills,
    // hooks, cron jobs, wake commands, subagents, and editable loops need a real registry with implemented/planned status,
    // authority metadata, and validation before user-authored loop definitions can safely reference broader capabilities.
    /// <summary>
    /// Identifies the conversation turn capability ID.
    /// </summary>
    public const string ConversationTurn = "conversation.turn";
    /// <summary>
    /// Identifies the conversation history capability ID.
    /// </summary>
    public const string ConversationHistory = "conversation.history";
    /// <summary>
    /// Identifies the agent context capability ID.
    /// </summary>
    public const string AgentContext = "agent.context";
    /// <summary>
    /// Identifies the provider inference capability ID.
    /// </summary>
    public const string ProviderInference = "provider.inference";
    /// <summary>
    /// Identifies the workspace command capability ID.
    /// </summary>
    public const string WorkspaceCommand = "workspace.command";
    /// <summary>
    /// Identifies the approval request capability ID.
    /// </summary>
    public const string ApprovalRequest = "approval.request";
    /// <summary>
    /// Identifies the audit write capability ID.
    /// </summary>
    public const string AuditWrite = "audit.write";

    /// <summary>
    /// Builds the command-specific capability ID required to authorize a governed workspace command.
    /// </summary>
    /// <param name="command">The governed workspace command.</param>
    /// <returns>The canonical command-specific capability ID.</returns>
    public static string WorkspaceCommandFor(ToolCommand command)
    {
        return WorkspaceCommand + "." + ToolCommandFormatter.Format(command);
    }

    /// <summary>
    /// Determines whether a capability set permits a governed workspace command.
    /// </summary>
    /// <param name="capabilityIds">The admitted capability IDs.</param>
    /// <param name="command">The governed workspace command.</param>
    /// <returns><see langword="true"/> when the set contains either the broad workspace-command capability or the command-specific capability; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="capabilityIds"/> is <see langword="null"/>.</exception>
    public static bool AllowsWorkspaceCommand(IReadOnlyCollection<string> capabilityIds, ToolCommand command)
    {
        ArgumentNullException.ThrowIfNull(capabilityIds);

        return capabilityIds.Contains(WorkspaceCommand, StringComparer.Ordinal)
            || capabilityIds.Contains(WorkspaceCommandFor(command), StringComparer.Ordinal);
    }
}
