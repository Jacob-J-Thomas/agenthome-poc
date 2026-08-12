using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>Adapts canonical publication proof into the shared governed effect-authority boundary.</summary>
public sealed class GovernedLoopConversationPublicationAuthorityBoundaryProvider(
    IGovernedLoopEffectAuthorityBoundary effectAuthorityBoundary)
    : IGovernedLoopConversationPublicationAuthorityBoundaryProvider
{
    private readonly IGovernedLoopEffectAuthorityBoundary _effectAuthorityBoundary = effectAuthorityBoundary
        ?? throw new ArgumentNullException(nameof(effectAuthorityBoundary));

    /// <inheritdoc />
    public ConversationPublicationCommitBoundary CreateCommitBoundary(
        GovernedLoopConversationPublicationAuthorityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var boundary = new GovernedLoopConversationPublicationCommitBoundary(
            _effectAuthorityBoundary,
            request.AdmissionReceipt,
            request.ExecutionBinding,
            request.GraphArtifact,
            request.NodeId,
            request.NodeAttempt,
            request.PublicationOperationId);
        return boundary.CommitAsync;
    }
}
