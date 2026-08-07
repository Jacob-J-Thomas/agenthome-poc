namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Identifies the explicit value channel used by a governed loop binding.</summary>
public enum GovernedLoopBindingKind
{
    /// <summary>An undefined binding kind.</summary>
    Unknown = 0,
    /// <summary>Typed operational data.</summary>
    Data,
    /// <summary>Typed context deliberately admitted to a node.</summary>
    Context
}
