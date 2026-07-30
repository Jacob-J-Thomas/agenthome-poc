namespace EmbodySense.Core.Common.Inference.Models;

/// <summary>
/// Identifies the supported LLM message role values.
/// </summary>
public enum LlmMessageRole
{
    /// <summary>
    /// Identifies the unknown LLM message role.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the system LLM message role.
    /// </summary>
    System = 1,
    /// <summary>
    /// Identifies the user LLM message role.
    /// </summary>
    User = 2,
    /// <summary>
    /// Identifies the assistant LLM message role.
    /// </summary>
    Assistant = 3,
    /// <summary>
    /// Identifies the tool LLM message role.
    /// </summary>
    Tool = 4
}
