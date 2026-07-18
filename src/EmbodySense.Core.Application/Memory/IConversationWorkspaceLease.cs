namespace EmbodySense.Core.Application.Memory;

public interface IConversationWorkspaceLease
{
    Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}
