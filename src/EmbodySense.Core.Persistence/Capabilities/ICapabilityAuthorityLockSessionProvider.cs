namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Acquires the trusted cross-process lock session underlying a workspace capability-authority transaction.</summary>
/// <remarks>This is an infrastructure composition seam. Returning a session that does not provide exclusive workspace authority invalidates the transaction's security guarantees.</remarks>
public interface ICapabilityAuthorityLockSessionProvider
{
    /// <summary>Attempts to acquire one exclusive authority session.</summary>
    /// <param name="cancellationToken">The cancellation token used while waiting for exclusive authority.</param>
    /// <returns>An owned session, or <see langword="null"/> when the authority root is unavailable.</returns>
    Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken = default);
}
