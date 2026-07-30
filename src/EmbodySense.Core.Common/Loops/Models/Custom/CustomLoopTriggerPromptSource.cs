namespace EmbodySense.Core.Common.Loops.Models.Custom;

/// <summary>
/// Identifies the supported custom loop trigger prompt source values.
/// </summary>
public enum CustomLoopTriggerPromptSource
{
    /// <summary>
    /// Identifies the unknown custom loop trigger prompt source.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the invocation custom loop trigger prompt source.
    /// </summary>
    Invocation = 1,
    /// <summary>
    /// Identifies the preset custom loop trigger prompt source.
    /// </summary>
    Preset = 2,
    /// <summary>
    /// Identifies the none custom loop trigger prompt source.
    /// </summary>
    None = 3
}
