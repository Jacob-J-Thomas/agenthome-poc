namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopConversationPublisher
{
    Task<CustomLoopConversationPublicationResult> PublishAsync(CustomLoopConversationPublicationRequest request, CancellationToken cancellationToken = default);
}
