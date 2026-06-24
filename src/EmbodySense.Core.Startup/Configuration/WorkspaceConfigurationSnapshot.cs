namespace EmbodySense.Core.Startup.Configuration;

public sealed record WorkspaceConfigurationSnapshot(
    DateTimeOffset GeneratedAtUtc,
    WorkspaceRuntimeConfiguration Runtime,
    WorkspaceConfigurationStatus Status,
    IReadOnlyList<WorkspaceConfigurationPath> Paths,
    WorkspacePermissionsConfiguration Permissions,
    IReadOnlyList<WorkspaceConfigurationDocument> Documents,
    WorkspaceAuditConfiguration Audit,
    WorkspaceConversationHistoryConfiguration ConversationHistory,
    IReadOnlyList<WorkspaceConfigurationConcept> Concepts);
