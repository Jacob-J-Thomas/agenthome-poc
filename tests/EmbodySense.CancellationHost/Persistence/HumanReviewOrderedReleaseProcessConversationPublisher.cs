using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessConversationPublisher : ICustomLoopConversationPublisher
{
    public Task<CustomLoopConversationPublicationResult> PublishAsync(CustomLoopConversationPublicationRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("The process-observable Human Review fixture has no invoking conversation.");
}
