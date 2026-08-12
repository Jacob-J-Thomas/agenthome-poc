using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Startup.Workspace;

/// <summary>Observes the read-only freshness decision before workspace initialization mutates <c>.agent</c>.</summary>
/// <remarks>Implementations must not treat observation as an authority or completion boundary.</remarks>
public interface IWorkspaceInitializationBoundaryObserver
{
    /// <summary>Observes the captured freshness and completion-marker state before mutation begins.</summary>
    /// <param name="paths">The canonical workspace paths.</param>
    /// <param name="wasFreshAgentHome">Whether <c>.agent</c> was absent at the read-only snapshot.</param>
    /// <param name="hadValidCompletionMarker">Whether an existing <c>.agent</c> had an exact valid completion marker.</param>
    /// <param name="cancellationToken">The token used to cancel observation.</param>
    /// <returns>A task that completes after observation.</returns>
    ValueTask OnFreshnessCapturedAsync(
        WorkspacePaths paths,
        bool wasFreshAgentHome,
        bool hadValidCompletionMarker,
        CancellationToken cancellationToken = default);
}
