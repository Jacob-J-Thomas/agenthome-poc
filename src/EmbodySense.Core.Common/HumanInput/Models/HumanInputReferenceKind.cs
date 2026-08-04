namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Identifies a non-secret reference form permitted by a reference response.
/// </summary>
public enum HumanInputReferenceKind
{
    /// <summary>Unspecified and invalid.</summary>
    Unknown = 0,
    /// <summary>An application artifact identifier.</summary>
    Artifact = 1,
    /// <summary>An opaque safe-reference identifier, not a URL, path, or credential.</summary>
    Reference = 2
}
