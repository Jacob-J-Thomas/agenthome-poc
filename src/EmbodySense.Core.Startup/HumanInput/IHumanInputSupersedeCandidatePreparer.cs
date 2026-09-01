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
}
