namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Owns one active-attempt cancellation registration and its interruption proof.
/// </summary>
public interface ICustomLoopAttemptCancellationRegistration : IDisposable
{
    /// <summary>
    /// Attempts to confirm provider interruption.
    /// </summary>
    /// <param name="observedCancellationToken">The observed cancellation token.</param>
    /// <returns><see langword="true"/> when the observed token proves this registered request was interrupted; otherwise, <see langword="false"/>.</returns>
    bool TryConfirmProviderInterruption(CancellationToken observedCancellationToken);
}
