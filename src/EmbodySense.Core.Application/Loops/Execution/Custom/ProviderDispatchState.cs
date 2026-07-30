namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Represents a provider dispatch state.
/// </summary>
internal sealed class ProviderDispatchState
{
    private int _providerWasInvoked;

    /// <summary>
    /// Gets a value indicating whether the provider was invoked condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the provider was invoked condition holds; otherwise, <see langword="false"/>.</value>
    public bool ProviderWasInvoked => Volatile.Read(ref _providerWasInvoked) != 0;

    /// <summary>
    /// Executes the mark provider request started operation.
    /// </summary>
    /// <returns>The operation.</returns>
    public void MarkProviderRequestStarted() => Interlocked.Exchange(ref _providerWasInvoked, 1);
}
