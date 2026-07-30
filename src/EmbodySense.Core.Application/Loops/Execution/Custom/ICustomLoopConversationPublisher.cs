using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Publishes selected custom-loop outputs into a conversation through an idempotent operation.
/// </summary>
public interface ICustomLoopConversationPublisher
{
    /// <summary>
    /// Publishes one retained output subject to expected conversation identity and version.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The committed, replayed, or compare-and-append conflict result.</returns>
    Task<CustomLoopConversationPublicationResult> PublishAsync(CustomLoopConversationPublicationRequest request, CancellationToken cancellationToken = default);
}
