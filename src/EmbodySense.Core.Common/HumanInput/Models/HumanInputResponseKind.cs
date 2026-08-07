namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Identifies the only response data shapes supported by schema 1.
/// </summary>
public enum HumanInputResponseKind
{
    /// <summary>Unspecified and invalid.</summary>
    Unknown = 0,
    /// <summary>Bounded text data.</summary>
    Text = 1,
    /// <summary>One selected, predeclared choice.</summary>
    Choice = 2,
    /// <summary>A boolean selection recorded as data, never an approval.</summary>
    Confirmation = 3,
    /// <summary>A bounded set of typed fields.</summary>
    Structured = 4,
    /// <summary>A safe, declared artifact or reference identifier.</summary>
    Reference = 5
}
