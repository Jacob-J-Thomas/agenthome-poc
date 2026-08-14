using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Application.Loops.Execution.Authority;

internal static class GovernedLoopEffectAuthorityRequestValidator
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";

    internal static bool IsValid(GovernedLoopEffectAuthorityRequest? request)
    {
        if (request?.AdmissionReceipt is null
            || request.ExecutionBinding is null
            || request.GraphArtifact is null
            || !GovernedLoopAdmissionValidator.Validate(request.AdmissionReceipt).IsValid
            || !Equals(request.ExecutionBinding, request.AdmissionReceipt.Evidence.Binding)
            || !IsIdentifier(request.NodeId)
            || request.NodeAttempt is < 1 or > GovernedLoopEffectAuthorityContractLimits.MaxNodeAttempt
            || !IsIdentifier(request.EffectOperationId)
            || !IsIdentifier(request.CorrelationId)
            || !Enum.IsDefined(request.BoundaryKind)
            || Convert.ToInt32(request.BoundaryKind, System.Globalization.CultureInfo.InvariantCulture) == 0
            || !AuthorityProfileValidator.ValidateCeiling(request.RequiredAuthority).IsValid
            || request.RequiredCapabilityPins is null
            || request.RequiredCapabilityPins.Count is < 1 or > GovernedLoopEffectAuthorityContractLimits.MaxRequiredCapabilityPins)
        {
            return false;
        }

        try
        {
            if (!string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(request.GraphArtifact), request.GraphArtifact.ArtifactHash, StringComparison.Ordinal)
                || !string.Equals(request.GraphArtifact.ArtifactHash, request.AdmissionReceipt.Intent.GraphArtifactHash, StringComparison.Ordinal)
                || !string.Equals(request.GraphArtifact.LayoutHash, request.AdmissionReceipt.Intent.GraphLayoutHash, StringComparison.Ordinal)
                || !Equals(request.GraphArtifact.RevisionArtifact.Revision, request.ExecutionBinding.Revision)
                || !Equals(request.AdmissionReceipt.Intent.Publication.Revision, request.ExecutionBinding.Revision)
                || !Equals(request.GraphArtifact.Graph.OwningRole, request.AdmissionReceipt.Intent.Role))
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        var node = request.GraphArtifact.Graph.Nodes.SingleOrDefault(item => string.Equals(item.Id, request.NodeId, StringComparison.Ordinal));
        if (node is null)
        {
            return false;
        }

        var admitted = request.AdmissionReceipt.Evidence;
        if (!BoundaryMatchesNode(request.BoundaryKind, node, request.RequiredCapabilityPins)
            || !TargetFingerprintMatchesBoundary(request.BoundaryKind, request.TargetFingerprint)
            || !IsEqualOrNarrow(request.RequiredAuthority, admitted.EffectiveAuthority)
            || !PinsExactlyDescribeRequiredAuthority(request.RequiredCapabilityPins, request.RequiredAuthority.Capabilities)
            || request.RequiredCapabilityPins.Any(required => !admitted.CapabilityAdmission.Pins.Contains(required))
            || request.RequiredCapabilityPins.Any(required => !node.AuthorityCeiling.CapabilityIds.Contains(required.DescriptorIdentity.Id.Value, StringComparer.Ordinal)))
        {
            return false;
        }

        return true;
    }

    private static bool BoundaryMatchesNode(
        GovernedLoopEffectBoundaryKind boundaryKind,
        EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopNodeDefinition node,
        IReadOnlyList<CapabilityAdmissionPin> pins)
    {
        return boundaryKind switch
        {
            GovernedLoopEffectBoundaryKind.ProviderTransport => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference)
                && RequiresProviderCapabilities(node.AuthorityCeiling.CapabilityIds, pins),
            GovernedLoopEffectBoundaryKind.WorkspaceToolIntake or GovernedLoopEffectBoundaryKind.WorkspaceActuation => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference)
                && RequiresOnly(pins, WorkspaceCommandCapabilityId),
            GovernedLoopEffectBoundaryKind.ConversationPublication => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit)
                && RequiresOnly(pins, ConversationTurnCapabilityId),
            _ => false,
        };
    }

    private static bool RequiresProviderCapabilities(
        IReadOnlyList<string> nodeCapabilities,
        IReadOnlyList<CapabilityAdmissionPin> pins)
    {
        var toolEnabled = nodeCapabilities.Contains(WorkspaceCommandCapabilityId, StringComparer.Ordinal);
        if (!nodeCapabilities.Contains(ModelInferenceCapabilityId, StringComparer.Ordinal)
            || nodeCapabilities.Any(value => !string.Equals(value, ModelInferenceCapabilityId, StringComparison.Ordinal)
                && !string.Equals(value, WorkspaceCommandCapabilityId, StringComparison.Ordinal)))
        {
            return false;
        }

        var required = toolEnabled
            ? new[] { ModelInferenceCapabilityId, WorkspaceCommandCapabilityId }
            : [ModelInferenceCapabilityId];
        return pins.Count == required.Length
            && pins.Select(pin => pin.DescriptorIdentity.Id.Value).ToHashSet(StringComparer.Ordinal).SetEquals(required);
    }

    private static bool RequiresOnly(IReadOnlyList<CapabilityAdmissionPin> pins, string capabilityId)
        => pins.Count == 1 && string.Equals(pins[0].DescriptorIdentity.Id.Value, capabilityId, StringComparison.Ordinal);

    private static bool TargetFingerprintMatchesBoundary(GovernedLoopEffectBoundaryKind boundaryKind, string? targetFingerprint)
    {
        var targetBoundary = boundaryKind is GovernedLoopEffectBoundaryKind.WorkspaceToolIntake
            or GovernedLoopEffectBoundaryKind.WorkspaceActuation
            or GovernedLoopEffectBoundaryKind.ConversationPublication;
        return targetBoundary
            ? targetFingerprint is { Length: GovernedLoopEffectAuthorityContractLimits.Sha256HexCharacters }
                && targetFingerprint.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            : targetFingerprint is null;
    }

    private static bool IsIdentifier(string? value)
        => CustomLoopArtifactIdentifier.IsValid(value, GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters);

    private static bool IsEqualOrNarrow(AuthorityCeiling candidate, AuthorityCeiling current)
        => AuthorityCeilingSubset.IsEqual(candidate, current) || AuthorityCeilingSubset.IsStrictSubset(candidate, current);

    private static bool PinsExactlyDescribeRequiredAuthority(
        IReadOnlyList<CapabilityAdmissionPin> pins,
        IReadOnlyList<CapabilityDescriptorIdentity> capabilities)
    {
        return pins.Select(item => item.DescriptorIdentity.Id.Value).Distinct(StringComparer.Ordinal).Count() == pins.Count
            && pins.Select(item => item.DescriptorIdentity).ToHashSet().SetEquals(capabilities);
    }
}
