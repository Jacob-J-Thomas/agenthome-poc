using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>
/// Captures the non-widening tool authority evaluated for one custom-loop model attempt.
/// </summary>
/// <param name="RoleId">The workspace role identifier.</param>
/// <param name="AdmittedMaximum">The immutable assignment ceiling admitted when the run started.</param>
/// <param name="CurrentRoleCeiling">The assignments currently permitted by the workspace role.</param>
/// <param name="ImplementedCatalog">The assignments implemented by the current runtime.</param>
/// <param name="EffectiveAssignments">The intersection that the model may actually invoke.</param>
/// <param name="RoleCeilingHash">The integrity hash for the current role ceiling.</param>
/// <param name="CatalogHash">The integrity hash for the implemented catalog.</param>
/// <param name="EvaluatedAtUtc">The UTC authority-evaluation time.</param>
/// <param name="IsValid">Whether the snapshot was evaluated successfully.</param>
/// <param name="Detail">Human-readable authority evidence.</param>
public sealed record CustomLoopToolAuthoritySnapshot(
    string RoleId,
    CustomLoopToolAssignment[] AdmittedMaximum,
    CustomLoopToolAssignment[] CurrentRoleCeiling,
    CustomLoopToolAssignment[] ImplementedCatalog,
    CustomLoopToolAssignment[] EffectiveAssignments,
    string RoleCeilingHash,
    string CatalogHash,
    DateTimeOffset EvaluatedAtUtc,
    bool IsValid,
    string Detail)
{
    /// <summary>
    /// Determines whether another snapshot is exactly equivalent, including its evidence time and integrity hashes.
    /// </summary>
    /// <param name="other">The snapshot to compare.</param>
    /// <returns><see langword="true"/> for reference identity or exact ordinal/value equality across every field and ordered assignment collection; otherwise, <see langword="false"/>.</returns>
    public bool Matches(CustomLoopToolAuthoritySnapshot? other)
    {
        return ReferenceEquals(this, other)
            || other is not null
            && AdmittedMaximum is not null
            && CurrentRoleCeiling is not null
            && ImplementedCatalog is not null
            && EffectiveAssignments is not null
            && other.AdmittedMaximum is not null
            && other.CurrentRoleCeiling is not null
            && other.ImplementedCatalog is not null
            && other.EffectiveAssignments is not null
            && string.Equals(RoleId, other.RoleId, StringComparison.Ordinal)
            && AdmittedMaximum.SequenceEqual(other.AdmittedMaximum)
            && CurrentRoleCeiling.SequenceEqual(other.CurrentRoleCeiling)
            && ImplementedCatalog.SequenceEqual(other.ImplementedCatalog)
            && EffectiveAssignments.SequenceEqual(other.EffectiveAssignments)
            && string.Equals(RoleCeilingHash, other.RoleCeilingHash, StringComparison.Ordinal)
            && string.Equals(CatalogHash, other.CatalogHash, StringComparison.Ordinal)
            && EvaluatedAtUtc == other.EvaluatedAtUtc
            && IsValid == other.IsValid
            && string.Equals(Detail, other.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Determines whether the effective authority permits a governed workspace command.
    /// </summary>
    /// <param name="command">The governed command to map to an assignment.</param>
    /// <returns><see langword="true"/> when the command maps to a known assignment present in <see cref="EffectiveAssignments"/>; otherwise, <see langword="false"/>.</returns>
    public bool AllowsCommand(ToolCommand command)
    {
        var assignment = command switch
        {
            ToolCommand.List => CustomLoopToolAssignment.List,
            ToolCommand.Read => CustomLoopToolAssignment.Read,
            ToolCommand.Search => CustomLoopToolAssignment.Search,
            _ => CustomLoopToolAssignment.Unknown
        };
        return assignment != CustomLoopToolAssignment.Unknown && EffectiveAssignments is not null && EffectiveAssignments.Contains(assignment);
    }

    /// <summary>
    /// Determines whether this snapshot is a non-widening refresh of the attempt-start authority.
    /// </summary>
    /// <param name="attemptStart">The authority snapshot captured at attempt start.</param>
    /// <returns><see langword="true"/> when admission and catalog ceilings are unchanged, evaluation time does not move backward, and every effective assignment remains within all three ceilings. The role identity must also match unless this snapshot is invalid and grants no effective assignments.</returns>
    public bool IsBoundedRefreshOf(CustomLoopToolAuthoritySnapshot? attemptStart)
    {
        return attemptStart is not null
            && AdmittedMaximum is not null
            && CurrentRoleCeiling is not null
            && ImplementedCatalog is not null
            && EffectiveAssignments is not null
            && attemptStart.AdmittedMaximum is not null
            && attemptStart.ImplementedCatalog is not null
            && AdmittedMaximum.SequenceEqual(attemptStart.AdmittedMaximum)
            && ImplementedCatalog.SequenceEqual(attemptStart.ImplementedCatalog)
            && string.Equals(CatalogHash, attemptStart.CatalogHash, StringComparison.Ordinal)
            && EvaluatedAtUtc >= attemptStart.EvaluatedAtUtc
            && (string.Equals(RoleId, attemptStart.RoleId, StringComparison.Ordinal) || !IsValid && EffectiveAssignments.Length == 0)
            && EffectiveAssignments.All(AdmittedMaximum.Contains)
            && EffectiveAssignments.All(CurrentRoleCeiling.Contains)
            && EffectiveAssignments.All(ImplementedCatalog.Contains);
    }
}
