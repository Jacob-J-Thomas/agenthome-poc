using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Prepares exact successor candidates from canonical Human Input state and grant evidence.</summary>
public interface IHumanInputSupersedeCandidatePreparer
{
    /// <summary>Creates one bounded opaque candidate for a server-owned actor.</summary>
    /// <param name="input">The detached surface proposal and exact optimistic target state.</param>
    /// <param name="cancellationToken">The token used before candidate preparation completes.</param>
    /// <returns>A bounded preparation result; private binding and grant material never leaves Startup.</returns>
    Task<HumanInputSupersedePreparationResult> PrepareAsync(HumanInputSupersedePreparationInput? input, CancellationToken cancellationToken = default);

    /// <summary>Creates bounded server-generated reroute alternatives from canonical eligible respondents.</summary>
    /// <param name="input">The exact pending request and short candidate expiry.</param>
    /// <param name="cancellationToken">The token used before candidate preparation completes.</param>
    /// <returns>Opaque generic options or a value-free fail-closed disposition.</returns>
    Task<HumanInputReroutePreparationResult> PrepareRerouteAsync(HumanInputReroutePreparationInput? input, CancellationToken cancellationToken = default);

    /// <summary>Creates one bounded server-generated amend candidate from canonical request state.</summary>
    /// <param name="input">The exact pending request and bounded content/privacy/expiry proposal.</param>
    /// <param name="cancellationToken">The token used before candidate preparation completes.</param>
    /// <returns>An opaque candidate key or a value-free fail-closed disposition.</returns>
    Task<HumanInputAmendPreparationResult> PrepareAmendAsync(HumanInputAmendPreparationInput? input, CancellationToken cancellationToken = default);
}
