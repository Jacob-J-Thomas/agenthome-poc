using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Revalidates current non-effect authority for one exact canonical Human Review continuation.</summary>
/// <remarks>The source never recreates historical authority, grants a wider ceiling, reserves usage, or crosses a release boundary. It admits only an exact current grant and capability posture rooted in the canonical admission receipt.</remarks>
public sealed class CurrentHumanReviewContinuationAuthoritySource : IHumanReviewContinuationAuthoritySource
{
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly ICapabilityAdmissionService _capabilities;

    /// <summary>Initializes the current-authority source over canonical grant and capability revalidation ports.</summary>
    /// <param name="grantResolver">The exact current immutable grant resolver.</param>
    /// <param name="capabilities">The canonical immutable capability-pin revalidator.</param>
    public CurrentHumanReviewContinuationAuthoritySource(IAuthorityGrantResolver grantResolver, ICapabilityAdmissionService capabilities)
    {
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    /// <inheritdoc />
    public async Task<HumanReviewContinuationAuthorityReadResult> ReadAsync(HumanReviewContinuationAuthorityQuery query, CancellationToken cancellationToken = default)
    {
        if (!TryGetAdmitted(query, out var admitted))
        {
            return Result(HumanReviewContinuationAuthorityReadStatus.Invalid);
        }

        AuthorityGrantResolution? resolution;
        try
        {
            resolution = await _grantResolver.ResolveAsync(admitted!.Intent.AuthorityGrant, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(HumanReviewContinuationAuthorityReadStatus.Unavailable);
        }

        var grantPosture = ClassifyGrant(admitted!, resolution);
        if (grantPosture != HumanReviewContinuationAuthorityReadStatus.Current)
        {
            return Result(grantPosture);
        }

        CapabilityRevalidationResult? capability;
        try
        {
            var allowed = resolution!.EffectiveCeiling.Capabilities.Select(identity => identity.Id).ToArray();
            capability = await _capabilities.RevalidateAsync(admitted.Evidence.CapabilityAdmission, allowed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(HumanReviewContinuationAuthorityReadStatus.Unavailable);
        }

        return Result(ClassifyCapabilities(admitted, capability));
    }

    private static bool TryGetAdmitted(HumanReviewContinuationAuthorityQuery? query, out GovernedLoopAdmissionReceipt? admitted)
    {
        admitted = null;
        try
        {
            if (query?.Binding is null
                || query.AdapterBinding is null
                || query.GraphArtifact is null
                || !HumanReviewContractHash.MatchesBinding(query.Binding)
                || !GovernedLoopSequentialContractValidator.Validate(query.AdapterBinding).IsValid
                || !GovernedLoopAdmissionValidator.Validate(query.AdapterBinding.AdmissionReceipt).IsValid
                || !MatchesGraph(query.AdapterBinding, query.GraphArtifact)
                || !MatchesBinding(query.Binding, query.AdapterBinding))
            {
                return false;
            }

            admitted = query.AdapterBinding.AdmissionReceipt;
            return true;
        }
        catch
        {
            admitted = null;
            return false;
        }
    }

    private static bool MatchesGraph(GovernedLoopSequentialAdapterBinding adapter, GovernedLoopGraphRevisionArtifact artifact)
        => artifact.SchemaVersion == GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion
            && string.Equals(adapter.GraphArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal)
            && string.Equals(adapter.GraphLayoutHash, artifact.LayoutHash, StringComparison.Ordinal)
            && string.Equals(adapter.ExecutionBinding.Revision.GraphId, artifact.RevisionArtifact.Revision.GraphId, StringComparison.Ordinal)
            && string.Equals(adapter.ExecutionBinding.Revision.RevisionId, artifact.RevisionArtifact.Revision.RevisionId, StringComparison.Ordinal)
            && string.Equals(adapter.ExecutionBinding.Revision.ExecutableHash, artifact.RevisionArtifact.Revision.ExecutableHash, StringComparison.Ordinal);

    private static bool MatchesBinding(HumanReviewBinding binding, GovernedLoopSequentialAdapterBinding adapter)
    {
        var evidence = adapter.AdmissionReceipt.Evidence;
        return string.Equals(binding.WorkspaceId, adapter.WorkspaceId, StringComparison.Ordinal)
            && string.Equals(binding.RunId, adapter.ExecutionBinding.RunId, StringComparison.Ordinal)
            && string.Equals(binding.GraphId, adapter.ExecutionBinding.Revision.GraphId, StringComparison.Ordinal)
            && string.Equals(binding.RevisionId, adapter.ExecutionBinding.Revision.RevisionId, StringComparison.Ordinal)
            && string.Equals(binding.RevisionHash, adapter.ExecutionBinding.Revision.ExecutableHash, StringComparison.Ordinal)
            && string.Equals(binding.AuthorityProfileHash, evidence.GrantProfile.ContentHash.Value, StringComparison.Ordinal)
            && string.Equals(binding.AuthorityGrantHash, evidence.GrantDependencyEvidenceHash, StringComparison.Ordinal)
            && string.Equals(binding.CapabilityHash, GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(evidence.CapabilityAdmission), StringComparison.Ordinal)
            && string.Equals(binding.ModelProfileHash, evidence.ModelRoutingAdmission.ContentHash, StringComparison.Ordinal);
    }

    private static HumanReviewContinuationAuthorityReadStatus ClassifyGrant(GovernedLoopAdmissionReceipt admitted, AuthorityGrantResolution? resolution)
    {
        if (resolution is null || !Enum.IsDefined(resolution.Status))
        {
            return HumanReviewContinuationAuthorityReadStatus.Unavailable;
        }

        if (resolution.Status is AuthorityGrantResolutionStatus.Unknown or AuthorityGrantResolutionStatus.Unavailable or AuthorityGrantResolutionStatus.Ambiguous)
        {
            return HumanReviewContinuationAuthorityReadStatus.Unavailable;
        }
        if (resolution.Status is AuthorityGrantResolutionStatus.NotEffective or AuthorityGrantResolutionStatus.Suspended
            or AuthorityGrantResolutionStatus.Revoked or AuthorityGrantResolutionStatus.Expired
            or AuthorityGrantResolutionStatus.NotFound or AuthorityGrantResolutionStatus.ProfileUnavailable
            or AuthorityGrantResolutionStatus.RoleUnavailable or AuthorityGrantResolutionStatus.LoopUnavailable)
        {
            return HumanReviewContinuationAuthorityReadStatus.Revoked;
        }
        if (resolution.Status == AuthorityGrantResolutionStatus.Stale)
        {
            return HumanReviewContinuationAuthorityReadStatus.Stale;
        }
        if (resolution.Status == AuthorityGrantResolutionStatus.CeilingExceeded)
        {
            return HumanReviewContinuationAuthorityReadStatus.Narrowed;
        }
        if (resolution.Status != AuthorityGrantResolutionStatus.Active || !IsExactActiveGrant(admitted, resolution))
        {
            return HumanReviewContinuationAuthorityReadStatus.Stale;
        }

        if (AuthorityCeilingSubset.IsStrictSubset(resolution.EffectiveCeiling, admitted.Evidence.EffectiveAuthority))
        {
            return HumanReviewContinuationAuthorityReadStatus.Narrowed;
        }
        return AuthorityCeilingSubset.IsEqual(resolution.EffectiveCeiling, admitted.Evidence.EffectiveAuthority)
            ? HumanReviewContinuationAuthorityReadStatus.Current
            : HumanReviewContinuationAuthorityReadStatus.Stale;
    }

    private static bool IsExactActiveGrant(GovernedLoopAdmissionReceipt admitted, AuthorityGrantResolution resolution)
    {
        try
        {
            var grant = resolution.CurrentGrant ?? resolution.Grant;
            if (grant is null || !AuthorityGrantContractValidator.Validate(grant).IsValid)
            {
                return false;
            }

            var reference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
            var admittedBinding = new AuthorityGrantBinding(admitted.Evidence.GrantProfile, admitted.Intent.Role, admitted.Intent.Publication);
            return Equals(resolution.RequestedReference, admitted.Intent.AuthorityGrant)
                && Equals(reference, admitted.Intent.AuthorityGrant)
                && Equals(grant.Binding, admittedBinding)
                && string.Equals(resolution.DependencyEvidenceHash, admitted.Evidence.GrantDependencyEvidenceHash, StringComparison.Ordinal)
                && resolution.EvaluatedAtUtc != default
                && resolution.EvaluatedAtUtc.Offset == TimeSpan.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static HumanReviewContinuationAuthorityReadStatus ClassifyCapabilities(GovernedLoopAdmissionReceipt admitted, CapabilityRevalidationResult? capability)
    {
        if (capability is null || !Enum.IsDefined(capability.Status))
        {
            return HumanReviewContinuationAuthorityReadStatus.Unavailable;
        }
        if (capability.Status is CapabilityRevalidationStatus.Unknown or CapabilityRevalidationStatus.CatalogUnavailable or CapabilityRevalidationStatus.CatalogAmbiguous)
        {
            return HumanReviewContinuationAuthorityReadStatus.Unavailable;
        }
        if (capability.Status == CapabilityRevalidationStatus.AuthorityNarrowed)
        {
            return HumanReviewContinuationAuthorityReadStatus.Narrowed;
        }
        if (capability.Status is CapabilityRevalidationStatus.InvalidSnapshot or CapabilityRevalidationStatus.WorkspaceMismatch)
        {
            return HumanReviewContinuationAuthorityReadStatus.Invalid;
        }
        if (capability.Status is not CapabilityRevalidationStatus.Active
            || !capability.IsValid
            || capability.ObservedPins is { Count: > 0 }
            || !capability.EffectivePins.ToHashSet().SetEquals(admitted.Evidence.CapabilityAdmission.Pins))
        {
            return HumanReviewContinuationAuthorityReadStatus.Stale;
        }

        return HumanReviewContinuationAuthorityReadStatus.Current;
    }

    private static HumanReviewContinuationAuthorityReadResult Result(HumanReviewContinuationAuthorityReadStatus status) => new(status);
}
