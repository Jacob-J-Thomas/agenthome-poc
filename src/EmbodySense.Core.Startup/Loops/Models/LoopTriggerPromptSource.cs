namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Identifies the supported loop trigger prompt source values.
/// </summary>
public enum LoopTriggerPromptSource
{
    /// <summary>
    /// No supported prompt source was selected.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// The invoker supplies the trigger prompt.
    /// </summary>
    Invocation = 1,
    /// <summary>
    /// The persisted definition supplies the trigger prompt.
    /// </summary>
    Preset = 2,
    /// <summary>
    /// The run begins without trigger-prompt content.
    /// </summary>
    None = 3
}
