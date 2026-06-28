namespace EmbodySense.Core.Application.Loops.Models;

public sealed record LoopDefinition(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string Description,
    string RoleId,
    string Trigger,
    string MemoryScope,
    string[] CapabilityIds,
    string ReviewPolicy,
    string FailurePolicy,
    string State)
{
    public const int CurrentSchemaVersion = 1;

    public static LoopDefinition CreateDefaultConversation()
    {
        return new LoopDefinition(
            CurrentSchemaVersion,
            "default-conversation",
            "Default conversation loop",
            "The governed loop behind ordinary chat turns in this workspace.",
            "default-assistant",
            "human-message",
            "workspace-startup-context",
            [
                "conversation.turn",
                "conversation.history",
                "agent.context",
                "provider.inference",
                "workspace.command",
                "approval.request",
                "audit.write"
            ],
            "review-at-authority-boundaries",
            "record-failure-and-surface-to-user",
            "enabled");
    }
}
