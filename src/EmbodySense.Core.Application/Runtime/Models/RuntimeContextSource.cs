namespace EmbodySense.Core.Application.Runtime.Models;

/// <summary>
/// Identifies the supported runtime context source values.
/// </summary>
public enum RuntimeContextSource
{
    /// <summary>
    /// Identifies the unknown runtime context source.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the startup context runtime context source.
    /// </summary>
    StartupContext,
    /// <summary>
    /// Identifies the restored conversation history runtime context source.
    /// </summary>
    RestoredConversationHistory,
    /// <summary>
    /// Identifies the session transcript runtime context source.
    /// </summary>
    SessionTranscript,
    /// <summary>
    /// Identifies the current turn input runtime context source.
    /// </summary>
    CurrentTurnInput
}
