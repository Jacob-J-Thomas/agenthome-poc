namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Identifies the result of pure boundary validation, not lifecycle acceptance, persistence, or continuation.
/// </summary>
public enum HumanInputResponseOutcomeKind
{
    /// <summary>The response is structurally valid untrusted data.</summary>
    Valid = 1,
    /// <summary>The response is invalid and must be rejected by a future lifecycle owner.</summary>
    Invalid = 2
}
