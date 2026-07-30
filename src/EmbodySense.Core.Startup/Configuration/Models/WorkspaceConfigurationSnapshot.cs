namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Aggregates the bounded interface projection of live workspace configuration.
/// </summary>
/// <param name="GeneratedAtUtc">The UTC instant at which the assembled snapshot was returned.</param>
/// <param name="Runtime">The caller-supplied runtime selection and compatibility status.</param>
/// <param name="Status">The observed workspace initialization and default-access status.</param>
/// <param name="Paths">Canonical locations and their observed existence.</param>
/// <param name="Permissions">The bounded permission-document projection.</param>
/// <param name="Documents">Selected bounded and redacted startup documents.</param>
/// <param name="Audit">The bounded recent audit projection.</param>
/// <param name="ConversationHistory">The coordinated, bounded conversation-history projection.</param>
/// <param name="Concepts">High-level presence summaries for workspace capabilities and configuration.</param>
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
