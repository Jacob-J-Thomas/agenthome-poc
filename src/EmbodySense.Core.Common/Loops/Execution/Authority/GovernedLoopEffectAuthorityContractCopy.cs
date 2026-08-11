using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Authority;

internal static class GovernedLoopEffectAuthorityContractCopy
{
    internal static GovernedLoopEffectAuthorityProof Copy(GovernedLoopEffectAuthorityProof? value)
        => value is null
            ? null!
            : new GovernedLoopEffectAuthorityProof(
                value.SchemaVersion,
                Copy(value.Grant),
                Copy(value.Binding),
                value.GrantStatus,
                value.GrantPosture,
                Copy(value.Boundary),
                Copy(value.Ceiling),
                Copy(value.CapabilityPins, GovernedLoopEffectAuthorityContractLimits.MaxCapabilityPins),
                Copy(value.ObservedCapabilityPins, GovernedLoopEffectAuthorityContractLimits.MaxCapabilityPins),
                value.DependencyEvidenceHash);

    internal static AuthorityGrantReference Copy(AuthorityGrantReference? value)
        => value is null ? null! : new AuthorityGrantReference(value.GrantId, value.Revision, value.ContentHash);

    internal static AuthorityGrantBinding Copy(AuthorityGrantBinding? value)
        => value is null
            ? null!
            : new AuthorityGrantBinding(Copy(value.Profile), Copy(value.Role), Copy(value.Loop));

    internal static AuthorityGrantProfilePin Copy(AuthorityGrantProfilePin? value)
        => value is null ? null! : new AuthorityGrantProfilePin(value.Reference, value.ContentHash);

    internal static ContextualRoleRevisionPin Copy(ContextualRoleRevisionPin? value)
        => value is null ? null! : new ContextualRoleRevisionPin(value.Identity, value.ContentHash);

    internal static GovernedLoopRevisionPublicationPin Copy(GovernedLoopRevisionPublicationPin? value)
        => value is null
            ? null!
            : new GovernedLoopRevisionPublicationPin(
                value.SchemaVersion,
                value.Revision,
                value.PublicationOperationId,
                value.ValidationEvidenceHash);

    internal static AuthorityGrantBoundary Copy(AuthorityGrantBoundary? value)
        => value is null ? null! : new AuthorityGrantBoundary(value.EffectiveAtUtc, value.ExpiresAtUtc, value.CompletionConstraint);

    internal static AuthorityCeiling Copy(AuthorityCeiling? value)
        => value is null || value.Capabilities is null || value.DataClasses is null
            ? null!
            : new AuthorityCeiling(
                Snapshot(value.Capabilities, EmbodySense.Core.Common.Authority.AuthorityContractLimits.MaxCapabilitiesPerCeiling),
                Snapshot(value.DataClasses, EmbodySense.Core.Common.Authority.AuthorityContractLimits.MaxDataClassesPerCeiling),
                value.MaxTargetCount,
                value.MaxSideEffectClass,
                value.AllowsRecurrence,
                value.AllowsExternalPublication,
                value.AllowsIrreversibleAction);

    internal static IReadOnlyList<CapabilityAdmissionPin> Copy(IReadOnlyList<CapabilityAdmissionPin>? values, int maximum)
        => values is null ? null! : Snapshot(values, maximum);

    private static IReadOnlyList<TValue> Snapshot<TValue>(IEnumerable<TValue> values, int maximum)
        => Array.AsReadOnly(values.Take(maximum + 1).ToArray());
}
