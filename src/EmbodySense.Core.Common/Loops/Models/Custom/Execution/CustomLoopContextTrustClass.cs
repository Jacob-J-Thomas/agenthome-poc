namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Identifies the supported custom loop context trust class values.
/// </summary>
public enum CustomLoopContextTrustClass
{
    /// <summary>
    /// Identifies the unknown custom loop context trust class.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the non overridable governance custom loop context trust class.
    /// </summary>
    NonOverridableGovernance = 1,
    /// <summary>
    /// Identifies the trusted instruction custom loop context trust class.
    /// </summary>
    TrustedInstruction = 2,
    /// <summary>
    /// Identifies the trusted metadata custom loop context trust class.
    /// </summary>
    TrustedMetadata = 3,
    /// <summary>
    /// Identifies the untrusted data custom loop context trust class.
    /// </summary>
    UntrustedData = 4
}
