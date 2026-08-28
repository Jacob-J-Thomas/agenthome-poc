using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Policies;

/// <summary>Contains deterministic validation failures for one Human Input policy artifact.</summary>
/// <param name="Errors">The ordered validation failures.</param>
public sealed record HumanInputPolicyArtifactValidationResult(IReadOnlyList<HumanInputPolicyArtifactValidationError> Errors)
{
    /// <summary>Gets whether the artifact is complete, canonical, and hash-authenticated.</summary>
    public bool IsValid => Errors.Count == 0;
}
