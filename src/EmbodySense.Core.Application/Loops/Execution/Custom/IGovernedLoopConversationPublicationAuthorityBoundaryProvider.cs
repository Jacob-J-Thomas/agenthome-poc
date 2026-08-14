using EmbodySense.Core.Application.Loops.Execution.Custom.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>Creates one caller-owned conversation-publication commit boundary from exact canonical run proof.</summary>
public interface IGovernedLoopConversationPublicationAuthorityBoundaryProvider
{
    /// <summary>Creates the single-use commit boundary for one exact canonical success-Exit publication.</summary>
    /// <param name="request">The complete immutable admission, execution, artifact, node, attempt, and publication identity.</param>
    /// <returns>A boundary that must invoke the publisher-owned append at most once.</returns>
    ConversationPublicationCommitBoundary CreateCommitBoundary(
        GovernedLoopConversationPublicationAuthorityRequest request);
}
