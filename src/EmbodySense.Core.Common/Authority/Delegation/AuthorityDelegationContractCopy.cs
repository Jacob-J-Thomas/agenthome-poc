using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Authority.Delegation;

internal static class AuthorityDelegationContractCopy
{
    internal static IReadOnlyList<TValue> Snapshot<TValue>(IReadOnlyList<TValue>? values, int maximum)
    {
        if (values is null)
        {
            return null!;
        }

        try
        {
            var declaredCount = values.Count;
            if (declaredCount is < 0 || declaredCount > maximum)
            {
                return null!;
            }

            var snapshot = new List<TValue>(Math.Min(declaredCount, maximum));
            foreach (var value in values)
            {
                if (snapshot.Count == maximum)
                {
                    return null!;
                }

                snapshot.Add(value);
            }

            return snapshot.Count == declaredCount ? Array.AsReadOnly(snapshot.ToArray()) : null!;
        }
        catch (Exception)
        {
            return null!;
        }
    }

    internal static AuthorityDelegationParentEvidenceReference Copy(AuthorityDelegationParentEvidenceReference? value)
    {
        try
        {
            return value is null
                ? null!
                : new AuthorityDelegationParentEvidenceReference(
                    value.WorkspaceId,
                    Copy(value.ParentExecution),
                    value.OriginNodeId,
                    value.OriginNodeAttempt,
                    value.ParentAdmissionReceiptHash,
                    value.ActorId,
                    Copy(value.GrantReference),
                    Copy(value.GrantBinding),
                    value.OriginBindingEvidenceHash,
                    value.GrantDependencyEvidenceHash,
                    value.EvaluatedAtUtc,
                    value.ContentHash);
        }
        catch (Exception)
        {
            return null!;
        }
    }

    internal static AuthorityDelegationTargetBinding Copy(AuthorityDelegationTargetBinding? value)
    {
        try
        {
            return value is null
                ? null!
                : new AuthorityDelegationTargetBinding(value.Kind, Copy(value.Role), Copy(value.Loop), value.NodeId, value.BindingEvidenceHash);
        }
        catch (Exception)
        {
            return null!;
        }
    }

    internal static AuthorityDelegationRevocationLink Copy(AuthorityDelegationRevocationLink? value)
        => value is null
            ? null!
            : new AuthorityDelegationRevocationLink(
                Copy(value.ParentGrant),
                value.ParentAdmissionReceiptHash,
                value.WorkspaceId,
                value.ParentRunId,
                value.ParentExecutionGeneration,
                value.LinkageHash);

    internal static AuthorityDelegationSubsetProof Copy(AuthorityDelegationSubsetProof? value)
        => value is null
            ? null!
            : new AuthorityDelegationSubsetProof(
                value.ParentEvidenceHash,
                value.ParentAuthorityScopeHash,
                value.DelegatedAuthorityScopeHash,
                value.TargetMaximumEvidenceHash,
                value.NarrowingDimensions,
                value.ContentHash);

    internal static AuthorityDelegationBoundary Copy(AuthorityDelegationBoundary? value)
        => value is null ? null! : new AuthorityDelegationBoundary(value.EffectiveAtUtc, value.ExpiresAtUtc, value.CompletionConstraint);

    internal static GovernedLoopExecutionBinding Copy(GovernedLoopExecutionBinding? value)
        => value is null
            ? null!
            : GovernedLoopExecutionBinding.Create(value.SchemaVersion, value.RunId, Copy(value.Revision), value.ExecutionGeneration);

    internal static GovernedLoopRevisionReference Copy(GovernedLoopRevisionReference? value)
        => value is null
            ? null!
            : GovernedLoopRevisionReference.Create(value.SchemaVersion, value.GraphId, value.RevisionId, value.ExecutableHash);

    internal static ContextualRoleRevisionPin Copy(ContextualRoleRevisionPin? value)
        => value?.Identity is null
            ? null!
            : new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity(value.Identity.RoleId, value.Identity.Revision), value.ContentHash);

    internal static GovernedLoopRevisionPublicationPin? Copy(GovernedLoopRevisionPublicationPin? value)
        => value is null
            ? null
            : new GovernedLoopRevisionPublicationPin(value.SchemaVersion, Copy(value.Revision), value.PublicationOperationId, value.ValidationEvidenceHash);

    internal static AuthorityGrantReference Copy(AuthorityGrantReference? value)
        => value is null ? null! : new AuthorityGrantReference(value.GrantId, value.Revision, value.ContentHash);

    internal static AuthorityGrantBinding Copy(AuthorityGrantBinding? value)
        => value is null
            ? null!
            : new AuthorityGrantBinding(
                new AuthorityGrantProfilePin(
                    new AuthorityProfileReference(value.Profile.Reference.ProfileId, value.Profile.Reference.Revision),
                    value.Profile.ContentHash),
                Copy(value.Role),
                Copy(value.Loop)!);

    internal static AuthorityCeiling Copy(AuthorityCeiling? value)
    {
        try
        {
            if (value is null)
            {
                return null!;
            }

            var capabilities = Snapshot(value.Capabilities, AuthorityContractLimits.MaxCapabilitiesPerCeiling);
            var dataClasses = Snapshot(value.DataClasses, AuthorityContractLimits.MaxDataClassesPerCeiling);
            return capabilities is null || dataClasses is null
                ? null!
                : new AuthorityCeiling(
                    capabilities.Select(Copy).ToArray(),
                    dataClasses.ToArray(),
                    value.MaxTargetCount,
                    value.MaxSideEffectClass,
                    value.AllowsRecurrence,
                    value.AllowsExternalPublication,
                    value.AllowsIrreversibleAction);
        }
        catch (Exception)
        {
            return null!;
        }
    }

    internal static IReadOnlyList<CapabilityAdmissionPin> CopyPins(IReadOnlyList<CapabilityAdmissionPin>? values)
    {
        var snapshot = Snapshot(values, CapabilityContractLimits.MaxCapabilityAdmissionPins);
        if (snapshot is null)
        {
            return null!;
        }

        try
        {
            return Array.AsReadOnly(snapshot.Select(Copy).ToArray());
        }
        catch (Exception)
        {
            return null!;
        }
    }

    private static CapabilityDescriptorIdentity Copy(CapabilityDescriptorIdentity value)
        => new(value.Id, value.Version, value.Hash);

    private static CapabilityAdmissionPin Copy(CapabilityAdmissionPin value)
        => new(
            Copy(value.DescriptorIdentity),
            value.Kind,
            new CapabilityImplementationIdentity(value.Implementation.ProviderId, value.Implementation.ImplementationId),
            new CapabilityProvenance(value.Provenance.Kind, value.Provenance.SourceUri, value.Provenance.SourceRevision, value.Provenance.Integrity),
            new CapabilityDependencyArtifactMetadata(value.Artifact.Checksum, value.Artifact.Signature),
            value.SafeDescription);
}
