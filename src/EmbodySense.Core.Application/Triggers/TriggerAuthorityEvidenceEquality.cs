using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

internal static class TriggerAuthorityEvidenceEquality
{
    internal static bool Equals(TriggerAuthorityEvidence left, TriggerAuthorityEvidence right)
    {
        return left.Profile == right.Profile
            && left.BoundaryReceipt.SchemaVersion == right.BoundaryReceipt.SchemaVersion
            && left.BoundaryReceipt.Decision == right.BoundaryReceipt.Decision
            && left.BoundaryReceipt.EvaluatedAtUtc == right.BoundaryReceipt.EvaluatedAtUtc
            && left.BoundaryReceipt.Conditions.SequenceEqual(right.BoundaryReceipt.Conditions)
            && left.BoundaryReceipt.Profiles.SequenceEqual(right.BoundaryReceipt.Profiles);
    }
}
