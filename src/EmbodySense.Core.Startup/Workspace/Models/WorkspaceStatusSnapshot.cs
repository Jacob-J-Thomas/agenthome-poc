namespace EmbodySense.Core.Startup.Workspace.Models;

/// <summary>
/// Provides an interface-safe point-in-time view of workspace initialization paths and directory policy.
/// </summary>
/// <param name="RootPath">The normalized absolute workspace root.</param>
/// <param name="AgentPath">The derived durable <c>.agent</c> directory.</param>
/// <param name="WorkspacePath">The governed working area, currently equal to <paramref name="RootPath"/>.</param>
/// <param name="IsInitialized">Whether the agent directory, role document, and permissions document all exist.</param>
/// <param name="EventsLogPath">The canonical append-only audit event path.</param>
/// <param name="PermissionsPath">The canonical directory permission document path.</param>
/// <param name="TasksPath">The canonical durable task directory.</param>
/// <param name="DefaultAccess">A human-readable explanation of unmatched-policy behavior.</param>
/// <param name="ApprovedEntries">Human-readable approved directory rules in stored policy order.</param>
/// <param name="DeniedEntries">Human-readable denied directory rules in stored policy order.</param>
public sealed record WorkspaceStatusSnapshot(
    string RootPath,
    string AgentPath,
    string WorkspacePath,
    bool IsInitialized,
    string EventsLogPath,
    string PermissionsPath,
    string TasksPath,
    string DefaultAccess,
    IReadOnlyList<string> ApprovedEntries,
    IReadOnlyList<string> DeniedEntries);
