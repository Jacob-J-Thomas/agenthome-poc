using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>
/// Provides interface transport limits derived from the enforced custom-loop request bounds.
/// </summary>
public static class LoopRunTransportLimits
{
    private const int MaxJsonUtf8BytesPerCharacter = 6;
    private const int SignalRInvocationEnvelopeUtf8Bytes = 8_192;

    /// <summary>
    /// Gets the SignalR receive limit that safely contains a maximally sized invocation prompt plus
    /// worst-case JSON escaping and bounded protocol-envelope overhead.
    /// </summary>
    public const long MaxSignalRInvocationMessageUtf8Bytes = (long)CustomLoopLimits.MaxPresetPromptCharacters * MaxJsonUtf8BytesPerCharacter + SignalRInvocationEnvelopeUtf8Bytes;
}
