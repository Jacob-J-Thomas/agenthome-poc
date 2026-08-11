using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>Creates exact effect-authority requests for one admitted conversation publication.</summary>
public static class ConversationPublicationEffectAuthorityRequestFactory
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";

    /// <summary>Creates one deterministic publication request bound to complete immutable admission evidence.</summary>
    /// <param name="admissionReceipt">The complete exact successful admission receipt retained by the run.</param>
    /// <param name="executionBinding">The exact run, revision, and execution generation.</param>
    /// <param name="graphArtifact">The exact immutable graph artifact retained by the run.</param>
    /// <param name="nodeId">The exact success-Exit node identity.</param>
    /// <param name="nodeAttempt">The exact positive node-attempt number.</param>
    /// <param name="publicationOperationId">The stable identity-bearing conversation publication operation.</param>
    /// <returns>A request containing the exact admitted conversation-turn pin and a non-granting publication ceiling.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required evidence object is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the node attempt is unsupported.</exception>
    /// <exception cref="ArgumentException">Thrown when any retained identity, graph, authority, pin, or publication identity is not exact and bounded.</exception>
    public static GovernedLoopEffectAuthorityRequest Create(
        GovernedLoopAdmissionReceipt admissionReceipt,
        GovernedLoopExecutionBinding executionBinding,
        GovernedLoopGraphRevisionArtifact graphArtifact,
        string nodeId,
        int nodeAttempt,
        string publicationOperationId)
    {
        ArgumentNullException.ThrowIfNull(admissionReceipt);
        ArgumentNullException.ThrowIfNull(executionBinding);
        ArgumentNullException.ThrowIfNull(graphArtifact);

        if (nodeAttempt is < 1 or > GovernedLoopEffectAuthorityContractLimits.MaxNodeAttempt)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeAttempt), nodeAttempt, "The node attempt is outside the governed effect-authority bound.");
        }

        nodeId = CustomLoopArtifactIdentifier.Require(nodeId, nameof(nodeId), GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters);
        publicationOperationId = CustomLoopArtifactIdentifier.Require(
            publicationOperationId,
            nameof(publicationOperationId),
            GovernedLoopEffectAuthorityContractLimits.MaxIdentifierCharacters);
        ValidateRetainedEvidence(admissionReceipt, executionBinding, graphArtifact);

        var node = graphArtifact.Graph.Nodes.SingleOrDefault(item => string.Equals(item.Id, nodeId, StringComparison.Ordinal));
        if (node is null || !Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit))
        {
            throw new ArgumentException("Conversation-publication authority requires the exact admitted success-Exit node.", nameof(nodeId));
        }

        var admittedPins = admissionReceipt.Evidence.CapabilityAdmission.Pins;
        var conversationPins = admittedPins
            .Where(item => string.Equals(item.DescriptorIdentity.Id.Value, ConversationTurnCapabilityId, StringComparison.Ordinal))
            .ToArray();
        if (conversationPins.Length != 1
            || !node.AuthorityCeiling.CapabilityIds.Contains(ConversationTurnCapabilityId, StringComparer.Ordinal)
            || !admissionReceipt.Evidence.EffectiveAuthority.Capabilities.Contains(conversationPins[0].DescriptorIdentity))
        {
            throw new ArgumentException("The exact Exit-node ceiling and successful admission receipt must contain one identical conversation-turn pin.", nameof(admissionReceipt));
        }

        var admittedAuthority = admissionReceipt.Evidence.EffectiveAuthority;
        if (admittedAuthority.MaxTargetCount < 1 || !admittedAuthority.AllowsExternalPublication)
        {
            throw new ArgumentException("The admitted authority cannot be widened to one external conversation publication target.", nameof(admissionReceipt));
        }

        var requiredAuthority = new AuthorityCeiling(
            [conversationPins[0].DescriptorIdentity],
            admittedAuthority.DataClasses.ToArray(),
            1,
            CapabilitySideEffectClass.None,
            false,
            true,
            false);
        if (!AuthorityProfileValidator.ValidateCeiling(requiredAuthority).IsValid
            || !(AuthorityCeilingSubset.IsEqual(requiredAuthority, admittedAuthority)
                || AuthorityCeilingSubset.IsStrictSubset(requiredAuthority, admittedAuthority)))
        {
            throw new ArgumentException("The derived conversation-publication ceiling was not an exact non-granting narrowing of admitted authority.", nameof(admissionReceipt));
        }

        var targetFingerprint = GovernedLoopEffectAuthorityOperationIdentity.CreateConversationPublicationTargetFingerprint(admissionReceipt);
        var effectOperationId = GovernedLoopEffectAuthorityOperationIdentity.CreateConversationPublication(
            admissionReceipt,
            executionBinding,
            graphArtifact,
            nodeId,
            nodeAttempt,
            publicationOperationId,
            targetFingerprint);
        return new GovernedLoopEffectAuthorityRequest(
            admissionReceipt,
            executionBinding,
            graphArtifact,
            nodeId,
            nodeAttempt,
            effectOperationId,
            publicationOperationId,
            GovernedLoopEffectBoundaryKind.ConversationPublication,
            requiredAuthority,
            conversationPins);
    }

    private static void ValidateRetainedEvidence(
        GovernedLoopAdmissionReceipt receipt,
        GovernedLoopExecutionBinding binding,
        GovernedLoopGraphRevisionArtifact artifact)
    {
        try
        {
            if (!GovernedLoopAdmissionValidator.Validate(receipt).IsValid
                || !Equals(binding, receipt.Evidence.Binding)
                || !string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact), artifact.ArtifactHash, StringComparison.Ordinal)
                || !string.Equals(artifact.ArtifactHash, receipt.Intent.GraphArtifactHash, StringComparison.Ordinal)
                || !string.Equals(artifact.LayoutHash, receipt.Intent.GraphLayoutHash, StringComparison.Ordinal)
                || !Equals(artifact.RevisionArtifact.Revision, binding.Revision)
                || !Equals(receipt.Intent.Publication.Revision, binding.Revision)
                || !Equals(artifact.Graph.OwningRole, receipt.Intent.Role))
            {
                throw new ArgumentException("The retained admission receipt, execution binding, and graph artifact do not identify one exact admitted run.", nameof(receipt));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new ArgumentException("The retained admission receipt, execution binding, or graph artifact was malformed.", nameof(receipt), exception);
        }
    }

}
