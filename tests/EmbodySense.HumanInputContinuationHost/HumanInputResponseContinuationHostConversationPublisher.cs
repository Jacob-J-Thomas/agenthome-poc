using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;

namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputResponseContinuationHostConversationPublisher : ICustomLoopConversationPublisher
{
    public Task<CustomLoopConversationPublicationResult> PublishAsync(CustomLoopConversationPublicationRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("The bounded process fixture has no conversation target and must not publish a message.");
}
