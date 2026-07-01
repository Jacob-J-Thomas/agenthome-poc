using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Loops.Models;

public static class LoopCapabilityIds
{
    // TODO(loop-capability-registry): Raw capability ids are enough for the default workspace-command loop gate, but skills,
    // hooks, cron jobs, wake commands, subagents, and editable loops need a real registry with implemented/planned status,
    // authority metadata, and validation before user-authored loop definitions can safely reference broader capabilities.
    public const string ConversationTurn = "conversation.turn";
    public const string ConversationHistory = "conversation.history";
    public const string AgentContext = "agent.context";
    public const string ProviderInference = "provider.inference";
    public const string WorkspaceCommand = "workspace.command";
    public const string ApprovalRequest = "approval.request";
    public const string AuditWrite = "audit.write";

    public static string WorkspaceCommandFor(ToolCommand command)
    {
        return WorkspaceCommand + "." + ToolCommandFormatter.Format(command);
    }

    public static bool AllowsWorkspaceCommand(IReadOnlyCollection<string> capabilityIds, ToolCommand command)
    {
        ArgumentNullException.ThrowIfNull(capabilityIds);

        return capabilityIds.Contains(WorkspaceCommand, StringComparer.Ordinal)
            || capabilityIds.Contains(WorkspaceCommandFor(command), StringComparer.Ordinal);
    }
}
