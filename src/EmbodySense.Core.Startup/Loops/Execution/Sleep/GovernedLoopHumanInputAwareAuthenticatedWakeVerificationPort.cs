using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.HumanInput;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Routes reserved Human Input response wake verification without replacing the configured external Wait verifier.</summary>
/// <remarks>
/// The reserved Human Input event-reference prefix is not authorable by generic Wait nodes. This router owns no
/// authentication state: it delegates each exact request to either the canonical Human Input response verifier or the
/// existing surface-owned external verifier, preserving their independent fail-closed policies.
/// </remarks>
public sealed class GovernedLoopHumanInputAwareAuthenticatedWakeVerificationPort : IGovernedLoopAuthenticatedWakeVerificationPort
{
    private readonly IGovernedLoopAuthenticatedWakeVerificationPort _external;
    private readonly IGovernedLoopAuthenticatedWakeVerificationPort _humanInput;

    /// <summary>Creates one exact-prefix router over the existing external and Human Input verification ports.</summary>
    /// <param name="external">The configured verifier for every non-Human-Input authenticated event.</param>
    /// <param name="humanInput">The canonical verifier for reserved Human Input response events.</param>
    public GovernedLoopHumanInputAwareAuthenticatedWakeVerificationPort(
        IGovernedLoopAuthenticatedWakeVerificationPort external,
        IGovernedLoopAuthenticatedWakeVerificationPort humanInput)
    {
        _external = external ?? throw new ArgumentNullException(nameof(external));
        _humanInput = humanInput ?? throw new ArgumentNullException(nameof(humanInput));
    }

    /// <inheritdoc />
    public Task<GovernedLoopAuthenticatedWakeVerificationResult?> VerifyAsync(
        GovernedLoopAuthenticatedWakeVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.AuthenticatedEventReference.StartsWith(
            GovernedLoopHumanInputContinuationVocabulary.AuthenticatedEventReferencePrefix,
            StringComparison.Ordinal)
            ? _humanInput.VerifyAsync(request, cancellationToken)
            : _external.VerifyAsync(request, cancellationToken);
    }
}
