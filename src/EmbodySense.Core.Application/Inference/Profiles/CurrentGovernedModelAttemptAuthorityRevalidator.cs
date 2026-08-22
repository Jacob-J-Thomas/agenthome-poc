using System.Globalization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Revalidates the exact current grant and only the primary model-attempt capability subset.</summary>
public sealed class CurrentGovernedModelAttemptAuthorityRevalidator : IModelAttemptAuthorityRevalidator
{
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly ICapabilityAdmissionService _capabilityAdmissionService;

    /// <summary>Creates the current authority adapter over existing generic grant and capability ports.</summary>
    public CurrentGovernedModelAttemptAuthorityRevalidator(
        IAuthorityGrantResolver grantResolver,
        ICapabilityAdmissionService capabilityAdmissionService)
    {
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _capabilityAdmissionService = capabilityAdmissionService ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
    }

    /// <inheritdoc />
    public async Task<ModelAttemptAuthorityEvidence> RevalidateAsync(
        GovernedModelAttemptAdmissionRequest request,
        GovernedModelRoutingAdmissionEntry node,
        GovernedModelProfilePin primary,
        ModelInferenceDataPosture dataPosture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(dataPosture);
        if (!IsExactRequest(request, node, primary)
            || dataPosture.Status != ModelInferenceDataPostureStatus.Available)
        {
            return Result(request, node, primary, ModelAttemptAuthorityStatus.Unavailable, null);
        }

        AuthorityGrantResolution resolution;
        try
        {
            resolution = await _grantResolver.ResolveAsync(request.AdmissionReceipt.Intent.AuthorityGrant, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(request, node, primary, ModelAttemptAuthorityStatus.Unavailable, null);
        }

        if (resolution is null || resolution.Status is AuthorityGrantResolutionStatus.Unknown
            or AuthorityGrantResolutionStatus.Unavailable
            or AuthorityGrantResolutionStatus.Ambiguous)
        {
            return Result(request, node, primary, ModelAttemptAuthorityStatus.Unavailable, null);
        }

        if (resolution.Status != AuthorityGrantResolutionStatus.Active || !IsExactActiveGrant(request, resolution, dataPosture))
        {
            return Result(
                request,
                node,
                primary,
                ModelAttemptAuthorityStatus.Denied,
                AuthorityEvidenceHash(request, node, primary, resolution, []));
        }

        var requiredIds = new[]
        {
            ParseCapabilityId(ModelInferenceCapabilityId),
            primary.Capability.DescriptorIdentity.Id,
        };
        if (!TryNarrowCapabilityAdmission(
                request.AdmissionReceipt.Evidence.CapabilityAdmission,
                requiredIds,
                out var narrowed))
        {
            return Result(request, node, primary, ModelAttemptAuthorityStatus.Unavailable, null);
        }

        CapabilityRevalidationResult current;
        try
        {
            current = await _capabilityAdmissionService.RevalidateAsync(
                narrowed!,
                resolution.EffectiveCeiling.Capabilities.Select(identity => identity.Id).ToArray(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result(request, node, primary, ModelAttemptAuthorityStatus.Unavailable, null);
        }

        if (current is null || current.Status is CapabilityRevalidationStatus.Unknown
            or CapabilityRevalidationStatus.CatalogUnavailable
            or CapabilityRevalidationStatus.CatalogAmbiguous
            or CapabilityRevalidationStatus.InvalidSnapshot
            or CapabilityRevalidationStatus.WorkspaceMismatch)
        {
            return Result(request, node, primary, ModelAttemptAuthorityStatus.Unavailable, null);
        }

        var requiredPins = narrowed!.Pins;
        var exactCurrent = current.IsValid
            && current.Status == CapabilityRevalidationStatus.Active
            && current.EffectivePins.ToHashSet().SetEquals(requiredPins)
            && (current.ObservedPins?.Count ?? 0) == 0;
        var evidenceHash = AuthorityEvidenceHash(request, node, primary, resolution, current.EffectivePins);
        return Result(
            request,
            node,
            primary,
            exactCurrent ? ModelAttemptAuthorityStatus.Allowed : ModelAttemptAuthorityStatus.Denied,
            evidenceHash);
    }

    private static bool IsExactRequest(
        GovernedModelAttemptAdmissionRequest request,
        GovernedModelRoutingAdmissionEntry node,
        GovernedModelProfilePin primary)
    {
        try
        {
            var receipt = request.AdmissionReceipt;
            return GovernedModelContractValidator.IsValid(request.RoutingAdmission)
                && GovernedLoopAdmissionValidator.Validate(receipt).IsValid
                && GovernedLoopAdmissionContractHash.Matches(receipt)
                && string.Equals(receipt.Evidence.ModelRoutingAdmission.ContentHash, request.RoutingAdmission.ContentHash, StringComparison.Ordinal)
                && string.Equals(receipt.Evidence.Binding.RunId, request.RunId, StringComparison.Ordinal)
                && receipt.Evidence.Binding.ExecutionGeneration == request.ExecutionGeneration
                && string.Equals(request.RoutingAdmission.OwningRoleId, receipt.Intent.Role.Identity.RoleId, StringComparison.Ordinal)
                && string.Equals(node.NodeId, request.NodeId, StringComparison.Ordinal)
                && string.Equals(node.NodeTypeId, request.NodeTypeId, StringComparison.Ordinal)
                && string.Equals(node.ContentHash, request.RoutingAdmission.Entries.Single(entry => string.Equals(entry.NodeId, request.NodeId, StringComparison.Ordinal)).ContentHash, StringComparison.Ordinal)
                && string.Equals(primary.ContentHash, node.Primary.ContentHash, StringComparison.Ordinal)
                && string.Equals(request.RequestedPrimaryPinHash, primary.ContentHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExactActiveGrant(
        GovernedModelAttemptAdmissionRequest request,
        AuthorityGrantResolution resolution,
        ModelInferenceDataPosture dataPosture)
    {
        var receipt = request.AdmissionReceipt;
        var grant = resolution.CurrentGrant ?? resolution.Grant;
        if (grant is null || !AuthorityGrantContractValidator.Validate(grant).IsValid)
        {
            return false;
        }

        var reference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
        var admittedBinding = new AuthorityGrantBinding(receipt.Evidence.GrantProfile, receipt.Intent.Role, receipt.Intent.Publication);
        var currentIsEqualOrNarrow = AuthorityCeilingSubset.IsEqual(resolution.EffectiveCeiling, receipt.Evidence.EffectiveAuthority)
            || AuthorityCeilingSubset.IsStrictSubset(resolution.EffectiveCeiling, receipt.Evidence.EffectiveAuthority);
        var modelInferenceIdentity = receipt.Evidence.CapabilityAdmission.Pins.SingleOrDefault(pin =>
            string.Equals(pin.DescriptorIdentity.Id.Value, ModelInferenceCapabilityId, StringComparison.Ordinal))?.DescriptorIdentity;
        var primaryIdentity = receipt.Evidence.CapabilityAdmission.Pins.SingleOrDefault(pin =>
            string.Equals(pin.DescriptorIdentity.Id.Value,
                request.RoutingAdmission.Entries.Single(entry => string.Equals(entry.NodeId, request.NodeId, StringComparison.Ordinal)).Primary.Capability.DescriptorIdentity.Id.Value,
                StringComparison.Ordinal))?.DescriptorIdentity;
        return Equals(resolution.RequestedReference, receipt.Intent.AuthorityGrant)
            && Equals(reference, receipt.Intent.AuthorityGrant)
            && Equals(grant.Binding, admittedBinding)
            && string.Equals(resolution.DependencyEvidenceHash, receipt.Evidence.GrantDependencyEvidenceHash, StringComparison.Ordinal)
            && resolution.EvaluatedAtUtc != default
            && resolution.EvaluatedAtUtc.Offset == TimeSpan.Zero
            && currentIsEqualOrNarrow
            && modelInferenceIdentity is not null
            && primaryIdentity is not null
            && resolution.EffectiveCeiling.Capabilities.Contains(modelInferenceIdentity)
            && resolution.EffectiveCeiling.Capabilities.Contains(primaryIdentity)
            && dataPosture.DataClasses.All(resolution.EffectiveCeiling.DataClasses.Contains);
    }

    private static bool TryNarrowCapabilityAdmission(
        CapabilityAdmissionSnapshot admitted,
        IReadOnlyCollection<CapabilityId> requiredIds,
        out CapabilityAdmissionSnapshot? narrowed)
    {
        narrowed = null;
        try
        {
            var required = requiredIds.ToHashSet();
            var manifest = admitted.Requirements with
            {
                Required = admitted.Requirements.Required.Where(item => required.Contains(item.CapabilityId)).ToArray(),
                Optional = admitted.Requirements.Optional.Where(item => required.Contains(item.CapabilityId)).ToArray(),
            };
            var pins = admitted.Pins.Where(pin => required.Contains(pin.DescriptorIdentity.Id)).ToArray();
            var evidence = admitted.Evidence.Where(item => required.Contains(item.DependencyId)).ToArray();
            if (pins.Length != required.Count
                || evidence.Length != required.Count
                || manifest.Required.Count + manifest.Optional.Count != required.Count
                || !CapabilityDependencyManifestHash.TryCompute(manifest, out var manifestHash, out _))
            {
                return false;
            }

            var candidate = new CapabilityAdmissionSnapshot(
                admitted.SchemaVersion,
                admitted.WorkspaceScopeId,
                manifest,
                manifestHash!.Value,
                pins,
                evidence,
                admitted.AdmittedAtUtc);
            if (CapabilityAdmissionSnapshotValidator.Validate(candidate) is not null)
            {
                return false;
            }

            narrowed = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string AuthorityEvidenceHash(
        GovernedModelAttemptAdmissionRequest request,
        GovernedModelRoutingAdmissionEntry node,
        GovernedModelProfilePin primary,
        AuthorityGrantResolution resolution,
        IReadOnlyList<CapabilityAdmissionPin> currentPins)
        => GovernedModelAttemptEvidenceHash.Create(
            "embodysense.model-attempt-current-authority.v1",
            request.RoutingAdmission.ContentHash,
            request.AdmissionReceipt.ContentHash,
            request.RunId,
            request.ExecutionGeneration.ToString(CultureInfo.InvariantCulture),
            node.ContentHash,
            primary.ContentHash,
            request.PlanOrdinal.ToString(CultureInfo.InvariantCulture),
            request.ActivationOrdinal.ToString(CultureInfo.InvariantCulture),
            request.VisitOrdinal.ToString(CultureInfo.InvariantCulture),
            request.AttemptNumber.ToString(CultureInfo.InvariantCulture),
            request.AttemptOperationId,
            ((int)resolution.Status).ToString(CultureInfo.InvariantCulture),
            resolution.DependencyEvidenceHash,
            string.Join('\n', currentPins.OrderBy(pin => pin.DescriptorIdentity.Id.Value, StringComparer.Ordinal)
                .Select(pin => pin.DescriptorIdentity.Id.Value + "\n" + pin.DescriptorIdentity.Version.Value + "\n" + pin.DescriptorIdentity.Hash.Value)));

    private static ModelAttemptAuthorityEvidence Result(
        GovernedModelAttemptAdmissionRequest request,
        GovernedModelRoutingAdmissionEntry node,
        GovernedModelProfilePin primary,
        ModelAttemptAuthorityStatus status,
        string? evidenceHash)
        => new(
            status,
            request.RoutingAdmission.ContentHash,
            request.RunId,
            request.ExecutionGeneration,
            request.RoutingAdmission.OwningRoleId,
            node.NodeId,
            primary.ContentHash,
            request.PlanOrdinal,
            request.ActivationOrdinal,
            request.VisitOrdinal,
            request.AttemptNumber,
            request.AttemptOperationId,
            evidenceHash);

    private static CapabilityId ParseCapabilityId(string value)
    {
        _ = CapabilityId.TryParse(value, out var result, out _);
        return result!;
    }
}
