namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Provides the bounded, display-oriented projection of the workspace permission document.
/// </summary>
/// <param name="Path">The canonical permission-document path.</param>
/// <param name="Exists">Whether the document existence probe reported true.</param>
/// <param name="Parsed">Whether the bounded content parsed as the supported version-one schema.</param>
/// <param name="Version">The parsed schema version, or null when parsing failed.</param>
/// <param name="Scope">The parsed policy scope, or an empty string when unavailable.</param>
/// <param name="DefaultAccess">A human-readable explanation of unmatched-policy behavior.</param>
/// <param name="RawJson">Bounded JSON with likely secret-bearing lines redacted and truncation marked.</param>
/// <param name="Approved">Parsed approved directory rules.</param>
/// <param name="Denied">Parsed denied directory rules.</param>
/// <param name="ReadProblems">Bounded missing, parse, and truncation diagnostics.</param>
public sealed record WorkspacePermissionsConfiguration(
    string Path,
    bool Exists,
    bool Parsed,
    int? Version,
    string Scope,
    string DefaultAccess,
    string RawJson,
    IReadOnlyList<WorkspacePermissionRule> Approved,
    IReadOnlyList<WorkspacePermissionRule> Denied,
    IReadOnlyList<string> ReadProblems);
