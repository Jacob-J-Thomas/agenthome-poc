using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Verifies already-authenticated event evidence before a durable wake continuation is prepared.</summary>
/// <remarks>
/// Implementations project an authoritative authentication source. They do not sense new events and must bind every
/// result to the exact checkpoint, admitted event reference, and submitted evidence hash in the request.
/// </remarks>
public interface IGovernedLoopAuthenticatedWakeVerificationPort
{
    /// <summary>Verifies one exact authenticated-event delivery and its eligibility for the sleeping checkpoint.</summary>
    /// <param name="request">The exact checkpoint and submitted authentication-evidence coordinates.</param>
    /// <param name="cancellationToken">The token used before durable prepared wake intent exists.</param>
    /// <returns>A bounded verification result, or <see langword="null"/> when an adapter violates the port contract.</returns>
    Task<GovernedLoopAuthenticatedWakeVerificationResult?> VerifyAsync(
        GovernedLoopAuthenticatedWakeVerificationRequest request,
        CancellationToken cancellationToken = default);
}
