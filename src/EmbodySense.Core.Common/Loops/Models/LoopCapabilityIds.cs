using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Loops.Models;

public static class LoopCapabilityIds
{
    public const string ConversationTurn = "conversation.turn";
    public const string ConversationHistory = "conversation.history";
    public const string AgentContext = "agent.context";
    public const string ProviderInference = "provider.inference";
    public const string WorkspaceCommand = "workspace.command";
    public const string ApprovalRequest = "approval.request";
    public const string AuditWrite = "audit.write";

    public static string WorkspaceCommandFor(ToolCommand command)
    {
        return WorkspaceCommand + "." + command.ToString().ToLowerInvariant();
    }

    public static bool AllowsWorkspaceCommand(IReadOnlyCollection<string> capabilityIds, ToolCommand command)
    {
        ArgumentNullException.ThrowIfNull(capabilityIds);

        return capabilityIds.Contains(WorkspaceCommand, StringComparer.Ordinal)
            || capabilityIds.Contains(WorkspaceCommandFor(command), StringComparer.Ordinal);
    }
}
