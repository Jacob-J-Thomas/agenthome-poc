namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Identifies the catalog-owned canonical value semantics of one executable node parameter.</summary>
public enum GovernedLoopParameterValueKind
{
    /// <summary>An undefined parameter value kind.</summary>
    Unknown = 0,
    /// <summary>Bounded canonical Unicode text.</summary>
    Text,
    /// <summary>A canonical lowercase Boolean literal.</summary>
    Boolean,
    /// <summary>A canonical base-10 signed integer within an explicit inclusive range.</summary>
    Integer,
    /// <summary>A canonical custom-loop artifact identifier.</summary>
    Identifier,
    /// <summary>One exact ordinal value from a bounded catalog enumeration.</summary>
    Enumeration
}
