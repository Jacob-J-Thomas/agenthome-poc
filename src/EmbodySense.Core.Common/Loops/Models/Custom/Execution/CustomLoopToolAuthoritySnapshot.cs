using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

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
}
