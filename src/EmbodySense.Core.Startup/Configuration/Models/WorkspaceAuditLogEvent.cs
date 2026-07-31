namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Provides an interface-safe projection of one audit record in a configuration snapshot.
/// </summary>
/// <param name="Sequence">The one-based source-file line number, including blank and malformed lines.</param>
/// <param name="TimestampUtc">The event timestamp recorded by its producer.</param>
/// <param name="Actor">The canonical actor identifier.</param>
/// <param name="Action">The canonical action identifier.</param>
/// <param name="Target">The resource or surface targeted by the action.</param>
/// <param name="Outcome">The canonical outcome identifier.</param>
/// <param name="Detail">The human-readable event detail.</param>
/// <param name="Metadata">Structured metadata projected to display strings.</param>
public sealed record WorkspaceAuditLogEvent(
    int Sequence,
    DateTimeOffset TimestampUtc,
    string Actor,
    string Action,
    string Target,
    string Outcome,
    string Detail,
    IReadOnlyDictionary<string, string> Metadata);
