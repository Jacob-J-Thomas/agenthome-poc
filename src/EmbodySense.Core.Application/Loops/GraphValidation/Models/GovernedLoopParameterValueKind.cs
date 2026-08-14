namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Identifies the catalog-owned canonical value semantics of one executable node parameter.</summary>
public enum GovernedLoopParameterValueKind
{
    /// <summary>An undefined parameter value kind.</summary>
    Unknown = 0,
    /// <summary>Bounded canonical Unicode text.</summary>
    Text = 1,
    /// <summary>A canonical lowercase Boolean literal.</summary>
    Boolean = 2,
    /// <summary>A canonical base-10 signed integer within an explicit inclusive range.</summary>
    Integer = 3,
    /// <summary>A canonical custom-loop artifact identifier.</summary>
    Identifier = 4,
    /// <summary>One exact ordinal value from a bounded catalog enumeration.</summary>
    Enumeration = 5,
    /// <summary>A canonical finite IEEE-754 JSON number without negative zero.</summary>
    Number = 6,
    /// <summary>A bounded canonical RFC 6901 JSON pointer, including the empty root pointer.</summary>
    JsonPointer = 7
}
