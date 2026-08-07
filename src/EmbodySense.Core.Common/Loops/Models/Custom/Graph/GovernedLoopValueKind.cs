namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Identifies the portable value shape carried by a governed loop port.</summary>
public enum GovernedLoopValueKind
{
    /// <summary>An undefined value kind.</summary>
    Unknown = 0,
    /// <summary>A Unicode text value.</summary>
    Text,
    /// <summary>A Boolean value.</summary>
    Boolean,
    /// <summary>A signed integer value.</summary>
    Integer,
    /// <summary>A finite numeric value.</summary>
    Number,
    /// <summary>A structured object value.</summary>
    Object,
    /// <summary>An ordered array value.</summary>
    Array,
    /// <summary>An opaque binary value.</summary>
    Binary
}
