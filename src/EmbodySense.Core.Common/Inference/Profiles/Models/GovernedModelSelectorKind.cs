namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Identifies how a routing policy chooses its primary profile at admission.</summary>
public enum GovernedModelSelectorKind
{
    /// <summary>The selector is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>The policy names one exact profile.</summary>
    Exact = 1,
    /// <summary>The policy resolves the host default inside an explicit permitted set.</summary>
    Inherit = 2
}
