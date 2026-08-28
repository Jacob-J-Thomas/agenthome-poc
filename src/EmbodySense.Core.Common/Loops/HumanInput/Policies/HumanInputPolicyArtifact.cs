using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Policies;

/// <summary>Defines one immutable, scope-bound, non-authorizing Human Input timeout or terminal-disposition policy revision.</summary>
/// <param name="SchemaVersion">The policy schema version, which must be 1.</param>
/// <param name="PolicyId">The stable policy identity.</param>
/// <param name="RevisionId">The immutable policy-revision identity.</param>
/// <param name="Kind">The closed timeout or terminal-disposition policy kind.</param>
/// <param name="WorkspaceId">The server-owned workspace scope.</param>
/// <param name="GraphId">The exact governed-loop graph scope.</param>
/// <param name="AuthorityActorId">The server-derived actor that authored the scoped policy; this is attribution rather than an authority grant.</param>
/// <param name="ResponseWindowMilliseconds">The finite positive response-window duration for a <see cref="HumanInputPolicyKind.ResponseWindow"/> policy, otherwise null.</param>
/// <param name="TerminalDisposition">The closed deadline terminal disposition for a <see cref="HumanInputPolicyKind.DeadlineDisposition"/> policy, otherwise <see cref="HumanInputTerminalDisposition.Unknown"/>.</param>
/// <param name="ContentHash">The canonical lowercase SHA-256 digest over every behavior-affecting field.</param>
public sealed record HumanInputPolicyArtifact(
    int SchemaVersion,
    string PolicyId,
    string RevisionId,
    HumanInputPolicyKind Kind,
    string WorkspaceId,
    string GraphId,
    string AuthorityActorId,
    long? ResponseWindowMilliseconds,
    HumanInputTerminalDisposition TerminalDisposition,
    string ContentHash)
{
    /// <summary>Gets the only supported Human Input policy schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets this artifact's exact immutable reference.</summary>
    public HumanInputPolicyReference Reference => new(PolicyId, RevisionId);
}
