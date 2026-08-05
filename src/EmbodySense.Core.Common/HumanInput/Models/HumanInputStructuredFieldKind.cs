namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Identifies a field data shape permitted inside a structured response.
/// </summary>
public enum HumanInputStructuredFieldKind
{
    /// <summary>Unspecified and invalid.</summary>
    Unknown = 0,
    /// <summary>Bounded text data.</summary>
    Text = 1,
    /// <summary>One selected, predeclared choice.</summary>
    Choice = 2
}
