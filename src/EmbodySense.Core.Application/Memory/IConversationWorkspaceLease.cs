namespace EmbodySense.Core.Application.Memory;

/// <summary>
/// Provides cross-process exclusive ownership of a workspace conversation.
/// </summary>
public interface IConversationWorkspaceLease
{
    /// <summary>
    /// Waits for exclusive workspace ownership.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A lease whose disposal releases ownership.</returns>
    Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}
