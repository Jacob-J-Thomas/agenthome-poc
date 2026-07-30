namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Provides the bounded audit portion of a workspace configuration snapshot.
/// </summary>
/// <param name="Path">The canonical audit event-stream path.</param>
/// <param name="Exists">Whether the existence probe observed the event stream.</param>
/// <param name="Events">Up to the configured number of most recent successfully parsed nonblank events, in file order.</param>
/// <param name="ReadProblems">Bounded parse and omission diagnostics collected while reading the stream.</param>
public sealed record WorkspaceAuditConfiguration(
    string Path,
    bool Exists,
    IReadOnlyList<WorkspaceAuditLogEvent> Events,
    IReadOnlyList<string> ReadProblems);
